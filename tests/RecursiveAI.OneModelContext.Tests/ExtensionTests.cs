using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Extensions;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>The public extension mechanism, on the modern era and on two legacy revisions.</summary>
public class ExtensionTests
{
  private const string Ext = "com.example/echo";
  private static readonly JsonElement ObjectSchema = Json.Parse("""{"type":"object"}""");

  public static IEnumerable<object[]> Revisions() =>
    [[EraPreference.Modern, ProtocolVersions.V20260728], [EraPreference.Legacy, ProtocolVersions.V20251125], [EraPreference.Legacy, ProtocolVersions.V20250326]];

  private sealed class Pair : IAsyncDisposable
  {
    private readonly CancellationTokenSource _stop = new();
    private Task? _serving;

    public required ContextServer Server { get; init; }

    public ContextClient Client { get; private set; } = null!;

    public static async Task<Pair> ConnectAsync(ContextServer server, EraPreference era, string version, params ClientExtension[] extensions)
    {
      var pair = new Pair { Server = server };
      var (a, b) = PipeEnd.CreatePair();
      pair._serving = server.RunAsync(ContextTransport.Stream(a), TestContext.Current.CancellationToken);
      pair.Client = await ContextClient.ConnectAsync(
        ContextTransport.Stream(b),
        new ContextClientOptions { Era = era, SupportedVersions = [version], Extensions = extensions, EraCache = null },
        TestContext.Current.CancellationToken);
      return pair;
    }

    public async ValueTask DisposeAsync()
    {
      await Client.DisposeAsync();
      await _stop.CancelAsync();
      if (_serving is not null)
      {
        await _serving.WaitAsync(TimeSpan.FromSeconds(10));
      }
    }
  }

  private static ContextServer NewServer() => new("ext", "1.0");

  private static JsonElement Settings(string json) => Json.Parse(json);

  private static void AddProbe(ContextServer server, string id = Ext) =>
    server.Tools.Add(new ToolDefinition("probe", "Reports what the caller declared.", ObjectSchema), (_, ctx) =>
    {
      var settings = ctx.TryGetExtensionSettings(id, out var s) ? s.GetRawText() : "none";
      return ValueTask.FromResult(CallToolResult.FromText(ctx.SupportsExtension(id) + "|" + settings));
    });

  [Fact]
  public void Identifiers_follow_the_meta_key_rules_with_a_mandatory_prefix()
  {
    Assert.True(ExtensionId.IsValid("io.modelcontextprotocol/ui", out _));
    Assert.True(ExtensionId.IsValid("com.example/my-extension_2.x", out _));
    foreach (var bad in new[] { "", "ui", "/ui", "com.example/", "com.example/a/b", "com..example/x", "1com.example/x", "com.-example/x", "com.example-/x", "com.example/-x", "com.example/x-", "com.exa mple/x", "com.example/x y", "com.example/é" })
    {
      Assert.False(ExtensionId.IsValid(bad, out var reason), bad);
      Assert.NotEmpty(reason);
    }
  }

  [Fact]
  public void The_protocols_own_prefixes_are_reserved_for_official_extensions()
  {
    Assert.Throws<ArgumentException>(() => new ExtensionRegistration("io.modelcontextprotocol/mine"));
    Assert.Throws<ArgumentException>(() => new ClientExtension("org.mcp.tools/mine"));
    Assert.Throws<ArgumentException>(() => new ExtensionRegistration("not valid"));
    Assert.Throws<ArgumentException>(() => new ExtensionRegistration(Ext, Json.Parse("[1]")));
  }

  [Fact]
  public void Methods_cannot_shadow_the_core_and_cannot_be_added_to_a_registered_extension()
  {
    var registration = new ExtensionRegistration(Ext);
    Assert.Throws<ArgumentException>(() => registration.Request("tools/call", (_, _) => default));
    registration.Request("example/echo", (p, _) => ValueTask.FromResult(p));
    Assert.Throws<ArgumentException>(() => registration.Request("example/echo", (p, _) => ValueTask.FromResult(p)));

    var server = NewServer();
    server.Extensions.Add(registration);
    Assert.Throws<InvalidOperationException>(() => registration.Request("example/late", (p, _) => ValueTask.FromResult(p)));
    var rival = new ExtensionRegistration("com.example/rival").Request("example/echo", (p, _) => ValueTask.FromResult(p));
    Assert.Throws<ArgumentException>(() => server.Extensions.Add(rival));
  }

  [Theory]
  [MemberData(nameof(Revisions))]
  public async Task Nothing_is_advertised_or_declared_unless_the_host_opts_in(EraPreference era, string version)
  {
    var server = NewServer();
    AddProbe(server);
    await using var pair = await Pair.ConnectAsync(server, era, version);

    var result = await pair.Client.CallToolAsync("probe", cancellationToken: TestContext.Current.CancellationToken);

    Assert.Empty(pair.Client.ServerExtensions);
    Assert.False(pair.Client.ServerSupportsExtension(Ext));
    Assert.Equal("False|none", result.Text);
    Assert.Null(server.Capabilities.Extensions);
  }

  [Theory]
  [MemberData(nameof(Revisions))]
  public async Task Each_side_sees_the_others_extension_and_settings(EraPreference era, string version)
  {
    var server = NewServer();
    server.Extensions.Add(new ExtensionRegistration(Ext, Settings("""{"revision":"2","flag":true}""")));
    AddProbe(server);
    await using var pair = await Pair.ConnectAsync(server, era, version, new ClientExtension(Ext, Settings("""{"mode":"fast"}""")));

    var result = await pair.Client.CallToolAsync("probe", cancellationToken: TestContext.Current.CancellationToken);

    Assert.True(pair.Client.ServerSupportsExtension(Ext));
    Assert.Equal("2", pair.Client.ServerExtensions[Ext].GetProperty("revision").GetString());
    Assert.True(pair.Client.ServerExtensions[Ext].GetProperty("flag").GetBoolean());
    Assert.Equal("True|{\"mode\":\"fast\"}", result.Text);
  }

  [Theory]
  [MemberData(nameof(Revisions))]
  public async Task Unknown_extensions_in_the_peers_capabilities_are_kept_and_ignored(EraPreference era, string version)
  {
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("declared", "Lists the declared extensions.", ObjectSchema), (_, ctx) =>
      ValueTask.FromResult(CallToolResult.FromText(string.Join(",", ctx.ClientCapabilities.Extensions!.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Key + "=" + e.Value.GetRawText())))));
    await using var pair = await Pair.ConnectAsync(server, era, version, new ClientExtension("org.other/novel", Settings("""{"x":[1,2],"nested":{"y":null}}""")));

    var result = await pair.Client.CallToolAsync("declared", cancellationToken: TestContext.Current.CancellationToken);

    Assert.Equal("org.other/novel={\"x\":[1,2],\"nested\":{\"y\":null}}", result.Text);
    Assert.Null(pair.Server.Capabilities.Extensions);
  }

  [Theory]
  [MemberData(nameof(Revisions))]
  public async Task An_extension_request_round_trips_for_a_peer_that_declared_it(EraPreference era, string version)
  {
    var server = NewServer();
    server.Extensions.Add(new ExtensionRegistration(Ext)
      .Request("example/echo", (p, ctx) => ValueTask.FromResult(Json.Parse("{\"echo\":" + p.GetProperty("text").GetRawText() + ",\"era\":\"" + ctx.Era + "\"}"))));
    await using var pair = await Pair.ConnectAsync(server, era, version, new ClientExtension(Ext));

    var answer = await pair.Client.CallExtensionAsync("example/echo", Json.Parse("""{"text":"hello"}"""), TestContext.Current.CancellationToken);

    Assert.Equal("hello", answer.GetProperty("echo").GetString());
    Assert.Equal(era == EraPreference.Modern ? "Modern" : "Legacy", answer.GetProperty("era").GetString());
  }

  [Theory]
  [MemberData(nameof(Revisions))]
  public async Task An_extension_request_from_a_peer_without_it_is_method_not_found(EraPreference era, string version)
  {
    var server = NewServer();
    server.Extensions.Add(new ExtensionRegistration(Ext).Request("example/echo", (p, _) => ValueTask.FromResult(p)));
    await using var pair = await Pair.ConnectAsync(server, era, version);

    var error = await Assert.ThrowsAsync<ProtocolException>(async () => await pair.Client.CallExtensionAsync("example/echo", Json.Parse("{}"), TestContext.Current.CancellationToken));

    Assert.Equal(-32601, error.Code);
  }

  [Theory]
  [MemberData(nameof(Revisions))]
  public async Task Notifications_flow_both_ways_to_the_extensions_that_declared_them(EraPreference era, string version)
  {
    var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var toClient = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var server = NewServer();
    server.Extensions.Add(new ExtensionRegistration(Ext)
      .Notification("notifications/example/ping", (p, peer, _) =>
      {
        received.TrySetResult(p.GetProperty("n").GetInt32() + "@" + peer.ProtocolVersion);
        return ValueTask.CompletedTask;
      }));
    server.Tools.Add(new ToolDefinition("push", "Sends the extension's notification.", ObjectSchema), async (_, ctx) =>
    {
      var sent = await ctx.NotifyExtensionAsync(Ext, "notifications/example/pong", Json.Parse("""{"n":7}"""));
      return CallToolResult.FromText(sent.ToString());
    });
    var client = new ClientExtension(Ext).Notification("notifications/example/pong", p => toClient.TrySetResult(p.GetProperty("n").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)));
    await using var pair = await Pair.ConnectAsync(server, era, version, client);

    await pair.Client.SendExtensionNotificationAsync("notifications/example/ping", Json.Parse("""{"n":3}"""), TestContext.Current.CancellationToken);
    var pushed = await pair.Client.CallToolAsync("push", cancellationToken: TestContext.Current.CancellationToken);

    Assert.Equal("3@" + version, await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    Assert.Equal("True", pushed.Text);
    Assert.Equal("7", await toClient.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
  }

  [Theory]
  [MemberData(nameof(Revisions))]
  public async Task A_notification_from_a_peer_without_the_extension_is_dropped_and_not_sent(EraPreference era, string version)
  {
    var handled = 0;
    var server = NewServer();
    server.Extensions.Add(new ExtensionRegistration(Ext).Notification("notifications/example/ping", (_, _, _) =>
    {
      Interlocked.Increment(ref handled);
      return ValueTask.CompletedTask;
    }));
    server.Tools.Add(new ToolDefinition("push", "Sends the extension's notification.", ObjectSchema), async (_, ctx) =>
      CallToolResult.FromText((await ctx.NotifyExtensionAsync(Ext, "notifications/example/pong")).ToString()));
    await using var pair = await Pair.ConnectAsync(server, era, version);

    await pair.Client.SendExtensionNotificationAsync("notifications/example/ping", default, TestContext.Current.CancellationToken);
    var pushed = await pair.Client.CallToolAsync("push", cancellationToken: TestContext.Current.CancellationToken);

    Assert.Equal("False", pushed.Text);
    Assert.Equal(0, Volatile.Read(ref handled));
  }

  [Theory]
  [MemberData(nameof(Revisions))]
  public async Task A_required_extension_rejects_a_peer_without_it_and_admits_one_with_it(EraPreference era, string version)
  {
    var server = NewServer();
    server.Extensions.Add(new ExtensionRegistration(Ext) { Required = true });
    AddProbe(server);

    await using (var without = await Pair.ConnectAsync(server, era, version))
    {
      var error = await Assert.ThrowsAnyAsync<ProtocolException>(async () => await without.Client.CallToolAsync("probe", cancellationToken: TestContext.Current.CancellationToken));

      // 2026-07-28 has -32021; the legacy revisions do not, so the codec says invalid params with the same message.
      Assert.Equal(era == EraPreference.Modern ? -32021 : -32602, error.Code);
      Assert.Contains(Ext, error.Message, StringComparison.Ordinal);
      if (era == EraPreference.Modern)
      {
        var missing = Assert.IsType<MissingCapabilityException>(error);
        Assert.True(missing.Required.Extensions!.ContainsKey(Ext));
      }
    }

    await using var with = await Pair.ConnectAsync(server, era, version, new ClientExtension(Ext));
    Assert.StartsWith("True|", (await with.Client.CallToolAsync("probe", cancellationToken: TestContext.Current.CancellationToken)).Text, StringComparison.Ordinal);
  }

  [Theory]
  [MemberData(nameof(Revisions))]
  public async Task A_client_can_insist_on_a_server_extension(EraPreference era, string version)
  {
    var server = NewServer();
    var (a, b) = PipeEnd.CreatePair();
    var serving = server.RunAsync(ContextTransport.Stream(a), TestContext.Current.CancellationToken);

    var error = await Assert.ThrowsAsync<ExtensionRequiredException>(async () => await ContextClient.ConnectAsync(
      ContextTransport.Stream(b),
      new ContextClientOptions { Era = era, SupportedVersions = [version], EraCache = null, Extensions = [new ClientExtension(Ext) { Required = true }] },
      TestContext.Current.CancellationToken));

    Assert.Equal(Ext, error.ExtensionId);
    await serving.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
  }

  [Fact]
  public async Task An_extension_limited_to_a_revision_is_not_offered_on_the_others()
  {
    var server = NewServer();
    server.Extensions.Add(new ExtensionRegistration(Ext, default, [ProtocolVersions.V20260728]).Request("example/echo", (p, _) => ValueTask.FromResult(p)));

    await using (var modern = await Pair.ConnectAsync(server, EraPreference.Modern, ProtocolVersions.V20260728, new ClientExtension(Ext)))
    {
      Assert.True(modern.Client.ServerSupportsExtension(Ext));
      await modern.Client.CallExtensionAsync("example/echo", Json.Parse("{}"), TestContext.Current.CancellationToken);
    }

    await using var legacy = await Pair.ConnectAsync(server, EraPreference.Legacy, ProtocolVersions.V20251125, new ClientExtension(Ext));
    Assert.False(legacy.Client.ServerSupportsExtension(Ext));
    var error = await Assert.ThrowsAsync<ProtocolException>(async () => await legacy.Client.CallExtensionAsync("example/echo", Json.Parse("{}"), TestContext.Current.CancellationToken));
    Assert.Equal(-32601, error.Code);
  }

  [Fact]
  public async Task Extension_metadata_in_a_request_survives_to_the_handler()
  {
    var server = NewServer();
    server.Extensions.Add(new ExtensionRegistration(Ext).Request("example/echo", (p, _) => ValueTask.FromResult(Json.Parse("{\"seen\":" + p.GetProperty("_meta").GetProperty("com.example/key").GetRawText() + "}"))));
    await using var pair = await Pair.ConnectAsync(server, EraPreference.Modern, ProtocolVersions.V20260728, new ClientExtension(Ext));

    var answer = await pair.Client.CallExtensionAsync("example/echo", Json.Parse("""{"_meta":{"com.example/key":{"a":1}}}"""), TestContext.Current.CancellationToken);

    Assert.Equal(1, answer.GetProperty("seen").GetProperty("a").GetInt32());
  }

  [Fact]
  public async Task Raw_messages_get_the_right_errors_for_a_bad_extension_call()
  {
    var server = NewServer();
    server.Extensions.Add(new ExtensionRegistration(Ext).Request("example/bad", (_, _) => ValueTask.FromResult(Json.Parse("[1,2]"))));
    var session = new ServerSessionState();

    var init = await server.HandleMessageAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{"extensions":{"com.example/echo":{}}},"clientInfo":{"name":"t","version":"1"}}}""", session, cancellationToken: TestContext.Current.CancellationToken);
    var bad = await server.HandleMessageAsync("""{"jsonrpc":"2.0","id":2,"method":"example/bad","params":{}}""", session, cancellationToken: TestContext.Current.CancellationToken);

    Assert.Contains("\"com.example/echo\"", init, StringComparison.Ordinal);
    using var doc = JsonDocument.Parse(bad!);
    Assert.Equal(-32603, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
  }
}
