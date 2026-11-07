using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>A hand-written 2026-07-28 client: every request carries the per-request _meta, answers come back as parsed JSON.</summary>
internal sealed class ModernWire(ContextServer server)
{
  private int _id;

  public ServerSessionState Session { get; } = new();

  /// <param name="method">The method.</param>
  /// <param name="members">The params as a JSON object (without _meta), or null.</param>
  /// <param name="capabilities">The client capabilities object.</param>
  /// <param name="extraMeta">More _meta members as a JSON object, or null.</param>
  public async Task<JsonElement> SendAsync(string method, string? members = null, string capabilities = "{}", string? extraMeta = null, ClaimsPrincipal? principal = null, IServerSink? sink = null)
  {
    var meta = new JsonObject
    {
      ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
      ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "wire", ["version"] = "1" },
      ["io.modelcontextprotocol/clientCapabilities"] = JsonNode.Parse(capabilities),
    };
    if (extraMeta is not null)
    {
      foreach (var (key, value) in JsonNode.Parse(extraMeta)!.AsObject().ToArray())
      {
        meta[key] = value?.DeepClone();
      }
    }

    var parameters = members is null ? new JsonObject() : JsonNode.Parse(members)!.AsObject();
    parameters["_meta"] = meta;
    var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++_id, ["method"] = method, ["params"] = parameters };
    return await RawAsync(body.ToJsonString(), principal, sink);
  }

  public async Task<JsonElement> RawAsync(string body, ClaimsPrincipal? principal = null, IServerSink? sink = null)
  {
    var reply = await server.HandleMessageAsync(body, Session, sink, principal, TestContext.Current.CancellationToken);
    Assert.NotNull(reply);
    return Json.Parse(reply);
  }
}

public class ModernServerTests
{
  private static readonly JsonElement ObjectSchema = Json.Parse("""{"type":"object"}""");
  private const string Elicit = """{"elicitation":{}}""";

  private static ContextServer NewServer(ContextServerOptions? options = null) => new("modern-test", "1.2.3", options);

  private static string Call(string tool, string? arguments = null)
  {
    var call = new JsonObject { ["name"] = tool };
    if (arguments is not null)
    {
      call["arguments"] = JsonNode.Parse(arguments);
    }

    return call.ToJsonString();
  }

  // The retry of a tool call: the same name and arguments, plus the answers and the state exactly as the server issued it.
  private static string Retry(string tool, string state, string answers, string? arguments = null)
  {
    var call = JsonNode.Parse(Call(tool, arguments))!.AsObject();
    call["inputResponses"] = JsonNode.Parse(answers);
    call["requestState"] = state;
    return call.ToJsonString();
  }

  private static string RetryRead(string uri, string state, string answers) =>
    new JsonObject { ["uri"] = uri, ["inputResponses"] = JsonNode.Parse(answers), ["requestState"] = state }.ToJsonString();

  private static string Answers(params (string Key, string Json)[] items)
  {
    var answers = new JsonObject();
    foreach (var (key, json) in items)
    {
      answers[key] = JsonNode.Parse(json);
    }

    return answers.ToJsonString();
  }

  private static string Confirm(bool yes) => "{\"action\":\"accept\",\"content\":{\"confirm\":" + (yes ? "true" : "false") + "}}";

  [Fact]
  public async Task Discover_names_the_versions_capabilities_and_server()
  {
    var server = NewServer(new ContextServerOptions { ListCacheTtl = TimeSpan.FromSeconds(30), ListCacheScope = "public" });
    server.Instructions = "Be kind.";
    server.Resources.Add(new ResourceDefinition("mem://r", "r"), (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [] }));
    server.Tools.Add(new ToolDefinition("t", "d", ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("x")));
    var wire = new ModernWire(server);

    var reply = await wire.SendAsync("server/discover");

    var result = reply.GetProperty("result");
    Assert.Equal("complete", result.GetProperty("resultType").GetString());
    Assert.Equal(ProtocolVersions.Supported, result.GetProperty("supportedVersions").EnumerateArray().Select(v => v.GetString()!).ToArray());
    Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
    Assert.True(result.GetProperty("capabilities").TryGetProperty("logging", out _));
    Assert.True(result.GetProperty("capabilities").GetProperty("resources").GetProperty("subscribe").GetBoolean());
    Assert.Equal("Be kind.", result.GetProperty("instructions").GetString());
    Assert.Equal("modern-test", result.GetProperty("_meta").GetProperty(WireNames.MetaServerInfo).GetProperty("name").GetString());
    Assert.Equal(30_000, result.GetProperty("ttlMs").GetInt64());
    Assert.Equal("public", result.GetProperty("cacheScope").GetString());
  }

  [Theory]
  [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")]
  [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""")]
  [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":5}}""")]
  [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}""")]
  [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":[]}}}""")]
  [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":7,"io.modelcontextprotocol/clientCapabilities":{}}}}""")]
  [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{"elicitation":3}}}}""")]
  [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"progressToken":true,"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}""")]
  [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/clientInfo":"me"}}}""")]
  [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}},"inputResponses":[1]}}""")]
  [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}},"requestState":9}}""")]
  public async Task Malformed_request_metadata_is_invalid_params_and_never_throws(string body)
  {
    var wire = new ModernWire(NewServer());

    var reply = await wire.RawAsync(body);

    Assert.Equal(-32602, reply.GetProperty("error").GetProperty("code").GetInt32());
    Assert.Equal(1, reply.GetProperty("id").GetInt32());
  }

  [Fact]
  public async Task An_unsupported_version_gets_32022_listing_what_the_server_speaks()
  {
    var audited = new List<AuditRecord>();
    var server = NewServer(new ContextServerOptions { Audit = audited.Add });
    var wire = new ModernWire(server);

    var reply = await wire.RawAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"1900-01-01","io.modelcontextprotocol/clientCapabilities":{}}}}""");

    var error = reply.GetProperty("error");
    Assert.Equal(-32022, error.GetProperty("code").GetInt32());
    Assert.Equal("1900-01-01", error.GetProperty("data").GetProperty("requested").GetString());
    Assert.Equal(ProtocolVersions.Supported, error.GetProperty("data").GetProperty("supported").EnumerateArray().Select(v => v.GetString()!).ToArray());
    var record = Assert.Single(audited);
    Assert.Equal(RequestOutcome.ProtocolError, record.Outcome);
    Assert.Equal(-32022, record.ErrorCode);
  }

  [Fact]
  public async Task A_legacy_revision_in_a_modern_envelope_is_also_unsupported()
  {
    var wire = new ModernWire(NewServer());

    var reply = await wire.RawAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2025-11-25","io.modelcontextprotocol/clientCapabilities":{}}}}""");

    Assert.Equal(-32022, reply.GetProperty("error").GetProperty("code").GetInt32());
  }

  [Fact]
  public async Task Legacy_only_methods_are_not_found_on_modern()
  {
    var wire = new ModernWire(NewServer());

    Assert.Equal(-32601, (await wire.SendAsync("ping")).GetProperty("error").GetProperty("code").GetInt32());
    Assert.Equal(-32601, (await wire.SendAsync("logging/setLevel", """{"level":"info"}""")).GetProperty("error").GetProperty("code").GetInt32());
    Assert.Equal(-32601, (await wire.SendAsync("resources/subscribe", """{"uri":"x://y"}""")).GetProperty("error").GetProperty("code").GetInt32());
  }

  [Fact]
  public async Task Every_result_names_its_type_and_the_server_and_lists_carry_cache_hints()
  {
    var server = NewServer(new ContextServerOptions { ListCacheTtl = TimeSpan.FromSeconds(5) });
    server.Tools.Add(new ToolDefinition("b", "d", ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("b")));
    server.Tools.Add(new ToolDefinition("a", "d", ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("a")));
    server.Resources.Add(new ResourceDefinition("mem://x", "x"), (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, Text = "X" }] }));
    server.Prompts.Add(new PromptDefinition("p"), (_, _) => ValueTask.FromResult(new GetPromptResult { Messages = [] }));
    var wire = new ModernWire(server);

    var tools = (await wire.SendAsync("tools/list")).GetProperty("result");
    var call = (await wire.SendAsync("tools/call", """{"name":"a"}""")).GetProperty("result");
    var read = (await wire.SendAsync("resources/read", """{"uri":"mem://x"}""")).GetProperty("result");
    var prompt = (await wire.SendAsync("prompts/get", """{"name":"p"}""")).GetProperty("result");

    // Deterministic order: by name, whatever the registration order.
    Assert.Equal(["a", "b"], tools.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray());
    foreach (var result in new[] { tools, call, read, prompt })
    {
      Assert.Equal("complete", result.GetProperty("resultType").GetString());
      Assert.Equal("modern-test", result.GetProperty("_meta").GetProperty(WireNames.MetaServerInfo).GetProperty("name").GetString());
    }

    Assert.Equal(5000, tools.GetProperty("ttlMs").GetInt64());
    Assert.Equal("private", tools.GetProperty("cacheScope").GetString());
    Assert.Equal(5000, read.GetProperty("ttlMs").GetInt64());
    Assert.False(call.TryGetProperty("ttlMs", out _));
    Assert.False(prompt.TryGetProperty("ttlMs", out _));
  }

  [Fact]
  public async Task Unknown_fields_and_meta_survive_in_and_out()
  {
    var server = NewServer();
    JsonElement seenMeta = default;
    server.Tools.Add(new ToolDefinition("echo", "d", ObjectSchema), (args, ctx) =>
    {
      seenMeta = JsonSerializer.SerializeToElement(ctx.Peer.Meta, WireJsonContext.Default.MetaObject);
      return ValueTask.FromResult(new CallToolResult
      {
        Content = [TextContent.Of("ok")],
        Meta = new MetaObject { AdditionalProperties = new() { ["vendor/note"] = Json.Parse("\"kept\"") } },
        AdditionalProperties = new() { ["futureField"] = Json.Parse("""{"n":1}""") },
      });
    });
    var wire = new ModernWire(server);

    var reply = await wire.SendAsync("tools/call", """{"name":"echo","arguments":{},"futureParam":true}""", extraMeta: """{"vendor/trace":"abc"}""");

    var result = reply.GetProperty("result");
    Assert.Equal("kept", result.GetProperty("_meta").GetProperty("vendor/note").GetString());
    Assert.Equal("modern-test", result.GetProperty("_meta").GetProperty(WireNames.MetaServerInfo).GetProperty("name").GetString());
    Assert.Equal(1, result.GetProperty("futureField").GetProperty("n").GetInt32());
    Assert.Equal("abc", seenMeta.GetProperty("vendor/trace").GetString());
  }

  [Fact]
  public async Task Resource_not_found_is_invalid_params_on_modern()
  {
    var server = NewServer();
    var wire = new ModernWire(server);

    var reply = await wire.SendAsync("resources/read", """{"uri":"mem://nope"}""");

    Assert.Equal(-32602, reply.GetProperty("error").GetProperty("code").GetInt32());
    Assert.Equal("mem://nope", reply.GetProperty("error").GetProperty("data").GetProperty("uri").GetString());
  }

  [Fact]
  public async Task An_unknown_tool_is_invalid_params_and_a_failing_tool_is_an_error_result()
  {
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("boom", "d", ObjectSchema), (_, _) => throw new ToolException("it broke"));
    var wire = new ModernWire(server);

    var unknown = await wire.SendAsync("tools/call", """{"name":"nope"}""");
    var failed = await wire.SendAsync("tools/call", """{"name":"boom"}""");

    Assert.Equal(-32602, unknown.GetProperty("error").GetProperty("code").GetInt32());
    Assert.True(failed.GetProperty("result").GetProperty("isError").GetBoolean());
    Assert.Equal("it broke", failed.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
  }

  // ---- multi round-trip requests ----

  [Fact]
  public async Task A_handler_that_asks_for_input_is_run_again_from_the_start_with_the_answer()
  {
    var runs = 0;
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("delete", "d", ObjectSchema), async (_, ctx) =>
    {
      runs++;
      return CallToolResult.FromText(await ctx.ConfirmAsync("Delete everything?") ? "Deleted." : "Cancelled.");
    });
    var wire = new ModernWire(server);

    var first = (await wire.SendAsync("tools/call", """{"name":"delete","arguments":{"path":"/tmp"}}""", Elicit)).GetProperty("result");

    Assert.Equal("input_required", first.GetProperty("resultType").GetString());
    var ask = first.GetProperty("inputRequests").GetProperty("input-1");
    Assert.Equal("elicitation/create", ask.GetProperty("method").GetString());
    Assert.Equal("form", ask.GetProperty("params").GetProperty("mode").GetString());
    Assert.Equal("Delete everything?", ask.GetProperty("params").GetProperty("message").GetString());
    var state = first.GetProperty("requestState").GetString()!;
    Assert.Equal(1, runs);

    var second = (await wire.SendAsync("tools/call", Retry("delete", state, Answers(("input-1", Confirm(true))), """{"path":"/tmp"}"""), Elicit)).GetProperty("result");

    Assert.Equal("complete", second.GetProperty("resultType").GetString());
    Assert.Equal("Deleted.", second.GetProperty("content")[0].GetProperty("text").GetString());
    Assert.Equal(2, runs);
  }

  [Fact]
  public async Task Asking_a_client_that_cannot_answer_is_a_missing_capability_error()
  {
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("delete", "d", ObjectSchema), async (_, ctx) => CallToolResult.FromText(await ctx.ConfirmAsync("sure?") ? "y" : "n"));
    server.Tools.Add(new ToolDefinition("sample", "d", ObjectSchema), async (_, ctx) =>
    {
      var answer = await ctx.SampleAsync(new CreateMessageRequestParams { Messages = [], MaxTokens = 5 });
      return CallToolResult.FromText(answer.Model);
    });
    server.Tools.Add(new ToolDefinition("roots", "d", ObjectSchema), async (_, ctx) => CallToolResult.FromText((await ctx.ListRootsAsync()).Roots.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    var wire = new ModernWire(server);

    foreach (var (tool, needs) in new[] { ("delete", "elicitation"), ("sample", "sampling"), ("roots", "roots") })
    {
      var reply = await wire.SendAsync("tools/call", Call(tool));
      var error = reply.GetProperty("error");
      Assert.Equal(-32021, error.GetProperty("code").GetInt32());
      Assert.True(error.GetProperty("data").GetProperty("requiredCapabilities").TryGetProperty(needs, out _));
    }
  }

  [Fact]
  public async Task Several_rounds_see_every_earlier_answer()
  {
    var runs = 0;
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("two", "d", ObjectSchema), async (_, ctx) =>
    {
      runs++;
      var a = await ctx.ConfirmAsync("first?");
      var b = await ctx.ConfirmAsync("second?");
      return CallToolResult.FromText($"{a}/{b}");
    });
    var wire = new ModernWire(server);

    var round1 = (await wire.SendAsync("tools/call", """{"name":"two"}""", Elicit)).GetProperty("result");
    Assert.Equal("first?", round1.GetProperty("inputRequests").GetProperty("input-1").GetProperty("params").GetProperty("message").GetString());
    Assert.False(round1.GetProperty("inputRequests").TryGetProperty("input-2", out _));

    var round2 = (await wire.SendAsync("tools/call", Retry("two", round1.GetProperty("requestState").GetString()!, Answers(("input-1", Confirm(true)))), Elicit)).GetProperty("result");
    Assert.Equal("second?", round2.GetProperty("inputRequests").GetProperty("input-2").GetProperty("params").GetProperty("message").GetString());

    // This retry carries only the second answer: the first one comes back out of the protected state.
    var round3 = (await wire.SendAsync("tools/call", Retry("two", round2.GetProperty("requestState").GetString()!, Answers(("input-2", Confirm(false)))), Elicit)).GetProperty("result");

    Assert.Equal("complete", round3.GetProperty("resultType").GetString());
    Assert.Equal("True/False", round3.GetProperty("content")[0].GetProperty("text").GetString());
    Assert.Equal(3, runs);
  }

  [Fact]
  public async Task Asks_started_together_share_one_round()
  {
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("both", "d", ObjectSchema), async (_, ctx) =>
    {
      var answers = await Task.WhenAll(ctx.ConfirmAsync("one?").AsTask(), ctx.ConfirmAsync("two?").AsTask());
      return CallToolResult.FromText(string.Join(",", answers));
    });
    var wire = new ModernWire(server);

    var round1 = (await wire.SendAsync("tools/call", """{"name":"both"}""", Elicit)).GetProperty("result");

    var keys = round1.GetProperty("inputRequests").EnumerateObject().Select(p => p.Name).ToArray();
    Assert.Equal(["input-1", "input-2"], keys);
    var round2 = (await wire.SendAsync("tools/call", Retry("both", round1.GetProperty("requestState").GetString()!, Answers(("input-1", Confirm(true)), ("input-2", Confirm(true)))), Elicit)).GetProperty("result");
    Assert.Equal("True,True", round2.GetProperty("content")[0].GetProperty("text").GetString());
  }

  [Fact]
  public async Task A_missing_answer_is_asked_again_instead_of_failing()
  {
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("t", "d", ObjectSchema), async (_, ctx) => CallToolResult.FromText(await ctx.ConfirmAsync("sure?") ? "y" : "n"));
    var wire = new ModernWire(server);
    var round1 = (await wire.SendAsync("tools/call", """{"name":"t"}""", Elicit)).GetProperty("result");

    // The retry answers nothing that was asked (spec: ignore what is not needed, ask again for what is).
    var round2 = (await wire.SendAsync("tools/call", Retry("t", round1.GetProperty("requestState").GetString()!, Answers(("unrelated", "{}"))), Elicit)).GetProperty("result");

    Assert.Equal("input_required", round2.GetProperty("resultType").GetString());
    Assert.True(round2.GetProperty("inputRequests").TryGetProperty("input-1", out _));
  }

  [Fact]
  public async Task State_set_by_the_handler_comes_back_on_the_retry()
  {
    var computed = 0;
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("t", "d", ObjectSchema), async (_, ctx) =>
    {
      if (!ctx.State.TryGetValue("expensive", out var value))
      {
        value = "v" + ++computed;
        ctx.State["expensive"] = value;
      }

      var yes = await ctx.ConfirmAsync("go?");
      return CallToolResult.FromText(value + (yes ? "!" : "?"));
    });
    var wire = new ModernWire(server);
    var round1 = (await wire.SendAsync("tools/call", """{"name":"t"}""", Elicit)).GetProperty("result");

    var round2 = (await wire.SendAsync("tools/call", Retry("t", round1.GetProperty("requestState").GetString()!, Answers(("input-1", Confirm(true)))), Elicit)).GetProperty("result");

    Assert.Equal("v1!", round2.GetProperty("content")[0].GetProperty("text").GetString());
    Assert.Equal(1, computed);
  }

  [Fact]
  public async Task Sampling_and_roots_are_answered_through_the_same_mechanism()
  {
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("t", "d", ObjectSchema), async (_, ctx) =>
    {
      var roots = await ctx.ListRootsAsync();
      var sampled = await ctx.SampleAsync(new CreateMessageRequestParams { Messages = [new SamplingMessage { Role = "user", Content = [TextContent.Of("hi")] }], MaxTokens = 10 });
      return CallToolResult.FromText(roots.Roots[0].Uri + "|" + sampled.Model);
    });
    var wire = new ModernWire(server);
    const string caps = """{"roots":{},"sampling":{}}""";

    var round1 = (await wire.SendAsync("tools/call", """{"name":"t"}""", caps)).GetProperty("result");
    Assert.Equal("roots/list", round1.GetProperty("inputRequests").GetProperty("input-1").GetProperty("method").GetString());
    var round2 = (await wire.SendAsync("tools/call", Retry("t", round1.GetProperty("requestState").GetString()!, Answers(("input-1", """{"roots":[{"uri":"file:///work"}]}"""))), caps)).GetProperty("result");
    Assert.Equal("sampling/createMessage", round2.GetProperty("inputRequests").GetProperty("input-2").GetProperty("method").GetString());
    var round3 = (await wire.SendAsync("tools/call", Retry("t", round2.GetProperty("requestState").GetString()!, Answers(("input-2", """{"role":"assistant","content":{"type":"text","text":"yo"},"model":"m1"}"""))), caps)).GetProperty("result");

    Assert.Equal("file:///work|m1", round3.GetProperty("content")[0].GetProperty("text").GetString());
  }

  [Fact]
  public async Task Prompts_and_resources_can_ask_for_input_but_other_requests_cannot()
  {
    var server = NewServer();
    server.Prompts.Add(new PromptDefinition("p"), async (_, ctx) =>
    {
      var yes = await ctx.ConfirmAsync("really?");
      return new GetPromptResult { Messages = [new PromptMessage { Role = "user", Content = TextContent.Of(yes ? "y" : "n") }] };
    });
    server.Resources.Add(new ResourceDefinition("mem://r", "r"), async (uri, ctx) =>
    {
      var yes = await ctx.ConfirmAsync("read it?");
      return new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, Text = yes ? "yes" : "no" }] };
    });
    server.Completions = async (_, ctx) =>
    {
      await ctx.ConfirmAsync("complete?");
      return new CompleteResult { Completion = new CompletionValues { Values = [] } };
    };
    var wire = new ModernWire(server);

    var prompt1 = (await wire.SendAsync("prompts/get", """{"name":"p"}""", Elicit)).GetProperty("result");
    var prompt2 = (await wire.SendAsync("prompts/get", Retry("p", prompt1.GetProperty("requestState").GetString()!, Answers(("input-1", Confirm(true)))), Elicit)).GetProperty("result");
    var read1 = (await wire.SendAsync("resources/read", """{"uri":"mem://r"}""", Elicit)).GetProperty("result");
    var read2 = (await wire.SendAsync("resources/read", RetryRead("mem://r", read1.GetProperty("requestState").GetString()!, Answers(("input-1", Confirm(true)))), Elicit)).GetProperty("result");
    var complete = await wire.SendAsync("completion/complete", """{"ref":{"type":"ref/prompt","name":"p"},"argument":{"name":"a","value":""}}""", Elicit);

    Assert.Equal("input_required", prompt1.GetProperty("resultType").GetString());
    Assert.Equal("y", prompt2.GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString());
    Assert.Equal("input_required", read1.GetProperty("resultType").GetString());
    Assert.False(read1.TryGetProperty("ttlMs", out _));
    Assert.Equal("yes", read2.GetProperty("contents")[0].GetProperty("text").GetString());
    Assert.Equal(-32603, complete.GetProperty("error").GetProperty("code").GetInt32());
  }

  private static async Task<(ModernWire Wire, string State, ContextServer Server)> FirstRoundAsync(ContextServerOptions? options = null, ClaimsPrincipal? principal = null)
  {
    var server = NewServer(options);
    server.Tools.Add(new ToolDefinition("t", "d", ObjectSchema), async (_, ctx) => CallToolResult.FromText(await ctx.ConfirmAsync("sure?") ? "y" : "n"));
    server.Tools.Add(new ToolDefinition("other", "d", ObjectSchema), async (_, ctx) => CallToolResult.FromText(await ctx.ConfirmAsync("sure?") ? "other-y" : "other-n"));
    var wire = new ModernWire(server);
    var first = (await wire.SendAsync("tools/call", """{"name":"t","arguments":{"a":1}}""", Elicit, principal: principal)).GetProperty("result");
    return (wire, first.GetProperty("requestState").GetString()!, server);
  }

  private static string RetryWith(string state, string tool = "t", string arguments = """{"a":1}""") =>
    Retry(tool, state, Answers(("input-1", Confirm(true))), arguments);

  private static void AssertRefused(JsonElement reply)
  {
    Assert.Equal(-32602, reply.GetProperty("error").GetProperty("code").GetInt32());
    Assert.Contains("requestState", reply.GetProperty("error").GetProperty("message").GetString());
  }

  [Fact]
  public async Task A_tampered_request_state_is_refused()
  {
    var (wire, state, _) = await FirstRoundAsync();

    // Flip one character in the middle of the token, then cut it short, then replace it with noise.
    var chars = state.ToCharArray();
    chars[chars.Length / 2] = chars[chars.Length / 2] == 'A' ? 'B' : 'A';
    AssertRefused(await wire.SendAsync("tools/call", RetryWith(new string(chars)), Elicit));
    AssertRefused(await wire.SendAsync("tools/call", RetryWith(state[..^4]), Elicit));
    AssertRefused(await wire.SendAsync("tools/call", RetryWith("not-a-state"), Elicit));
    AssertRefused(await wire.SendAsync("tools/call", RetryWith(string.Empty), Elicit));
    Assert.Equal("y", (await wire.SendAsync("tools/call", RetryWith(state), Elicit)).GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
  }

  [Fact]
  public async Task A_request_state_does_not_move_to_another_request_or_other_arguments()
  {
    var (wire, state, _) = await FirstRoundAsync();

    AssertRefused(await wire.SendAsync("tools/call", RetryWith(state, tool: "other"), Elicit));
    AssertRefused(await wire.SendAsync("tools/call", RetryWith(state, arguments: """{"a":2}"""), Elicit));
    AssertRefused(await wire.SendAsync("tools/call", RetryWith(state, arguments: """{"a":1,"b":true}"""), Elicit));

    // The same arguments written differently are still the same request.
    Assert.True((await wire.SendAsync("tools/call", RetryWith(state, arguments: """{ "a" : 1 }"""), Elicit)).TryGetProperty("result", out _));
  }

  [Fact]
  public async Task A_request_state_is_bound_to_the_caller_who_got_it()
  {
    static ClaimsPrincipal User(string sub) => new(new ClaimsIdentity([new Claim("sub", sub)], "test"));
    var (wire, state, _) = await FirstRoundAsync(principal: User("alice"));

    AssertRefused(await wire.SendAsync("tools/call", RetryWith(state), Elicit, principal: User("mallory")));
    AssertRefused(await wire.SendAsync("tools/call", RetryWith(state), Elicit, principal: null));
    Assert.True((await wire.SendAsync("tools/call", RetryWith(state), Elicit, principal: User("alice"))).TryGetProperty("result", out _));
  }

  [Fact]
  public async Task A_request_state_expires()
  {
    var clock = new ManualTimeProvider();
    var (wire, state, _) = await FirstRoundAsync(new ContextServerOptions { TimeProvider = clock, RequestStateLifetime = TimeSpan.FromMinutes(5) });

    clock.Advance(TimeSpan.FromMinutes(4));
    Assert.True((await wire.SendAsync("tools/call", RetryWith(state), Elicit)).TryGetProperty("result", out _));
    clock.Advance(TimeSpan.FromMinutes(2));
    AssertRefused(await wire.SendAsync("tools/call", RetryWith(state), Elicit));
  }

  [Fact]
  public async Task A_request_state_from_another_server_key_is_refused_and_a_shared_key_is_accepted()
  {
    var key = new byte[32];
    System.Security.Cryptography.RandomNumberGenerator.Fill(key);
    var (_, state, _) = await FirstRoundAsync(new ContextServerOptions { RequestStateKey = key });

    var sameKey = NewServer(new ContextServerOptions { RequestStateKey = key });
    sameKey.Tools.Add(new ToolDefinition("t", "d", ObjectSchema), async (_, ctx) => CallToolResult.FromText(await ctx.ConfirmAsync("sure?") ? "y" : "n"));
    var otherKey = NewServer();
    otherKey.Tools.Add(new ToolDefinition("t", "d", ObjectSchema), async (_, ctx) => CallToolResult.FromText(await ctx.ConfirmAsync("sure?") ? "y" : "n"));

    Assert.True((await new ModernWire(sameKey).SendAsync("tools/call", RetryWith(state), Elicit)).TryGetProperty("result", out _));
    AssertRefused(await new ModernWire(otherKey).SendAsync("tools/call", RetryWith(state), Elicit));
  }

  [Fact]
  public async Task Encrypted_state_hides_what_the_handler_stored()
  {
    var server = NewServer(new ContextServerOptions { EncryptRequestState = true });
    server.Tools.Add(new ToolDefinition("t", "d", ObjectSchema), async (_, ctx) =>
    {
      ctx.State["secret"] = "hunter2-marker";
      return CallToolResult.FromText(ctx.State["secret"] + (await ctx.ConfirmAsync("sure?") ? "!" : "?"));
    });
    var signedOnly = NewServer();
    signedOnly.Tools.Add(new ToolDefinition("t", "d", ObjectSchema), async (_, ctx) =>
    {
      ctx.State["secret"] = "hunter2-marker";
      return CallToolResult.FromText(ctx.State["secret"] + (await ctx.ConfirmAsync("sure?") ? "!" : "?"));
    });
    var wire = new ModernWire(server);

    var first = (await wire.SendAsync("tools/call", """{"name":"t"}""", Elicit)).GetProperty("result");
    var state = first.GetProperty("requestState").GetString()!;
    var plain = (await new ModernWire(signedOnly).SendAsync("tools/call", """{"name":"t"}""", Elicit)).GetProperty("result").GetProperty("requestState").GetString()!;

    Assert.DoesNotContain("hunter2", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Pad(state))), StringComparison.Ordinal);
    Assert.Contains("hunter2", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Pad(plain))), StringComparison.Ordinal);
    var second = (await wire.SendAsync("tools/call", RetryWith(state, arguments: "{}").Replace(",\"arguments\":{}", string.Empty, StringComparison.Ordinal), Elicit)).GetProperty("result");
    Assert.Equal("hunter2-marker!", second.GetProperty("content")[0].GetProperty("text").GetString());

    static string Pad(string base64Url) => base64Url.Replace('-', '+').Replace('_', '/').PadRight((base64Url.Length + 3) / 4 * 4, '=');
  }

  [Fact]
  public async Task A_handler_that_swallows_the_wait_still_ends_with_input_required()
  {
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("t", "d", ObjectSchema), async (_, ctx) =>
    {
      try
      {
        await ctx.ConfirmAsync("sure?");
      }
      catch (InputRequiredException)
      {
        // A handler that hides the wait must not get to answer as if it had the input.
      }

      return CallToolResult.FromText("done anyway");
    });
    var wire = new ModernWire(server);

    var result = (await wire.SendAsync("tools/call", """{"name":"t"}""", Elicit)).GetProperty("result");

    Assert.Equal("input_required", result.GetProperty("resultType").GetString());
  }

  [Fact]
  public async Task State_larger_than_the_limit_is_an_internal_error_not_a_crash()
  {
    var server = NewServer(new ContextServerOptions { MaxRequestStateBytes = 256 });
    server.Tools.Add(new ToolDefinition("t", "d", ObjectSchema), async (_, ctx) =>
    {
      ctx.State["blob"] = new string('x', 1000);
      return CallToolResult.FromText(await ctx.ConfirmAsync("sure?") ? "y" : "n");
    });

    var reply = await new ModernWire(server).SendAsync("tools/call", """{"name":"t"}""", Elicit);

    Assert.Equal(-32603, reply.GetProperty("error").GetProperty("code").GetInt32());
  }

  [Fact]
  public async Task The_audit_hook_runs_once_per_request_and_reports_input_required()
  {
    var audited = new List<AuditRecord>();
    var (wire, state, server) = await FirstRoundAsync(new ContextServerOptions { Audit = audited.Add });
    await wire.SendAsync("tools/call", RetryWith(state), Elicit);
    await wire.SendAsync("tools/list");

    Assert.Equal([RequestOutcome.InputRequired, RequestOutcome.Success, RequestOutcome.Success], audited.Select(a => a.Outcome).ToArray());
    Assert.Equal(["tools/call", "tools/call", "tools/list"], audited.Select(a => a.Method).ToArray());
    Assert.All(audited, a => Assert.Equal(ProtocolEra.Modern, a.Era));
    Assert.Equal("wire", audited[0].ClientInfo?.Name);
    Assert.Equal("t", audited[0].Name);
    Assert.NotNull(server);
  }

  // ---- logging, progress, trace context ----

  [Fact]
  public async Task Log_messages_are_sent_only_when_the_request_asked_for_a_level()
  {
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("t", "d", ObjectSchema), async (_, ctx) =>
    {
      await ctx.LogAsync(LoggingLevels.Debug, "dbg");
      await ctx.LogAsync(LoggingLevels.Warning, "warn");
      return CallToolResult.FromText("ok");
    });
    var wire = new ModernWire(server);

    var quiet = new RecordingSink();
    await wire.SendAsync("tools/call", """{"name":"t"}""", sink: quiet);
    var asked = new RecordingSink();
    await wire.SendAsync("tools/call", """{"name":"t"}""", extraMeta: """{"io.modelcontextprotocol/logLevel":"info"}""", sink: asked);

    Assert.Empty(quiet.Notifications);
    var note = Assert.Single(asked.Notifications);
    Assert.Equal("notifications/message", note.Method);
    Assert.Equal("warning", note.Params.GetProperty("level").GetString());
  }

  [Fact]
  public async Task Progress_goes_to_the_token_the_request_carried()
  {
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("t", "d", ObjectSchema), async (_, ctx) =>
    {
      await ctx.ReportProgressAsync(1, 3, "one");
      await ctx.ReportProgressAsync(2, 3);
      return CallToolResult.FromText("ok");
    });
    var wire = new ModernWire(server);
    var sink = new RecordingSink();

    await wire.SendAsync("tools/call", """{"name":"t"}""", extraMeta: """{"progressToken":"tok-9"}""", sink: sink);
    var none = new RecordingSink();
    await wire.SendAsync("tools/call", """{"name":"t"}""", sink: none);

    Assert.Equal(2, sink.Notifications.Count);
    Assert.All(sink.Notifications, n => Assert.Equal("tok-9", n.Params.GetProperty("progressToken").GetString()));
    Assert.Equal(1, sink.Notifications[0].Params.GetProperty("progress").GetDouble());
    Assert.Equal("one", sink.Notifications[0].Params.GetProperty("message").GetString());
    Assert.Empty(none.Notifications);
  }

  [Fact]
  public async Task The_request_span_joins_the_callers_trace()
  {
    var started = new List<System.Diagnostics.Activity>();
    using var listener = new System.Diagnostics.ActivityListener
    {
      ShouldListenTo = source => source.Name == ContextDiagnostics.SourceName,
      Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
      ActivityStarted = a => { lock (started) { started.Add(a); } },
    };
    System.Diagnostics.ActivitySource.AddActivityListener(listener);
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("t", "d", ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("ok")));
    var wire = new ModernWire(server);

    await wire.SendAsync("tools/call", """{"name":"t"}""", extraMeta: """{"traceparent":"00-0af7651916cd43dd8448eb211c80319c-00f067aa0ba902b7-01"}""");
    await wire.SendAsync("tools/call", """{"name":"t"}""", extraMeta: """{"traceparent":"garbage"}""");

    System.Diagnostics.Activity[] spans;
    lock (started)
    {
      spans = started.Where(a => a.OperationName == "tools/call t").ToArray();
    }

    Assert.Equal(2, spans.Length);
    Assert.Equal("0af7651916cd43dd8448eb211c80319c", spans[0].TraceId.ToHexString());
    Assert.Equal("00f067aa0ba902b7", spans[0].ParentSpanId.ToHexString());
    Assert.NotEqual("0af7651916cd43dd8448eb211c80319c", spans[1].TraceId.ToHexString());
  }

  // ---- subscriptions/listen ----

  [Fact]
  public async Task Listen_acknowledges_then_streams_only_what_was_asked_for_and_ends_gracefully()
  {
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("t", "d", ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("ok")));
    server.Resources.Add(new ResourceDefinition("mem://watched", "w"), (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [] }));
    var wire = new ModernWire(server);
    var sink = new RecordingSink();

    var listening = wire.SendAsync("subscriptions/listen", """{"notifications":{"toolsListChanged":true,"resourceSubscriptions":["mem://watched"]}}""", sink: sink);
    await WaitForAsync(() => sink.Notifications.Count >= 1);

    server.Prompts.Add(new PromptDefinition("later"), (_, _) => ValueTask.FromResult(new GetPromptResult { Messages = [] }));
    server.Tools.Add(new ToolDefinition("t2", "d", ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("ok")));
    server.NotifyResourceUpdated("mem://other");
    server.NotifyResourceUpdated("mem://watched");
    await WaitForAsync(() => sink.Notifications.Count >= 3);
    server.EndSubscriptions();
    var reply = await listening;

    string[] methods;
    lock (sink.Notifications)
    {
      methods = sink.Notifications.Select(n => n.Method).ToArray();
    }

    Assert.Equal(["notifications/subscriptions/acknowledged", "notifications/tools/list_changed", "notifications/resources/updated"], methods);
    Assert.All(sink.Notifications, n => Assert.Equal(1, n.Params.GetProperty("_meta").GetProperty(WireNames.MetaSubscriptionId).GetInt32()));
    var honored = sink.Notifications[0].Params.GetProperty("notifications");
    Assert.True(honored.GetProperty("toolsListChanged").GetBoolean());
    Assert.False(honored.TryGetProperty("promptsListChanged", out _));
    Assert.Equal("mem://watched", sink.Notifications[2].Params.GetProperty("uri").GetString());
    Assert.Equal("complete", reply.GetProperty("result").GetProperty("resultType").GetString());
    Assert.Equal(1, reply.GetProperty("result").GetProperty("_meta").GetProperty(WireNames.MetaSubscriptionId).GetInt32());
  }

  [Fact]
  public async Task Listen_without_a_filter_is_invalid_params()
  {
    var reply = await new ModernWire(NewServer()).SendAsync("subscriptions/listen");

    Assert.Equal(-32602, reply.GetProperty("error").GetProperty("code").GetInt32());
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
