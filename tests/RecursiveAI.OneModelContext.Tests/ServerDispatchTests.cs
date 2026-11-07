using System;
using System.Collections.Generic;
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

public class ServerDispatchTests
{
  private static readonly JsonElement ObjectSchema = Json.Parse("""{"type":"object"}""");

  private static ContextServer NewServer(ProtocolEra era = ProtocolEra.Modern, Action<ContextServerOptions>? _ = null, Action<AuditRecord>? audit = null, int maxInline = 4 * 1024 * 1024, int pageSize = 1000, bool elicitation = true)
  {
    var version = era == ProtocolEra.Modern ? ProtocolVersions.V20260728 : ProtocolVersions.V20251125;
    return new ContextServer("test", "1.0", new ContextServerOptions
    {
      Codecs = [new TestServerCodec(era, version, elicitation)],
      Audit = audit,
      MaxInlineContentBytes = maxInline,
      MaxPageSize = pageSize,
      ListCacheTtl = TimeSpan.FromSeconds(30),
    });
  }

  private static async Task<JsonElement> CallAsync(ContextServer server, string method, string? parameters, ServerSessionState? session = null, IServerSink? sink = null)
  {
    var message = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"" + method + "\"" + (parameters is null ? string.Empty : ",\"params\":" + parameters) + "}";
    var response = await server.HandleMessageAsync(message, session, sink, cancellationToken: TestContext.Current.CancellationToken);
    return Json.Parse(response!);
  }

  private static ToolHandler Text(string text) => (_, _) => ValueTask.FromResult(CallToolResult.FromText(text));

  [Fact]
  public async Task Tools_list_is_ordered_by_name_whatever_the_registration_order()
  {
    var server = NewServer();
    foreach (var name in new[] { "zeta", "alpha", "Mid", "beta" })
    {
      server.Tools.Add(new ToolDefinition(name, "d", ObjectSchema), Text(name));
    }

    var response = await CallAsync(server, "tools/list", null);

    var names = response.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
    Assert.Equal(["Mid", "alpha", "beta", "zeta"], names);
    Assert.Equal(30000, response.GetProperty("result").GetProperty("ttlMs").GetInt64());
    Assert.Equal("private", response.GetProperty("result").GetProperty("cacheScope").GetString());
    Assert.Equal("complete", response.GetProperty("result").GetProperty("resultType").GetString());
  }

  [Fact]
  public async Task Tool_annotations_and_schemas_are_listed()
  {
    var server = NewServer();
    server.Tools.Add(
      new ToolDefinition("t", "d", ObjectSchema) { Annotations = new ToolAnnotations { ReadOnlyHint = true }, OutputSchema = Json.Parse("""{"type":"object","properties":{"n":{"type":"integer"}}}""") },
      Text("x"));

    var tool = (await CallAsync(server, "tools/list", null)).GetProperty("result").GetProperty("tools")[0];

    Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
    Assert.Equal("object", tool.GetProperty("outputSchema").GetProperty("type").GetString());
    Assert.Equal("object", tool.GetProperty("inputSchema").GetProperty("type").GetString());
  }

  [Fact]
  public async Task A_tool_is_called_with_its_arguments()
  {
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("echo", "d", ObjectSchema), (args, _) => ValueTask.FromResult(CallToolResult.FromText(args.GetProperty("text").GetString()!)));

    var response = await CallAsync(server, "tools/call", """{"name":"echo","arguments":{"text":"hello"}}""");

    Assert.Equal("hello", response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
  }

  [Theory]
  [InlineData("""{"name":"nope"}""")]
  [InlineData("""{"arguments":{}}""")]
  [InlineData("""{"name":5}""")]
  [InlineData("""{"name":"echo","arguments":[1]}""")]
  [InlineData("""{"name":"echo","arguments":"x"}""")]
  public async Task A_bad_tools_call_is_invalid_params_not_an_exception(string parameters)
  {
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("echo", "d", ObjectSchema), Text("x"));

    var response = await CallAsync(server, "tools/call", parameters);

    Assert.Equal(-32602, response.GetProperty("error").GetProperty("code").GetInt32());
  }

  [Fact]
  public async Task A_tools_call_without_params_is_invalid_params()
  {
    var response = await CallAsync(NewServer(), "tools/call", null);

    Assert.Equal(-32602, response.GetProperty("error").GetProperty("code").GetInt32());
  }

  [Fact]
  public async Task A_tool_exception_is_a_tool_error_result_not_a_protocol_error()
  {
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("fail", "d", ObjectSchema), (_, _) => throw new ToolException("file not found"));

    var response = await CallAsync(server, "tools/call", """{"name":"fail"}""");

    Assert.False(response.TryGetProperty("error", out _));
    Assert.True(response.GetProperty("result").GetProperty("isError").GetBoolean());
    Assert.Equal("file not found", response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
  }

  [Fact]
  public async Task An_unexpected_exception_in_a_tool_is_reported_without_its_message()
  {
    var events = new List<DiagnosticEvent>();
    using var subscription = ContextDiagnostics.Subscribe(events.Add);
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("boom", "d", ObjectSchema), (_, _) => throw new InvalidOperationException("connection string password=hunter2"));

    var response = await CallAsync(server, "tools/call", """{"name":"boom"}""");

    var text = response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
    Assert.True(response.GetProperty("result").GetProperty("isError").GetBoolean());
    Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
    Assert.Contains(events, e => e.Kind == DiagnosticKind.HandlerFault && e.Exception is InvalidOperationException);
  }

  [Fact]
  public async Task A_tool_can_throw_a_protocol_error_to_reject_the_call()
  {
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("strict", "d", ObjectSchema), (_, _) => throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "limit must be positive"));

    var response = await CallAsync(server, "tools/call", """{"name":"strict"}""");

    Assert.Equal(-32602, response.GetProperty("error").GetProperty("code").GetInt32());
    Assert.Equal("limit must be positive", response.GetProperty("error").GetProperty("message").GetString());
  }

  [Fact]
  public async Task Oversized_inline_images_are_replaced_by_a_note()
  {
    var server = NewServer(maxInline: 8);
    var big = Convert.ToBase64String(new byte[100]);
    var small = Convert.ToBase64String(new byte[6]);
    server.Tools.Add(new ToolDefinition("img", "d", ObjectSchema), (_, _) => ValueTask.FromResult(new CallToolResult
    {
      Content = [new ImageContent { Data = big, MimeType = "image/png" }, new ImageContent { Data = small, MimeType = "image/png" }, TextContent.Of("t")],
    }));

    var content = (await CallAsync(server, "tools/call", """{"name":"img"}""")).GetProperty("result").GetProperty("content");

    Assert.Equal("text", content[0].GetProperty("type").GetString());
    Assert.Contains("100 bytes exceeds the inline limit of 8", content[0].GetProperty("text").GetString(), StringComparison.Ordinal);
    Assert.Equal("image", content[1].GetProperty("type").GetString());
    Assert.Equal("t", content[2].GetProperty("text").GetString());
  }

  [Fact]
  public async Task Resources_are_listed_read_and_matched_by_template()
  {
    var server = NewServer();
    server.Resources.Add(new ResourceDefinition("file:///readme", "readme") { MimeType = "text/plain" }, (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, Text = "hi" }] }));
    server.ResourceTemplates.Add(new ResourceTemplateDefinition("file:///logs/{name}", "log"), (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, Text = "log " + uri }] }));

    var list = await CallAsync(server, "resources/list", null);
    var templates = await CallAsync(server, "resources/templates/list", null);
    var exact = await CallAsync(server, "resources/read", """{"uri":"file:///readme"}""");
    var templated = await CallAsync(server, "resources/read", """{"uri":"file:///logs/app"}""");

    Assert.Equal("file:///readme", list.GetProperty("result").GetProperty("resources")[0].GetProperty("uri").GetString());
    Assert.Equal("file:///logs/{name}", templates.GetProperty("result").GetProperty("resourceTemplates")[0].GetProperty("uriTemplate").GetString());
    Assert.Equal("hi", exact.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString());
    Assert.Equal("log file:///logs/app", templated.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString());
  }

  [Theory]
  [InlineData(ProtocolEra.Modern, -32602)]
  [InlineData(ProtocolEra.Legacy, -32002)]
  public async Task A_missing_resource_uses_the_code_of_the_era(ProtocolEra era, int code)
  {
    var server = NewServer(era);

    var response = await CallAsync(server, "resources/read", """{"uri":"file:///missing"}""", new ServerSessionState { Era = era });

    Assert.Equal(code, response.GetProperty("error").GetProperty("code").GetInt32());
    Assert.Equal("file:///missing", response.GetProperty("error").GetProperty("data").GetProperty("uri").GetString());
  }

  [Fact]
  public async Task Prompts_are_listed_and_a_missing_required_argument_is_invalid_params()
  {
    var server = NewServer();
    server.Prompts.Add(
      new PromptDefinition("greet", "Greets") { Arguments = [new PromptArgument { Name = "who", Required = true }] },
      (args, _) => ValueTask.FromResult(new GetPromptResult { Messages = [new PromptMessage { Role = "user", Content = TextContent.Of("hello " + args["who"]) }] }));

    var ok = await CallAsync(server, "prompts/get", """{"name":"greet","arguments":{"who":"you"}}""");
    var missing = await CallAsync(server, "prompts/get", """{"name":"greet"}""");
    var unknown = await CallAsync(server, "prompts/get", """{"name":"nope"}""");
    var list = await CallAsync(server, "prompts/list", null);

    Assert.Equal("hello you", ok.GetProperty("result").GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString());
    Assert.Equal(-32602, missing.GetProperty("error").GetProperty("code").GetInt32());
    Assert.Equal(-32602, unknown.GetProperty("error").GetProperty("code").GetInt32());
    Assert.True(list.GetProperty("result").GetProperty("prompts")[0].GetProperty("arguments")[0].GetProperty("required").GetBoolean());
  }

  [Fact]
  public async Task Lists_are_paged_by_an_opaque_cursor_that_survives_changes()
  {
    var server = NewServer(pageSize: 2);
    foreach (var name in new[] { "a", "b", "c", "d", "e" })
    {
      server.Tools.Add(new ToolDefinition(name, "d", ObjectSchema), Text(name));
    }

    var first = (await CallAsync(server, "tools/list", null)).GetProperty("result");
    server.Tools.Remove("b");
    server.Tools.Add(new ToolDefinition("aa", "d", ObjectSchema), Text("aa"));
    var second = (await CallAsync(server, "tools/list", "{\"cursor\":\"" + first.GetProperty("nextCursor").GetString() + "\"}")).GetProperty("result");
    var third = (await CallAsync(server, "tools/list", "{\"cursor\":\"" + second.GetProperty("nextCursor").GetString() + "\"}")).GetProperty("result");

    string[] Names(JsonElement page) => page.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();
    Assert.Equal(["a", "b"], Names(first));
    Assert.Equal(["c", "d"], Names(second));
    Assert.Equal(["e"], Names(third));
    Assert.False(third.TryGetProperty("nextCursor", out _));
  }

  [Fact]
  public async Task A_cursor_that_is_not_ours_is_invalid_params()
  {
    var server = NewServer();

    var response = await CallAsync(server, "tools/list", """{"cursor":"!!!not base64!!!"}""");

    Assert.Equal(-32602, response.GetProperty("error").GetProperty("code").GetInt32());
  }

  [Fact]
  public async Task An_unknown_method_is_method_not_found()
  {
    var response = await CallAsync(NewServer(), "tools/frobnicate", null);

    Assert.Equal(-32601, response.GetProperty("error").GetProperty("code").GetInt32());
  }

  [Fact]
  public async Task Completion_needs_a_handler()
  {
    var server = NewServer();
    var request = """{"ref":{"type":"ref/prompt","name":"p"},"argument":{"name":"a","value":"x"}}""";

    var before = await CallAsync(server, "completion/complete", request);
    server.Completions = (r, _) => ValueTask.FromResult(new CompleteResult { Completion = new CompletionValues { Values = [r.Argument.Value + "1", r.Argument.Value + "2"] } });
    var after = await CallAsync(server, "completion/complete", request);

    Assert.Equal(-32601, before.GetProperty("error").GetProperty("code").GetInt32());
    Assert.Equal(["x1", "x2"], after.GetProperty("result").GetProperty("completion").GetProperty("values").EnumerateArray().Select(v => v.GetString()).ToArray());
    Assert.True(server.Capabilities.Completions.ValueKind == JsonValueKind.Object);
  }

  [Fact]
  public async Task Capabilities_follow_what_is_registered()
  {
    var server = NewServer();
    Assert.Null(server.Capabilities.Tools);
    Assert.Null(server.Capabilities.Resources);

    server.Tools.Add(new ToolDefinition("t", "d", ObjectSchema), Text("x"));
    server.ResourceTemplates.Add(new ResourceTemplateDefinition("a://{b}", "t"), (_, _) => throw new NotSupportedException());

    Assert.True(server.Capabilities.Tools!.ListChanged);
    Assert.NotNull(server.Capabilities.Resources);
    Assert.Null(server.Capabilities.Prompts);
    await Task.CompletedTask;
  }

  [Fact]
  public async Task Registrations_can_change_while_the_server_runs()
  {
    var server = NewServer();
    var changes = 0;
    server.Tools.Changed += (_, _) => Interlocked.Increment(ref changes);

    server.Tools.Add(new ToolDefinition("t", "v1", ObjectSchema), Text("one"));
    var one = await CallAsync(server, "tools/call", """{"name":"t"}""");
    server.Tools.Add(new ToolDefinition("t", "v2", ObjectSchema), Text("two"));
    var two = await CallAsync(server, "tools/call", """{"name":"t"}""");
    Assert.True(server.Tools.Remove("t"));
    var gone = await CallAsync(server, "tools/call", """{"name":"t"}""");

    Assert.Equal("one", one.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    Assert.Equal("two", two.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    Assert.Equal(-32602, gone.GetProperty("error").GetProperty("code").GetInt32());
    Assert.Equal(3, changes);
    Assert.False(server.Tools.Remove("t"));
  }

  [Fact]
  public async Task A_server_with_no_codec_says_so()
  {
    var server = new ContextServer("t", "1", new ContextServerOptions { Codecs = [] });

    var response = await CallAsync(server, "tools/list", null);

    Assert.Equal(-32601, response.GetProperty("error").GetProperty("code").GetInt32());
    Assert.Contains("no modern protocol codec", response.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
  }

  [Fact]
  public async Task A_codec_can_answer_or_reject_a_request_itself()
  {
    var server = NewServer();

    var response = await CallAsync(server, "test/reject", null);

    Assert.Equal(-32022, response.GetProperty("error").GetProperty("code").GetInt32());
  }

  [Fact]
  public async Task The_audit_hook_sees_each_request_with_its_outcome_and_never_the_arguments()
  {
    var records = new List<AuditRecord>();
    var server = NewServer(audit: records.Add);
    server.Tools.Add(new ToolDefinition("ok", "d", ObjectSchema), Text("x"));
    server.Tools.Add(new ToolDefinition("bad", "d", ObjectSchema), (_, _) => throw new ToolException("no"));

    await CallAsync(server, "tools/call", """{"name":"ok","arguments":{"secret":"s3cr3t"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"cli","version":"9"}}}""");
    await CallAsync(server, "tools/call", """{"name":"bad"}""");
    await CallAsync(server, "tools/call", """{"name":"missing"}""");
    await CallAsync(server, "tools/list", null);

    Assert.Equal(4, records.Count);
    Assert.Equal(("tools/call", "ok", RequestOutcome.Success), (records[0].Method, records[0].Name, records[0].Outcome));
    Assert.Equal("cli", records[0].ClientInfo!.Name);
    Assert.Equal(ProtocolEra.Modern, records[0].Era);
    Assert.Equal(RequestOutcome.ToolError, records[1].Outcome);
    Assert.Equal(RequestOutcome.ProtocolError, records[2].Outcome);
    Assert.Equal(-32602, records[2].ErrorCode);
    Assert.Equal(("tools/list", null), (records[3].Method, records[3].Name));
    Assert.DoesNotContain("s3cr3t", records[0].ToString(), StringComparison.Ordinal);
  }

  [Fact]
  public async Task A_failing_audit_callback_does_not_break_the_request()
  {
    var server = NewServer(audit: _ => throw new InvalidOperationException("audit store down"));

    var response = await CallAsync(server, "tools/list", null);

    Assert.True(response.TryGetProperty("result", out _));
  }

  [Fact]
  public async Task The_audit_hook_sees_the_principal_the_host_supplied()
  {
    var records = new List<AuditRecord>();
    var server = NewServer(audit: records.Add);
    var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sub", "alice")], "test"));

    await server.HandleMessageAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", principal: principal, cancellationToken: TestContext.Current.CancellationToken);

    Assert.Equal("alice", records[0].Principal!.FindFirst("sub")!.Value);
  }

  [Fact]
  public async Task Handlers_see_the_principal_and_the_request_facts()
  {
    var server = NewServer();
    RequestContext? seen = null;
    server.Tools.Add(new ToolDefinition("who", "d", ObjectSchema), (_, ctx) =>
    {
      seen = ctx;
      return ValueTask.FromResult(CallToolResult.FromText("x"));
    });
    var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sub", "bob")], "test"));

    await server.HandleMessageAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"who","_meta":{"io.modelcontextprotocol/clientInfo":{"name":"c","version":"1"}}}}""", principal: principal, cancellationToken: TestContext.Current.CancellationToken);

    Assert.Equal("bob", seen!.Principal!.FindFirst("sub")!.Value);
    Assert.Equal("c", seen.ClientInfo!.Name);
    Assert.Equal(ProtocolEra.Modern, seen.Era);
    Assert.Equal("tools/call", seen.Method);
    Assert.Equal("who", seen.Name);
    Assert.Same(server, seen.Server);
  }

  [Fact]
  public async Task Progress_goes_to_the_sink_only_when_the_request_has_a_token()
  {
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("work", "d", ObjectSchema), async (_, ctx) =>
    {
      await ctx.ReportProgressAsync(1, 3, "one");
      await ctx.ReportProgressAsync(2, 3);
      return CallToolResult.FromText("done");
    });
    var withToken = new RecordingSink();
    var without = new RecordingSink();

    await server.HandleMessageAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"work","_meta":{"progressToken":"p9"}}}""", sink: withToken, cancellationToken: TestContext.Current.CancellationToken);
    await server.HandleMessageAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"work"}}""", sink: without, cancellationToken: TestContext.Current.CancellationToken);

    Assert.Equal(2, withToken.Notifications.Count);
    Assert.Equal("notifications/progress", withToken.Notifications[0].Method);
    Assert.Equal("p9", withToken.Notifications[0].Params.GetProperty("progressToken").GetString());
    Assert.Equal("one", withToken.Notifications[0].Params.GetProperty("message").GetString());
    Assert.Empty(without.Notifications);
  }

  [Fact]
  public async Task Log_messages_follow_the_level_the_request_asked_for()
  {
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("log", "d", ObjectSchema), async (_, ctx) =>
    {
      await ctx.LogAsync("debug", "d");
      await ctx.LogAsync("warning", "w");
      return CallToolResult.FromText("x");
    });
    var sink = new RecordingSink();

    await server.HandleMessageAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"log","_meta":{"io.modelcontextprotocol/logLevel":"info"}}}""", sink: sink, cancellationToken: TestContext.Current.CancellationToken);
    var none = new RecordingSink();
    await server.HandleMessageAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"log"}}""", sink: none, cancellationToken: TestContext.Current.CancellationToken);

    var only = Assert.Single(sink.Notifications);
    Assert.Equal("w", only.Params.GetProperty("data").GetString());
    Assert.Empty(none.Notifications);
  }

  [Fact]
  public async Task Confirm_asks_through_the_input_channel_and_reads_the_answer()
  {
    var server = NewServer(ProtocolEra.Legacy);
    server.Tools.Add(new ToolDefinition("rm", "d", ObjectSchema), async (_, ctx) => CallToolResult.FromText(await ctx.ConfirmAsync("Delete?") ? "deleted" : "kept"));
    var sink = new RecordingSink();

    string? asked = null;
    sink.Answer = (method, parameters) =>
    {
      asked = method + ":" + parameters.GetProperty("message").GetString();
      return Json.Parse("""{"action":"accept","content":{"confirm":true}}""");
    };
    var yes = await CallAsync(server, "tools/call", """{"name":"rm"}""", new ServerSessionState { Era = ProtocolEra.Legacy }, sink);
    sink.Answer = (_, _) => Json.Parse("""{"action":"decline"}""");
    var no = await CallAsync(server, "tools/call", """{"name":"rm"}""", new ServerSessionState { Era = ProtocolEra.Legacy }, sink);

    Assert.Equal("elicitation/create:Delete?", asked);
    Assert.Equal("deleted", yes.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    Assert.Equal("kept", no.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
  }

  [Fact]
  public async Task Eliciting_from_a_client_without_the_capability_is_a_missing_capability_error()
  {
    var server = NewServer(elicitation: false);
    server.Tools.Add(new ToolDefinition("rm", "d", ObjectSchema), async (_, ctx) => CallToolResult.FromText(await ctx.ConfirmAsync("Delete?") ? "y" : "n"));

    var response = await CallAsync(server, "tools/call", """{"name":"rm"}""");

    Assert.Equal(-32021, response.GetProperty("error").GetProperty("code").GetInt32());
    Assert.True(response.GetProperty("error").GetProperty("data").GetProperty("requiredCapabilities").TryGetProperty("elicitation", out _));
  }

  [Fact]
  public async Task Typed_elicitation_reads_the_form_values()
  {
    var server = NewServer(ProtocolEra.Legacy);
    server.Tools.Add(new ToolDefinition("form", "d", ObjectSchema), async (_, ctx) =>
    {
      var answer = await ctx.ElicitAsync("Who?", Json.Parse("""{"type":"object","properties":{"name":{"type":"string"}}}"""), WireJsonContext.Default.DictionaryStringJsonElement);
      return CallToolResult.FromText(answer.Accepted ? answer.Content!["name"].GetString()! : answer.Action);
    });
    var sink = new RecordingSink { Answer = (_, _) => Json.Parse("""{"action":"accept","content":{"name":"Ada"}}""") };

    var response = await CallAsync(server, "tools/call", """{"name":"form"}""", new ServerSessionState { Era = ProtocolEra.Legacy }, sink);

    Assert.Equal("Ada", response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
  }

  [Fact]
  public async Task An_input_required_exception_ends_the_attempt_with_that_result()
  {
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("ask", "d", ObjectSchema), (_, _) =>
      throw new InputRequiredException(new InputRequiredResult { ResultType = "input_required", RequestState = "s1" }));
    var records = new List<AuditRecord>();
    var audited = new ContextServer("t", "1", new ContextServerOptions { Codecs = [new TestServerCodec(ProtocolEra.Modern, "2026-07-28")], Audit = records.Add });
    audited.Tools.Add(new ToolDefinition("ask", "d", ObjectSchema), (_, _) => throw new InputRequiredException(new InputRequiredResult { ResultType = "input_required" }));

    var response = await CallAsync(server, "tools/call", """{"name":"ask"}""");
    await CallAsync(audited, "tools/call", """{"name":"ask"}""");

    Assert.Equal("input_required", response.GetProperty("result").GetProperty("resultType").GetString());
    Assert.Equal("s1", response.GetProperty("result").GetProperty("requestState").GetString());
    Assert.Equal(RequestOutcome.InputRequired, records[0].Outcome);
  }

  [Fact]
  public async Task Bad_messages_to_the_message_entry_return_errors_and_never_throw()
  {
    var server = NewServer();

    var parse = Json.Parse((await server.HandleMessageAsync("{nope", cancellationToken: TestContext.Current.CancellationToken))!);
    var invalid = Json.Parse((await server.HandleMessageAsync("[1]", cancellationToken: TestContext.Current.CancellationToken))!);
    var notification = await server.HandleMessageAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", cancellationToken: TestContext.Current.CancellationToken);
    var response = await server.HandleMessageAsync("""{"jsonrpc":"2.0","id":1,"result":{}}""", cancellationToken: TestContext.Current.CancellationToken);

    Assert.Equal(-32700, parse.GetProperty("error").GetProperty("code").GetInt32());
    Assert.Equal(JsonValueKind.Null, parse.GetProperty("id").ValueKind);
    Assert.Equal(-32600, invalid.GetProperty("error").GetProperty("code").GetInt32());
    Assert.Null(notification);
    Assert.Null(response);
  }

  [Fact]
  public async Task Batches_are_refused_unless_the_session_negotiated_2025_03_26()
  {
    var server = new ContextServer("t", "1", new ContextServerOptions { Codecs = [new TestServerCodec(ProtocolEra.Legacy, ProtocolVersions.V20250326)] });
    server.Tools.Add(new ToolDefinition("t", "d", ObjectSchema), Text("x"));
    var batch = """[{"jsonrpc":"2.0","id":1,"method":"tools/list"},{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"t"}},{"jsonrpc":"2.0","method":"notifications/initialized"}]""";
    var session = new ServerSessionState();

    var before = Json.Parse((await server.HandleMessageAsync(batch, session, cancellationToken: TestContext.Current.CancellationToken))!);
    await server.HandleMessageAsync("""{"jsonrpc":"2.0","id":9,"method":"initialize","params":{}}""", session, cancellationToken: TestContext.Current.CancellationToken);
    var after = Json.Parse((await server.HandleMessageAsync(batch, session, cancellationToken: TestContext.Current.CancellationToken))!);

    Assert.Equal(-32600, before.GetProperty("error").GetProperty("code").GetInt32());
    Assert.Equal(JsonValueKind.Array, after.ValueKind);
    Assert.Equal(2, after.GetArrayLength());
  }
}

public class CodecSelectorTests
{
  [Theory]
  [InlineData("initialize", null, false, ProtocolEra.Legacy)]
  [InlineData("notifications/initialized", null, false, ProtocolEra.Legacy)]
  [InlineData("server/discover", null, false, ProtocolEra.Modern)]
  [InlineData("tools/list", """{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}""", false, ProtocolEra.Modern)]
  [InlineData("tools/list", null, true, ProtocolEra.Legacy)]
  [InlineData("tools/list", null, false, ProtocolEra.Modern)]
  [InlineData("tools/list", """{"_meta":{"progressToken":1}}""", false, ProtocolEra.Modern)]
  [InlineData("tools/list", """{"_meta":{"progressToken":1}}""", true, ProtocolEra.Legacy)]
  public void The_codec_follows_the_message(string method, string? parameters, bool hasSessionId, ProtocolEra expected)
  {
    var element = parameters is null ? default : Json.Parse(parameters);

    Assert.Equal(expected, CodecSelector.Select(method, element, new ServerSessionState(), hasSessionId));
  }

  [Fact]
  public void A_connection_that_settled_on_an_era_stays_there_until_a_message_says_otherwise()
  {
    var session = new ServerSessionState { Era = ProtocolEra.Legacy };

    Assert.Equal(ProtocolEra.Legacy, CodecSelector.Select("tools/list", default, session));
    Assert.Equal(ProtocolEra.Modern, CodecSelector.Select("server/discover", default, session));
    Assert.Equal(ProtocolEra.Modern, CodecSelector.Select("tools/list", Json.Parse("""{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}"""), session));
  }

  [Fact]
  public async Task Both_eras_are_answered_on_one_server_by_their_own_codec()
  {
    var server = new ContextServer("t", "1", new ContextServerOptions { Codecs = [new TestServerCodec(ProtocolEra.Modern, "2026-07-28"), new TestServerCodec(ProtocolEra.Legacy, "2025-11-25")] });
    var eras = new List<ProtocolEra?>();
    var audited = new ContextServer("t", "1", new ContextServerOptions { Codecs = [new TestServerCodec(ProtocolEra.Modern, "2026-07-28"), new TestServerCodec(ProtocolEra.Legacy, "2025-11-25")], Audit = r => eras.Add(r.Era) });

    await audited.HandleMessageAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}""", cancellationToken: TestContext.Current.CancellationToken);
    var session = new ServerSessionState();
    await audited.HandleMessageAsync("""{"jsonrpc":"2.0","id":2,"method":"initialize","params":{}}""", session, cancellationToken: TestContext.Current.CancellationToken);
    await audited.HandleMessageAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""", session, cancellationToken: TestContext.Current.CancellationToken);

    Assert.Equal([ProtocolEra.Modern, ProtocolEra.Legacy, ProtocolEra.Legacy], eras);
    Assert.NotNull(server);
  }
}
