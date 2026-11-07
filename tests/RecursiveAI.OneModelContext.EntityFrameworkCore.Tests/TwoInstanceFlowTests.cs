using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using RecursiveAI.OneModelContext.Auth;
using RecursiveAI.OneModelContext.Auth.Server;
using RecursiveAI.OneModelContext.Hosting;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.Jose;
using RecursiveAI.OneModelContext.OAuth;
using RecursiveAI.OneModelContext.Protocol;
using Xunit;

namespace RecursiveAI.OneModelContext.EntityFrameworkCore.Tests;

/// <summary>
/// The OAuth client against two authorization servers that share one database and nothing else in memory (one key, one resume key):
/// the authorization request goes to the first, the code is redeemed at the second, and refreshing goes to whichever answers next.
/// </summary>
public sealed class TwoInstanceFlowTests : IAsyncDisposable
{
  private const string Redirect = "http://127.0.0.1:9/callback";

  private readonly ManualClock _time = new();
  private readonly Database _database = new();
  private readonly HttpServer _http;
  private readonly ECDsa _signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
  private readonly byte[] _resumeKey = RandomNumberGenerator.GetBytes(32);
  private readonly AuthorizationServer[] _servers = new AuthorizationServer[2];
  private readonly EfAuthorizationStore<AppDbContext>[] _stores = new EfAuthorizationStore<AppDbContext>[2];
  private readonly int[] _hits = new int[2];
  private readonly ResourceServer _resourceServer;
  private readonly StreamableHttpEndpoint _endpoint;
  private readonly Uri _resource;
  private int _tokenRequests;

  public TwoInstanceFlowTests()
  {
    _http = new HttpServer(HandleAsync);
    _http.Start();
    var origin = _http.BaseAddress!;
    _resource = new Uri(origin, "/mcp");
    for (var i = 0; i < 2; i++)
    {
      _stores[i] = new EfAuthorizationStore<AppDbContext>(_database.NewFactory());
      _servers[i] = new AuthorizationServer(new AuthorizationServerOptions
      {
        Issuer = new Uri(origin.GetLeftPart(UriPartial.Authority)),
        Resources = [_resource.AbsoluteUri],
        SigningKeys = [JsonWebSigningKey.FromECDsa(_signing, "shared")],
        ScopesSupported = ["files:read"],
        DefaultScopes = ["files:read"],
        Store = _stores[i],
        TimeProvider = _time,
        AccessTokenLifetime = TimeSpan.FromMinutes(5),
        ResumeKey = _resumeKey,
        ClientMetadataDocuments = null,
        AuthorizeUser = (request, _) => ValueTask.FromResult(AuthorizationDecision.Approve("alice", request.RequestedScopes)),
      });
    }

    _resourceServer = new ResourceServer(new ResourceServerOptions(_resource, _servers[1].CreateTokenValidator())
    {
      AuthorizationServers = [_servers[0].Issuer],
      ScopesSupported = ["files:read"],
    });
    var server = new ContextServer("ef", "1");
    var schema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
    server.Tools.Add(new ToolDefinition("read", "Reads.", schema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("read ok")));
    _endpoint = new StreamableHttpEndpoint(server, new StreamableHttpOptions { ResourceServer = _resourceServer });
  }

  public async ValueTask DisposeAsync()
  {
    await _http.DisposeAsync();
    _endpoint.Dispose();
    foreach (var s in _servers)
    {
      s.Dispose();
    }

    _signing.Dispose();
    _database.Dispose();
  }

  private ValueTask HandleAsync(IHttpExchange exchange)
  {
    var path = exchange.Request.Path;
    if (_servers[0].IsServerPath(path))
    {
      // The authorization endpoint is served by the first instance. Token requests alternate: the first (the code) goes to the second
      // instance, the refresh to the first, the next to the second again.
      var instance = 0;
      if (path == "/token")
      {
        instance = Interlocked.Increment(ref _tokenRequests) % 2;
      }

      if (path is "/token" or "/authorize")
      {
        Interlocked.Increment(ref _hits[instance]);
      }

      return Await(_servers[instance].HandleAsync(exchange));
    }

    if (path == "/mcp" || _resourceServer.IsMetadataPath(path))
    {
      return _endpoint.HandleAsync(exchange);
    }

    exchange.Response.StatusCode = 404;
    return ValueTask.CompletedTask;
  }

  private static async ValueTask Await(ValueTask<bool> pending) => await pending;

  private sealed class Browser : IAuthorizationUserAgent
  {
    public Uri RedirectUri { get; } = new(Redirect);

    public int Runs { get; private set; }

    public async ValueTask<Uri> AuthorizeAsync(Uri authorizationUrl, CancellationToken cancellationToken)
    {
      Runs++;
      using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
      using var response = await client.GetAsync(authorizationUrl, cancellationToken);
      Assert.Equal(HttpStatusCode.Found, response.StatusCode);
      return response.Headers.Location!;
    }
  }

  [Fact]
  public async Task Authorize_on_one_instance_then_redeem_and_refresh_on_the_other()
  {
    var ct = TestContext.Current.CancellationToken;
    await _stores[0].SaveClientAsync(new RegisteredClient { ClientId = "ef-app", Name = "EF App", RedirectUris = [Redirect], CreatedAt = _time.GetUtcNow() }, ct);
    var browser = new Browser();

    using var oauth = new OAuthClient(new OAuthClientOptions(_resource) { UserAgent = browser, PreRegistered = new PreRegisteredClient("ef-app"), AllowDynamicRegistration = false, TimeProvider = _time });
    using var http = oauth.CreateHttpClient();

    await using (var client = await ContextClient.ConnectAsync(ContextTransport.Http(_resource, http), cancellationToken: ct))
    {
      Assert.Equal("read ok", (await client.CallToolAsync("read", cancellationToken: ct)).Text);
    }

    var first = await oauth.GetAccessTokenAsync(ct);
    Assert.NotNull(first);

    _time.Advance(TimeSpan.FromMinutes(10));
    await using (var client = await ContextClient.ConnectAsync(ContextTransport.Http(_resource, http), cancellationToken: ct))
    {
      Assert.Equal("read ok", (await client.CallToolAsync("read", cancellationToken: ct)).Text);
    }

    var second = await oauth.GetAccessTokenAsync(ct);
    Assert.NotEqual(first, second);
    Assert.Equal(1, browser.Runs);

    // Both instances did work: the first answered the authorization request, and the token requests were split between them.
    Assert.True(_hits[0] >= 1 && _hits[1] >= 1, $"hits: {_hits[0]}, {_hits[1]}");
  }
}
