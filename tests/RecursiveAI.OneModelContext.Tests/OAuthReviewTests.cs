using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.OAuth;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>
/// Regression tests for the client-auth review: credentials only go to an application-named issuer, a stray request cannot end a
/// sign-in, a stale process cannot erase a rotated token, endpoints and handlers cannot be steered, and the token file is never
/// destroyed.
/// </summary>
public sealed class OAuthReviewTests
{
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  /// <summary>A scripted server on the loopback interface that records every request.</summary>
  private sealed class Stub : IAsyncDisposable
  {
    private readonly HttpServer _http;
    private readonly Func<IHttpExchange, string, Task> _handler;

    internal Stub(Func<IHttpExchange, string, Task> handler)
    {
      _handler = handler;
      _http = new HttpServer(HandleAsync);
      _http.Start();
      Origin = _http.BaseAddress!.GetLeftPart(UriPartial.Authority);
    }

    internal string Origin { get; }

    internal List<string> Requests { get; } = [];

    internal List<string> Bodies { get; } = [];

    private async ValueTask HandleAsync(IHttpExchange exchange)
    {
      using var buffer = new MemoryStream();
      await exchange.Request.Body.CopyToAsync(buffer);
      var body = Encoding.UTF8.GetString(buffer.ToArray());
      lock (Requests)
      {
        Requests.Add(exchange.Request.Method + " " + exchange.Request.Path);
        Bodies.Add(body);
      }

      await _handler(exchange, body);
    }

    public ValueTask DisposeAsync() => _http.DisposeAsync();

    internal static async Task Json(IHttpExchange exchange, int status, string json)
    {
      exchange.Response.StatusCode = status;
      exchange.Response.SetHeader("Content-Type", "application/json");
      await exchange.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(json));
    }
  }

  private static string TokenJson(string access, string? refresh = null, int expires = 3600) =>
    "{\"access_token\":\"" + access + "\",\"token_type\":\"Bearer\",\"expires_in\":" + expires + (refresh is null ? string.Empty : ",\"refresh_token\":\"" + refresh + "\"") + "}";

  // ----- M1 ---------------------------------------------------------------------------------------------------------------------------

  [Fact]
  public async Task A_client_secret_is_never_sent_to_an_authorization_server_the_resource_chose()
  {
    await using var attacker = new Stub((e, _) => Stub.Json(e, 401, "{\"error\":\"invalid_client\"}"));
    Stub? self = null;
    await using var resource = new Stub((e, _) => e.Request.Path.StartsWith("/.well-known/oauth-protected-resource", StringComparison.Ordinal)
      ? Stub.Json(e, 200, "{\"resource\":\"" + self!.Origin + "/mcp\",\"authorization_servers\":[\"" + attacker.Origin + "\"]}")
      : Stub.Json(e, 401, "{}"));
    self = resource;
    var url = new Uri(resource.Origin + "/mcp");

    var configurations = new OAuthClientOptions[]
    {
      new(url) { ClientCredentials = new ClientCredentialsGrant("machine", "THE-SECRET-VALUE") },
      new(url) { Enterprise = new EnterpriseManagedAuthorization(new Uri(attacker.Origin + "/idp"), _ => ValueTask.FromResult("id-token"), "machine") },
      new(url) { UserAgent = new NoBrowser(), PreRegistered = new PreRegisteredClient("machine", "THE-SECRET-VALUE") },
    };
    foreach (var options in configurations)
    {
      using var client = new OAuthClient(options);
      var ex = await Assert.ThrowsAsync<OAuthException>(() => client.AuthorizeAsync(null, null, false, Ct).AsTask());
      Assert.Equal("issuer_required", ex.Error);
      Assert.DoesNotContain("THE-SECRET-VALUE", ex.Message, StringComparison.Ordinal);
    }

    // Not one request, to either server: the check comes before discovery.
    Assert.Empty(attacker.Requests);
    Assert.Empty(resource.Requests);
  }

  [Fact]
  public async Task A_public_pre_registered_client_still_needs_no_pinned_issuer()
  {
    await using var resource = new Stub((e, _) => Stub.Json(e, 404, "{}"));
    using var client = new OAuthClient(new OAuthClientOptions(new Uri(resource.Origin + "/mcp")) { UserAgent = new NoBrowser(), PreRegistered = new PreRegisteredClient("public-app") });

    // The flow went on to the user agent, which is where this test stops it.
    await Assert.ThrowsAsync<InvalidOperationException>(() => client.AuthorizeAsync(null, null, false, Ct).AsTask());
  }

  private sealed class NoBrowser : IAuthorizationUserAgent
  {
    public Uri RedirectUri { get; } = new("http://127.0.0.1:9/callback");

    public ValueTask<Uri> AuthorizeAsync(Uri authorizationUrl, CancellationToken cancellationToken) => throw new InvalidOperationException("no browser in this test");
  }

  // ----- L1 ---------------------------------------------------------------------------------------------------------------------------

  [Fact]
  public async Task A_stray_request_to_the_loopback_receiver_does_not_end_the_sign_in()
  {
    using var receiver = new LoopbackRedirectReceiver((_, _) => ValueTask.CompletedTask);
    var pending = receiver.AuthorizeAsync(new Uri("https://auth.example/authorize?client_id=c&state=REAL%2Bvalue"), Ct).AsTask();
    using var browser = new HttpClient();

    // What any web page can trigger: a request to the port with a state of its own, or none.
    using var wrong = await browser.GetAsync(receiver.RedirectUri + "?state=ATTACKER&code=x", Ct);
    using var none = await browser.GetAsync(receiver.RedirectUri + "?code=x", Ct);
    using var near = await browser.GetAsync(receiver.RedirectUri + "?state=REAL%2Bvalu&code=x", Ct);
    Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, near.StatusCode);
    Assert.False(pending.IsCompleted);

    using var genuine = await browser.GetAsync(receiver.RedirectUri + "?state=REAL%2Bvalue&code=good", Ct);
    var callback = await pending;

    Assert.Equal(HttpStatusCode.OK, genuine.StatusCode);
    Assert.Contains("code=good", callback.Query, StringComparison.Ordinal);
  }

  // ----- L2 ---------------------------------------------------------------------------------------------------------------------------

  private static Stub RotatingServer(Func<string> currentRefresh, Action<string> rotate)
  {
    var n = 0;
    Stub? self = null;
    self = new Stub(async (e, body) =>
    {
      var path = e.Request.Path;
      if (path.Contains("oauth-authorization-server", StringComparison.Ordinal))
      {
        await Stub.Json(e, 200, "{\"issuer\":\"" + self!.Origin + "\",\"token_endpoint\":\"" + self.Origin + "/token\"}");
      }
      else if (path == "/token" && body.Contains("refresh_token=" + currentRefresh(), StringComparison.Ordinal))
      {
        n++;
        var next = "RT" + (n + 1);
        rotate(next);
        await Stub.Json(e, 200, TokenJson("AT" + n, next));
      }
      else if (path == "/token")
      {
        await Stub.Json(e, 400, "{\"error\":\"invalid_grant\"}");
      }
      else
      {
        await Stub.Json(e, 404, "{}");
      }
    });
    return self;
  }

  [Fact]
  public async Task A_process_that_read_the_store_early_adopts_a_rotation_made_by_another_instead_of_wiping_it()
  {
    var refresh = "RT1";
    await using var server = RotatingServer(() => refresh, next => refresh = next);
    var canonical = server.Origin + "/mcp";
    var dir = Path.Combine(Path.GetTempPath(), "oauth-race-" + Guid.NewGuid().ToString("N"));
    var path = Path.Combine(dir, "tokens.json");
    try
    {
      var clock = new ManualTimeProvider();
      using (var seed = new FileTokenStore(path))
      {
        await seed.SetAsync("token:" + canonical, new TokenRecord { Issuer = server.Origin, ClientId = "c", AccessToken = "OLD", RefreshToken = "RT1", ExpiresAt = clock.GetUtcNow().AddMinutes(-5), Resource = canonical }, Ct);
      }

      using var storeA = new FileTokenStore(path);
      using var storeB = new FileTokenStore(path);
      using var a = new OAuthClient(new OAuthClientOptions(new Uri(canonical)) { Store = storeA, TimeProvider = clock });
      using var b = new OAuthClient(new OAuthClientOptions(new Uri(canonical)) { Store = storeB, TimeProvider = clock });
      await b.GetRequestedScopeAsync(Ct); // B starts and reads the store: the old tokens.

      var fromA = await a.GetAccessTokenAsync(Ct);
      var fromB = await b.GetAccessTokenAsync(Ct);

      Assert.Equal("AT1", fromA);
      Assert.Equal("AT1", fromB);
      Assert.Equal(1, server.Bodies.Count(x => x.Contains("grant_type=refresh_token", StringComparison.Ordinal)));
      using var fresh = new FileTokenStore(path);
      var stored = await fresh.GetAsync("token:" + canonical, Ct);
      Assert.Equal("AT1", stored!.AccessToken);
      Assert.Equal("RT2", stored.RefreshToken);
    }
    finally
    {
      if (Directory.Exists(dir))
      {
        Directory.Delete(dir, recursive: true);
      }
    }
  }

  [Fact]
  public async Task A_refresh_token_refused_as_spent_is_replaced_by_the_one_another_process_stored()
  {
    var clock = new ManualTimeProvider();
    await using var server = new Stub(async (e, _) =>
    {
      if (e.Request.Path.Contains("oauth-authorization-server", StringComparison.Ordinal))
      {
        var origin = "http://" + e.Request.Headers.GetFirst("Host");
        await Stub.Json(e, 200, "{\"issuer\":\"" + origin + "\",\"token_endpoint\":\"" + origin + "/token\"}");
      }
      else if (e.Request.Path == "/token")
      {
        await Stub.Json(e, 400, "{\"error\":\"invalid_grant\"}");
      }
      else
      {
        await Stub.Json(e, 404, "{}");
      }
    });
    var canonical = server.Origin + "/mcp";
    var stale = new TokenRecord { Issuer = server.Origin, ClientId = "c", AccessToken = "OLD", RefreshToken = "RT1", ExpiresAt = clock.GetUtcNow().AddMinutes(-5), Resource = canonical };
    var rotated = new TokenRecord { Issuer = server.Origin, ClientId = "c", AccessToken = "AT-ROTATED", RefreshToken = "RT2", ExpiresAt = clock.GetUtcNow().AddHours(1), Resource = canonical };
    // This process sees the old record twice (its first read and the look before refreshing), then the other process's write.
    var store = new SequenceStore("token:" + canonical, stale, stale, rotated);
    using var client = new OAuthClient(new OAuthClientOptions(new Uri(canonical)) { Store = store, TimeProvider = clock });

    var token = await client.GetAccessTokenAsync(Ct);

    Assert.Equal("AT-ROTATED", token);
    Assert.Equal(0, store.Writes);
  }

  [Fact]
  public async Task A_refresh_token_that_is_really_spent_is_still_forgotten_and_a_rejected_registration_is_dropped()
  {
    var clock = new ManualTimeProvider();
    await using var server = new Stub(async (e, _) =>
    {
      if (e.Request.Path.Contains("oauth-authorization-server", StringComparison.Ordinal))
      {
        var origin = "http://" + e.Request.Headers.GetFirst("Host");
        await Stub.Json(e, 200, "{\"issuer\":\"" + origin + "\",\"token_endpoint\":\"" + origin + "/token\"}");
      }
      else if (e.Request.Path == "/token")
      {
        await Stub.Json(e, 401, "{\"error\":\"invalid_client\"}");
      }
      else
      {
        await Stub.Json(e, 404, "{}");
      }
    });
    var canonical = server.Origin + "/mcp";
    var store = new MemoryTokenStore();
    await store.SetAsync("token:" + canonical, new TokenRecord { Issuer = server.Origin, ClientId = "dyn", AccessToken = "OLD", RefreshToken = "RT1", ExpiresAt = clock.GetUtcNow().AddMinutes(-5), Resource = canonical }, Ct);
    await store.SetAsync("client:" + server.Origin, new TokenRecord { Issuer = server.Origin, ClientId = "dyn", Dynamic = true }, Ct);
    using var client = new OAuthClient(new OAuthClientOptions(new Uri(canonical)) { Store = store, TimeProvider = clock });

    Assert.Null(await client.GetAccessTokenAsync(Ct));

    Assert.Null((await store.GetAsync("token:" + canonical, Ct))!.RefreshToken);
    Assert.Null(await store.GetAsync("client:" + server.Origin, Ct));
  }

  private sealed class SequenceStore(string key, params TokenRecord[] reads) : ITokenStore
  {
    private int _reads;

    internal int Writes { get; private set; }

    public ValueTask<TokenRecord?> GetAsync(string k, CancellationToken cancellationToken) =>
      ValueTask.FromResult<TokenRecord?>(k == key ? reads[Math.Min(_reads++, reads.Length - 1)] : null);

    public ValueTask SetAsync(string k, TokenRecord record, CancellationToken cancellationToken)
    {
      Writes++;
      return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string k, CancellationToken cancellationToken) => ValueTask.CompletedTask;
  }

  // ----- L3 ---------------------------------------------------------------------------------------------------------------------------

  [Fact]
  public void Plain_http_loopback_endpoints_are_allowed_only_for_a_loopback_resource()
  {
    var local = new Uri("http://127.0.0.1:8080/token");
    Assert.True(OAuthUri.IsAllowedEndpoint(local, resourceIsLoopback: true));
    Assert.False(OAuthUri.IsAllowedEndpoint(local, resourceIsLoopback: false));
    Assert.False(OAuthUri.IsAllowedEndpoint(new Uri("http://localhost/token"), resourceIsLoopback: false));
    Assert.False(OAuthUri.IsAllowedEndpoint(new Uri("http://[::1]/token"), resourceIsLoopback: false));
    Assert.True(OAuthUri.IsAllowedEndpoint(new Uri("https://auth.example/token"), resourceIsLoopback: false));
    Assert.False(OAuthUri.IsAllowedEndpoint(new Uri("http://auth.example/token"), resourceIsLoopback: true));
    Assert.False(OAuthUri.TryHttp("http://127.0.0.1/authorize", resourceIsLoopback: false, out _));
    Assert.True(OAuthUri.TryHttp("http://127.0.0.1/authorize", resourceIsLoopback: true, out _));
  }

  // ----- L4 ---------------------------------------------------------------------------------------------------------------------------

  [Fact]
  public void A_handler_that_follows_redirects_or_cannot_be_checked_is_refused()
  {
    var resource = new Uri("http://127.0.0.1:9/mcp");
    Assert.Throws<ArgumentException>(() => new OAuthClient(new OAuthClientOptions(resource) { HttpHandler = new SocketsHttpHandler { AllowAutoRedirect = true } }));
    Assert.Throws<ArgumentException>(() => new OAuthClient(new OAuthClientOptions(resource) { HttpHandler = new HttpClientHandler { AllowAutoRedirect = true } }));
    Assert.Throws<ArgumentException>(() => new OAuthClient(new OAuthClientOptions(resource) { HttpHandler = new PassThrough(new SocketsHttpHandler()) }));
    Assert.Throws<ArgumentException>(() => new OAuthClient(new OAuthClientOptions(resource) { HttpHandler = new Opaque() }));

    using var ok = new OAuthClient(new OAuthClientOptions(resource) { HttpHandler = new PassThrough(new SocketsHttpHandler { AllowAutoRedirect = false }) });
    Assert.NotNull(ok);
  }

  [Fact]
  public async Task A_307_from_the_token_endpoint_does_not_replay_the_secret_elsewhere()
  {
    await using var catcher = new Stub((e, _) => Stub.Json(e, 400, "{\"error\":\"x\"}"));
    Stub? asrv = null;
    asrv = new Stub(async (e, _) =>
    {
      if (e.Request.Path.Contains("oauth-authorization-server", StringComparison.Ordinal))
      {
        await Stub.Json(e, 200, "{\"issuer\":\"" + asrv!.Origin + "\",\"token_endpoint\":\"" + asrv.Origin + "/token\"}");
      }
      else if (e.Request.Path == "/token")
      {
        e.Response.SetHeader("Location", catcher.Origin + "/steal");
        await Stub.Json(e, 307, "{}");
      }
      else
      {
        await Stub.Json(e, 404, "{}");
      }
    });
    await using var _ = asrv;
    // The server is its own authorization server (no resource metadata), pinned by the application.
    using var client = new OAuthClient(new OAuthClientOptions(new Uri(asrv.Origin + "/mcp"))
    {
      ClientCredentials = new ClientCredentialsGrant("cid", "SECRET-123") { ExpectedIssuer = asrv.Origin },
      HttpHandler = new SocketsHttpHandler { AllowAutoRedirect = false },
    });

    var ex = await Assert.ThrowsAsync<OAuthException>(() => client.AuthorizeAsync(null, null, false, Ct).AsTask());

    Assert.Contains("HTTP 307", ex.Message, StringComparison.Ordinal);
    Assert.Empty(catcher.Requests);
    Assert.DoesNotContain("SECRET-123", ex.Message, StringComparison.Ordinal);
  }

  private sealed class PassThrough(HttpMessageHandler inner) : DelegatingHandler(inner);

  private sealed class Opaque : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
  }

  // ----- L5, I6 -----------------------------------------------------------------------------------------------------------------------

  private sealed class Xor(byte key, bool fail = false) : ITokenProtector
  {
    public byte[] Protect(byte[] plain) => [.. plain.Select(b => (byte)(b ^ key))];

    public byte[] Unprotect(byte[] bytes) => fail ? throw new CryptographicException("key unavailable") : Protect(bytes);
  }

  private static string TempDir()
  {
    var dir = Path.Combine(Path.GetTempPath(), "oauth-review-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    return dir;
  }

  [Fact]
  public async Task A_protector_that_fails_does_not_cost_the_user_the_store()
  {
    var dir = TempDir();
    try
    {
      var path = Path.Combine(dir, "prot.json");
      var good = new Xor(0x5a);
      using (var first = new FileTokenStore(path, good))
      {
        await first.SetAsync("one", new TokenRecord { AccessToken = "A" }, Ct);
        await first.SetAsync("two", new TokenRecord { AccessToken = "B" }, Ct);
      }

      var before = await File.ReadAllBytesAsync(path, Ct);
      using (var broken = new FileTokenStore(path, new Xor(0, fail: true)))
      {
        Assert.Null(await broken.GetAsync("one", Ct));
        await Assert.ThrowsAsync<IOException>(() => broken.SetAsync("three", new TokenRecord { AccessToken = "C" }, Ct).AsTask());
        await Assert.ThrowsAsync<IOException>(() => broken.RemoveAsync("one", Ct).AsTask());
      }

      Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));
      using var recovered = new FileTokenStore(path, good);
      Assert.Equal("A", (await recovered.GetAsync("one", Ct))!.AccessToken);
      Assert.Equal("B", (await recovered.GetAsync("two", Ct))!.AccessToken);
    }
    finally
    {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Fact]
  public async Task A_store_file_writable_by_others_is_not_trusted_or_overwritten()
  {
    if (OperatingSystem.IsWindows())
    {
      return;
    }

    var dir = TempDir();
    try
    {
      var path = Path.Combine(dir, "tokens.json");
      using (var seed = new FileTokenStore(path))
      {
        await seed.SetAsync("k", new TokenRecord { AccessToken = "mine" }, Ct);
      }

      File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupWrite);
      var before = await File.ReadAllBytesAsync(path, Ct);
      using var store = new FileTokenStore(path);

      Assert.Null(await store.GetAsync("k", Ct));
      var ex = await Assert.ThrowsAsync<IOException>(() => store.SetAsync("k", new TokenRecord { AccessToken = "x" }, Ct).AsTask());
      Assert.Contains("writable", ex.Message, StringComparison.Ordinal);
      Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct));

      File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
      Assert.Equal("mine", (await store.GetAsync("k", Ct))!.AccessToken);
    }
    finally
    {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Fact]
  public async Task Temporary_files_are_removed_after_a_failed_write_and_when_abandoned_by_a_crash()
  {
    var dir = TempDir();
    try
    {
      // A failed write: the final path is a directory, so the move fails.
      var blocked = Path.Combine(dir, "blocked.json");
      Directory.CreateDirectory(blocked);
      using (var store = new FileTokenStore(blocked))
      {
        await Assert.ThrowsAnyAsync<Exception>(() => store.SetAsync("k", new TokenRecord { AccessToken = "x" }, Ct).AsTask());
      }

      Assert.Empty(Directory.GetFiles(dir, "*.tmp"));

      // A crash left one behind long ago, and a write is in flight in another process right now.
      var path = Path.Combine(dir, "tokens.json");
      var abandoned = path + ".abandoned.tmp";
      var live = path + ".live.tmp";
      await File.WriteAllTextAsync(abandoned, "old", Ct);
      await File.WriteAllTextAsync(live, "new", Ct);
      File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddHours(-2));
      using (var store = new FileTokenStore(path))
      {
        await store.SetAsync("k", new TokenRecord { AccessToken = "x" }, Ct);
      }

      Assert.False(File.Exists(abandoned));
      Assert.True(File.Exists(live));
    }
    finally
    {
      Directory.Delete(dir, recursive: true);
    }
  }

  // ----- L6 ---------------------------------------------------------------------------------------------------------------------------

  [Fact]
  public async Task A_scope_less_insufficient_scope_does_not_open_a_new_authorization_for_every_request()
  {
    var tokens = 0;
    Stub? server = null;
    server = new Stub(async (e, _) =>
    {
      var path = e.Request.Path;
      if (path.Contains("oauth-authorization-server", StringComparison.Ordinal))
      {
        await Stub.Json(e, 200, "{\"issuer\":\"" + server!.Origin + "\",\"token_endpoint\":\"" + server.Origin + "/token\"}");
      }
      else if (path == "/token")
      {
        await Stub.Json(e, 200, TokenJson("T" + Interlocked.Increment(ref tokens)));
      }
      else if (path.Contains("protected-resource", StringComparison.Ordinal))
      {
        await Stub.Json(e, 404, "{}");
      }
      else
      {
        e.Response.SetHeader("WWW-Authenticate", "Bearer error=\"insufficient_scope\"");
        await Stub.Json(e, 403, "{}");
      }
    });
    await using var _ = server;
    var clock = new ManualTimeProvider();
    using var oauth = new OAuthClient(new OAuthClientOptions(new Uri(server.Origin + "/mcp"))
    {
      ClientCredentials = new ClientCredentialsGrant("c", "s") { ExpectedIssuer = server.Origin },
      TimeProvider = clock,
    });
    using var http = oauth.CreateHttpClient();

    for (var i = 0; i < 3; i++)
    {
      using var refused = await http.GetAsync(server.Origin + "/mcp", Ct);
      Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    Assert.Equal(1, tokens);

    clock.Advance(TimeSpan.FromMinutes(6));
    using var later = await http.GetAsync(server.Origin + "/mcp", Ct);
    Assert.Equal(HttpStatusCode.Forbidden, later.StatusCode);
    Assert.Equal(2, tokens);
  }
}
