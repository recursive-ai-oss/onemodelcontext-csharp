using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>The legacy codec (2024-11-05 to 2025-11-25) end to end: our client against our server, and hand-written messages against the server.</summary>
public class LegacyCodecTests
{
  private static readonly JsonElement ObjectSchema = Json.Parse("""{"type":"object"}""");

  public static TheoryData<string> Revisions => new()
  {
    ProtocolVersions.V20241105,
    ProtocolVersions.V20250326,
    ProtocolVersions.V20250618,
    ProtocolVersions.V20251125,
  };

  private sealed class Handler(Func<string, JsonElement, Result> answer) : IClientRequestHandler
  {
    public ValueTask<Result> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken) =>
      ValueTask.FromResult(answer(method, parameters));
  }

  private sealed class Pair : IAsyncDisposable
  {
    private readonly CancellationTokenSource _stop = new();

    public required ContextServer Server { get; init; }

    public required ContextClient Client { get; init; }

    public required Task Serving { get; init; }

    public List<JsonRpcNotification> Notifications { get; } = [];

    public async ValueTask DisposeAsync()
    {
      await Client.DisposeAsync();
      await _stop.CancelAsync();
      try
      {
        await Serving.WaitAsync(TimeSpan.FromSeconds(5));
      }
      catch (OperationCanceledException)
      {
      }
    }
  }

  private static ContextServer NewServer(string version)
  {
    var server = new ContextServer("legacy-test", "1.2", new ContextServerOptions { SupportedVersions = [version] });
    server.Tools.Add(new ToolDefinition("echo", "Echoes.", ObjectSchema), (args, _) => ValueTask.FromResult(CallToolResult.FromText("hi")));
    return server;
  }

  private static async Task<Pair> ConnectAsync(ContextServer server, string version, ClientCapabilities? capabilities = null, IClientRequestHandler? handler = null)
  {
    var (a, b) = PipeEnd.CreatePair();
    var serving = server.RunAsync(ContextTransport.Stream(a), TestContext.Current.CancellationToken);
    var client = await ContextClient.ConnectAsync(
      ContextTransport.Stream(b),
      new ContextClientOptions { Era = EraPreference.Legacy, SupportedVersions = [version], Capabilities = capabilities ?? new ClientCapabilities(), RequestHandler = handler },
      TestContext.Current.CancellationToken);
    var pair = new Pair { Server = server, Client = client, Serving = serving };
    client.NotificationReceived += n =>
    {
      lock (pair.Notifications)
      {
        pair.Notifications.Add(n);
      }
    };
    return pair;
  }

  private static async Task<List<JsonRpcNotification>> WaitForAsync(Pair pair, string method, int count = 1)
  {
    for (var i = 0; i < 400; i++)
    {
      lock (pair.Notifications)
      {
        var found = pair.Notifications.Where(n => n.Method == method).ToList();
        if (found.Count >= count)
        {
          return found;
        }
      }

      await Task.Delay(10, TestContext.Current.CancellationToken);
    }

    throw new TimeoutException("No " + method + " arrived.");
  }

  private static int Count(Pair pair, string method)
  {
    lock (pair.Notifications)
    {
      return pair.Notifications.Count(n => n.Method == method);
    }
  }

  private static async Task<JsonElement> SendAsync(ContextServer server, ServerSessionState session, string json)
  {
    var reply = await server.HandleMessageAsync(json, session, cancellationToken: TestContext.Current.CancellationToken);
    return reply is null ? default : Json.Parse(reply);
  }

  private static string Init(string version, string capabilities = "{}") =>
    "{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"" + version + "\",\"capabilities\":" + capabilities + ",\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}";

  private static async Task<ServerSessionState> InitializedAsync(ContextServer server, string version, string capabilities = "{}")
  {
    var session = new ServerSessionState();
    var reply = await SendAsync(server, session, Init(version, capabilities));
    Assert.Equal(version, reply.GetProperty("result").GetProperty("protocolVersion").GetString());
    await SendAsync(server, session, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
    return session;
  }

  [Theory]
  [MemberData(nameof(Revisions))]
  public async Task Our_client_and_server_negotiate_each_revision(string version)
  {
    var server = NewServer(version);
    server.Instructions = "be nice";
    await using var pair = await ConnectAsync(server, version);

    Assert.Equal(ProtocolEra.Legacy, pair.Client.Era);
    Assert.Equal(version, pair.Client.ProtocolVersion);
    Assert.Equal("legacy-test", pair.Client.ServerInfo.Name);
    Assert.Equal("be nice", pair.Client.Instructions);
    Assert.NotNull(pair.Client.ServerCapabilities.Tools);
    Assert.Equal("hi", (await pair.Client.CallToolAsync("echo", default, TestContext.Current.CancellationToken)).Text);
    Assert.Equal("echo", Assert.Single(await pair.Client.ListToolsAsync(TestContext.Current.CancellationToken)).Name);
  }

  [Fact]
  public async Task The_server_answers_the_clients_version_when_it_supports_it_and_its_newest_otherwise()
  {
    var server = new ContextServer("s", "1");
    var known = await SendAsync(server, new ServerSessionState(), Init(ProtocolVersions.V20250326));
    Assert.Equal(ProtocolVersions.V20250326, known.GetProperty("result").GetProperty("protocolVersion").GetString());

    var unknown = await SendAsync(server, new ServerSessionState(), Init("1999-01-01"));
    Assert.Equal(ProtocolVersions.V20251125, unknown.GetProperty("result").GetProperty("protocolVersion").GetString());

    // A client that asks for a newer revision than any legacy one gets the newest legacy one and decides for itself.
    var modern = await SendAsync(server, new ServerSessionState(), Init(ProtocolVersions.V20260728));
    Assert.Equal(ProtocolVersions.V20251125, modern.GetProperty("result").GetProperty("protocolVersion").GetString());

    var limited = new ContextServer("s", "1", new ContextServerOptions { SupportedVersions = [ProtocolVersions.V20250618, ProtocolVersions.V20241105] });
    var older = await SendAsync(limited, new ServerSessionState(), Init(ProtocolVersions.V20251125));
    Assert.Equal(ProtocolVersions.V20250618, older.GetProperty("result").GetProperty("protocolVersion").GetString());
  }

  [Fact]
  public async Task A_server_with_no_legacy_revision_refuses_initialize_naming_what_it_supports()
  {
    var server = new ContextServer("s", "1", new ContextServerOptions { SupportedVersions = [ProtocolVersions.V20260728] });
    var reply = await SendAsync(server, new ServerSessionState(), Init(ProtocolVersions.V20250618));

    Assert.Equal(-32602, reply.GetProperty("error").GetProperty("code").GetInt32());
    Assert.Equal("2026-07-28", reply.GetProperty("error").GetProperty("data").GetProperty("supported")[0].GetString());
    Assert.Equal("2025-06-18", reply.GetProperty("error").GetProperty("data").GetProperty("requested").GetString());
  }

  [Fact]
  public async Task A_client_refuses_a_revision_it_does_not_speak_and_names_both_sides()
  {
    var server = NewServer(ProtocolVersions.V20241105);
    var (a, b) = PipeEnd.CreatePair();
    _ = server.RunAsync(ContextTransport.Stream(a), TestContext.Current.CancellationToken);

    var ex = await Assert.ThrowsAsync<ProtocolVersionException>(async () => await ContextClient.ConnectAsync(
      ContextTransport.Stream(b),
      new ContextClientOptions { Era = EraPreference.Legacy, SupportedVersions = [ProtocolVersions.V20251125] },
      TestContext.Current.CancellationToken));

    Assert.Contains("2025-11-25", ex.Message, StringComparison.Ordinal);
    Assert.Contains("2024-11-05", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Requests_before_initialize_are_refused_but_ping_is_allowed()
  {
    var server = NewServer(ProtocolVersions.V20251125);
    var session = new ServerSessionState { Era = ProtocolEra.Legacy };

    var tools = await SendAsync(server, session, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
    var ping = await SendAsync(server, session, """{"jsonrpc":"2.0","id":2,"method":"ping"}""");

    Assert.Equal(-32600, tools.GetProperty("error").GetProperty("code").GetInt32());
    Assert.Equal(JsonValueKind.Object, ping.GetProperty("result").ValueKind);
  }

  [Theory]
  [MemberData(nameof(Revisions))]
  public async Task The_client_pings_the_server(string version)
  {
    await using var pair = await ConnectAsync(NewServer(version), version);
    await pair.Client.PingAsync(TestContext.Current.CancellationToken);
  }

  [Fact]
  public async Task The_client_answers_a_server_ping()
  {
    var (a, b) = PipeEnd.CreatePair();
    var lines = new LineReader(a);
    var connecting = ContextClient.ConnectAsync(
      ContextTransport.Stream(b),
      new ContextClientOptions { Era = EraPreference.Legacy, SupportedVersions = [ProtocolVersions.V20250618] },
      TestContext.Current.CancellationToken).AsTask();

    var init = await lines.ReadJsonAsync();
    Assert.Equal("initialize", init.GetProperty("method").GetString());
    var initId = init.GetProperty("id").GetRawText();
    Assert.Equal("2025-06-18", init.GetProperty("params").GetProperty("protocolVersion").GetString());
    await a.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":" + initId + ",\"result\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"serverInfo\":{\"name\":\"hand\",\"version\":\"1\"}}}\n"), TestContext.Current.CancellationToken);
    var initialized = await lines.ReadJsonAsync();
    Assert.Equal("notifications/initialized", initialized.GetProperty("method").GetString());
    await using var client = await connecting;

    await a.WriteAsync(System.Text.Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":"p1","method":"ping"}""" + "\n"), TestContext.Current.CancellationToken);
    var pong = await lines.ReadJsonAsync();

    Assert.Equal("p1", pong.GetProperty("id").GetString());
    Assert.Equal(JsonValueKind.Object, pong.GetProperty("result").ValueKind);
  }

  [Theory]
  [MemberData(nameof(Revisions))]
  public async Task Logging_follows_setLevel(string version)
  {
    var server = NewServer(version);
    server.Tools.Add(new ToolDefinition("noisy", "Logs.", ObjectSchema), async (_, ctx) =>
    {
      await ctx.LogAsync(LoggingLevels.Debug, "d");
      await ctx.LogAsync(LoggingLevels.Warning, "w");
      await ctx.LogAsync(LoggingLevels.Error, "e");
      return CallToolResult.FromText("done");
    });
    await using var pair = await ConnectAsync(server, version);
    Assert.NotEqual(default, pair.Client.ServerCapabilities.Logging);

    await pair.Client.CallToolAsync("noisy", default, TestContext.Current.CancellationToken);
    Assert.Equal(0, Count(pair, "notifications/message"));

    await pair.Client.SetLoggingLevelAsync(LoggingLevels.Warning, TestContext.Current.CancellationToken);
    await pair.Client.CallToolAsync("noisy", default, TestContext.Current.CancellationToken);
    var messages = await WaitForAsync(pair, "notifications/message", 2);
    await Task.Delay(50, TestContext.Current.CancellationToken);

    Assert.Equal(2, Count(pair, "notifications/message"));
    Assert.Equal("warning", messages[0].Params.GetProperty("level").GetString());
    Assert.Equal("error", messages[1].Params.GetProperty("level").GetString());
  }

  [Fact]
  public async Task SetLevel_rejects_an_unknown_level()
  {
    var server = NewServer(ProtocolVersions.V20251125);
    var session = await InitializedAsync(server, ProtocolVersions.V20251125);

    var reply = await SendAsync(server, session, """{"jsonrpc":"2.0","id":1,"method":"logging/setLevel","params":{"level":"loud"}}""");

    Assert.Equal(-32602, reply.GetProperty("error").GetProperty("code").GetInt32());
  }

  [Theory]
  [MemberData(nameof(Revisions))]
  public async Task Subscribed_resources_are_announced_and_unsubscribing_stops_it(string version)
  {
    var server = NewServer(version);
    server.Resources.Add(new ResourceDefinition("mem://a", "a"), (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, Text = "A" }] }));
    await using var pair = await ConnectAsync(server, version);
    Assert.True(pair.Client.ServerCapabilities.Resources?.Subscribe);

    await server.NotifyResourceUpdatedAsync("mem://a", TestContext.Current.CancellationToken);
    await pair.Client.SubscribeResourceAsync("mem://a", TestContext.Current.CancellationToken);
    await server.NotifyResourceUpdatedAsync("mem://other", TestContext.Current.CancellationToken);
    await server.NotifyResourceUpdatedAsync("mem://a", TestContext.Current.CancellationToken);
    var updates = await WaitForAsync(pair, "notifications/resources/updated");
    await Task.Delay(50, TestContext.Current.CancellationToken);

    Assert.Equal("mem://a", Assert.Single(updates).Params.GetProperty("uri").GetString());

    await pair.Client.UnsubscribeResourceAsync("mem://a", TestContext.Current.CancellationToken);
    await server.NotifyResourceUpdatedAsync("mem://a", TestContext.Current.CancellationToken);
    await Task.Delay(50, TestContext.Current.CancellationToken);
    Assert.Equal(1, Count(pair, "notifications/resources/updated"));
  }

  [Fact]
  public async Task Subscriptions_are_bounded()
  {
    var server = new ContextServer("s", "1", new ContextServerOptions { MaxSubscriptionsPerSession = 2 });
    server.Resources.Add(new ResourceDefinition("mem://a", "a"), (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [] }));
    var session = await InitializedAsync(server, ProtocolVersions.V20251125);

    await SendAsync(server, session, """{"jsonrpc":"2.0","id":1,"method":"resources/subscribe","params":{"uri":"a://1"}}""");
    await SendAsync(server, session, """{"jsonrpc":"2.0","id":2,"method":"resources/subscribe","params":{"uri":"a://2"}}""");
    var third = await SendAsync(server, session, """{"jsonrpc":"2.0","id":3,"method":"resources/subscribe","params":{"uri":"a://3"}}""");
    var again = await SendAsync(server, session, """{"jsonrpc":"2.0","id":4,"method":"resources/subscribe","params":{"uri":"a://1"}}""");

    Assert.Equal(-32602, third.GetProperty("error").GetProperty("code").GetInt32());
    Assert.True(again.TryGetProperty("result", out _));
  }

  [Fact]
  public async Task Subscribing_without_resources_is_method_not_found()
  {
    var server = NewServer(ProtocolVersions.V20251125);
    var session = await InitializedAsync(server, ProtocolVersions.V20251125);

    var reply = await SendAsync(server, session, """{"jsonrpc":"2.0","id":1,"method":"resources/subscribe","params":{"uri":"a://1"}}""");

    Assert.Equal(-32601, reply.GetProperty("error").GetProperty("code").GetInt32());
  }

  [Fact]
  public async Task List_changes_are_announced_for_what_the_session_was_told_could_change()
  {
    var server = NewServer(ProtocolVersions.V20250618);
    await using var pair = await ConnectAsync(server, ProtocolVersions.V20250618);

    server.Tools.Add(new ToolDefinition("second", "Two.", ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("2")));
    server.Prompts.Add(new PromptDefinition("p"), (_, _) => ValueTask.FromResult(new GetPromptResult { Messages = [] }));

    await WaitForAsync(pair, "notifications/tools/list_changed");
    await Task.Delay(50, TestContext.Current.CancellationToken);
    // This session was told at initialize that the server had no prompts, so a prompt list change is not announced to it.
    Assert.Equal(0, Count(pair, "notifications/prompts/list_changed"));
  }

  [Fact]
  public async Task Batches_are_accepted_on_2025_03_26_only()
  {
    const string batch = """[{"jsonrpc":"2.0","id":1,"method":"ping"},{"jsonrpc":"2.0","id":2,"method":"tools/list"},{"jsonrpc":"2.0","method":"notifications/initialized"}]""";

    var server = NewServer(ProtocolVersions.V20250326);
    var allowed = await SendAsync(server, await InitializedAsync(server, ProtocolVersions.V20250326), batch);
    Assert.Equal(JsonValueKind.Array, allowed.ValueKind);
    Assert.Equal(2, allowed.GetArrayLength());

    foreach (var version in new[] { ProtocolVersions.V20241105, ProtocolVersions.V20250618, ProtocolVersions.V20251125 })
    {
      var other = NewServer(version);
      var refused = await SendAsync(other, await InitializedAsync(other, version), batch);
      Assert.Equal(-32600, refused.GetProperty("error").GetProperty("code").GetInt32());
    }

    // A batch cannot be the first message: nothing is negotiated yet.
    var fresh = await SendAsync(NewServer(ProtocolVersions.V20250326), new ServerSessionState(), batch);
    Assert.Equal(-32600, fresh.GetProperty("error").GetProperty("code").GetInt32());
  }

  [Theory]
  [InlineData(ProtocolVersions.V20250618)]
  [InlineData(ProtocolVersions.V20251125)]
  public async Task Elicitation_is_a_real_request_the_handler_awaits(string version)
  {
    var server = NewServer(version);
    server.Tools.Add(new ToolDefinition("delete", "Deletes.", ObjectSchema), async (_, ctx) =>
      CallToolResult.FromText(await ctx.ConfirmAsync("Really?") ? "deleted" : "kept"));
    var asked = new List<string>();
    var answer = "accept";
    var handler = new Handler((method, p) =>
    {
      asked.Add(method + ":" + p.GetProperty("message").GetString());
      return new ElicitResult { Action = answer, Content = answer == "accept" ? new Dictionary<string, JsonElement> { ["confirm"] = Json.Parse("true") } : null };
    });
    await using var pair = await ConnectAsync(server, version, new ClientCapabilities { Elicitation = new ElicitationCapability() }, handler);

    var yes = await pair.Client.CallToolAsync("delete", default, TestContext.Current.CancellationToken);
    answer = "decline";
    var no = await pair.Client.CallToolAsync("delete", default, TestContext.Current.CancellationToken);

    Assert.Equal("deleted", yes.Text);
    Assert.Equal("kept", no.Text);
    Assert.Equal(["elicitation/create:Really?", "elicitation/create:Really?"], asked);
  }

  [Fact]
  public async Task Eliciting_from_a_client_that_did_not_declare_it_fails_the_call_with_invalid_params()
  {
    var server = NewServer(ProtocolVersions.V20251125);
    server.Tools.Add(new ToolDefinition("ask", "Asks.", ObjectSchema), async (_, ctx) => CallToolResult.FromText(await ctx.ConfirmAsync("?") ? "y" : "n"));
    await using var pair = await ConnectAsync(server, ProtocolVersions.V20251125);

    var ex = await Assert.ThrowsAsync<ProtocolException>(async () => await pair.Client.CallToolAsync("ask", default, TestContext.Current.CancellationToken));

    Assert.Equal(-32602, ex.Code);
  }

  [Fact]
  public async Task Url_elicitation_carries_an_id_and_the_server_announces_completion()
  {
    var server = NewServer(ProtocolVersions.V20251125);
    var proceed = new TaskCompletionSource();
    server.Tools.Add(new ToolDefinition("link", "Links.", ObjectSchema), async (_, ctx) =>
    {
      var result = await ctx.ElicitUrlAsync("Sign in", "https://example.test/auth", "e-1");
      await proceed.Task.WaitAsync(TimeSpan.FromSeconds(5));
      await ctx.NotifyElicitationCompleteAsync("e-1");
      return CallToolResult.FromText(result.Action);
    });
    JsonElement seen = default;
    var handler = new Handler((_, p) =>
    {
      seen = p.Clone();
      return new ElicitResult { Action = "accept" };
    });
    await using var pair = await ConnectAsync(server, ProtocolVersions.V20251125, new ClientCapabilities { Elicitation = new ElicitationCapability { Url = ContextServer.EmptyObject } }, handler);

    var call = pair.Client.CallToolAsync("link", default, TestContext.Current.CancellationToken).AsTask();
    proceed.SetResult();
    var result = await call;
    var completed = await WaitForAsync(pair, "notifications/elicitation/complete");

    Assert.Equal("accept", result.Text);
    Assert.Equal("url", seen.GetProperty("mode").GetString());
    Assert.Equal("https://example.test/auth", seen.GetProperty("url").GetString());
    Assert.Equal("e-1", seen.GetProperty("elicitationId").GetString());
    Assert.Equal("e-1", Assert.Single(completed).Params.GetProperty("elicitationId").GetString());
  }

  [Fact]
  public async Task Url_elicitation_needs_the_url_capability_and_2025_11_25()
  {
    var withoutUrl = NewServer(ProtocolVersions.V20251125);
    withoutUrl.Tools.Add(new ToolDefinition("link", "Links.", ObjectSchema), async (_, ctx) => CallToolResult.FromText((await ctx.ElicitUrlAsync("m", "https://example.test", "x")).Action));
    await using (var pair = await ConnectAsync(withoutUrl, ProtocolVersions.V20251125, new ClientCapabilities { Elicitation = new ElicitationCapability() }, new Handler((_, _) => new ElicitResult { Action = "accept" })))
    {
      await Assert.ThrowsAsync<ProtocolException>(async () => await pair.Client.CallToolAsync("link", default, TestContext.Current.CancellationToken));
    }

    var older = NewServer(ProtocolVersions.V20250618);
    older.Tools.Add(new ToolDefinition("link", "Links.", ObjectSchema), async (_, ctx) => CallToolResult.FromText((await ctx.ElicitUrlAsync("m", "https://example.test", "x")).Action));
    await using var pair2 = await ConnectAsync(older, ProtocolVersions.V20250618, new ClientCapabilities { Elicitation = new ElicitationCapability { Url = ContextServer.EmptyObject } }, new Handler((_, _) => new ElicitResult { Action = "accept" }));
    await Assert.ThrowsAsync<ProtocolException>(async () => await pair2.Client.CallToolAsync("link", default, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task Sampling_and_roots_are_awaited_requests_gated_on_capabilities()
  {
    var server = NewServer(ProtocolVersions.V20251125);
    server.Tools.Add(new ToolDefinition("think", "Samples.", ObjectSchema), async (_, ctx) =>
    {
      var roots = await ctx.ListRootsAsync();
      var reply = await ctx.SampleAsync(new CreateMessageRequestParams { Messages = [new SamplingMessage { Role = "user", Content = [TextContent.Of("hi")] }], MaxTokens = 10 });
      return CallToolResult.FromText(roots.Roots[0].Uri + "|" + reply.Model);
    });
    var handler = new Handler((method, _) => method switch
    {
      "roots/list" => new ListRootsResult { Roots = [new Root { Uri = "file:///work" }] },
      "sampling/createMessage" => new CreateMessageResult { Role = "assistant", Content = [TextContent.Of("ok")], Model = "m1" },
      _ => throw new ProtocolException(-32601, "no"),
    });
    await using var pair = await ConnectAsync(server, ProtocolVersions.V20251125, new ClientCapabilities { Roots = new RootsCapability(), Sampling = new SamplingCapability() }, handler);
    Assert.Equal("file:///work|m1", (await pair.Client.CallToolAsync("think", default, TestContext.Current.CancellationToken)).Text);

    await using var bare = await ConnectAsync(server, ProtocolVersions.V20251125);
    await Assert.ThrowsAsync<ProtocolException>(async () => await bare.Client.CallToolAsync("think", default, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task Results_carry_only_the_fields_of_the_negotiated_revision()
  {
    static ContextServer Build(string version)
    {
      var server = new ContextServer("s", "1", new ContextServerOptions { SupportedVersions = [version], ListCacheTtl = TimeSpan.FromSeconds(30) });
      server.Tools.Add(
        new ToolDefinition("t", "d", ObjectSchema) { Title = "T", OutputSchema = Json.Parse("""{"type":"object"}"""), Annotations = new ToolAnnotations { ReadOnlyHint = true } },
        (_, _) => ValueTask.FromResult(new CallToolResult { Content = [TextContent.Of("x")], StructuredContent = Json.Parse("""{"a":1}""") }));
      return server;
    }

    foreach (var version in new[] { ProtocolVersions.V20241105, ProtocolVersions.V20250326, ProtocolVersions.V20250618, ProtocolVersions.V20251125 })
    {
      var server = Build(version);
      var session = await InitializedAsync(server, version);
      var rank = Array.IndexOf(new[] { ProtocolVersions.V20241105, ProtocolVersions.V20250326, ProtocolVersions.V20250618, ProtocolVersions.V20251125 }, version);
      var list = (await SendAsync(server, session, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")).GetProperty("result");
      var call = (await SendAsync(server, session, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"t"}}""")).GetProperty("result");
      var tool = list.GetProperty("tools")[0];

      Assert.False(list.TryGetProperty("ttlMs", out _), version);
      Assert.False(list.TryGetProperty("cacheScope", out _), version);
      Assert.False(call.TryGetProperty("resultType", out _), version);
      Assert.False(list.TryGetProperty("_meta", out _), version);
      Assert.Equal(rank >= 1, tool.TryGetProperty("annotations", out _));
      Assert.Equal(rank >= 2, tool.TryGetProperty("outputSchema", out _));
      Assert.Equal(rank >= 2, tool.TryGetProperty("title", out _));
      Assert.Equal(rank >= 2, call.TryGetProperty("structuredContent", out _));
    }
  }

  [Fact]
  public async Task Unknown_fields_and_meta_survive_the_trip()
  {
    var server = NewServer(ProtocolVersions.V20250618);
    JsonElement seenMeta = default;
    server.Tools.Add(new ToolDefinition("m", "d", ObjectSchema), async (_, ctx) =>
    {
      seenMeta = ctx.Peer.Meta!.AdditionalProperties!["vendor/key"].Clone();
      await ctx.ReportProgressAsync(1, 2, "half");
      return new CallToolResult { Content = [TextContent.Of("x")], AdditionalProperties = new Dictionary<string, JsonElement> { ["vendorField"] = Json.Parse("7") }, Meta = new MetaObject { AdditionalProperties = new Dictionary<string, JsonElement> { ["vendor/out"] = Json.Parse("true") } } };
    });
    var session = await InitializedAsync(server, ProtocolVersions.V20250618);
    var notes = new RecordingSink();

    var reply = await server.HandleMessageAsync(
      """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"m","_meta":{"progressToken":"pt","vendor/key":{"k":1}}}}""",
      session,
      notes,
      cancellationToken: TestContext.Current.CancellationToken);

    var result = Json.Parse(reply!).GetProperty("result");
    Assert.Equal(1, seenMeta.GetProperty("k").GetInt32());
    Assert.Equal(7, result.GetProperty("vendorField").GetInt32());
    Assert.True(result.GetProperty("_meta").GetProperty("vendor/out").GetBoolean());
    Assert.Equal("pt", Assert.Single(notes.Notifications).Params.GetProperty("progressToken").GetString());
  }

  [Fact]
  public async Task Bad_input_never_throws_and_gets_the_seed_codes()
  {
    var server = NewServer(ProtocolVersions.V20251125);
    server.Resources.Add(new ResourceDefinition("mem://a", "a"), (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [] }));
    var session = await InitializedAsync(server, ProtocolVersions.V20251125);

    async Task<int> CodeAsync(string message)
    {
      var reply = await SendAsync(server, session, message);
      return reply.GetProperty("error").GetProperty("code").GetInt32();
    }

    Assert.Equal(-32700, await CodeAsync("not json"));
    Assert.Equal(-32700, await CodeAsync("{\"jsonrpc\":"));
    Assert.Equal(-32600, await CodeAsync("42"));
    Assert.Equal(-32600, await CodeAsync("\"text\""));
    Assert.Equal(-32600, await CodeAsync("""{"jsonrpc":"2.0","id":1}"""));
    Assert.Equal(-32600, await CodeAsync("""{"jsonrpc":"2.0","id":1,"method":7}"""));
    Assert.Equal(-32600, await CodeAsync("[]"));
    Assert.Equal(-32601, await CodeAsync("""{"jsonrpc":"2.0","id":1,"method":"nope/nothing"}"""));
    Assert.Equal(-32601, await CodeAsync("""{"jsonrpc":"2.0","id":1,"method":"server/discover"}"""));
    Assert.Equal(-32602, await CodeAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}"""));
    Assert.Equal(-32602, await CodeAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"missing"}}"""));
    Assert.Equal(-32602, await CodeAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":5}}"""));
    Assert.Equal(-32602, await CodeAsync("""{"jsonrpc":"2.0","id":1,"method":"resources/subscribe","params":{"uri":5}}"""));
    Assert.Equal(-32602, await CodeAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","_meta":{"progressToken":true}}}"""));
    Assert.Equal(-32602, await CodeAsync(Init("x").Replace("\"protocolVersion\":\"x\",", string.Empty, StringComparison.Ordinal)));

    var huge = new ContextServer("s", "1", new ContextServerOptions { ReaderOptions = new JsonRpcReaderOptions { MaxMessageBytes = 64 } });
    var tooBig = await SendAsync(huge, new ServerSessionState(), """{"jsonrpc":"2.0","id":1,"method":"ping","params":{"pad":"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"}}""");
    Assert.Equal(-32600, tooBig.GetProperty("error").GetProperty("code").GetInt32());

    var deep = new string('[', 200) + new string(']', 200);
    Assert.Equal(-32700, await CodeAsync(deep));
  }

  [Fact]
  public async Task Responses_from_the_peer_and_notifications_get_no_reply()
  {
    var server = NewServer(ProtocolVersions.V20251125);
    var session = await InitializedAsync(server, ProtocolVersions.V20251125);

    Assert.Equal(default, await SendAsync(server, session, """{"jsonrpc":"2.0","id":99,"result":{}}"""));
    Assert.Equal(default, await SendAsync(server, session, """{"jsonrpc":"2.0","method":"notifications/roots/list_changed"}"""));
    Assert.Equal(default, await SendAsync(server, session, """{"jsonrpc":"2.0","method":"notifications/unknown"}"""));
  }

  [Fact]
  public async Task Initializing_again_starts_the_session_over()
  {
    var server = new ContextServer("s", "1");
    var session = await InitializedAsync(server, ProtocolVersions.V20251125);
    await SendAsync(server, session, """{"jsonrpc":"2.0","id":1,"method":"logging/setLevel","params":{"level":"error"}}""");

    await SendAsync(server, session, Init(ProtocolVersions.V20250326));

    Assert.Equal(ProtocolVersions.V20250326, session.NegotiatedVersion);
    Assert.Null(session.LogLevel);
    Assert.False(session.Initialized);
  }
}
