using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using RecursiveAI.OneModelContext.Auth;
using RecursiveAI.OneModelContext.Hosting;
using RecursiveAI.OneModelContext.Protocol;
using Xunit;

namespace RecursiveAI.OneModelContext.AspNetCore.Tests;

/// <summary>The authentication handler and <c>RequireAuthorization()</c> on a real Kestrel listener bound to a loopback port.</summary>
public sealed class AuthTests : IAsyncDisposable
{
  private const string ReadToken = "tok_reader_0123456789abcdef01234567";
  private const string WriteToken = "tok_writer_0123456789abcdef01234567";
  private readonly HttpClient _http = new();
  private WebApplication? _app;
  private string _base = string.Empty;

  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  public async ValueTask DisposeAsync()
  {
    _http.Dispose();
    if (_app is not null)
    {
      await _app.StopAsync(Ct);
      await _app.DisposeAsync();
    }
  }

  private async Task StartAsync(bool withHandler)
  {
    var builder = WebApplication.CreateSlimBuilder(["--urls", "http://127.0.0.1:0"]);
    var validator = new StaticTokenValidator()
      .Add(ReadToken, "reader", ["files:read"])
      .Add(WriteToken, "writer", ["files:read", "files:write"]);

    // The resource is the address clients use; Kestrel picks a free port, so the port is not known yet. A fixed name is enough for a
    // static-token validator, which has no audience, and for the metadata URL in challenges.
    var resourceServer = new ResourceServer(new ResourceServerOptions(new Uri("http://127.0.0.1/mcp"), validator)
    {
      AuthorizationServers = ["http://127.0.0.1:9"],
      ScopesSupported = ["files:read", "files:write"],
    });
    if (withHandler)
    {
      builder.Services.AddAuthentication(ResourceServerDefaults.AuthenticationScheme).AddResourceServer(resourceServer);
      builder.Services.AddAuthorization();
    }

    _app = builder.Build();
    if (withHandler)
    {
      _app.UseAuthentication();
      _app.UseAuthorization();
    }

    var server = new ContextServer("auth-sample", "1.0");
    var schema = JsonDocument.Parse("""{"type":"object"}""").RootElement;
    server.Tools.Add(new ToolDefinition("whoami", "Names the caller.", schema), (_, ctx) => ValueTask.FromResult(CallToolResult.FromText(ctx.Principal?.FindFirst("sub")?.Value ?? "nobody")));
    server.Tools.Add(new ToolDefinition("write", "Writes.", schema) { RequiredScopes = ["files:write"] }, (_, _) => ValueTask.FromResult(CallToolResult.FromText("written")));

    var endpoint = _app.MapModelContext("/mcp", server, new StreamableHttpOptions { ResourceServer = resourceServer });
    if (withHandler)
    {
      endpoint.RequireAuthorization();
    }

    await _app.StartAsync(Ct);
    _base = _app.Urls.First();
  }

  private HttpRequestMessage Call(string tool, string? token)
  {
    var json = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":{},\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientInfo\":{\"name\":\"t\",\"version\":\"1\"},\"io.modelcontextprotocol/clientCapabilities\":{}}}}";
    var request = new HttpRequestMessage(HttpMethod.Post, _base + "/mcp") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
    request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2026-07-28");
    request.Headers.TryAddWithoutValidation("Mcp-Method", "tools/call");
    request.Headers.TryAddWithoutValidation("Mcp-Name", tool);
    if (token is not null)
    {
      request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    return request;
  }

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task A_request_without_a_token_is_challenged_with_the_metadata_url(bool withHandler)
  {
    await StartAsync(withHandler);

    using var response = await _http.SendAsync(Call("whoami", null), Ct);

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    Assert.Equal("Bearer resource_metadata=\"http://127.0.0.1/.well-known/oauth-protected-resource/mcp\"", string.Join(", ", response.Headers.WwwAuthenticate));
  }

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task A_bad_token_gets_invalid_token(bool withHandler)
  {
    await StartAsync(withHandler);

    using var response = await _http.SendAsync(Call("whoami", "tok_unknown_0123456789abcdef0123456"), Ct);

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    var challenge = string.Join(", ", response.Headers.WwwAuthenticate);
    Assert.Contains("error=\"invalid_token\"", challenge, StringComparison.Ordinal);
    Assert.DoesNotContain("tok_unknown", challenge + await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task A_valid_token_reaches_the_tool_as_a_principal(bool withHandler)
  {
    await StartAsync(withHandler);

    using var response = await _http.SendAsync(Call("whoami", ReadToken), Ct);
    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("reader", body.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
  }

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task A_tool_scope_is_enforced_with_a_step_up_challenge(bool withHandler)
  {
    await StartAsync(withHandler);

    using var denied = await _http.SendAsync(Call("write", ReadToken), Ct);
    using var allowed = await _http.SendAsync(Call("write", WriteToken), Ct);

    Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    var challenge = string.Join(", ", denied.Headers.WwwAuthenticate);
    Assert.Contains("error=\"insufficient_scope\"", challenge, StringComparison.Ordinal);
    Assert.Contains("scope=\"files:write\"", challenge, StringComparison.Ordinal);
    Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
  }

  [Theory]
  [InlineData("/.well-known/oauth-protected-resource/mcp")]
  [InlineData("/.well-known/oauth-protected-resource")]
  public async Task The_metadata_is_served_without_a_token_beside_the_protected_endpoint(string path)
  {
    await StartAsync(withHandler: true);

    using var response = await _http.GetAsync(_base + path, Ct);
    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("http://127.0.0.1/mcp", doc.RootElement.GetProperty("resource").GetString());
    Assert.Equal("http://127.0.0.1:9", doc.RootElement.GetProperty("authorization_servers")[0].GetString());
  }
}
