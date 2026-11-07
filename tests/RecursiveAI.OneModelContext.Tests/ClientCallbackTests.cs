using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Protocol;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

// The typed client callbacks, the cache flag and the message-level session, on the real codecs of both eras.
public class ClientCallbackTests
{
  private static readonly JsonElement ObjectSchema = Json.Parse("""{"type":"object"}""");

  public static IEnumerable<object[]> Eras() => [[EraPreference.Modern], [EraPreference.Legacy]];

  private static async Task<(ContextClient Client, Func<Task> Stop)> ConnectAsync(ContextServer server, ContextClientOptions options)
  {
    var (a, b) = PipeEnd.CreatePair();
    var serving = server.RunAsync(ContextTransport.Stream(a), TestContext.Current.CancellationToken);
    var client = await ContextClient.ConnectAsync(ContextTransport.Stream(b), options, TestContext.Current.CancellationToken);
    return (client, async () =>
    {
      await client.DisposeAsync();
      await serving.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    });
  }

  private static ContextServer ServerWithAsks()
  {
    var server = new ContextServer("asks", "1");
    server.Tools.Add(new ToolDefinition("confirm", null, ObjectSchema), async (_, ctx) => CallToolResult.FromText(await ctx.ConfirmAsync("Proceed?") ? "yes" : "no"));
    server.Tools.Add(new ToolDefinition("form", null, ObjectSchema), async (_, ctx) =>
    {
      var answer = await ctx.ElicitAsync("Name?", Json.Parse("""{"type":"object","properties":{"name":{"type":"string"}}}"""));
      return CallToolResult.FromText(answer.Action + ":" + (answer.Content is { } c && c.TryGetValue("name", out var n) ? n.GetString() : ""));
    });
    server.Tools.Add(new ToolDefinition("roots", null, ObjectSchema), async (_, ctx) =>
      CallToolResult.FromText(string.Join(',', (await ctx.ListRootsAsync()).Roots.Select(r => r.Uri))));
    server.Tools.Add(new ToolDefinition("sample", null, ObjectSchema), async (_, ctx) =>
    {
      var answer = await ctx.SampleAsync(new CreateMessageRequestParams { Messages = [new SamplingMessage { Role = "user", Content = [TextContent.Of("hi")] }], MaxTokens = 10 });
      return CallToolResult.FromText(((TextContent)answer.Content[0]).Text);
    });
    return server;
  }

  [Theory]
  [MemberData(nameof(Eras))]
  public async Task OnConfirm_answers_yes_no_questions_and_declares_the_capability(EraPreference era)
  {
    var asked = new List<string>();
    var answer = true;
    var (client, stop) = await ConnectAsync(ServerWithAsks(), new ContextClientOptions
    {
      Era = era,
      OnConfirm = (message, _) =>
      {
        asked.Add(message);
        return ValueTask.FromResult(answer);
      },
    });

    Assert.Equal("yes", (await client.CallToolAsync("confirm", cancellationToken: TestContext.Current.CancellationToken)).Text);
    answer = false;
    Assert.Equal("no", (await client.CallToolAsync("confirm", cancellationToken: TestContext.Current.CancellationToken)).Text);
    Assert.Equal(["Proceed?", "Proceed?"], asked);

    // Only OnConfirm is set, so a general form is declined rather than failing the call.
    Assert.Equal("decline:", (await client.CallToolAsync("form", cancellationToken: TestContext.Current.CancellationToken)).Text);
    await stop();
  }

  [Theory]
  [MemberData(nameof(Eras))]
  public async Task OnElicit_receives_forms_and_confirmations(EraPreference era)
  {
    var seen = new List<bool>();
    var (client, stop) = await ConnectAsync(ServerWithAsks(), new ContextClientOptions
    {
      Era = era,
      OnElicit = (request, _) =>
      {
        seen.Add(request.IsConfirmation);
        return ValueTask.FromResult(request.IsConfirmation
          ? ElicitResult.Confirmation(true)
          : ElicitResult.Accept(new Dictionary<string, JsonElement> { ["name"] = Json.Parse("\"Ada\"") }));
      },
    });

    Assert.Equal("accept:Ada", (await client.CallToolAsync("form", cancellationToken: TestContext.Current.CancellationToken)).Text);
    Assert.Equal("yes", (await client.CallToolAsync("confirm", cancellationToken: TestContext.Current.CancellationToken)).Text);
    Assert.Equal([false, true], seen);
    await stop();
  }

  [Theory]
  [MemberData(nameof(Eras))]
  public async Task OnSample_and_OnListRoots_answer_the_server(EraPreference era)
  {
    var (client, stop) = await ConnectAsync(ServerWithAsks(), new ContextClientOptions
    {
      Era = era,
      OnSample = (_, _) => ValueTask.FromResult(new CreateMessageResult { Role = "assistant", Model = "m", Content = [TextContent.Of("hello back")] }),
      OnListRoots = _ => ValueTask.FromResult(new ListRootsResult { Roots = [new Root { Uri = "file:///work" }] }),
    });

    Assert.Equal("hello back", (await client.CallToolAsync("sample", cancellationToken: TestContext.Current.CancellationToken)).Text);
    Assert.Equal("file:///work", (await client.CallToolAsync("roots", cancellationToken: TestContext.Current.CancellationToken)).Text);
    await stop();
  }

  [Fact]
  public async Task A_client_without_callbacks_is_not_asked()
  {
    var (client, stop) = await ConnectAsync(ServerWithAsks(), new ContextClientOptions { Era = EraPreference.Legacy });
    await Assert.ThrowsAnyAsync<ContextException>(async () => await client.CallToolAsync("confirm", cancellationToken: TestContext.Current.CancellationToken));
    await stop();
  }

  [Fact]
  public void IsConfirmation_is_true_only_for_the_confirm_form()
  {
    Assert.True(new ElicitRequestParams { Message = "m", RequestedSchema = Json.Parse("""{"type":"object","properties":{"confirm":{"type":"boolean"}}}""") }.IsConfirmation);
    Assert.False(new ElicitRequestParams { Message = "m", RequestedSchema = Json.Parse("""{"type":"object","properties":{"confirm":{"type":"string"}}}""") }.IsConfirmation);
    Assert.False(new ElicitRequestParams { Message = "m", RequestedSchema = Json.Parse("""{"type":"object","properties":{"confirm":{"type":"boolean"},"x":{"type":"string"}}}""") }.IsConfirmation);
    Assert.False(new ElicitRequestParams { Message = "m", Mode = "url", Url = "https://example.com" }.IsConfirmation);
  }

  [Fact]
  public async Task Lists_say_whether_they_came_from_the_cache()
  {
    var server = new ContextServer("c", "1", new ContextServerOptions { ListCacheTtl = TimeSpan.FromMinutes(5) });
    server.Tools.Add(new ToolDefinition("t", null, ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("")));
    var (client, stop) = await ConnectAsync(server, new ContextClientOptions { Era = EraPreference.Modern });

    var first = await client.ListToolsPageAsync(cancellationToken: TestContext.Current.CancellationToken);
    var second = await client.ListToolsPageAsync(cancellationToken: TestContext.Current.CancellationToken);
    Assert.False(first.FromCache);
    Assert.True(second.FromCache);
    Assert.False(first.FromCache);
    client.InvalidateCache();
    Assert.False((await client.ListToolsPageAsync(cancellationToken: TestContext.Current.CancellationToken)).FromCache);
    await stop();
  }

  [Theory]
  [MemberData(nameof(Eras))]
  public async Task The_happy_path_connect_takes_the_callbacks(EraPreference era)
  {
    var server = ServerWithAsks();
    var (a, b) = PipeEnd.CreatePair();
    var serving = server.RunAsync(ContextTransport.Stream(a), TestContext.Current.CancellationToken);
    await using (var client = await ModelContext.ConnectAsync(
      ContextTransport.Stream(b),
      era,
      onConfirm: (_, _) => ValueTask.FromResult(true),
      onElicit: (_, _) => ValueTask.FromResult(ElicitResult.Accept(new Dictionary<string, JsonElement> { ["name"] = Json.Parse("\"Ada\"") })),
      onSample: (_, _) => ValueTask.FromResult(new CreateMessageResult { Role = "assistant", Model = "m", Content = [TextContent.Of("sampled")] }),
      onListRoots: _ => ValueTask.FromResult(new ListRootsResult { Roots = [new Root { Uri = "file:///r" }] }),
      cancellationToken: TestContext.Current.CancellationToken))
    {
      Assert.Equal("yes", (await client.CallToolAsync("confirm", cancellationToken: TestContext.Current.CancellationToken)).Text);
      Assert.Equal("accept:Ada", (await client.CallToolAsync("form", cancellationToken: TestContext.Current.CancellationToken)).Text);
      Assert.Equal("sampled", (await client.CallToolAsync("sample", cancellationToken: TestContext.Current.CancellationToken)).Text);
      Assert.Equal("file:///r", (await client.CallToolAsync("roots", cancellationToken: TestContext.Current.CancellationToken)).Text);
    }

    await serving.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
  }

  [Fact]
  public async Task Full_listings_say_whether_they_came_from_the_cache()
  {
    var server = new ContextServer("c", "1", new ContextServerOptions { ListCacheTtl = TimeSpan.FromMinutes(5) });
    server.Tools.Add(new ToolDefinition("t", null, ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("")));
    server.Resources.Add(new ResourceDefinition("mem://a", "a"), (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, Text = "A" }] }));
    server.ResourceTemplates.Add(new ResourceTemplateDefinition("mem://{x}", "x"), (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, Text = "X" }] }));
    server.Prompts.Add(new PromptDefinition("p"), (_, _) => ValueTask.FromResult(new GetPromptResult { Messages = [new PromptMessage { Role = "user", Content = TextContent.Of("hi") }] }));
    var (client, stop) = await ConnectAsync(server, new ContextClientOptions { Era = EraPreference.Modern });
    var token = TestContext.Current.CancellationToken;

    Assert.False((await client.ListToolsAsync(token)).FromCache);
    var tools = await client.ListToolsAsync(token);
    Assert.True(tools.FromCache);
    Assert.Equal("t", Assert.Single(tools).Name);
    Assert.False((await client.ListResourcesAsync(token)).FromCache);
    Assert.True((await client.ListResourcesAsync(token)).FromCache);
    Assert.False((await client.ListResourceTemplatesAsync(token)).FromCache);
    Assert.True((await client.ListResourceTemplatesAsync(token)).FromCache);
    Assert.False((await client.ListPromptsAsync(token)).FromCache);
    Assert.True((await client.ListPromptsAsync(token)).FromCache);
    await stop();
  }

  [Fact]
  public async Task A_session_handles_whole_messages_on_either_era()
  {
    var server = new ContextServer("s", "1");
    server.Tools.Add(new ToolDefinition("t", null, ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("ok")));

    var legacy = server.OpenSession();
    Assert.Null(legacy.Era);
    Assert.Contains("\"protocolVersion\":\"2025-06-18\"", await legacy.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"p","version":"1"}}}""", TestContext.Current.CancellationToken), StringComparison.Ordinal);
    Assert.Equal(ProtocolEra.Legacy, legacy.Era);
    Assert.Equal("2025-06-18", legacy.NegotiatedVersion);
    Assert.Contains("ok", await legacy.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"t"}}""", TestContext.Current.CancellationToken), StringComparison.Ordinal);

    var modern = server.OpenSession();
    const string meta = "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientInfo\":{\"name\":\"p\",\"version\":\"1\"},\"io.modelcontextprotocol/clientCapabilities\":{}}";
    Assert.Contains("ok", await modern.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"t\"," + meta + "}}", TestContext.Current.CancellationToken), StringComparison.Ordinal);
    Assert.Null(modern.Era);
  }

  [Fact]
  public void The_version_error_says_what_to_do_when_the_peer_named_no_versions()
  {
    var error = new ProtocolVersionException(["2026-07-28", "2025-11-25"], []);
    Assert.Contains("named no supported versions", error.Message, StringComparison.Ordinal);
    Assert.Contains("EraPreference.Legacy", error.Message, StringComparison.Ordinal);
    Assert.DoesNotContain("EraPreference", new ProtocolVersionException(["2026-07-28"], ["2025-11-25"]).Message, StringComparison.Ordinal);
  }
}
