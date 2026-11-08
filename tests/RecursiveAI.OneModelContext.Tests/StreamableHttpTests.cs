using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Hosting;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

public class StreamableHttpTests
{
  private static readonly JsonElement ObjectSchema = Json.Parse("""{"type":"object"}""");

  private static (ContextServer Server, StreamableHttpEndpoint Endpoint) Create(StreamableHttpOptions? options = null, TimeProvider? time = null, string legacyVersion = ProtocolVersions.V20251125)
  {
    var server = new ContextServer(
      "http-test",
      "1.0",
      new ContextServerOptions
      {
        Codecs = [new TestServerCodec(ProtocolEra.Modern, ProtocolVersions.V20260728), new TestServerCodec(ProtocolEra.Legacy, legacyVersion)],
        TimeProvider = time ?? TimeProvider.System,
      });
    server.Tools.Add(new ToolDefinition("echo", "Echoes.", ObjectSchema), (args, _) => ValueTask.FromResult(CallToolResult.FromText(args.ValueKind == JsonValueKind.Object && args.TryGetProperty("text", out var t) ? t.GetString()! : "ok")));
    server.Tools.Add(
      new ToolDefinition("region", "Mirrors a parameter into a header.", Json.Parse("""{"type":"object","properties":{"region":{"type":"string","x-mcp-header":"Region"},"count":{"type":"integer","x-mcp-header":"Count"}}}""")),
      (_, _) => ValueTask.FromResult(CallToolResult.FromText("ok")));
    return (server, new StreamableHttpEndpoint(server, options));
  }

  private static string Call(string tool, string arguments = "{}", int id = 1, string extraMeta = "") =>
    $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"method":"tools/call","params":{"name":"{{{{tool}}}}","arguments":{{{{arguments}}}},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28"{{{{extraMeta}}}}}}}""";

  private static (string, string)[] ModernHeaders(string method = "tools/call", string? name = "echo", params (string, string)[] more)
  {
    var headers = new List<(string, string)> { ("MCP-Protocol-Version", "2026-07-28"), ("Mcp-Method", method) };
    if (name is not null)
    {
      headers.Add(("Mcp-Name", name));
    }

    headers.AddRange(more);
    return [.. headers];
  }

  private static async Task<JsonElement> ErrorOf(TestExchange exchange, int status, int code)
  {
    var body = await exchange.JsonAsync();
    Assert.Equal(status, exchange.Status);
    Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetInt32());
    return body;
  }

  // ---- modern ----------------------------------------------------------------------------------------------------------------

  [Theory]
  [InlineData("bad\\ud800")]
  [InlineData("bad\\udc00")]
  public async Task Malformed_tool_name_encoding_is_invalid_params_before_header_comparison(string name)
  {
    using var endpoint = new StreamableHttpEndpoint(new ContextServer("test", "1"));
    using var exchange = TestExchange.Post(Call(name), ModernHeaders()).Run(endpoint);
    await ErrorOf(exchange, 400, JsonRpcErrorCodes.InvalidParams);
  }

  [Fact]
  public async Task A_modern_call_with_matching_headers_gets_a_json_result()
  {
    var (_, endpoint) = Create();
    var exchange = TestExchange.Post(Call("echo", """{"text":"hi"}"""), ModernHeaders()).Run(endpoint);

    var body = await exchange.JsonAsync();

    Assert.Equal(200, exchange.Status);
    Assert.Equal("application/json", exchange.ResponseHeaders.GetFirst("Content-Type"));
    Assert.Equal("hi", body.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    Assert.Equal(0, endpoint.SessionCount);
    Assert.False(exchange.ResponseHeaders.Contains("Mcp-Session-Id"));
  }

  [Fact]
  public async Task A_missing_protocol_version_header_is_a_header_mismatch()
  {
    var (_, endpoint) = Create();
    var exchange = TestExchange.Post(Call("echo"), ("Mcp-Method", "tools/call"), ("Mcp-Name", "echo")).Run(endpoint);

    await ErrorOf(exchange, 400, ProtocolErrorCodes.HeaderMismatch);
  }

  [Fact]
  public async Task The_version_header_must_equal_the_version_in_the_body()
  {
    var (_, endpoint) = Create();
    var exchange = TestExchange.Post(Call("echo"), ("MCP-Protocol-Version", "2025-11-25"), ("Mcp-Method", "tools/call"), ("Mcp-Name", "echo")).Run(endpoint);

    await ErrorOf(exchange, 400, ProtocolErrorCodes.HeaderMismatch);
  }

  [Fact]
  public async Task An_unsupported_version_header_is_answered_with_the_supported_list()
  {
    var (_, endpoint) = Create();
    var exchange = TestExchange.Post(Call("echo"), ("MCP-Protocol-Version", "1900-01-01"), ("Mcp-Method", "tools/call"), ("Mcp-Name", "echo")).Run(endpoint);

    var body = await ErrorOf(exchange, 400, ProtocolErrorCodes.UnsupportedProtocolVersion);

    Assert.Contains(ProtocolVersions.V20260728, body.GetProperty("error").GetProperty("data").GetProperty("supported").EnumerateArray().Select(e => e.GetString()));
    Assert.Equal("1900-01-01", body.GetProperty("error").GetProperty("data").GetProperty("requested").GetString());
  }

  [Fact]
  public async Task An_error_for_an_unsupported_version_carries_the_id_of_the_request()
  {
    var (_, endpoint) = Create();
    var exchange = TestExchange.Post(Call("echo", id: 301), ("MCP-Protocol-Version", "1900-01-01"), ("Mcp-Method", "tools/call"), ("Mcp-Name", "echo")).Run(endpoint);

    var body = await ErrorOf(exchange, 400, ProtocolErrorCodes.UnsupportedProtocolVersion);

    Assert.Equal(301, body.GetProperty("id").GetInt32());
  }

  [Fact]
  public async Task Initialize_naming_the_modern_version_is_method_not_found_with_404()
  {
    // 2026-07-28 Streamable HTTP, "Protocol Version Header": an unimplemented method is 404 with -32601.
    var (_, endpoint) = Create();
    var exchange = TestExchange.Post("""{"jsonrpc":"2.0","id":7,"method":"initialize","params":{}}""", ("MCP-Protocol-Version", "2026-07-28"), ("Mcp-Method", "initialize")).Run(endpoint);

    var body = await ErrorOf(exchange, 404, JsonRpcErrorCodes.MethodNotFound);

    Assert.Equal(7, body.GetProperty("id").GetInt32());
  }

  [Theory]
  [InlineData("tools/list")]
  [InlineData("")]
  public async Task The_method_header_must_equal_the_method_in_the_body(string headerMethod)
  {
    var (_, endpoint) = Create();
    var headers = headerMethod.Length == 0 ? new[] { ("MCP-Protocol-Version", "2026-07-28"), ("Mcp-Name", "echo") } : ModernHeaders(headerMethod);
    var exchange = TestExchange.Post(Call("echo"), headers).Run(endpoint);

    await ErrorOf(exchange, 400, ProtocolErrorCodes.HeaderMismatch);
  }

  [Fact]
  public async Task The_name_header_must_equal_the_tool_name_in_the_body()
  {
    var (_, endpoint) = Create();
    var exchange = TestExchange.Post(Call("echo"), ModernHeaders(name: "other")).Run(endpoint);

    var body = await ErrorOf(exchange, 400, ProtocolErrorCodes.HeaderMismatch);

    Assert.Equal(1, body.GetProperty("id").GetInt32());
  }

  [Fact]
  public async Task A_base64_encoded_name_header_is_decoded_before_the_comparison()
  {
    var (_, endpoint) = Create();
    var encoded = "=?base64?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("echo")) + "?=";
    var exchange = TestExchange.Post(Call("echo"), ModernHeaders(name: encoded)).Run(endpoint);

    await exchange.BodyAsync();

    Assert.Equal(200, exchange.Status);
  }

  [Fact]
  public async Task A_name_header_that_is_not_valid_base64_is_refused()
  {
    var (_, endpoint) = Create();
    var exchange = TestExchange.Post(Call("echo"), ModernHeaders(name: "=?base64?***?=")).Run(endpoint);

    await ErrorOf(exchange, 400, ProtocolErrorCodes.HeaderMismatch);
  }

  [Fact]
  public async Task Annotated_parameters_must_be_mirrored_into_headers_and_agree()
  {
    var (_, endpoint) = Create();

    var ok = TestExchange.Post(Call("region", """{"region":"us-west1","count":42}"""), ModernHeaders(name: "region", more: [("Mcp-Param-Region", "us-west1"), ("Mcp-Param-Count", "42.0")])).Run(endpoint);
    await ok.BodyAsync();
    Assert.Equal(200, ok.Status);

    var missing = TestExchange.Post(Call("region", """{"region":"us-west1"}"""), ModernHeaders(name: "region")).Run(endpoint);
    await ErrorOf(missing, 400, ProtocolErrorCodes.HeaderMismatch);

    var different = TestExchange.Post(Call("region", """{"region":"us-west1"}"""), ModernHeaders(name: "region", more: [("Mcp-Param-Region", "eu")])).Run(endpoint);
    await ErrorOf(different, 400, ProtocolErrorCodes.HeaderMismatch);

    var absentInBody = TestExchange.Post(Call("region", "{}"), ModernHeaders(name: "region")).Run(endpoint);
    await absentInBody.BodyAsync();
    Assert.Equal(200, absentInBody.Status);

    var control = TestExchange.Post(Call("region", """{"region":"x"}"""), ModernHeaders(name: "region", more: [("Mcp-Param-Region", "x\u0001y")])).Run(endpoint);
    await ErrorOf(control, 400, ProtocolErrorCodes.HeaderMismatch);
  }

  [Fact]
  public async Task A_modern_request_without_metadata_is_rejected_with_400()
  {
    var (_, endpoint) = Create();
    var exchange = TestExchange.Post("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", ("MCP-Protocol-Version", "2026-07-28"), ("Mcp-Method", "ping")).Run(endpoint);

    await exchange.BodyAsync();

    Assert.True(exchange.Status is 400 or 404);
  }

  [Fact]
  public async Task Get_and_delete_without_a_session_are_405()
  {
    var (_, endpoint) = Create();
    var get = new TestExchange("GET", headers: ("Accept", "text/event-stream")).Run(endpoint);
    var delete = new TestExchange("DELETE").Run(endpoint);

    await get.BodyAsync();
    await delete.BodyAsync();

    Assert.Equal(405, get.Status);
    Assert.Equal(405, delete.Status);
  }

  [Fact]
  public async Task A_stray_session_id_on_a_modern_request_is_ignored_and_not_echoed()
  {
    var (_, endpoint) = Create();
    var exchange = TestExchange.Post(Call("echo"), ModernHeaders(more: ("Mcp-Session-Id", "nonsense"))).Run(endpoint);

    await exchange.BodyAsync();

    Assert.Equal(200, exchange.Status);
    Assert.False(exchange.ResponseHeaders.Contains("Mcp-Session-Id"));
  }

  [Fact]
  public async Task Notifications_are_accepted_with_202_and_no_body()
  {
    var (_, endpoint) = Create();
    var exchange = TestExchange.Post("""{"jsonrpc":"2.0","method":"notifications/something"}""", ModernHeaders("notifications/something", null)).Run(endpoint);

    Assert.Equal(string.Empty, await exchange.BodyAsync());
    Assert.Equal(202, exchange.Status);
  }

  [Fact]
  public async Task A_modern_progress_notification_switches_the_response_to_an_event_stream()
  {
    var (server, endpoint) = Create();
    server.Tools.Add(
      new ToolDefinition("slow", "Reports progress.", ObjectSchema),
      async (_, ctx) =>
      {
        await ctx.ReportProgressAsync(1, 2, "half");
        return CallToolResult.FromText("done");
      });
    var exchange = TestExchange.Post(Call("slow", extraMeta: ",\"progressToken\":\"p1\""), ModernHeaders(name: "slow")).Run(endpoint);

    var events = exchange.Events();
    var first = await events.NextAsync();
    var second = await events.NextAsync();
    var end = await events.NextAsync();

    Assert.Equal("text/event-stream", exchange.ResponseHeaders.GetFirst("Content-Type"));
    Assert.Equal("no", exchange.ResponseHeaders.GetFirst("X-Accel-Buffering"));
    Assert.Equal("notifications/progress", JsonDocument.Parse(first!.Data).RootElement.GetProperty("method").GetString());
    Assert.Equal("done", JsonDocument.Parse(second!.Data).RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    Assert.Null(first.Id);
    Assert.Null(end);
  }

  [Fact]
  public async Task A_client_that_accepts_only_json_gets_the_result_without_the_notifications()
  {
    var (server, endpoint) = Create();
    server.Tools.Add(new ToolDefinition("slow", "Reports progress.", ObjectSchema), async (_, ctx) =>
    {
      await ctx.ReportProgressAsync(1);
      return CallToolResult.FromText("done");
    });
    var bytes = Encoding.UTF8.GetBytes(Call("slow", extraMeta: ",\"progressToken\":\"p1\""));
    var exchange = new TestExchange("POST", new System.IO.MemoryStream(bytes), bytes.Length, [("Content-Type", "application/json"), ("Accept", "application/json"), .. ModernHeaders(name: "slow")]).Run(endpoint);

    var body = await exchange.JsonAsync();

    Assert.Equal("application/json", exchange.ResponseHeaders.GetFirst("Content-Type"));
    Assert.Equal("done", body.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
  }

  [Fact]
  public async Task Closing_a_modern_response_stream_cancels_the_request()
  {
    var (server, endpoint) = Create();
    var cancelled = new TaskCompletionSource();
    var started = new TaskCompletionSource();
    server.Tools.Add(new ToolDefinition("hang", "Waits.", ObjectSchema), async (_, ctx) =>
    {
      await ctx.ReportProgressAsync(1);
      started.TrySetResult();
      try
      {
        await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
      }
      catch (OperationCanceledException)
      {
        cancelled.TrySetResult();
        throw;
      }

      return CallToolResult.FromText("never");
    });
    var exchange = TestExchange.Post(Call("hang", extraMeta: ",\"progressToken\":\"p\""), ModernHeaders(name: "hang")).Run(endpoint);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    exchange.Disconnect();

    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    await exchange.Completed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
  }

  // ---- hostile input ---------------------------------------------------------------------------------------------------------

  [Theory]
  [InlineData("not json")]
  [InlineData("")]
  [InlineData("[]")]
  [InlineData("null")]
  [InlineData("123")]
  [InlineData("{\"jsonrpc\":\"1.0\",\"id\":1,\"method\":\"x\"}")]
  [InlineData("{\"jsonrpc\":\"2.0\",\"id\":{},\"method\":\"x\"}")]
  public async Task Malformed_bodies_get_a_400_and_never_throw(string body)
  {
    var (_, endpoint) = Create();
    var exchange = TestExchange.Post(body, ModernHeaders()).Run(endpoint);

    var text = await exchange.BodyAsync();

    Assert.Equal(400, exchange.Status);
    Assert.Equal(JsonValueKind.Object, JsonDocument.Parse(text).RootElement.ValueKind);
  }

  [Fact]
  public async Task A_deeply_nested_body_is_a_parse_error_not_a_crash()
  {
    var (_, endpoint) = Create();
    var body = string.Concat(Enumerable.Repeat("[", 5000)) + string.Concat(Enumerable.Repeat("]", 5000));
    var exchange = TestExchange.Post(body, ModernHeaders()).Run(endpoint);

    await exchange.BodyAsync();

    Assert.Equal(400, exchange.Status);
  }

  [Fact]
  public async Task A_body_declared_larger_than_the_limit_gets_413_without_being_read()
  {
    var (_, endpoint) = Create(new StreamableHttpOptions { MaxRequestBodyBytes = 100 });
    var exchange = new TestExchange("POST", new System.IO.MemoryStream(new byte[10]), 1_000_000, ("Content-Type", "application/json"), ("Accept", "application/json")).Run(endpoint);

    await exchange.BodyAsync();

    Assert.Equal(413, exchange.Status);
  }

  [Fact]
  public async Task A_chunked_body_that_outgrows_the_limit_gets_413()
  {
    var (_, endpoint) = Create(new StreamableHttpOptions { MaxRequestBodyBytes = 100 });
    var exchange = new TestExchange("POST", new System.IO.MemoryStream(Enumerable.Repeat((byte)' ', 5000).ToArray()), null, ("Content-Type", "application/json"), ("Accept", "application/json")).Run(endpoint);

    await exchange.BodyAsync();

    Assert.Equal(413, exchange.Status);
  }

  [Fact]
  public async Task A_body_that_trickles_in_too_slowly_gets_408()
  {
    var clock = new ManualTimeProvider();
    var (_, endpoint) = Create(new StreamableHttpOptions { BodyReadTimeout = TimeSpan.FromSeconds(30) }, clock);
    var exchange = new TestExchange("POST", new StallingStream(), null, ("Content-Type", "application/json"), ("Accept", "application/json")).Run(endpoint);
    await Task.Delay(200, TestContext.Current.CancellationToken);

    clock.Advance(TimeSpan.FromSeconds(31));
    await exchange.BodyAsync();

    Assert.Equal(408, exchange.Status);
  }

  [Theory]
  [InlineData("text/plain")]
  [InlineData("application/xml")]
  public async Task A_body_that_is_not_json_content_gets_415(string contentType)
  {
    var (_, endpoint) = Create();
    var exchange = new TestExchange("POST", new System.IO.MemoryStream([(byte)'{', (byte)'}']), 2, ("Content-Type", contentType), ("Accept", "application/json")).Run(endpoint);

    await exchange.BodyAsync();

    Assert.Equal(415, exchange.Status);
  }

  [Theory]
  [InlineData("http://evil.example")]
  [InlineData("https://evil.example:3000")]
  [InlineData("null")]
  [InlineData("http://localhost.evil.example")]
  [InlineData("http://127.0.0.1.evil.example")]
  [InlineData("file://")]
  [InlineData("http://localhost/")]
  [InlineData("http://localhost/path")]
  public async Task A_foreign_origin_is_refused_with_403(string origin)
  {
    var (_, endpoint) = Create();
    var exchange = TestExchange.Post(Call("echo"), [.. ModernHeaders(), ("Origin", origin)]).Run(endpoint);

    await ErrorOf(exchange, 403, JsonRpcErrorCodes.InvalidRequest);
  }

  [Theory]
  [InlineData("http://localhost:3000")]
  [InlineData("http://127.0.0.1:8080")]
  [InlineData("http://[::1]:8080")]
  [InlineData("https://LOCALHOST")]
  public async Task A_loopback_origin_is_allowed(string origin)
  {
    var (_, endpoint) = Create();
    var exchange = TestExchange.Post(Call("echo"), [.. ModernHeaders(), ("Origin", origin)]).Run(endpoint);

    await exchange.BodyAsync();

    Assert.Equal(200, exchange.Status);
  }

  [Fact]
  public async Task An_allow_listed_origin_is_accepted_and_a_second_origin_header_is_not()
  {
    var (_, endpoint) = Create(new StreamableHttpOptions { AllowedOrigins = ["https://app.example.com"] });

    var allowed = TestExchange.Post(Call("echo"), [.. ModernHeaders(), ("Origin", "https://APP.example.com")]).Run(endpoint);
    await allowed.BodyAsync();
    Assert.Equal(200, allowed.Status);

    var twice = TestExchange.Post(Call("echo"), [.. ModernHeaders(), ("Origin", "https://app.example.com"), ("Origin", "http://evil.example")]).Run(endpoint);
    await twice.BodyAsync();
    Assert.Equal(403, twice.Status);
  }

  [Theory]
  [InlineData("evil.example")]
  [InlineData("evil.example:8080")]
  [InlineData("localhost.evil.example")]
  public async Task A_host_the_server_does_not_serve_is_refused_with_403(string host)
  {
    var (_, endpoint) = Create();
    var exchange = TestExchange.Post(Call("echo"), [.. ModernHeaders(), ("Host", host)]).Run(endpoint);

    await ErrorOf(exchange, 403, JsonRpcErrorCodes.InvalidRequest);
  }

  [Theory]
  [InlineData("localhost:3000")]
  [InlineData("127.0.0.1:47300")]
  [InlineData("[::1]:47300")]
  [InlineData("app.example.com")]
  [InlineData("app.example.com:9000")]
  public async Task Loopback_and_configured_hosts_are_served(string host)
  {
    var (_, endpoint) = Create(new StreamableHttpOptions { AllowedHosts = ["app.example.com"] });
    var exchange = TestExchange.Post(Call("echo"), [.. ModernHeaders(), ("Host", host)]).Run(endpoint);

    await exchange.BodyAsync();

    Assert.Equal(200, exchange.Status);
  }

  [Fact]
  public async Task The_host_check_covers_get_too_because_a_rebound_same_origin_get_has_no_origin_header()
  {
    var (_, endpoint) = Create();
    var exchange = new TestExchange("GET", headers: [("Accept", "text/event-stream"), ("Host", "evil.example"), ("Mcp-Session-Id", "x")]).Run(endpoint);

    await exchange.BodyAsync();

    Assert.Equal(403, exchange.Status);
  }

  [Fact]
  public async Task Unknown_methods_get_405_with_an_allow_header()
  {
    var (_, endpoint) = Create();
    var exchange = new TestExchange("PUT").Run(endpoint);

    await exchange.BodyAsync();

    Assert.Equal(405, exchange.Status);
    Assert.Equal("POST, GET, DELETE", exchange.ResponseHeaders.GetFirst("Allow"));
  }

  // ---- legacy sessions -------------------------------------------------------------------------------------------------------

  private const string Initialize = """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""";

  private static async Task<string> OpenSessionAsync(StreamableHttpEndpoint endpoint)
  {
    var exchange = TestExchange.Post(Initialize).Run(endpoint);
    await exchange.BodyAsync();
    Assert.Equal(200, exchange.Status);
    return exchange.ResponseHeaders.GetFirst("Mcp-Session-Id")!;
  }

  private static string Respond(JsonElement id, string result) => "{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":" + result + "}";

  private static string LegacyCall(string tool, int id = 1) =>
    $$$$"""{"jsonrpc":"2.0","id":{{{{id}}}},"method":"tools/call","params":{"name":"{{{{tool}}}}","arguments":{}}}""";

  [Fact]
  public async Task Initialize_creates_a_session_whose_id_is_visible_ascii()
  {
    var (_, endpoint) = Create();

    var id = await OpenSessionAsync(endpoint);

    Assert.Equal(1, endpoint.SessionCount);
    Assert.All(id, c => Assert.InRange(c, '\x21', '\x7e'));
    Assert.True(id.Length >= 32);
  }

  [Fact]
  public async Task Two_sessions_get_different_ids()
  {
    var (_, endpoint) = Create();

    Assert.NotEqual(await OpenSessionAsync(endpoint), await OpenSessionAsync(endpoint));
  }

  [Fact]
  public async Task A_legacy_request_needs_its_session_id()
  {
    var (_, endpoint) = Create();
    var id = await OpenSessionAsync(endpoint);

    var with = TestExchange.Post(LegacyCall("echo"), ("Mcp-Session-Id", id), ("MCP-Protocol-Version", "2025-11-25")).Run(endpoint);
    await with.BodyAsync();
    var without = TestExchange.Post(LegacyCall("echo"), ("MCP-Protocol-Version", "2025-11-25")).Run(endpoint);
    await without.BodyAsync();
    var unknown = TestExchange.Post(LegacyCall("echo"), ("Mcp-Session-Id", "0123456789abcdef")).Run(endpoint);
    await unknown.BodyAsync();

    Assert.Equal(200, with.Status);
    Assert.True(without.Status is 400 or 404);
    Assert.Equal(404, unknown.Status);
  }

  [Fact]
  public async Task Delete_ends_the_session()
  {
    var (_, endpoint) = Create();
    var id = await OpenSessionAsync(endpoint);

    var delete = new TestExchange("DELETE", headers: ("Mcp-Session-Id", id)).Run(endpoint);
    await delete.BodyAsync();
    var after = TestExchange.Post(LegacyCall("echo"), ("Mcp-Session-Id", id)).Run(endpoint);
    await after.BodyAsync();
    var again = new TestExchange("DELETE", headers: ("Mcp-Session-Id", id)).Run(endpoint);
    await again.BodyAsync();

    Assert.Equal(200, delete.Status);
    Assert.Equal(404, after.Status);
    Assert.Equal(404, again.Status);
    Assert.Equal(0, endpoint.SessionCount);
  }

  [Fact]
  public async Task An_unsupported_version_header_on_a_legacy_request_is_400()
  {
    var (_, endpoint) = Create();
    var id = await OpenSessionAsync(endpoint);

    var exchange = TestExchange.Post(LegacyCall("echo"), ("Mcp-Session-Id", id), ("MCP-Protocol-Version", "1999-01-01")).Run(endpoint);
    await exchange.BodyAsync();

    Assert.Equal(400, exchange.Status);
  }

  [Fact]
  public async Task A_missing_version_header_on_a_legacy_session_is_tolerated()
  {
    var (_, endpoint) = Create();
    var id = await OpenSessionAsync(endpoint);

    var exchange = TestExchange.Post(LegacyCall("echo"), ("Mcp-Session-Id", id)).Run(endpoint);
    await exchange.BodyAsync();

    Assert.Equal(200, exchange.Status);
  }

  [Fact]
  public async Task Idle_sessions_expire_and_busy_ones_do_not()
  {
    var clock = new ManualTimeProvider();
    var (server, endpoint) = Create(new StreamableHttpOptions { SessionIdleTimeout = TimeSpan.FromMinutes(10) }, clock);
    var release = new TaskCompletionSource();
    server.Tools.Add(new ToolDefinition("wait", "Waits.", ObjectSchema), async (_, _) =>
    {
      await release.Task;
      return CallToolResult.FromText("x");
    });
    var idle = await OpenSessionAsync(endpoint);
    var busy = await OpenSessionAsync(endpoint);
    var running = TestExchange.Post(LegacyCall("wait"), ("Mcp-Session-Id", busy)).Run(endpoint);
    await Task.Delay(100, TestContext.Current.CancellationToken);

    clock.Advance(TimeSpan.FromMinutes(11));
    await OpenSessionAsync(endpoint);

    Assert.Equal(2, endpoint.SessionCount);
    var gone = TestExchange.Post(LegacyCall("echo"), ("Mcp-Session-Id", idle)).Run(endpoint);
    await gone.BodyAsync();
    Assert.Equal(404, gone.Status);
    release.SetResult();
    await running.BodyAsync();
  }

  [Fact]
  public async Task Sessions_are_capped()
  {
    var (_, endpoint) = Create(new StreamableHttpOptions { MaxSessions = 2 });
    await OpenSessionAsync(endpoint);
    await OpenSessionAsync(endpoint);

    var third = TestExchange.Post(Initialize).Run(endpoint);
    await third.BodyAsync();

    Assert.Equal(503, third.Status);
    Assert.Equal(2, endpoint.SessionCount);
  }

  // ---- legacy streams --------------------------------------------------------------------------------------------------------

  [Fact]
  public async Task A_legacy_server_request_goes_out_on_the_post_stream_and_the_answer_comes_back_by_post()
  {
    var (server, endpoint) = Create();
    server.Tools.Add(new ToolDefinition("ask", "Asks.", ObjectSchema), async (_, ctx) =>
    {
      var yes = await ctx.ConfirmAsync("Sure?");
      return CallToolResult.FromText(yes ? "yes" : "no");
    });
    var session = await OpenSessionAsync(endpoint);
    var call = TestExchange.Post(LegacyCall("ask"), ("Mcp-Session-Id", session)).Run(endpoint);
    var events = call.Events();

    var priming = await events.NextAsync();
    var request = await events.NextAsync();
    var asked = JsonDocument.Parse(request!.Data).RootElement;
    Assert.Equal("elicitation/create", asked.GetProperty("method").GetString());
    var answer = Respond(asked.GetProperty("id"), """{"action":"accept","content":{"confirm":true}}""");
    var post = TestExchange.Post(answer, ("Mcp-Session-Id", session)).Run(endpoint);
    await post.BodyAsync();
    var final = await events.NextAsync();

    Assert.Equal(202, post.Status);
    Assert.NotNull(priming!.Id);
    Assert.Equal(string.Empty, priming.Data);
    Assert.NotNull(request.Id);
    Assert.Equal("yes", JsonDocument.Parse(final!.Data).RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    Assert.Null(await events.NextAsync());
  }

  [Fact]
  public async Task Event_ids_are_unique_and_there_is_no_priming_event_before_2025_11_25()
  {
    var (server, endpoint) = Create(legacyVersion: ProtocolVersions.V20250618);
    server.Tools.Add(new ToolDefinition("ask", "Asks.", ObjectSchema), async (_, ctx) => CallToolResult.FromText(await ctx.ConfirmAsync("?") ? "y" : "n"));
    var session = await OpenSessionAsync(endpoint);
    var call = TestExchange.Post(LegacyCall("ask"), ("Mcp-Session-Id", session)).Run(endpoint);
    var events = call.Events();

    var request = await events.NextAsync();
    var asked = JsonDocument.Parse(request!.Data).RootElement;
    await TestExchange.Post(Respond(asked.GetProperty("id"), """{"action":"decline"}"""), ("Mcp-Session-Id", session)).Run(endpoint).BodyAsync();
    var final = await events.NextAsync();

    Assert.Equal("elicitation/create", asked.GetProperty("method").GetString());
    Assert.NotNull(request.Id);
    Assert.NotNull(final!.Id);
    Assert.NotEqual(request.Id, final.Id);
  }

  [Fact]
  public async Task A_response_nobody_is_waiting_for_is_a_400()
  {
    var (_, endpoint) = Create();
    var session = await OpenSessionAsync(endpoint);

    var exchange = TestExchange.Post("""{"jsonrpc":"2.0","id":99,"result":{}}""", ("Mcp-Session-Id", session)).Run(endpoint);
    await exchange.BodyAsync();

    Assert.Equal(400, exchange.Status);
  }

  [Fact]
  public async Task A_response_on_a_modern_request_is_a_400()
  {
    var (_, endpoint) = Create();
    var exchange = TestExchange.Post("""{"jsonrpc":"2.0","id":99,"result":{}}""", ("MCP-Protocol-Version", "2026-07-28")).Run(endpoint);
    await exchange.BodyAsync();

    Assert.Equal(400, exchange.Status);
  }

  [Fact]
  public async Task The_connection_can_be_closed_mid_request_and_the_answer_fetched_with_last_event_id()
  {
    var (server, endpoint) = Create();
    var proceed = new TaskCompletionSource();
    server.Tools.Add(new ToolDefinition("poll", "Closes its stream.", ObjectSchema), async (_, _) =>
    {
      await HttpRequestScope.Current!.CloseSseConnectionAsync(TimeSpan.FromMilliseconds(250));
      await proceed.Task;
      return CallToolResult.FromText("later");
    });
    var session = await OpenSessionAsync(endpoint);
    var call = TestExchange.Post(LegacyCall("poll"), ("Mcp-Session-Id", session)).Run(endpoint);
    var events = call.Events();

    var priming = await events.NextAsync();
    var retry = await events.NextAsync();
    var end = await events.NextAsync();
    await call.Completed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    proceed.SetResult();
    await Task.Delay(100, TestContext.Current.CancellationToken);
    var resume = new TestExchange("GET", headers: [("Accept", "text/event-stream"), ("Mcp-Session-Id", session), ("Last-Event-ID", priming!.Id!)]).Run(endpoint);
    var replayed = await resume.Events().NextAsync();

    Assert.Equal(250, retry!.Retry);
    Assert.Null(end);
    Assert.Equal("later", JsonDocument.Parse(replayed!.Data).RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    await resume.Completed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
  }

  [Fact]
  public async Task Resuming_while_the_request_is_still_running_continues_live()
  {
    var (server, endpoint) = Create();
    var proceed = new TaskCompletionSource();
    server.Tools.Add(new ToolDefinition("poll", "Closes its stream.", ObjectSchema), async (_, _) =>
    {
      await HttpRequestScope.Current!.CloseSseConnectionAsync(TimeSpan.Zero);
      await proceed.Task;
      return CallToolResult.FromText("live");
    });
    var session = await OpenSessionAsync(endpoint);
    var call = TestExchange.Post(LegacyCall("poll"), ("Mcp-Session-Id", session)).Run(endpoint);
    var priming = await call.Events().NextAsync();
    var resume = new TestExchange("GET", headers: [("Accept", "text/event-stream"), ("Mcp-Session-Id", session), ("Last-Event-ID", priming!.Id!)]).Run(endpoint);
    var events = resume.Events();
    await resume.HeadersSent.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    // The result reaches the resumed stream whether it was attached before or after this point: live, or replayed from the log.
    proceed.SetResult();
    var live = await events.NextAsync();

    Assert.Equal("live", JsonDocument.Parse(live!.Data).RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
  }

  [Fact]
  public async Task A_close_the_handler_asked_for_does_not_end_a_connection_that_resumed_in_the_meantime()
  {
    // The priming event is enough for a client to reconnect, which can happen before the handler gets to closing its connection.
    var (server, endpoint) = Create();
    var resumed = new TaskCompletionSource();
    var proceed = new TaskCompletionSource();
    server.Tools.Add(new ToolDefinition("stale", "Reports progress, then closes.", ObjectSchema), async (_, ctx) =>
    {
      await ctx.ReportProgressAsync(1);
      await resumed.Task;
      await HttpRequestScope.Current!.CloseSseConnectionAsync(TimeSpan.Zero);
      await proceed.Task;
      return CallToolResult.FromText("live");
    });
    var session = await OpenSessionAsync(endpoint);
    var call = TestExchange.Post(LegacyCall("stale").Replace("\"arguments\":{}", "\"arguments\":{},\"_meta\":{\"progressToken\":\"t\"}", StringComparison.Ordinal), ("Mcp-Session-Id", session)).Run(endpoint);
    var priming = await call.Events().NextAsync();
    var resume = new TestExchange("GET", headers: [("Accept", "text/event-stream"), ("Mcp-Session-Id", session), ("Last-Event-ID", priming!.Id!)]).Run(endpoint);
    var events = resume.Events();

    // The replayed progress event proves the resumed connection is attached; only then may the handler close its own.
    var progress = await events.NextAsync();
    Assert.Contains("notifications/progress", progress!.Data, StringComparison.Ordinal);
    resumed.SetResult();
    proceed.SetResult();
    var live = await events.NextAsync();

    Assert.Equal("live", JsonDocument.Parse(live!.Data).RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
  }

  [Fact]
  public async Task A_last_event_id_of_a_stream_the_session_does_not_have_is_a_400()
  {
    var (_, endpoint) = Create();
    var session = await OpenSessionAsync(endpoint);

    var exchange = new TestExchange("GET", headers: [("Accept", "text/event-stream"), ("Mcp-Session-Id", session), ("Last-Event-ID", "99-1")]).Run(endpoint);
    await exchange.BodyAsync();

    Assert.Equal(400, exchange.Status);
  }

  [Fact]
  public async Task Resuming_replays_only_events_of_the_stream_the_id_names_and_only_those_after_it()
  {
    var (server, endpoint) = Create();
    server.Tools.Add(new ToolDefinition("two", "Sends two notifications.", ObjectSchema), async (_, ctx) =>
    {
      await ctx.ReportProgressAsync(1);
      await ctx.ReportProgressAsync(2);
      return CallToolResult.FromText("end");
    });
    server.Tools.Add(new ToolDefinition("other", "Another stream.", ObjectSchema), async (_, ctx) =>
    {
      await ctx.ReportProgressAsync(7);
      return CallToolResult.FromText("other");
    });
    var session = await OpenSessionAsync(endpoint);
    var meta = ",\"_meta\":{\"progressToken\":\"t\"}";
    var first = TestExchange.Post(LegacyCall("two").Replace("\"arguments\":{}", "\"arguments\":{}" + meta, StringComparison.Ordinal), ("Mcp-Session-Id", session)).Run(endpoint);
    var firstEvents = first.Events();
    var priming = await firstEvents.NextAsync();
    var p1 = await firstEvents.NextAsync();
    var p2 = await firstEvents.NextAsync();
    var result = await firstEvents.NextAsync();
    await first.Completed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    var second = TestExchange.Post(LegacyCall("other", 2).Replace("\"arguments\":{}", "\"arguments\":{}" + meta, StringComparison.Ordinal), ("Mcp-Session-Id", session)).Run(endpoint);
    await second.BodyAsync();

    var resume = new TestExchange("GET", headers: [("Accept", "text/event-stream"), ("Mcp-Session-Id", session), ("Last-Event-ID", p1!.Id!)]).Run(endpoint);
    var replay = resume.Events();
    var r1 = await replay.NextAsync();
    var r2 = await replay.NextAsync();
    var r3 = await replay.NextAsync();

    Assert.NotNull(priming);
    Assert.Equal(p2!.Data, r1!.Data);
    Assert.Equal(result!.Data, r2!.Data);
    Assert.Null(r3);
  }

  [Fact]
  public async Task The_replay_log_is_bounded()
  {
    var (server, endpoint) = Create(new StreamableHttpOptions { MaxReplayEvents = 3 });
    server.Tools.Add(new ToolDefinition("many", "Sends many.", ObjectSchema), async (_, ctx) =>
    {
      for (var i = 0; i < 10; i++)
      {
        await ctx.ReportProgressAsync(i);
      }

      return CallToolResult.FromText("end");
    });
    var session = await OpenSessionAsync(endpoint);
    var call = TestExchange.Post(LegacyCall("many").Replace("\"arguments\":{}", "\"arguments\":{},\"_meta\":{\"progressToken\":\"t\"}", StringComparison.Ordinal), ("Mcp-Session-Id", session)).Run(endpoint);
    var events = call.Events();
    var priming = await events.NextAsync();
    while (await events.NextAsync() is not null)
    {
    }

    var resume = new TestExchange("GET", headers: [("Accept", "text/event-stream"), ("Mcp-Session-Id", session), ("Last-Event-ID", priming!.Id!)]).Run(endpoint);
    var count = 0;
    var replay = resume.Events();
    while (await replay.NextAsync() is not null)
    {
      count++;
    }

    Assert.Equal(3, count);
  }

  [Fact]
  public async Task The_standalone_get_stream_receives_session_notifications_and_keep_alive_comments()
  {
    var clock = new ManualTimeProvider();
    // The real legacy codec attaches the session for list-changed notifications.
    var server = new ContextServer("real", "1", new ContextServerOptions { TimeProvider = clock });
    server.Tools.Add(new ToolDefinition("echo", "Echoes.", ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("ok")));
    using var endpoint = new StreamableHttpEndpoint(server, new StreamableHttpOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
    var session = await OpenSessionAsync(endpoint);
    var get = new TestExchange("GET", headers: [("Accept", "text/event-stream"), ("Mcp-Session-Id", session)]).Run(endpoint);
    await get.HeadersSent;
    var events = get.Events();

    server.Tools.Add(new ToolDefinition("late", "Added later.", ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("x")));
    var note = await events.NextAsync();
    clock.Advance(TimeSpan.FromSeconds(16));
    var comment = await events.NextAsync();

    Assert.Equal("text/event-stream", get.ResponseHeaders.GetFirst("Content-Type"));
    Assert.Equal("notifications/tools/list_changed", JsonDocument.Parse(note!.Data).RootElement.GetProperty("method").GetString());
    Assert.True(comment!.IsComment);
    get.Disconnect();
    await get.Completed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
  }

  [Fact]
  public async Task Deleting_a_session_ends_its_open_get_stream()
  {
    var (_, endpoint) = Create();
    var session = await OpenSessionAsync(endpoint);
    var get = new TestExchange("GET", headers: [("Accept", "text/event-stream"), ("Mcp-Session-Id", session)]).Run(endpoint);
    await get.HeadersSent;

    await new TestExchange("DELETE", headers: ("Mcp-Session-Id", session)).Run(endpoint).BodyAsync();

    await get.Completed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
  }

  [Fact]
  public async Task Disposing_the_endpoint_ends_streams_and_cancels_handlers()
  {
    var (server, endpoint) = Create();
    var cancelled = new TaskCompletionSource();
    server.Tools.Add(new ToolDefinition("hang", "Waits.", ObjectSchema), async (_, ctx) =>
    {
      try
      {
        await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
      }
      catch (OperationCanceledException)
      {
        cancelled.TrySetResult();
        throw;
      }

      return CallToolResult.FromText("never");
    });
    var exchange = TestExchange.Post(Call("hang"), ModernHeaders(name: "hang")).Run(endpoint);
    await Task.Delay(100, TestContext.Current.CancellationToken);

    endpoint.Dispose();

    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    await exchange.Completed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
  }

  [Fact]
  public async Task A_batch_is_accepted_only_where_the_codec_allows_it()
  {
    var (_, endpoint) = Create(legacyVersion: ProtocolVersions.V20250326);
    var session = await OpenSessionAsync(endpoint);
    var batch = $"[{LegacyCall("echo", 1)},{LegacyCall("echo", 2)}]";

    var ok = TestExchange.Post(batch, ("Mcp-Session-Id", session)).Run(endpoint);
    var body = await ok.JsonAsync();
    Assert.Equal(200, ok.Status);
    Assert.Equal(2, body.GetArrayLength());

    var (_, strict) = Create(legacyVersion: ProtocolVersions.V20251125);
    var strictSession = await OpenSessionAsync(strict);
    var rejected = TestExchange.Post(batch, ("Mcp-Session-Id", strictSession)).Run(strict);
    await rejected.BodyAsync();
    Assert.Equal(400, rejected.Status);

    var noSession = TestExchange.Post(batch).Run(endpoint);
    await noSession.BodyAsync();
    Assert.Equal(400, noSession.Status);
  }

  [Fact]
  public async Task A_cancelled_notification_stops_the_running_legacy_handler()
  {
    var (server, endpoint) = Create();
    var cancelled = new TaskCompletionSource();
    var started = new TaskCompletionSource();
    server.Tools.Add(new ToolDefinition("hang", "Waits.", ObjectSchema), async (_, ctx) =>
    {
      started.TrySetResult();
      try
      {
        await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
      }
      catch (OperationCanceledException)
      {
        cancelled.TrySetResult();
        throw;
      }

      return CallToolResult.FromText("never");
    });
    var session = await OpenSessionAsync(endpoint);
    var call = TestExchange.Post(LegacyCall("hang", 7), ("Mcp-Session-Id", session)).Run(endpoint);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    var cancel = TestExchange.Post("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}""", ("Mcp-Session-Id", session)).Run(endpoint);
    await cancel.BodyAsync();

    Assert.Equal(202, cancel.Status);
    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    await call.Completed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
  }

  [Fact]
  public async Task A_legacy_disconnect_does_not_cancel_the_handler()
  {
    var (server, endpoint) = Create();
    var proceed = new TaskCompletionSource();
    var observed = new TaskCompletionSource<bool>();
    server.Tools.Add(new ToolDefinition("slow", "Slow.", ObjectSchema), async (_, ctx) =>
    {
      await ctx.ReportProgressAsync(1);
      await proceed.Task;
      observed.TrySetResult(ctx.CancellationToken.IsCancellationRequested);
      return CallToolResult.FromText("done");
    });
    var session = await OpenSessionAsync(endpoint);
    var call = TestExchange.Post(LegacyCall("slow").Replace("\"arguments\":{}", "\"arguments\":{},\"_meta\":{\"progressToken\":\"t\"}", StringComparison.Ordinal), ("Mcp-Session-Id", session)).Run(endpoint);
    var events = call.Events();
    await events.NextAsync();
    await events.NextAsync();

    call.Disconnect();
    await call.Completed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    proceed.SetResult();

    Assert.False(await observed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
  }

  /// <summary>A request body that never delivers a byte, like a slowloris client.</summary>
  private sealed class StallingStream : System.IO.Stream
  {
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
      get => throw new NotSupportedException();
      set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
      await Task.Delay(Timeout.Infinite, cancellationToken);
      return 0;
    }

    public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
