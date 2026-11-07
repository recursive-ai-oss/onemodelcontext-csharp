using System;
using System.IO;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Auth;
using RecursiveAI.OneModelContext.Auth.Server;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.Jose;

/// <summary>
/// The authorization server under NativeAOT: a full authorization-code flow with PKCE against a file store (source-generated JSON), the
/// published key set checked by the resource server's validator, and the guarded fetcher refusing a loopback target.
/// </summary>
internal static class AuthorizationServerSmoke
{
  internal static async Task<string?> RunAsync()
  {
    var path = Path.Combine(Path.GetTempPath(), "omc-aot-" + Guid.NewGuid().ToString("N"), "auth.json");
    try
    {
      using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
      var store = new FileAuthorizationStore(path);
      await store.SaveClientAsync(new RegisteredClient { ClientId = "app", RedirectUris = ["https://app.test/cb"], CreatedAt = DateTimeOffset.UnixEpoch }, CancellationToken.None);
      using var server = new AuthorizationServer(new AuthorizationServerOptions
      {
        Issuer = new Uri("https://as.test"),
        Resources = ["https://mcp.test/mcp"],
        SigningKeys = [JsonWebSigningKey.FromECDsa(key, "k1")],
        Store = store,
        AuthorizeUser = (_, _) => ValueTask.FromResult(AuthorizationDecision.Approve("alice")),
      });

      var verifier = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
      var challenge = System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
      using var authorize = new Exchange("GET", "/authorize", "response_type=code&client_id=app&redirect_uri=https%3A%2F%2Fapp.test%2Fcb&code_challenge=" + challenge + "&code_challenge_method=S256&resource=https%3A%2F%2Fmcp.test%2Fmcp&state=x", null);
      await server.HandleAsync(authorize);
      var location = authorize.Headers.GetFirst("Location") ?? string.Empty;
      var codeStart = location.IndexOf("code=", StringComparison.Ordinal);
      if (authorize.Status != 302 || codeStart < 0 || !location.Contains("iss=https%3A%2F%2Fas.test", StringComparison.Ordinal))
      {
        return "authorization failed: " + authorize.Status + " " + location;
      }

      var code = location[(codeStart + 5)..location.IndexOf('&', codeStart)];
      using var token = new Exchange("POST", "/token", string.Empty, "grant_type=authorization_code&client_id=app&redirect_uri=https%3A%2F%2Fapp.test%2Fcb&code=" + code + "&code_verifier=" + verifier);
      await server.HandleAsync(token);
      using var tokens = JsonDocument.Parse(token.Written.ToArray());
      var access = tokens.RootElement.GetProperty("access_token").GetString()!;

      using var jwks = new Exchange("GET", "/jwks", string.Empty, null);
      await server.HandleAsync(jwks);
      var validator = new JwtTokenValidator("https://as.test", new StaticJwksProvider(JsonWebKeySet.Parse(jwks.Written.ToArray())!));
      var checkedToken = await validator.ValidateAsync(access, new TokenValidationRequest("https://mcp.test/mcp"), CancellationToken.None);
      var reopened = await new FileAuthorizationStore(path).FindClientAsync("app", CancellationToken.None);
      if (!checkedToken.IsValid || reopened is null)
      {
        return "token not accepted: " + checkedToken.Description;
      }

      using var fetcher = new GuardedHttpFetcher();
      var refused = await fetcher.GetAsync(new Uri("https://127.0.0.1:1/c.json"));
      if (refused.Succeeded)
      {
        return "the fetcher reached a loopback address";
      }

      return null;
    }
    finally
    {
      var directory = Path.GetDirectoryName(path)!;
      if (Directory.Exists(directory))
      {
        Directory.Delete(directory, recursive: true);
      }
    }
  }

  private sealed class Exchange : IHttpExchange, IHttpRequest, IHttpResponse, IDisposable
  {
    private readonly MemoryStream _request;

    internal Exchange(string method, string path, string query, string? form)
    {
      Method = method;
      Path = path;
      Query = query;
      _request = new MemoryStream(Encoding.UTF8.GetBytes(form ?? string.Empty));
      ContentLength = _request.Length;
      if (form is not null)
      {
        RequestHeaders.Add("Content-Type", "application/x-www-form-urlencoded");
      }
    }

    public IHttpRequest Request => this;

    public IHttpResponse Response => this;

    public ClaimsPrincipal? User => null;

    public CancellationToken Aborted => CancellationToken.None;

    public string Method { get; }

    public string Path { get; }

    public string Query { get; }

    public HttpHeaders RequestHeaders { get; } = new();

    HttpHeaders IHttpRequest.Headers => RequestHeaders;

    public long? ContentLength { get; }

    Stream IHttpRequest.Body => _request;

    public IPAddress? RemoteAddress => IPAddress.Loopback;

    public int Status { get; private set; } = 200;

    int IHttpResponse.StatusCode
    {
      get => Status;
      set => Status = value;
    }

    internal HttpHeaders Headers { get; } = new();

    internal MemoryStream Written { get; } = new();

    public void SetHeader(string name, string value) => Headers.Set(name, value);

    Stream IHttpResponse.Body => Written;

    public void Dispose()
    {
      _request.Dispose();
      Written.Dispose();
    }
  }
}
