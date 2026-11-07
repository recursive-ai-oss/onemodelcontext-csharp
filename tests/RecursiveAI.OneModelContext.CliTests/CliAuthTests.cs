using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using RecursiveAI.OneModelContext.Auth;
using RecursiveAI.OneModelContext.Auth.Server;
using RecursiveAI.OneModelContext.Cli;
using RecursiveAI.OneModelContext.Hosting;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.Jose;
using RecursiveAI.OneModelContext.OAuth;
using RecursiveAI.OneModelContext.Protocol;
using Xunit;

namespace RecursiveAI.OneModelContext.CliTests;

/// <summary>login, logout and exit code 4, against the library's own authorization server and resource server on the built-in listener.</summary>
public sealed class CliAuthTests : IAsyncDisposable
{
  private readonly HttpServer _http;
  private readonly ECDsa _signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
  private readonly MemoryAuthorizationStore _store = new();
  private readonly AuthorizationServer _as;
  private readonly ResourceServer _rs;
  private readonly StreamableHttpEndpoint _endpoint;
  private readonly Uri _resource;
  private readonly string _dir = Path.Combine(Path.GetTempPath(), "omc-cli-auth-" + Guid.NewGuid().ToString("N"));

  public CliAuthTests()
  {
    Directory.CreateDirectory(_dir);
    _http = new HttpServer(HandleAsync);
    _http.Start();
    var origin = _http.BaseAddress!;
    _resource = new Uri(origin, "/mcp");
    _as = new AuthorizationServer(new AuthorizationServerOptions
    {
      Issuer = new Uri(origin.GetLeftPart(UriPartial.Authority)),
      Resources = [_resource.AbsoluteUri],
      SigningKeys = [JsonWebSigningKey.FromECDsa(_signing, "cli")],
      ScopesSupported = ["files:read"],
      DefaultScopes = ["files:read"],
      Store = _store,
      AccessTokenLifetime = TimeSpan.FromMinutes(5),
      ResumeKey = RandomNumberGenerator.GetBytes(32),
      AuthorizeUser = (request, _) => ValueTask.FromResult(AuthorizationDecision.Approve("alice", request.RequestedScopes)),
    });
    _rs = new ResourceServer(new ResourceServerOptions(_resource, _as.CreateTokenValidator())
    {
      AuthorizationServers = [_as.Issuer],
      ScopesSupported = ["files:read"],
    });
    var server = new ContextServer("cli-auth", "1");
    var schema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
    server.Tools.Add(new ToolDefinition("read", "Reads.", schema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("read ok")));
    _endpoint = new StreamableHttpEndpoint(server, new StreamableHttpOptions { ResourceServer = _rs });
  }

  public async ValueTask DisposeAsync()
  {
    await _http.DisposeAsync();
    _endpoint.Dispose();
    _as.Dispose();
    _signing.Dispose();
    Directory.Delete(_dir, recursive: true);
  }

  private ValueTask HandleAsync(IHttpExchange exchange)
  {
    var path = exchange.Request.Path;
    if (_as.IsServerPath(path))
    {
      return AsValueTask(_as.HandleAsync(exchange));
    }

    if (path == "/mcp" || _rs.IsMetadataPath(path))
    {
      return _endpoint.HandleAsync(exchange);
    }

    exchange.Response.StatusCode = 404;
    return ValueTask.CompletedTask;
  }

  private static async ValueTask AsValueTask(ValueTask<bool> pending) => await pending;

  private string TokenFile => Path.Combine(_dir, "tokens.json");

  private static async Task<(int Code, string Out, string Err)> RunAsync(Func<Uri, CancellationToken, ValueTask>? browser, params string[] args)
  {
    var output = new StringWriter();
    var error = new StringWriter();
    var app = new CliApp(output, error, new StringReader(""), "test", colour: false) { OpenBrowser = browser };
    var code = await app.RunAsync(args, TestContext.Current.CancellationToken);
    return (code, output.ToString(), error.ToString());
  }

  // A browser: opens the authorization URL without following redirects, then visits the redirect it is sent to.
  private static async ValueTask Browse(Uri url, CancellationToken ct)
  {
    using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
    using var authorize = await client.GetAsync(url, ct);
    Assert.Equal(HttpStatusCode.Found, authorize.StatusCode);
    using var callback = await client.GetAsync(authorize.Headers.Location!, ct);
    Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
  }

  [Fact]
  public async Task A_server_that_wants_a_token_ends_with_exit_4_and_says_how_to_sign_in()
  {
    var (code, text, err) = await RunAsync(null, "call", _resource.AbsoluteUri, "read", "--token-store", TokenFile);
    Assert.True(code == 4, err);
    Assert.Equal("", text);
    Assert.Contains("login", err);
    Assert.DoesNotContain("Bearer ey", err);
  }

  [Fact]
  public async Task A_refused_step_up_ends_with_exit_4_and_never_prints_the_token()
  {
    const string secret = "stored-secret-token-value";
    await using var stub = new HttpServer(exchange =>
    {
      exchange.Response.StatusCode = 403;
      exchange.Response.SetHeader("WWW-Authenticate", "Bearer error=\"insufficient_scope\", scope=\"admin\"");
      return ValueTask.CompletedTask;
    });
    stub.Start();
    var url = new Uri(stub.BaseAddress!, "/mcp");
    using (var seed = new FileTokenStore(TokenFile))
    {
      await seed.SetAsync("token:" + url.GetLeftPart(UriPartial.Authority) + "/mcp", new TokenRecord { Issuer = "http://127.0.0.1:9", AccessToken = secret, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) }, TestContext.Current.CancellationToken);
    }

    var (code, text, err) = await RunAsync(null, "call", url.AbsoluteUri, "read", "--token-store", TokenFile);
    Assert.True(code == 4, err);
    Assert.DoesNotContain(secret, text + err);
  }

  [Fact]
  public async Task Login_then_call_then_logout()
  {
    var ct = TestContext.Current.CancellationToken;
    const int port = 47801;
    var redirect = $"http://127.0.0.1:{port}/callback";
    await _store.SaveClientAsync(new RegisteredClient { ClientId = "cli-app", Name = "CLI", RedirectUris = [redirect], CreatedAt = DateTimeOffset.UtcNow }, ct);
    string[] common = ["--token-store", TokenFile];

    var (loginCode, loginOut, loginErr) = await RunAsync(Browse, ["login", _resource.AbsoluteUri, "--client-id", "cli-app", "--redirect-port", "47801", .. common]);
    Assert.True(loginCode == 0, loginErr);
    Assert.Contains("Signed in", loginOut);

    var stored = JsonDocument.Parse(await File.ReadAllTextAsync(TokenFile, ct)).RootElement.GetProperty("entries");
    var access = stored.EnumerateObject().Select(e => e.Value.TryGetProperty("access_token", out var a) ? a.GetString() : null).First(t => t is not null)!;
    var refresh = stored.EnumerateObject().Select(e => e.Value.TryGetProperty("refresh_token", out var r) ? r.GetString() : null).FirstOrDefault(t => t is not null);
    if (!OperatingSystem.IsWindows())
    {
      Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(TokenFile));
    }

    var (callCode, callOut, callErr) = await RunAsync(null, ["call", _resource.AbsoluteUri, "read", .. common]);
    Assert.True(callCode == 0, callErr);
    Assert.Equal("read ok", callOut.Trim());

    var (logoutCode, logoutOut, _) = await RunAsync(null, ["logout", _resource.AbsoluteUri, .. common]);
    Assert.Equal(0, logoutCode);
    Assert.Contains("Signed out", logoutOut);

    var (afterCode, _, afterErr) = await RunAsync(null, ["call", _resource.AbsoluteUri, "read", .. common]);
    Assert.Equal(4, afterCode);

    var everything = loginOut + loginErr + callOut + callErr + logoutOut + afterErr;
    Assert.DoesNotContain(access, everything);
    if (refresh is not null)
    {
      Assert.DoesNotContain(refresh, everything);
    }
  }

  [Fact]
  public async Task A_bearer_option_overrides_stored_tokens()
  {
    var (code, _, err) = await RunAsync(null, "call", _resource.AbsoluteUri, "read", "--bearer", "not-a-valid-token", "--token-store", TokenFile);
    Assert.Equal(4, code);
    Assert.DoesNotContain("not-a-valid-token", err);
  }

  [Fact]
  public async Task A_client_secret_needs_an_issuer_and_can_come_from_the_environment_standard_input_or_a_file()
  {
    var ct = TestContext.Current.CancellationToken;
    var url = _resource.AbsoluteUri;

    // Literal, file and standard input forms all reach the check that a secret must name its issuer.
    var secretFile = Path.Combine(_dir, "secret.txt");
    await File.WriteAllTextAsync(secretFile, "file-secret-value\n", ct);
    var name = "OMC_TEST_SECRET_" + Guid.NewGuid().ToString("N");
    Environment.SetEnvironmentVariable(name, "env-secret-value");
    try
    {
      foreach (var form in new[] { "literal-secret-value", "@" + secretFile, "env:" + name })
      {
        var (code, _, err) = await RunAsync(null, "login", url, "--client-id", "app", "--client-secret", form, "--token-store", TokenFile);
        Assert.Equal(2, code);
        Assert.Contains("--issuer", err);
        Assert.DoesNotContain("secret-value", err);
      }

      var output = new StringWriter();
      var error = new StringWriter();
      var app = new CliApp(output, error, new StringReader("stdin-secret-value\n"), "test", colour: false);
      var viaStdin = await app.RunAsync(["login", url, "--client-id", "app", "--client-secret", "-", "--token-store", TokenFile], ct);
      Assert.Equal(2, viaStdin);
      Assert.Contains("--issuer", error.ToString());

      var (missing, _, missingErr) = await RunAsync(null, "login", url, "--client-id", "app", "--client-secret", "env:OMC_NO_SUCH_VARIABLE_0", "--token-store", TokenFile);
      Assert.Equal(2, missing);
      Assert.Contains("OMC_NO_SUCH_VARIABLE_0", missingErr);
    }
    finally
    {
      Environment.SetEnvironmentVariable(name, null);
    }

    // Usage errors stop before anything is read or written.
    Assert.False(File.Exists(TokenFile));
  }

  [Fact]
  public async Task A_login_that_fails_leaves_the_stored_tokens_as_they_were()
  {
    var ct = TestContext.Current.CancellationToken;
    var key = "token:" + _resource.AbsoluteUri;
    using (var seed = new FileTokenStore(TokenFile))
    {
      await seed.SetAsync(key, new TokenRecord { Issuer = _as.Issuer.ToString().TrimEnd('/'), ClientId = "cli-app", AccessToken = "previous-access-token-value", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), Resource = _resource.AbsoluteUri }, ct);
    }

    // The user refuses at the authorization server: the browser sends the redirect back with an error and the right state.
    static async ValueTask Refuse(Uri url, CancellationToken token)
    {
      var query = url.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
      using var client = new HttpClient();
      using var _ = await client.GetAsync(query["redirect_uri"] + "?error=access_denied&state=" + Uri.EscapeDataString(query["state"]), token);
    }

    var (code, _, _) = await RunAsync(Refuse, "login", _resource.AbsoluteUri, "--client-id", "cli-app", "--token-store", TokenFile);

    Assert.NotEqual(0, code);
    using var after = new FileTokenStore(TokenFile);
    Assert.Equal("previous-access-token-value", (await after.GetAsync(key, ct))!.AccessToken);
  }
}
