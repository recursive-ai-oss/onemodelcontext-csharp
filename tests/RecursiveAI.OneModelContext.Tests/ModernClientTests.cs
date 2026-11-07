using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>Answers input requests from a table of canned replies and records what it was asked.</summary>
internal sealed class ScriptedClientHandler : IClientRequestHandler
{
  public List<(string Method, JsonElement Params)> Asked { get; } = [];

  public Func<string, JsonElement, Result> Answer { get; set; } = (_, _) => new ElicitResult { Action = "accept", Content = new() { ["confirm"] = Json.Parse("true") } };

  public ValueTask<Result> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
  {
    lock (Asked)
    {
      Asked.Add((method, parameters.Clone()));
    }

    return ValueTask.FromResult(Answer(method, parameters));
  }
}

public class ModernClientTests
{
  private static readonly JsonElement ObjectSchema = Json.Parse("""{"type":"object"}""");

  private sealed class Pair : IAsyncDisposable
  {
    public required ContextServer Server { get; init; }

    public required ContextClient Client { get; init; }

    public required Task Serving { get; init; }

    public async ValueTask DisposeAsync()
    {
      await Client.DisposeAsync();
      await Serving.WaitAsync(TimeSpan.FromSeconds(10));
    }
  }

  private static async Task<Pair> ConnectAsync(ContextServer server, ContextClientOptions? options = null)
  {
    var (a, b) = PipeEnd.CreatePair();
    var serving = server.RunAsync(ContextTransport.Stream(a), TestContext.Current.CancellationToken);
    var client = await ContextClient.ConnectAsync(ContextTransport.Stream(b), options, TestContext.Current.CancellationToken);
    return new Pair { Server = server, Client = client, Serving = serving };
  }

  private static ClientCapabilities Elicit => new() { Elicitation = new ElicitationCapability() };

  [Fact]
  public async Task The_client_negotiates_modern_and_uses_every_feature()
  {
    var server = new ContextServer("srv", "9.9");
    server.Instructions = "Use me.";
    server.Tools.Add(new ToolDefinition("add", "Adds.", ObjectSchema), (args, _) => ValueTask.FromResult(CallToolResult.FromText((args.GetProperty("a").GetInt32() + args.GetProperty("b").GetInt32()).ToString(System.Globalization.CultureInfo.InvariantCulture))));
    server.Resources.Add(new ResourceDefinition("mem://a", "a"), (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, Text = "A" }] }));
    server.ResourceTemplates.Add(new ResourceTemplateDefinition("mem://t/{id}", "t"), (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, Text = "T" }] }));
    server.Prompts.Add(new PromptDefinition("p", "a prompt"), (_, _) => ValueTask.FromResult(new GetPromptResult { Messages = [new PromptMessage { Role = "user", Content = TextContent.Of("hi") }] }));
    server.Completions = (request, _) => ValueTask.FromResult(new CompleteResult { Completion = new CompletionValues { Values = [request.Argument.Value + "1", request.Argument.Value + "2"] } });
    await using var pair = await ConnectAsync(server);
    var client = pair.Client;

    var tools = await client.ListToolsAsync(TestContext.Current.CancellationToken);
    var sum = await client.CallToolAsync("add", Json.Parse("""{"a":2,"b":3}"""), TestContext.Current.CancellationToken);
    var resources = await client.ListResourcesAsync(TestContext.Current.CancellationToken);
    var templates = await client.ListResourceTemplatesAsync(TestContext.Current.CancellationToken);
    var read = await client.ReadResourceAsync("mem://t/7", TestContext.Current.CancellationToken);
    var prompts = await client.ListPromptsAsync(TestContext.Current.CancellationToken);
    var prompt = await client.GetPromptAsync("p", null, TestContext.Current.CancellationToken);
    var completion = await client.CompleteAsync(
      new CompleteRequestParams { Ref = new CompletionReference { Type = "ref/prompt", Name = "p" }, Argument = new CompleteArgument { Name = "x", Value = "v" } },
      TestContext.Current.CancellationToken);

    Assert.Equal(ProtocolEra.Modern, client.Era);
    Assert.Equal("2026-07-28", client.ProtocolVersion);
    Assert.Equal("srv", client.ServerInfo.Name);
    Assert.Equal("Use me.", client.Instructions);
    Assert.NotNull(client.ServerCapabilities.Tools);
    Assert.Equal("add", Assert.Single(tools).Name);
    Assert.Equal("5", sum.Text);
    Assert.Equal("mem://a", Assert.Single(resources).Uri);
    Assert.Equal("mem://t/{id}", Assert.Single(templates).UriTemplate);
    Assert.Equal("T", Assert.IsType<TextResourceContents>(read.Contents[0]).Text);
    Assert.Equal("p", Assert.Single(prompts).Name);
    Assert.Equal("hi", Assert.IsType<TextContent>(prompt.Messages[0].Content).Text);
    Assert.Equal(["v1", "v2"], completion.Completion.Values);
  }

  [Fact]
  public async Task An_elicitation_round_trip_re_runs_the_tool_with_the_answer()
  {
    var runs = 0;
    var server = new ContextServer("srv", "1");
    server.Tools.Add(new ToolDefinition("delete", "d", ObjectSchema), async (_, ctx) =>
    {
      runs++;
      return CallToolResult.FromText(await ctx.ConfirmAsync("Delete?") ? "Deleted." : "Cancelled.");
    });
    var handler = new ScriptedClientHandler();
    await using var pair = await ConnectAsync(server, new ContextClientOptions { Capabilities = Elicit, RequestHandler = handler });

    var yes = await pair.Client.CallToolAsync("delete", cancellationToken: TestContext.Current.CancellationToken);
    handler.Answer = (_, _) => new ElicitResult { Action = "decline" };
    var no = await pair.Client.CallToolAsync("delete", cancellationToken: TestContext.Current.CancellationToken);

    Assert.Equal("Deleted.", yes.Text);
    Assert.Equal("Cancelled.", no.Text);
    Assert.Equal(4, runs);
    Assert.Equal(2, handler.Asked.Count);
    Assert.Equal("elicitation/create", handler.Asked[0].Method);
    Assert.Equal("Delete?", handler.Asked[0].Params.GetProperty("message").GetString());
  }

  [Fact]
  public async Task Sampling_and_roots_are_answered_by_the_handler_over_several_rounds()
  {
    var server = new ContextServer("srv", "1");
    server.Tools.Add(new ToolDefinition("t", "d", ObjectSchema), async (_, ctx) =>
    {
      var roots = await ctx.ListRootsAsync();
      var answer = await ctx.SampleAsync(new CreateMessageRequestParams { Messages = [], MaxTokens = 3 });
      return CallToolResult.FromText(roots.Roots[0].Uri + " " + answer.Model);
    });
    var handler = new ScriptedClientHandler
    {
      Answer = (method, _) => method == Methods.RootsList
        ? new ListRootsResult { Roots = [new Root { Uri = "file:///w" }] }
        : new CreateMessageResult { Role = "assistant", Content = [TextContent.Of("ok")], Model = "m" },
    };
    await using var pair = await ConnectAsync(server, new ContextClientOptions { Capabilities = new ClientCapabilities { Roots = new RootsCapability(), Sampling = new SamplingCapability() }, RequestHandler = handler });

    var result = await pair.Client.CallToolAsync("t", cancellationToken: TestContext.Current.CancellationToken);

    Assert.Equal("file:///w m", result.Text);
    Assert.Equal([Methods.RootsList, Methods.SamplingCreateMessage], handler.Asked.Select(a => a.Method).ToArray());
  }

  [Fact]
  public async Task Without_a_handler_the_call_fails_with_a_clear_message()
  {
    var server = new ContextServer("srv", "1");
    server.Tools.Add(new ToolDefinition("delete", "d", ObjectSchema), async (_, ctx) => CallToolResult.FromText(await ctx.ConfirmAsync("Delete?") ? "y" : "n"));
    await using var pair = await ConnectAsync(server, new ContextClientOptions { Capabilities = Elicit });

    var ex = await Assert.ThrowsAsync<ContextException>(async () => await pair.Client.CallToolAsync("delete", cancellationToken: TestContext.Current.CancellationToken));

    Assert.Contains("RequestHandler", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task A_client_that_cannot_elicit_gets_the_missing_capability_error()
  {
    var server = new ContextServer("srv", "1");
    server.Tools.Add(new ToolDefinition("delete", "d", ObjectSchema), async (_, ctx) => CallToolResult.FromText(await ctx.ConfirmAsync("Delete?") ? "y" : "n"));
    await using var pair = await ConnectAsync(server);

    var ex = await Assert.ThrowsAsync<MissingCapabilityException>(async () => await pair.Client.CallToolAsync("delete", cancellationToken: TestContext.Current.CancellationToken));

    Assert.NotNull(ex.Required.Elicitation);
  }

  [Fact]
  public async Task A_server_that_never_stops_asking_ends_at_the_round_limit()
  {
    var server = new ContextServer("srv", "1");
    server.Tools.Add(new ToolDefinition("loop", "d", ObjectSchema), async (_, ctx) =>
    {
      // Always a new question: the answer to the last one is never enough.
      for (var i = 0; ; i++)
      {
        if (!await ctx.ConfirmAsync("again " + i))
        {
          return CallToolResult.FromText("stopped");
        }
      }
    });
    var handler = new ScriptedClientHandler();
    await using var pair = await ConnectAsync(server, new ContextClientOptions { Capabilities = Elicit, RequestHandler = handler, MaxInputRounds = 3 });

    var ex = await Assert.ThrowsAsync<ContextException>(async () => await pair.Client.CallToolAsync("loop", cancellationToken: TestContext.Current.CancellationToken));

    Assert.Contains("more than 3 times", ex.Message, StringComparison.Ordinal);
    Assert.Equal(3, handler.Asked.Count);
  }

  [Fact]
  public async Task Progress_reaches_the_callers_progress_object_in_order()
  {
    var server = new ContextServer("srv", "1");
    server.Tools.Add(new ToolDefinition("work", "d", ObjectSchema), async (_, ctx) =>
    {
      for (var i = 1; i <= 3; i++)
      {
        await ctx.ReportProgressAsync(i, 3, "step " + i);
      }

      return CallToolResult.FromText("done");
    });
    await using var pair = await ConnectAsync(server);
    var seen = new List<ProgressNotificationParams>();
    var done = new TaskCompletionSource();

    var result = await pair.Client.CallToolAsync("work", default, new SyncProgress(p =>
    {
      lock (seen)
      {
        seen.Add(p);
        if (seen.Count == 3)
        {
          done.TrySetResult();
        }
      }
    }), TestContext.Current.CancellationToken);
    await done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    Assert.Equal("done", result.Text);
    Assert.Equal([1d, 2d, 3d], seen.Select(p => p.Progress).ToArray());
    Assert.Equal("step 2", seen[1].Message);
  }

  private sealed class SyncProgress(Action<ProgressNotificationParams> report) : IProgress<ProgressNotificationParams>
  {
    public void Report(ProgressNotificationParams value) => report(value);
  }

  [Fact]
  public async Task Cancelling_a_call_cancels_the_handler()
  {
    var started = new TaskCompletionSource();
    var handlerSawCancel = new TaskCompletionSource();
    var audited = new List<AuditRecord>();
    var server = new ContextServer("srv", "1", new ContextServerOptions { Audit = a => { lock (audited) { audited.Add(a); } } });
    server.Tools.Add(new ToolDefinition("hang", "d", ObjectSchema), async (_, ctx) =>
    {
      started.TrySetResult();
      try
      {
        await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
      }
      catch (OperationCanceledException)
      {
        handlerSawCancel.TrySetResult();
        throw;
      }

      return CallToolResult.FromText("never");
    });
    await using var pair = await ConnectAsync(server);
    using var cts = new CancellationTokenSource();

    var call = pair.Client.CallToolAsync("hang", cancellationToken: cts.Token).AsTask();
    await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    await cts.CancelAsync();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);
    await handlerSawCancel.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    // The connection is still good afterwards.
    Assert.NotNull(await pair.Client.ListToolsAsync(TestContext.Current.CancellationToken));
    await WaitForAsync(() => { lock (audited) { return audited.Any(a => a.Outcome == RequestOutcome.Cancelled); } });
  }

  [Fact]
  public async Task Unknown_fields_and_meta_from_the_server_survive_into_the_result()
  {
    var server = new ContextServer("srv", "1");
    server.Tools.Add(new ToolDefinition("t", "d", ObjectSchema), (_, _) => ValueTask.FromResult(new CallToolResult
    {
      Content = [TextContent.Of("x")],
      Meta = new MetaObject { AdditionalProperties = new() { ["vendor/note"] = Json.Parse("\"kept\"") } },
      AdditionalProperties = new() { ["futureField"] = Json.Parse("[1,2]") },
    }));
    await using var pair = await ConnectAsync(server);

    var result = await pair.Client.CallToolAsync("t", cancellationToken: TestContext.Current.CancellationToken);

    Assert.Equal("kept", result.Meta!.AdditionalProperties!["vendor/note"].GetString());
    Assert.Equal(2, result.AdditionalProperties!["futureField"].GetArrayLength());
    Assert.Equal("srv", result.Meta.ServerInfo!.Name);
    Assert.Equal("complete", result.ResultType);
  }

  [Fact]
  public async Task Malformed_input_from_the_client_never_breaks_the_connection()
  {
    var server = new ContextServer("srv", "1");
    server.Tools.Add(new ToolDefinition("t", "d", ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("ok")));
    var (a, b) = PipeEnd.CreatePair();
    var serving = server.RunAsync(ContextTransport.Stream(a), TestContext.Current.CancellationToken);
    var lines = new LineReader(b);
    var meta = """{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}""";
    string[] bad =
    [
      "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"_meta\":" + meta + ",\"name\":5}}",
      "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"_meta\":" + meta + ",\"name\":\"t\",\"arguments\":[1]}}",
      "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/list\",\"params\":{\"_meta\":" + meta + ",\"cursor\":{\"x\":1}}}",
      "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/list\",\"params\":{\"_meta\":" + meta + ",\"cursor\":\"!!not base64!!\"}}",
      "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"resources/read\",\"params\":{\"_meta\":" + meta + "}}",
      "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"completion/complete\",\"params\":{\"_meta\":" + meta + "}}",
      "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/list\",\"params\":\"nope\"}",
    ];

    foreach (var line in bad)
    {
      await b.WriteAsync(System.Text.Encoding.UTF8.GetBytes(line + "\n"), TestContext.Current.CancellationToken);
      var reply = await lines.ReadJsonAsync();
      Assert.True(reply.TryGetProperty("error", out var error), reply.GetRawText());
      Assert.Contains(error.GetProperty("code").GetInt32(), new[] { -32602, -32601 });
    }

    // Still alive: a good request works.
    await b.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/list\",\"params\":{\"_meta\":" + meta + "}}\n"), TestContext.Current.CancellationToken);
    Assert.True((await lines.ReadJsonAsync()).TryGetProperty("result", out _));
    b.Dispose();
    await serving.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
  }

  [Fact]
  public async Task The_callers_trace_context_travels_in_meta()
  {
    string? seenParent = null;
    var server = new ContextServer("srv", "1");
    server.Tools.Add(new ToolDefinition("t", "d", ObjectSchema), (_, ctx) =>
    {
      seenParent = ctx.Peer.Meta?.TraceParent;
      return ValueTask.FromResult(CallToolResult.FromText("ok"));
    });
    await using var pair = await ConnectAsync(server);
    using var source = new ActivitySource("test.caller");
    using var listener = new ActivityListener
    {
      ShouldListenTo = _ => true,
      Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    };
    ActivitySource.AddActivityListener(listener);
    using var activity = source.StartActivity("caller")!;

    await pair.Client.CallToolAsync("t", cancellationToken: TestContext.Current.CancellationToken);

    Assert.NotNull(seenParent);
    Assert.Contains(activity.TraceId.ToHexString(), seenParent, StringComparison.Ordinal);
  }

  [Fact]
  public async Task A_server_that_speaks_only_legacy_revisions_gives_a_version_error_naming_both_sides()
  {
    var server = new ContextServer("srv", "1", new ContextServerOptions { SupportedVersions = [ProtocolVersions.V20251125, ProtocolVersions.V20250618] });

    var ex = await Assert.ThrowsAsync<ProtocolVersionException>(async () => await ConnectAsync(server));

    Assert.Contains("modern 2026-07-28", ex.Message, StringComparison.Ordinal);
    Assert.Contains("legacy 2025-11-25, 2025-06-18", ex.Message, StringComparison.Ordinal);
    Assert.Equal([ProtocolVersions.V20251125, ProtocolVersions.V20250618], ex.Remote);
  }

  [Fact]
  public async Task A_peer_that_answers_discover_with_method_not_found_is_not_a_modern_server()
  {
    // A legacy server rejects the unknown method; the modern codec steps aside so the legacy codec can try.
    var (a, b) = PipeEnd.CreatePair();
    var peer = Task.Run(async () =>
    {
      var lines = new LineReader(a);
      var request = await lines.ReadJsonAsync();
      var reply = "{\"jsonrpc\":\"2.0\",\"id\":" + request.GetProperty("id").GetRawText() + ",\"error\":{\"code\":-32601,\"message\":\"no\"}}\n";
      await a.WriteAsync(System.Text.Encoding.UTF8.GetBytes(reply), TestContext.Current.CancellationToken);
    }, TestContext.Current.CancellationToken);

    var ex = await Assert.ThrowsAsync<ProtocolVersionException>(async () => await ContextClient.ConnectAsync(ContextTransport.Stream(b), cancellationToken: TestContext.Current.CancellationToken));
    await peer;

    Assert.Empty(ex.Remote);
  }

  // ---- caching, against a server played by hand so the test controls what it says ----

  private sealed class HandServer : IAsyncDisposable
  {
    private readonly PipeEnd _end;
    private readonly LineReader _lines;
    private readonly Task _loop;
    private int _toolsCalls;

    public HandServer(PipeEnd end, long ttlMs)
    {
      _end = end;
      _lines = new LineReader(end);
      TtlMs = ttlMs;
      _loop = Task.Run(RunAsync);
    }

    public long TtlMs { get; set; }

    public int ToolsListRequests => Volatile.Read(ref _toolsCalls);

    private async Task RunAsync()
    {
      while (await _lines.ReadLineAsync(TimeSpan.FromMinutes(1)) is { } line)
      {
        var request = JsonDocument.Parse(line).RootElement;
        var id = request.GetProperty("id").GetRawText();
        var method = request.GetProperty("method").GetString();
        string result = method switch
        {
          "server/discover" => """{"resultType":"complete","supportedVersions":["2026-07-28"],"capabilities":{"tools":{}},"ttlMs":0,"cacheScope":"private","_meta":{"io.modelcontextprotocol/serverInfo":{"name":"hand","version":"1"}}}""",
          "tools/list" => "{\"resultType\":\"complete\",\"tools\":[{\"name\":\"t" + Interlocked.Increment(ref _toolsCalls) + "\",\"inputSchema\":{\"type\":\"object\"}}],\"ttlMs\":" + TtlMs + ",\"cacheScope\":\"public\"}",
          _ => "{\"resultType\":\"complete\"}",
        };
        await Send("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + result + "}");
      }
    }

    public Task Send(string line) => _end.WriteAsync(System.Text.Encoding.UTF8.GetBytes(line + "\n")).AsTask();

    public async ValueTask DisposeAsync()
    {
      _end.Dispose();
      await _loop.WaitAsync(TimeSpan.FromSeconds(10));
    }
  }

  [Fact]
  public async Task List_results_are_reused_while_fresh_and_asked_again_when_stale()
  {
    var clock = new ManualTimeProvider();
    var (a, b) = PipeEnd.CreatePair();
    await using var server = new HandServer(a, ttlMs: 60_000);
    await using var client = await ContextClient.ConnectAsync(ContextTransport.Stream(b), new ContextClientOptions { TimeProvider = clock }, TestContext.Current.CancellationToken);

    var first = await client.ListToolsAsync(TestContext.Current.CancellationToken);
    clock.Advance(TimeSpan.FromSeconds(59));
    var second = await client.ListToolsAsync(TestContext.Current.CancellationToken);
    clock.Advance(TimeSpan.FromSeconds(2));
    var third = await client.ListToolsAsync(TestContext.Current.CancellationToken);

    Assert.Equal("t1", first[0].Name);
    Assert.Equal("t1", second[0].Name);
    Assert.Equal("t2", third[0].Name);
    Assert.Equal(2, server.ToolsListRequests);
  }

  [Fact]
  public async Task A_zero_ttl_or_a_disabled_cache_never_reuses()
  {
    var (a, b) = PipeEnd.CreatePair();
    await using var zero = new HandServer(a, ttlMs: 0);
    await using var client = await ContextClient.ConnectAsync(ContextTransport.Stream(b), cancellationToken: TestContext.Current.CancellationToken);
    await client.ListToolsAsync(TestContext.Current.CancellationToken);
    await client.ListToolsAsync(TestContext.Current.CancellationToken);
    Assert.Equal(2, zero.ToolsListRequests);

    var (c, d) = PipeEnd.CreatePair();
    await using var fresh = new HandServer(c, ttlMs: 60_000);
    await using var off = await ContextClient.ConnectAsync(ContextTransport.Stream(d), new ContextClientOptions { CacheResults = false }, TestContext.Current.CancellationToken);
    await off.ListToolsAsync(TestContext.Current.CancellationToken);
    await off.ListToolsAsync(TestContext.Current.CancellationToken);
    Assert.Equal(2, fresh.ToolsListRequests);
  }

  [Fact]
  public async Task A_list_changed_notification_drops_the_cached_list()
  {
    var (a, b) = PipeEnd.CreatePair();
    await using var server = new HandServer(a, ttlMs: 600_000);
    await using var client = await ContextClient.ConnectAsync(ContextTransport.Stream(b), cancellationToken: TestContext.Current.CancellationToken);
    var heard = new TaskCompletionSource();
    client.NotificationReceived += n =>
    {
      if (n.Method == Methods.NotificationsToolsListChanged)
      {
        heard.TrySetResult();
      }
    };

    var before = await client.ListToolsAsync(TestContext.Current.CancellationToken);
    await server.Send("""{"jsonrpc":"2.0","method":"notifications/tools/list_changed","params":{"_meta":{"io.modelcontextprotocol/subscriptionId":1}}}""");
    await heard.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    var after = await client.ListToolsAsync(TestContext.Current.CancellationToken);

    Assert.Equal("t1", before[0].Name);
    Assert.Equal("t2", after[0].Name);
  }

  [Fact]
  public async Task A_result_without_result_type_counts_as_complete()
  {
    var (a, b) = PipeEnd.CreatePair();
    var peer = Task.Run(async () =>
    {
      var lines = new LineReader(a);
      var discover = await lines.ReadJsonAsync();
      await a.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":" + discover.GetProperty("id").GetRawText() + ",\"result\":{\"supportedVersions\":[\"2026-07-28\"],\"capabilities\":{}}}\n"), TestContext.Current.CancellationToken);
      var call = await lines.ReadJsonAsync();
      await a.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":" + call.GetProperty("id").GetRawText() + ",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"old\"}]}}\n"), TestContext.Current.CancellationToken);
    }, TestContext.Current.CancellationToken);
    await using var client = await ContextClient.ConnectAsync(ContextTransport.Stream(b), cancellationToken: TestContext.Current.CancellationToken);

    var result = await client.CallToolAsync("x", cancellationToken: TestContext.Current.CancellationToken);
    await peer;

    Assert.Equal("old", result.Text);
    Assert.Equal("complete", result.EffectiveResultType);
  }

  private static async Task WaitForAsync(Func<bool> condition)
  {
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!condition())
    {
      Assert.True(DateTime.UtcNow < deadline, "Timed out waiting.");
      await Task.Delay(10, TestContext.Current.CancellationToken);
    }
  }
}
