using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Auth;
using RecursiveAI.OneModelContext.Auth.Server;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.Jose;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>The scope rules: a scope allow-list is required wherever a client acts for itself, and scope strings are well-formed tokens.</summary>
public sealed class AuthorizationServerScopeTests : IDisposable
{
  private readonly AsHarness _h = new() { ScopesSupported = [] };

  public AuthorizationServerScopeTests() => _h.Build([_h.Key1], registration: true);

  public void Dispose() => _h.Dispose();

  private async Task SaveAsync(string id, IReadOnlyList<string>? scopes)
  {
    await _h.Store.SaveClientAsync(new RegisteredClient
    {
      ClientId = id,
      RedirectUris = [AsHarness.RedirectUri],
      TokenEndpointAuthMethod = "client_secret_post",
      SecretHash = ClientSecret.Hash(AsHarness.ClientSecretValue),
      GrantTypes = ["authorization_code", "client_credentials"],
      Scopes = scopes,
      CreatedAt = _h.Time.GetUtcNow(),
    }, TestContext.Current.CancellationToken);
  }

  private Task<AsExchange> ClientCredentials(string id, string? scope) =>
    _h.TokenAsync([("grant_type", "client_credentials"), ("client_id", id), ("client_secret", AsHarness.ClientSecretValue), ("resource", AsHarness.Resource), ("scope", scope)]);

  [Fact]
  public async Task Client_credentials_are_refused_without_any_scope_allow_list()
  {
    await SaveAsync("open", scopes: null);

    var refused = await ClientCredentials("open", "admin");
    var noScope = await ClientCredentials("open", null);

    Assert.Equal("invalid_scope", refused.Json.GetProperty("error").GetString());
    Assert.Equal("invalid_scope", noScope.Json.GetProperty("error").GetString());
  }

  [Fact]
  public async Task Client_credentials_work_inside_the_clients_own_list_and_not_outside_it()
  {
    await SaveAsync("listed", ["files:read"]);

    var ok = await ClientCredentials("listed", "files:read");
    var outside = await ClientCredentials("listed", "admin");

    Assert.Equal(200, ok.Status);
    Assert.Equal("files:read", ok.Json.GetProperty("scope").GetString());
    Assert.Equal("invalid_scope", outside.Json.GetProperty("error").GetString());
  }

  [Fact]
  public async Task Client_credentials_work_when_the_server_has_a_list()
  {
    _h.ScopesSupported = ["files:read"];
    _h.Build([_h.Key1]);
    await SaveAsync("open", scopes: null);

    var ok = await ClientCredentials("open", "files:read");
    var outside = await ClientCredentials("open", "admin");

    Assert.Equal(200, ok.Status);
    Assert.Equal("invalid_scope", outside.Json.GetProperty("error").GetString());
  }

  [Fact]
  public async Task A_dynamically_registered_client_gets_no_scope_unless_the_server_has_a_list()
  {
    var body = """{"redirect_uris":["http://127.0.0.1/cb"],"application_type":"native","token_endpoint_auth_method":"none"}""";
    var created = await _h.SendAsync(new AsExchange("POST", "/register", string.Empty, body, "application/json"));
    var clientId = created.Json.GetProperty("client_id").GetString()!;
    var (_, challenge) = AsHarness.Pkce();

    var refused = await _h.AuthorizeAsync(challenge, ("client_id", clientId), ("redirect_uri", "http://127.0.0.1:4000/cb"), ("scope", "admin"));

    Assert.Equal(302, refused.Status);
    Assert.Equal("invalid_scope", refused.RedirectQuery["error"]);

    _h.ScopesSupported = ["files:read"];
    _h.Build([_h.Key1], registration: true);
    created = await _h.SendAsync(new AsExchange("POST", "/register", string.Empty, body, "application/json"));
    clientId = created.Json.GetProperty("client_id").GetString()!;
    var allowed = await _h.AuthorizeAsync(challenge, ("client_id", clientId), ("redirect_uri", "http://127.0.0.1:4000/cb"), ("scope", "files:read"));
    var outside = await _h.AuthorizeAsync(challenge, ("client_id", clientId), ("redirect_uri", "http://127.0.0.1:4000/cb"), ("scope", "admin"));

    Assert.True(allowed.RedirectQuery.ContainsKey("code"));
    Assert.Equal("invalid_scope", outside.RedirectQuery["error"]);
  }

  [Theory]
  [InlineData("a\"b")]
  [InlineData("a\\b")]
  [InlineData("caf\u00e9")]
  [InlineData("a\tb")]
  [InlineData("a\u007fb")]
  public async Task Scope_strings_must_be_scope_tokens_even_without_a_list(string scope)
  {
    await SaveAsync("listed", ["files:read"]);
    var (_, challenge) = AsHarness.Pkce();

    var authorize = await _h.AuthorizeAsync(challenge, ("client_id", "app"), ("scope", scope));
    var token = await ClientCredentials("listed", scope);

    Assert.Equal("invalid_scope", authorize.RedirectQuery["error"]);
    Assert.Equal("invalid_scope", token.Json.GetProperty("error").GetString());
  }

  [Fact]
  public async Task Interactive_grants_without_a_list_still_work_but_only_with_well_formed_scopes()
  {
    var (_, challenge) = AsHarness.Pkce();

    var ok = await _h.AuthorizeAsync(challenge, ("scope", "anything:goes"));

    Assert.True(ok.RedirectQuery.ContainsKey("code"));
  }
}

/// <summary>Redirect URIs: what is registered, what is requested, and that a redirect is never sent without a safe Location.</summary>
public sealed class AuthorizationServerRedirectTests : IDisposable
{
  private readonly AsHarness _h = new() { ScopesSupported = ["files:read"] };

  public AuthorizationServerRedirectTests() => _h.Build([_h.Key1], registration: true);

  public void Dispose() => _h.Dispose();

  [Theory]
  [InlineData("https://app.test/cb\r\nSet-Cookie: x=1")]
  [InlineData("https://app.test/cb\tx")]
  [InlineData("https://app.test/cb x")]
  [InlineData("https://app.test/cb\u0000")]
  [InlineData("https://app.test/\\cb")]
  [InlineData("https://app.t\u00e9st/cb")]
  public async Task A_requested_redirect_uri_with_control_characters_is_an_error_page_not_a_redirect(string redirect)
  {
    var (_, challenge) = AsHarness.Pkce();

    var response = await _h.AuthorizeAsync(challenge, ("redirect_uri", redirect));

    Assert.Equal(400, response.Status);
    Assert.Null(response.ResponseHeaders.GetFirst("Location"));
  }

  [Fact]
  public async Task A_registered_uri_that_cannot_be_sent_safely_is_never_redirected_to()
  {
    await _h.Store.SaveClientAsync(new RegisteredClient
    {
      ClientId = "odd",
      RedirectUris = ["https://app.test/cb\tx"],
      CreatedAt = _h.Time.GetUtcNow(),
    }, TestContext.Current.CancellationToken);
    var (_, challenge) = AsHarness.Pkce();

    var omitted = await _h.AuthorizeAsync(challenge, ("client_id", "odd"), ("redirect_uri", null));

    Assert.NotEqual(302, omitted.Status);
    Assert.Null(omitted.ResponseHeaders.GetFirst("Location"));
  }

  [Theory]
  [InlineData("""{"redirect_uris":["https://a.test/cb\tx"]}""")]
  [InlineData("""{"redirect_uris":["https://a.test/cb\r\nX: y"]}""")]
  [InlineData("""{"redirect_uris":["https://a.test/cb x"]}""")]
  [InlineData("""{"redirect_uris":["http://u@127.0.0.1/cb"],"application_type":"native"}""")]
  [InlineData("""{"redirect_uris":["http://127.0.0.1:80@evil.test/cb"],"application_type":"native"}""")]
  public async Task Registration_refuses_redirect_uris_with_control_characters_or_userinfo(string body)
  {
    var response = await _h.SendAsync(new AsExchange("POST", "/register", string.Empty, body, "application/json"));

    Assert.Equal(400, response.Status);
    Assert.Equal("invalid_redirect_uri", response.Json.GetProperty("error").GetString());
  }

  [Theory]
  [InlineData("http://127.0.0.1:80@evil.test/cb")]
  [InlineData("http://127.0.0.1:9@127.0.0.1/cb")]
  [InlineData("http://user:pw@127.0.0.1:9/cb")]
  [InlineData("http://localhost:1@evil.test/cb")]
  [InlineData("http://127.0.0.1\\@evil.test/cb")]
  public async Task A_userinfo_decoy_does_not_satisfy_the_loopback_port_rule(string redirect)
  {
    var (_, challenge) = AsHarness.Pkce();

    var response = await _h.AuthorizeAsync(challenge, ("redirect_uri", redirect));

    Assert.Equal(400, response.Status);
    Assert.Null(response.ResponseHeaders.GetFirst("Location"));
  }

  [Fact]
  public async Task A_loopback_redirect_may_still_change_only_its_port()
  {
    var (_, challenge) = AsHarness.Pkce();

    var ok = await _h.AuthorizeAsync(challenge, ("redirect_uri", "http://127.0.0.1:51234/cb"));
    var otherPath = await _h.AuthorizeAsync(challenge, ("redirect_uri", "http://127.0.0.1:51234/other"));

    Assert.True(ok.RedirectQuery.ContainsKey("code"));
    Assert.Equal(400, otherPath.Status);
  }
}

/// <summary>Replaying an authorization code after the server forgot the code itself must still revoke what it produced.</summary>
public sealed class AuthorizationCodeReplayTests : IDisposable
{
  private readonly string _dir = Path.Combine(Path.GetTempPath(), "omc-replay-" + Guid.NewGuid().ToString("N"));

  public void Dispose()
  {
    if (Directory.Exists(_dir))
    {
      Directory.Delete(_dir, recursive: true);
    }
  }

  [Theory]
  [InlineData("memory")]
  [InlineData("file")]
  public async Task A_late_replay_after_the_purge_still_revokes_the_family(string kind)
  {
    MemoryAuthorizationStore store = kind == "file" ? new FileAuthorizationStore(Path.Combine(_dir, "store.json")) : new MemoryAuthorizationStore();
    using var h = new AsHarness(store: store);
    var (code, verifier) = await h.CodeAsync();
    var tokens = await h.RedeemAsync(code, verifier);
    var refresh = tokens.GetProperty("refresh_token").GetString()!;

    // Long after the code expired, a purge pass runs (any token request triggers one).
    h.Time.Advance(TimeSpan.FromMinutes(30));
    await h.TokenAsync([("grant_type", "authorization_code"), ("code", "nothing"), ("code_verifier", verifier), ("client_id", "app")]);
    var replay = await h.TokenAsync([("grant_type", "authorization_code"), ("code", code), ("code_verifier", verifier), ("client_id", "app"), ("redirect_uri", AsHarness.RedirectUri)]);
    var refreshed = await h.TokenAsync([("grant_type", "refresh_token"), ("refresh_token", refresh), ("client_id", "app")]);

    Assert.Equal("invalid_grant", replay.Json.GetProperty("error").GetString());
    Assert.Equal("invalid_grant", refreshed.Json.GetProperty("error").GetString());
    Assert.Contains(h.SecurityEvents, e => e.Contains("replayed", StringComparison.Ordinal));
  }

  [Fact]
  public async Task Unredeemed_expired_codes_are_still_purged_and_spent_markers_go_with_the_family()
  {
    var store = new MemoryAuthorizationStore();
    var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    AuthorizationCodeGrant Grant(string hash) => new()
    {
      CodeHash = hash,
      ClientId = "app",
      RedirectUri = AsHarness.RedirectUri,
      Subject = "alice",
      Resource = AsHarness.Resource,
      CodeChallenge = "c",
      FamilyId = "f",
      ExpiresAt = now.AddMinutes(1),
      FamilyExpiresAt = now.AddDays(90),
    };

    await store.SaveCodeAsync(Grant("unused"), TestContext.Current.CancellationToken);
    await store.SaveCodeAsync(Grant("spent"), TestContext.Current.CancellationToken);
    await store.RedeemCodeAsync("spent", now, TestContext.Current.CancellationToken);

    await store.PurgeAsync(now.AddDays(1), TestContext.Current.CancellationToken);
    Assert.Equal(RedemptionStatus.Unknown, (await store.RedeemCodeAsync("unused", now.AddDays(1), TestContext.Current.CancellationToken)).Status);
    Assert.Equal(RedemptionStatus.AlreadyRedeemed, (await store.RedeemCodeAsync("spent", now.AddDays(1), TestContext.Current.CancellationToken)).Status);

    await store.PurgeAsync(now.AddDays(91), TestContext.Current.CancellationToken);
    Assert.Equal(RedemptionStatus.Unknown, (await store.RedeemCodeAsync("spent", now.AddDays(91), TestContext.Current.CancellationToken)).Status);
  }
}

/// <summary>Access-token type, and the ID-JAG defaults.</summary>
public sealed class AuthorizationServerTokenRuleTests : IDisposable
{
  private readonly AsHarness _h = new();

  public void Dispose() => _h.Dispose();

  private string Token(string type)
  {
    var now = _h.Time.GetUtcNow().ToUnixTimeSeconds();
    var claims = new Dictionary<string, object>
    {
      ["iss"] = AsHarness.Issuer,
      ["sub"] = "alice",
      ["aud"] = AsHarness.Resource,
      ["iat"] = now,
      ["exp"] = now + 300,
      ["jti"] = Guid.NewGuid().ToString("N"),
    };
    return _h.Key1.SignJwt(type, JsonSerializer.SerializeToUtf8Bytes(claims));
  }

  [Fact]
  public async Task The_servers_own_validator_requires_the_access_token_type()
  {
    var request = new TokenValidationRequest(AsHarness.Resource);
    var own = _h.Server.CreateTokenValidator();

    var wrong = await own.ValidateAsync(Token("JWT"), request, TestContext.Current.CancellationToken);
    var right = await own.ValidateAsync(Token("at+jwt"), request, TestContext.Current.CancellationToken);
    var prefixed = await own.ValidateAsync(Token("application/at+jwt"), request, TestContext.Current.CancellationToken);

    Assert.False(wrong.IsValid);
    Assert.True(right.IsValid, right.Description);
    Assert.True(prefixed.IsValid, prefixed.Description);
  }

  [Fact]
  public async Task The_plain_validator_checks_the_type_only_when_asked()
  {
    var keys = await _h.PublishedKeysAsync();
    var request = new TokenValidationRequest(AsHarness.Resource);
    var lenient = _h.PlainValidator(keys);
    var strict = new JwtTokenValidator(AsHarness.Issuer, new StaticJwksProvider(keys), _h.Time) { RequireAccessTokenType = true };

    Assert.True((await lenient.ValidateAsync(Token("JWT"), request, TestContext.Current.CancellationToken)).IsValid);
    Assert.False((await strict.ValidateAsync(Token("JWT"), request, TestContext.Current.CancellationToken)).IsValid);
    Assert.True((await strict.ValidateAsync(Token("at+jwt"), request, TestContext.Current.CancellationToken)).IsValid);
  }

  [Fact]
  public void Id_jags_need_a_confidential_client_unless_the_host_says_otherwise()
  {
    Assert.True(new EnterpriseAuthorizationOptions { IdentityProviders = [] }.RequireConfidentialClient);
  }

  [Fact]
  public void Secrets_use_a_fast_salted_hash_and_stay_constant_time_comparable()
  {
    var secret = ClientSecret.Generate();
    var first = ClientSecret.Hash(secret);

    Assert.StartsWith("hmac-sha256$", first, StringComparison.Ordinal);
    Assert.NotEqual(first, ClientSecret.Hash(secret));
    Assert.True(ClientSecret.Verify(secret, first));
    Assert.False(ClientSecret.Verify(secret + "x", first));
    Assert.False(ClientSecret.Verify(secret, "hmac-sha256$!!$!!"));
    Assert.False(ClientSecret.Verify(secret, "hmac-sha256$AA=="));
  }
}

/// <summary>The metadata-document cache stays useful under a flood, and one slow host cannot take every fetch slot.</summary>
public sealed class ClientMetadataCacheTests : IDisposable
{
  private readonly LoopbackServer _server = new();
  private readonly LoopbackServer _other = new();
  private AsHarness? _h;

  public void Dispose()
  {
    _h?.Dispose();
    _server.Dispose();
    _other.Dispose();
  }

  private AsHarness Harness(int maxEntries = 1000, int perHost = 2, TimeSpan? backoff = null)
  {
    _h?.Dispose();
    return _h = new AsHarness(cimd: new ClientMetadataDocumentOptions
    {
      MaxCacheEntries = maxEntries,
      MaxFetchesPerHost = perHost,
      HostFailureBackoff = backoff ?? TimeSpan.Zero,
      Fetch = new GuardedFetchOptions
      {
        Addresses = new AddressPolicy().Allow("127.0.0.1"),
        CertificateValidation = LoopbackServer.Trust,
      },
    });
  }

  private static string Document(LoopbackServer server, string path)
  {
    var url = server.Url(path);
    server.AddJson(path, $$"""{"client_id":"{{url}}","redirect_uris":["http://127.0.0.1/cb"],"token_endpoint_auth_method":"none"}""", "max-age=600");
    return url;
  }

  private static Task<AsExchange> Ask(AsHarness h, string clientId) =>
    h.AuthorizeAsync(AsHarness.Pkce().Challenge, ("client_id", clientId), ("redirect_uri", "http://127.0.0.1/cb"));

  [Fact]
  public async Task A_flood_of_junk_client_ids_does_not_turn_caching_off()
  {
    var h = Harness(maxEntries: 3);
    for (var i = 0; i < 20; i++)
    {
      await Ask(h, _server.Url("/missing" + i));
    }

    // Past the failure lifetime the junk is expired, so a real document is cached again.
    h.Time.Advance(TimeSpan.FromMinutes(1));
    var hitsBefore = _server.Hits;
    var good = Document(_server, "/good.json");
    await Ask(h, good);
    await Ask(h, good);

    Assert.Equal(hitsBefore + 1, _server.Hits);
  }

  [Fact]
  public async Task At_the_limit_the_entry_that_expires_first_makes_room()
  {
    var h = Harness(maxEntries: 2);
    var a = Document(_server, "/a.json");
    var b = Document(_server, "/b.json");
    var c = Document(_server, "/c.json");

    await Ask(h, a);
    h.Time.Advance(TimeSpan.FromSeconds(1));
    await Ask(h, b);
    h.Time.Advance(TimeSpan.FromSeconds(1));
    await Ask(h, c);
    var hits = _server.Hits;
    await Ask(h, b);
    await Ask(h, c);
    Assert.Equal(hits, _server.Hits);

    await Ask(h, a);
    Assert.Equal(hits + 1, _server.Hits);
  }

  [Theory]
  [InlineData("https://a.test/x/../c.json")]
  [InlineData("https://user@a.test/c.json")]
  [InlineData("https://a.test/c.json#f")]
  [InlineData("http://a.test/c.json")]
  public async Task Ids_that_can_never_be_fetched_take_no_cache_entry_and_no_request(string clientId)
  {
    var h = Harness(maxEntries: 1);
    var good = Document(_server, "/good.json");
    await Ask(h, good);
    var hits = _server.Hits;

    for (var i = 0; i < 5; i++)
    {
      await Ask(h, clientId);
    }

    await Ask(h, good);
    Assert.Equal(hits, _server.Hits);
  }

  [Fact]
  public async Task One_slow_host_cannot_hold_every_slot_while_other_hosts_still_sign_in()
  {
    var h = Harness(perHost: 1);
    _server.Add("/slow", new LoopbackServer.Route(Body: new byte[100], Stall: true, ContentLength: 100));
    var slow = _server.Url("/slow");
    var other = Document(_other, "/client.json");

    var first = Ask(h, slow);
    await WaitForHitsAsync(_server, 1);
    var second = await Ask(h, _server.Url("/another"));
    var fine = await Ask(h, other);

    Assert.Equal(400, second.Status);
    Assert.Equal(1, _server.Hits);
    Assert.True(fine.RedirectQuery.ContainsKey("code"));

    h.Time.Advance(TimeSpan.FromSeconds(10));
    Assert.Equal(400, (await first).Status);
  }

  [Fact]
  public async Task A_host_that_timed_out_is_left_alone_briefly()
  {
    var h = Harness(backoff: TimeSpan.FromSeconds(10));
    _server.Add("/slow", new LoopbackServer.Route(Body: new byte[100], Stall: true, ContentLength: 100));
    var stalled = Ask(h, _server.Url("/slow"));
    await WaitForHitsAsync(_server, 1);
    h.Time.Advance(TimeSpan.FromSeconds(6));
    Assert.Equal(400, (await stalled).Status);
    var hits = _server.Hits;
    var good = Document(_server, "/good.json");

    var during = await Ask(h, good);
    Assert.Equal(400, during.Status);
    Assert.Equal(hits, _server.Hits);

    h.Time.Advance(TimeSpan.FromSeconds(5));
    Assert.True((await Ask(h, good)).RedirectQuery.ContainsKey("code"));
  }

  [Theory]
  [InlineData("missing")]
  [InlineData("html")]
  [InlineData("huge")]
  public async Task A_bad_url_on_a_host_never_keeps_that_hosts_other_clients_from_signing_in(string kind)
  {
    var h = Harness(backoff: TimeSpan.FromSeconds(10));
    _server.Add("/bad", kind switch
    {
      "html" => new LoopbackServer.Route(ContentType: "text/html", Body: "<p>x</p>"u8.ToArray()),
      "huge" => new LoopbackServer.Route(Body: new byte[64 * 1024]),
      _ => new LoopbackServer.Route(404, Body: "{}"u8.ToArray()),
    });
    var good = Document(_server, "/good.json");

    Assert.Equal(400, (await Ask(h, _server.Url("/bad"))).Status);
    var legit = await Ask(h, good);

    Assert.True(legit.RedirectQuery.ContainsKey("code"));
  }

  private static async Task WaitForHitsAsync(LoopbackServer server, int hits)
  {
    for (var i = 0; i < 500 && server.Hits < hits; i++)
    {
      await Task.Delay(10, TestContext.Current.CancellationToken);
    }

    Assert.True(server.Hits >= hits);
  }
}
