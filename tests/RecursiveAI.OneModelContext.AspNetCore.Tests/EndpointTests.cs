using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AspNetCoreServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RecursiveAI.OneModelContext.AspNetCore;
using RecursiveAI.OneModelContext.Protocol;
using Xunit;

namespace RecursiveAI.OneModelContext.AspNetCore.Tests;

/// <summary>The adapter on a real Kestrel listener bound to a loopback port.</summary>
public sealed class EndpointTests : IAsyncDisposable
{
  private const string Modern = "2026-07-28";
  private readonly List<WebApplication> _apps = [];
  private readonly HttpClient _http = new();

  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => _http.SendAsync(request, Ct);

  public async ValueTask DisposeAsync()
  {
    _http.Dispose();
    foreach (var app in _apps)
    {
      await app.StopAsync(Ct);
      await app.DisposeAsync();
    }
  }

  private async Task<Uri> StartAsync(Action<WebApplication>? beforeMap = null)
  {
    var app = Site.Build(["--urls", "http://127.0.0.1:0"], beforeMap);
    _apps.Add(app);
    await app.StartAsync(Ct);
    return new Uri(app.Urls.First() + "/mcp");
  }

  private static HttpRequestMessage Post(Uri url, string json, params (string Name, string Value)[] headers)
  {
    var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
    foreach (var (name, value) in headers)
    {
      request.Headers.TryAddWithoutValidation(name, value);
    }

    return request;
  }

  private static string ModernCall(string tool, string arguments, int id = 1) =>
    "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + arguments + ",\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientInfo\":{\"name\":\"t\",\"version\":\"1\"},\"io.modelcontextprotocol/clientCapabilities\":{}}}}";

  private static (string, string)[] ModernHeaders(string tool) => [("MCP-Protocol-Version", Modern), ("Mcp-Method", "tools/call"), ("Mcp-Name", tool)];

  private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
  {
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
    return document.RootElement.Clone();
  }

  [Fact]
  public async Task A_modern_call_is_answered_through_the_pipeline()
  {
    var url = await StartAsync();

    using var response = await SendAsync(Post(url, ModernCall("greet", """{"name":"Ada"}"""), ModernHeaders("greet")));

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var body = await ReadJsonAsync(response);
    Assert.Equal("Hello, Ada!", body.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
  }

  [Fact]
  public async Task A_legacy_session_works_on_the_same_endpoint()
  {
    var url = await StartAsync();
    const string Initialize = """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""";

    using var init = await SendAsync(Post(url, Initialize));
    Assert.Equal(HttpStatusCode.OK, init.StatusCode);
    var session = init.Headers.GetValues("Mcp-Session-Id").Single();

    using var initialized = await SendAsync(Post(url, """{"jsonrpc":"2.0","method":"notifications/initialized"}""", ("Mcp-Session-Id", session), ("MCP-Protocol-Version", "2025-11-25")));
    Assert.Equal(HttpStatusCode.Accepted, initialized.StatusCode);

    var call = """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"add","arguments":{"a":2,"b":3}}}""";
    using var response = await SendAsync(Post(url, call, ("Mcp-Session-Id", session), ("MCP-Protocol-Version", "2025-11-25")));
    var body = await ReadJsonAsync(response);
    Assert.Equal("5", body.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
  }

  [Fact]
  public async Task Host_middleware_runs_before_the_protocol_handler()
  {
    var order = new List<string>();
    var url = await StartAsync(app => app.Use(async (context, next) =>
    {
      // A stand-in for an authentication gate: no key, no protocol handler.
      lock (order)
      {
        order.Add("gate");
      }

      if (context.Request.Headers["X-Api-Key"] != "let-me-in")
      {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
      }

      context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "ada")], "test"));
      await next();
    }));
    var call = ModernCall("greet", """{"name":"Ada"}""");

    using var denied = await SendAsync(Post(url, call, ModernHeaders("greet")));
    using var allowed = await SendAsync(Post(url, call, [.. ModernHeaders("greet"), ("X-Api-Key", "let-me-in")]));

    Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    Assert.Equal(["gate", "gate"], order);
  }

  [Fact]
  public async Task The_authenticated_caller_reaches_the_handler()
  {
    string? seen = null;
    var server = new ContextServer("p", "1");
    server.Tools.Add(
      new ToolDefinition("whoami", "Names the caller.", JsonDocument.Parse("""{"type":"object"}""").RootElement),
      (_, ctx) =>
      {
        seen = ctx.Principal?.Identity?.Name;
        return ValueTask.FromResult(CallToolResult.FromText("ok"));
      });
    var app = WebApplication.CreateSlimBuilder(["--urls", "http://127.0.0.1:0"]).Build();
    _apps.Add(app);
    app.Use((context, next) =>
    {
      context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "ada")], "test"));
      return next();
    });
    app.MapModelContext("/mcp", server);
    await app.StartAsync(Ct);

    using var response = await SendAsync(Post(new Uri(app.Urls.First() + "/mcp"), ModernCall("whoami", "{}"), ModernHeaders("whoami")));

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("ada", seen);
  }

  [Fact]
  public async Task A_foreign_origin_is_refused()
  {
    var url = await StartAsync();

    using var response = await SendAsync(Post(url, ModernCall("greet", """{"name":"x"}"""), [.. ModernHeaders("greet"), ("Origin", "https://evil.example")]));

    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
  }

  [Fact]
  public async Task A_header_that_disagrees_with_the_body_is_refused()
  {
    var url = await StartAsync();

    using var response = await SendAsync(Post(url, ModernCall("greet", """{"name":"x"}"""), ("MCP-Protocol-Version", Modern), ("Mcp-Method", "tools/call"), ("Mcp-Name", "add")));

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    var body = await ReadJsonAsync(response);
    Assert.Equal(-32020, body.GetProperty("error").GetProperty("code").GetInt32());
  }

  [Fact]
  public async Task Events_are_flushed_while_the_handler_is_still_running_and_a_disconnect_cancels_it()
  {
    var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var server = new ContextServer("s", "1");
    server.Tools.Add(
      new ToolDefinition("slow", "Reports progress and waits.", JsonDocument.Parse("""{"type":"object"}""").RootElement),
      async (_, ctx) =>
      {
        await ctx.ReportProgressAsync(1, 2, "half");
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
    var app = WebApplication.CreateSlimBuilder(["--urls", "http://127.0.0.1:0"]).Build();
    _apps.Add(app);
    app.MapModelContext("/mcp", server);
    await app.StartAsync(Ct);
    var url = new Uri(app.Urls.First() + "/mcp");
    var json = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"slow","arguments":{},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"t","version":"1"},"io.modelcontextprotocol/clientCapabilities":{},"progressToken":"p1"}}}""";

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    using var response = await _http.SendAsync(Post(url, json, ModernHeaders("slow")), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
    Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
    using var reader = new StreamReader(stream);
    string? line;
    var sawProgress = false;
    while ((line = await reader.ReadLineAsync(timeout.Token)) is not null)
    {
      if (line.Contains("notifications/progress", StringComparison.Ordinal))
      {
        sawProgress = true;
        break;
      }
    }

    Assert.True(sawProgress);

    // Hanging up cancels the request's token.
    response.Dispose();
    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
  }
}
