using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Auth;
using RecursiveAI.OneModelContext.Auth.Server;
using RecursiveAI.OneModelContext.Hosting;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.Jose;
using RecursiveAI.OneModelContext.OAuth;
using RecursiveAI.OneModelContext.Protocol;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>
/// The OAuth client against the library's own authorization server fronting its own resource server, all three on the built-in listener:
/// the authorization endpoint reads its parameters from the request query, so these runs also prove the listener supplies it.
/// </summary>
public sealed class OAuthEndToEndTests : IAsyncDisposable
{
  private const string Redirect = "http://127.0.0.1:9/callback";

  private readonly ManualTimeProvider _time = new();
  private readonly LoopbackServer _documents = new();
  private readonly HttpServer _http;
  private readonly ECDsa _signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
  private readonly MemoryAuthorizationStore _store = new();
  private AuthorizationServer? _as;
  private ResourceServer? _rs;
  private StreamableHttpEndpoint? _endpoint;
  private Uri _resource = null!;

  public OAuthEndToEndTests()
  {
    _http = new HttpServer(HandleAsync);
    _http.Start();
    var origin = _http.BaseAddress!;
    _resource = new Uri(origin, "/mcp");
    _as = new AuthorizationServer(new AuthorizationServerOptions
    {
      Issuer = new Uri(origin.GetLeftPart(UriPartial.Authority)),
      Resources = [_resource.AbsoluteUri],
      SigningKeys = [JsonWebSigningKey.FromECDsa(_signing, "e2e")],
      ScopesSupported = ["files:read"],
      DefaultScopes = ["files:read"],
      Store = _store,
      TimeProvider = _time,
      AccessTokenLifetime = TimeSpan.FromMinutes(5),
      ResumeKey = RandomNumberGenerator.GetBytes(32),
      // The metadata document lives on a loopback server of this test, so that one address is allow-listed explicitly.
      ClientMetadataDocuments = new ClientMetadataDocumentOptions
      {
        Fetch = new GuardedFetchOptions { Addresses = new AddressPolicy().Allow("127.0.0.1"), CertificateValidation = LoopbackServer.Trust },
      },
      AuthorizeUser = (request, _) => ValueTask.FromResult(AuthorizationDecision.Approve("alice", request.RequestedScopes)),
    });
    _rs = new ResourceServer(new ResourceServerOptions(_resource, _as.CreateTokenValidator())
    {
      AuthorizationServers = [_as.Issuer],
      ScopesSupported = ["files:read"],
    });
    var server = new ContextServer("e2e", "1");
    var schema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
    server.Tools.Add(new ToolDefinition("read", "Reads.", schema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("read ok")));
    _endpoint = new StreamableHttpEndpoint(server, new StreamableHttpOptions { ResourceServer = _rs });
  }

  public async ValueTask DisposeAsync()
  {
    await _http.DisposeAsync();
    _endpoint?.Dispose();
    _as?.Dispose();
    _documents.Dispose();
    _signing.Dispose();
  }

  private ValueTask HandleAsync(IHttpExchange exchange)
  {
    var path = exchange.Request.Path;
    if (_as!.IsServerPath(path))
    {
      return AsValueTask(_as.HandleAsync(exchange));
    }

    if (path == "/mcp" || _rs!.IsMetadataPath(path))
    {
      return _endpoint!.HandleAsync(exchange);
    }

    exchange.Response.StatusCode = 404;
    return ValueTask.CompletedTask;
  }

  private static async ValueTask AsValueTask(ValueTask<bool> pending) => await pending;

  /// <summary>A browser: opens the authorization URL over HTTP without following redirects and hands back the redirect it is sent to.</summary>
  private sealed class Browser : IAuthorizationUserAgent
  {
    public Uri RedirectUri { get; } = new(Redirect);

    public int Runs { get; private set; }

    public async ValueTask<Uri> AuthorizeAsync(Uri authorizationUrl, CancellationToken cancellationToken)
    {
      Runs++;
      using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
      using var response = await client.GetAsync(authorizationUrl, cancellationToken);
      Assert.Equal(System.Net.HttpStatusCode.Found, response.StatusCode);
      return response.Headers.Location!;
    }
  }

  private async Task SignInRefreshAndCallAsync(OAuthClientOptions options, Browser browser)
  {
    var ct = TestContext.Current.CancellationToken;
    using var oauth = new OAuthClient(options);
    using var http = oauth.CreateHttpClient();

    await using (var client = await ContextClient.ConnectAsync(ContextTransport.Http(_resource, http), cancellationToken: ct))
    {
      Assert.Equal("read ok", (await client.CallToolAsync("read", cancellationToken: ct)).Text);
    }

    var first = await oauth.GetAccessTokenAsync(ct);
    Assert.NotNull(first);
    Assert.Equal(1, browser.Runs);

    // The access token expires; the stored refresh token gets a new one without asking the user again.
    _time.Advance(TimeSpan.FromMinutes(10));
    await using (var client = await ContextClient.ConnectAsync(ContextTransport.Http(_resource, http), cancellationToken: ct))
    {
      Assert.Equal("read ok", (await client.CallToolAsync("read", cancellationToken: ct)).Text);
    }

    var second = await oauth.GetAccessTokenAsync(ct);
    Assert.NotNull(second);
    Assert.NotEqual(first, second);
    Assert.Equal(1, browser.Runs);
  }

  [Fact]
  public async Task A_metadata_document_client_signs_in_refreshes_and_calls_a_tool()
  {
    var clientId = _documents.Url("/client.json");
    _documents.AddJson("/client.json", $$"""{"client_id":"{{clientId}}","client_name":"E2E App","redirect_uris":["{{Redirect}}"],"token_endpoint_auth_method":"none"}""", "max-age=600");
    var browser = new Browser();

    await SignInRefreshAndCallAsync(
      new OAuthClientOptions(_resource) { UserAgent = browser, ClientIdMetadataUrl = clientId, TimeProvider = _time },
      browser);

    Assert.True(_documents.Hits >= 1);
  }

  [Fact]
  public async Task A_pre_registered_client_signs_in_refreshes_and_calls_a_tool()
  {
    await _store.SaveClientAsync(new RegisteredClient { ClientId = "e2e-app", Name = "E2E App", RedirectUris = [Redirect], CreatedAt = _time.GetUtcNow() }, TestContext.Current.CancellationToken);
    var browser = new Browser();

    await SignInRefreshAndCallAsync(
      new OAuthClientOptions(_resource) { UserAgent = browser, PreRegistered = new PreRegisteredClient("e2e-app"), AllowDynamicRegistration = false, TimeProvider = _time },
      browser);

    Assert.Equal(0, _documents.Hits);
  }

  [Fact]
  public async Task A_failed_sign_in_reaches_the_caller_as_the_OAuth_error_without_a_token()
  {
    var ct = TestContext.Current.CancellationToken;
    // No user agent, grant or stored token: the handler cannot sign in. The caller sees why, not a dropped connection.
    using var oauth = new OAuthClient(new OAuthClientOptions(_resource) { TimeProvider = _time });
    using var http = oauth.CreateHttpClient();

    var failure = await Assert.ThrowsAsync<OAuthException>(async () =>
    {
      await using var client = await ContextClient.ConnectAsync(ContextTransport.Http(_resource, http), cancellationToken: ct);
    });

    Assert.Equal("authorization_required", failure.Error);
    Assert.DoesNotContain("Bearer", failure.Message);
  }
}
