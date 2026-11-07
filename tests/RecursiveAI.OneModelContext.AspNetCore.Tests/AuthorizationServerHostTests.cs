using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using RecursiveAI.OneModelContext.Auth;
using RecursiveAI.OneModelContext.Auth.Server;
using RecursiveAI.OneModelContext.Hosting;
using RecursiveAI.OneModelContext.Jose;
using RecursiveAI.OneModelContext.Protocol;
using Xunit;

namespace RecursiveAI.OneModelContext.AspNetCore.Tests;

/// <summary>The authorization server and the MCP endpoint it protects, on one real Kestrel listener bound to a loopback port.</summary>
public sealed class AuthorizationServerHostTests : IAsyncDisposable
{
  private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false });
  private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
  private WebApplication? _app;
  private AuthorizationServer? _as;
  private string _origin = string.Empty;

  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  public async ValueTask DisposeAsync()
  {
    _http.Dispose();
    _key.Dispose();
    _as?.Dispose();
    if (_app is not null)
    {
      await _app.StopAsync(Ct);
      await _app.DisposeAsync();
    }
  }

  private async Task StartAsync(Action<IEndpointConventionBuilder>? configure = null)
  {
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    _origin = "http://127.0.0.1:" + port;

    var builder = WebApplication.CreateSlimBuilder(["--urls", _origin]);
    _app = builder.Build();

    // The authorization server and the MCP endpoint share one origin, as a 2025-03-26 client expects.
    _as = new AuthorizationServer(new AuthorizationServerOptions
    {
      Issuer = new Uri(_origin),
      Resources = [_origin + "/mcp"],
      SigningKeys = [JsonWebSigningKey.FromECDsa(_key, "host-1")],
      ScopesSupported = ["files:read"],
      AuthorizeUser = (context, _) => ValueTask.FromResult(
        context.Exchange.Request.Headers.GetFirst("Cookie") == "session=alice"
          ? AuthorizationDecision.Approve("alice")
          : AuthorizationDecision.Redirect(new Uri(_origin + "/login"))),
      Store = new MemoryAuthorizationStore(),
    });
    await _as.Store.SaveClientAsync(new RegisteredClient { ClientId = "app", RedirectUris = ["http://127.0.0.1/cb"], CreatedAt = DateTimeOffset.UtcNow }, Ct);
    _app.MapAuthorizationServer(_as, configure);

    var resourceServer = new ResourceServer(new ResourceServerOptions(new Uri(_origin + "/mcp"), _as.CreateTokenValidator())
    {
      AuthorizationServers = [_as.Issuer],
    });
    var server = new ContextServer("host-test", "1");
    server.Tools.Add(
      new ToolDefinition("who", "Names the caller.", JsonDocument.Parse("""{"type":"object"}""").RootElement),
      (_, ctx) => ValueTask.FromResult(CallToolResult.FromText(ctx.Principal?.FindFirst("sub")?.Value ?? "nobody")));
    _app.MapModelContext("/mcp", server, new StreamableHttpOptions { ResourceServer = resourceServer, AllowedHosts = ["127.0.0.1:" + port] });
    await _app.StartAsync(Ct);
  }

  [Fact]
  public async Task Discovery_authorization_token_and_the_mcp_call_work_over_http()
  {
    await StartAsync();

    var metadata = await _http.GetAsync(_origin + "/.well-known/oauth-authorization-server", Ct);
    using var doc = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync(Ct));
    Assert.Equal(_origin, doc.RootElement.GetProperty("issuer").GetString());
    Assert.True(doc.RootElement.GetProperty("authorization_response_iss_parameter_supported").GetBoolean());

    var verifier = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
    var challenge = System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    var query = $"response_type=code&client_id=app&redirect_uri={Uri.EscapeDataString("http://127.0.0.1:5000/cb")}&code_challenge={challenge}&code_challenge_method=S256&state=s1&scope=files%3Aread&resource={Uri.EscapeDataString(_origin + "/mcp")}";

    // Not signed in at the host yet: the host's redirect to its login page is passed through untouched.
    var anonymous = await _http.GetAsync(_origin + "/authorize?" + query, Ct);
    Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
    Assert.Equal(_origin + "/login", anonymous.Headers.Location!.AbsoluteUri);

    using var request = new HttpRequestMessage(HttpMethod.Get, _origin + "/authorize?" + query);
    request.Headers.Add("Cookie", "session=alice");
    var approved = await _http.SendAsync(request, Ct);
    Assert.Equal(HttpStatusCode.Redirect, approved.StatusCode);
    var location = approved.Headers.Location!.OriginalString;
    Assert.StartsWith("http://127.0.0.1:5000/cb?", location, StringComparison.Ordinal);
    var parameters = System.Web.HttpUtility.ParseQueryString(location[(location.IndexOf('?', StringComparison.Ordinal) + 1)..]);
    Assert.Equal(_origin, parameters["iss"]);
    Assert.Equal("s1", parameters["state"]);

    var token = await _http.PostAsync(_origin + "/token", new FormUrlEncodedContent(new Dictionary<string, string>
    {
      ["grant_type"] = "authorization_code",
      ["code"] = parameters["code"]!,
      ["code_verifier"] = verifier,
      ["client_id"] = "app",
      ["redirect_uri"] = "http://127.0.0.1:5000/cb",
    }), Ct);
    Assert.Equal(HttpStatusCode.OK, token.StatusCode);
    Assert.Equal("no-store", token.Headers.CacheControl!.ToString());
    using var tokens = JsonDocument.Parse(await token.Content.ReadAsStringAsync(Ct));
    var access = tokens.RootElement.GetProperty("access_token").GetString()!;

    var anonymousCall = await Call(null);
    Assert.Equal(HttpStatusCode.Unauthorized, anonymousCall.StatusCode);
    var call = await Call(access);
    Assert.Equal(HttpStatusCode.OK, call.StatusCode);
    Assert.Contains("alice", await call.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

    var jwks = await _http.GetStringAsync(_origin + "/jwks", Ct);
    Assert.Contains("host-1", jwks, StringComparison.Ordinal);
  }

  [Fact]
  public async Task The_configure_callback_reaches_every_endpoint_and_other_paths_are_untouched()
  {
    var seen = new List<string>();
    await StartAsync(_ => seen.Add("endpoint"));

    Assert.Equal(_as!.Paths.Count, seen.Count);
    var other = await _http.GetAsync(_origin + "/nothing-here", Ct);
    Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    var wrongMethod = await _http.PutAsync(_origin + "/token", new StringContent(string.Empty), Ct);
    Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
  }

  private async Task<HttpResponseMessage> Call(string? token)
  {
    const string Meta = """{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"t","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}""";
    using var request = new HttpRequestMessage(HttpMethod.Post, _origin + "/mcp")
    {
      Content = new StringContent($$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"who","arguments":{},"_meta":{{{Meta}}}}}""", Encoding.UTF8, "application/json"),
    };
    request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
    request.Headers.Add("Mcp-Method", "tools/call");
    request.Headers.Add("Mcp-Name", "who");
    request.Headers.Add("Accept", "application/json, text/event-stream");
    if (token is not null)
    {
      request.Headers.Add("Authorization", "Bearer " + token);
    }

    return await _http.SendAsync(request, Ct);
  }
}
