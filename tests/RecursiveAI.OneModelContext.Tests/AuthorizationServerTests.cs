using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Auth;
using RecursiveAI.OneModelContext.Auth.Server;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.Hosting;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.Jose;
using RecursiveAI.OneModelContext.Protocol;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>An authorization server wired to a memory store, a manual clock and keys generated for this run.</summary>
internal sealed class AsHarness : IDisposable
{
  internal const string Issuer = "https://as.test";
  internal const string Resource = "https://mcp.test/mcp";
  internal const string OtherResource = "https://other.test/mcp";
  internal const string RedirectUri = "https://app.test/cb";
  internal const string ClientSecretValue = "svc-secret-0123456789abcdefghijklmnop";

  private readonly List<IDisposable> _disposables = [];

  internal AsHarness(
    IReadOnlyList<JsonWebSigningKey>? keys = null,
    ClientMetadataDocumentOptions? cimd = null,
    bool cimdOff = false,
    EnterpriseAuthorizationOptions? enterprise = null,
    bool registration = false,
    bool allowMissingResource = false,
    MemoryAuthorizationStore? store = null)
  {
    Store = store ?? new MemoryAuthorizationStore();
    Rsa = RSA.Create(2048);
    Ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    Key1 = JsonWebSigningKey.FromRsa(Rsa, "k1", "RS256");
    Key2 = JsonWebSigningKey.FromECDsa(Ec, "k2");
    ClientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    _disposables.Add(Rsa);
    _disposables.Add(Ec);
    _disposables.Add(ClientKey);
    Decide = _ => AuthorizationDecision.Approve("alice");
    Build(keys ?? [Key1], cimd, cimdOff, enterprise, registration, allowMissingResource);
    Seed().GetAwaiter().GetResult();
  }

  /// <summary>The server-wide scope list used by the next <see cref="Build"/>; empty means none, which fails closed for some grants.</summary>
  internal IReadOnlyList<string> ScopesSupported { get; set; } = ["files:read", "files:write", "admin"];

  internal ManualTimeProvider Time { get; } = new();

  internal MemoryAuthorizationStore Store { get; }

  internal RSA Rsa { get; }

  internal ECDsa Ec { get; }

  internal ECDsa ClientKey { get; }

  internal JsonWebSigningKey Key1 { get; }

  internal JsonWebSigningKey Key2 { get; }

  internal AuthorizationServer Server { get; private set; } = null!;

  internal Func<AuthorizationRequestContext, AuthorizationDecision> Decide { get; set; }

  internal List<AuthorizationRequestContext> Requests { get; } = [];

  internal List<string> SecurityEvents { get; } = [];

  internal void Build(IReadOnlyList<JsonWebSigningKey> keys, ClientMetadataDocumentOptions? cimd = null, bool cimdOff = false, EnterpriseAuthorizationOptions? enterprise = null, bool registration = false, bool allowMissingResource = false)
  {
    Server?.Dispose();
#pragma warning disable CS0618
    Server = new AuthorizationServer(new AuthorizationServerOptions
    {
      Issuer = new Uri(Issuer),
      Resources = [Resource, OtherResource],
      SigningKeys = keys,
      Store = Store,
      ScopesSupported = ScopesSupported,
      TimeProvider = Time,
      AllowMissingResource = allowMissingResource,
      ClientMetadataDocuments = cimdOff ? null : cimd ?? new ClientMetadataDocumentOptions(),
      EnterpriseAuthorization = enterprise,
      DynamicClientRegistration = registration,
      ResumeKey = RandomNumberGenerator.GetBytes(32),
      OnSecurityEvent = SecurityEvents.Add,
      AuthorizeUser = (context, _) =>
      {
        Requests.Add(context);
        return ValueTask.FromResult(Decide(context));
      },
    });
#pragma warning restore CS0618
  }

  private async Task Seed()
  {
    await Store.SaveClientAsync(new RegisteredClient
    {
      ClientId = "app",
      Name = "App",
      RedirectUris = [RedirectUri, "http://127.0.0.1/cb"],
      CreatedAt = Time.GetUtcNow(),
    }, TestContext.Current.CancellationToken);
    await Store.SaveClientAsync(new RegisteredClient
    {
      ClientId = "svc",
      RedirectUris = [RedirectUri],
      TokenEndpointAuthMethod = "client_secret_basic",
      SecretHash = ClientSecret.Hash(ClientSecretValue),
      GrantTypes = ["authorization_code", "refresh_token", "client_credentials", "urn:ietf:params:oauth:grant-type:jwt-bearer"],
      Scopes = ["files:read", "files:write"],
      CreatedAt = Time.GetUtcNow(),
    }, TestContext.Current.CancellationToken);
    var jwks = new StringBuilder();
    var p = ClientKey.ExportParameters(false);
    jwks.Append("{\"keys\":[{\"kty\":\"EC\",\"kid\":\"ck\",\"crv\":\"P-256\",\"x\":\"").Append(System.Buffers.Text.Base64Url.EncodeToString(p.Q.X!)).Append("\",\"y\":\"").Append(System.Buffers.Text.Base64Url.EncodeToString(p.Q.Y!)).Append("\"}]}");
    await Store.SaveClientAsync(new RegisteredClient
    {
      ClientId = "kj",
      RedirectUris = [RedirectUri],
      TokenEndpointAuthMethod = "private_key_jwt",
      JwksJson = jwks.ToString(),
      GrantTypes = ["client_credentials"],
      CreatedAt = Time.GetUtcNow(),
    }, TestContext.Current.CancellationToken);
  }

  internal static (string Verifier, string Challenge) Pkce()
  {
    var verifier = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
    return (verifier, System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
  }

  internal static string Encode(IEnumerable<(string Name, string? Value)> parameters) =>
    string.Join('&', parameters.Where(p => p.Value is not null).Select(p => Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(p.Value!)));

  internal async Task<AsExchange> SendAsync(AsExchange exchange)
  {
    Assert.True(await Server.HandleAsync(exchange));
    return exchange;
  }

  /// <summary>Starts an authorization request; null values remove a default parameter.</summary>
  internal Task<AsExchange> AuthorizeAsync(string challenge, params (string Name, string? Value)[] overrides)
  {
    var parameters = new Dictionary<string, string?>
    {
      ["response_type"] = "code",
      ["client_id"] = "app",
      ["redirect_uri"] = RedirectUri,
      ["code_challenge"] = challenge,
      ["code_challenge_method"] = "S256",
      ["state"] = "st-1",
      ["scope"] = "files:read",
      ["resource"] = Resource,
    };
    foreach (var (name, value) in overrides)
    {
      parameters[name] = value;
    }

    return SendAsync(new AsExchange("GET", "/authorize", Encode(parameters.Select(kv => (kv.Key, kv.Value)))));
  }

  internal Task<AsExchange> TokenAsync(IEnumerable<(string Name, string? Value)> form, params (string Name, string Value)[] headers) =>
    SendAsync(new AsExchange("POST", "/token", string.Empty, Encode(form), headers: headers));

  /// <summary>Runs a full authorization and returns the code from the redirect.</summary>
  internal async Task<(string Code, string Verifier)> CodeAsync(string clientId = "app", string redirect = RedirectUri, params (string Name, string? Value)[] overrides)
  {
    var (verifier, challenge) = Pkce();
    var response = await AuthorizeAsync(challenge, [("client_id", clientId), ("redirect_uri", redirect), .. overrides]);
    Assert.Equal(302, response.Status);
    var q = response.RedirectQuery;
    Assert.True(q.ContainsKey("code"), response.ResponseHeaders.GetFirst("Location"));
    return (q["code"], verifier);
  }

  internal async Task<JsonElement> RedeemAsync(string code, string verifier, string clientId = "app", string redirect = RedirectUri, params (string Name, string? Value)[] extra)
  {
    var response = await TokenAsync([("grant_type", "authorization_code"), ("code", code), ("code_verifier", verifier), ("client_id", clientId), ("redirect_uri", redirect), .. extra]);
    Assert.Equal(200, response.Status);
    return response.Json;
  }

  internal async Task<JsonElement> FullFlowAsync()
  {
    var (code, verifier) = await CodeAsync();
    return await RedeemAsync(code, verifier);
  }

  internal async Task<JsonWebKeySet> PublishedKeysAsync()
  {
    var response = await SendAsync(new AsExchange("GET", "/jwks"));
    Assert.Equal(200, response.Status);
    return JsonWebKeySet.Parse(Encoding.UTF8.GetBytes(response.BodyText))!;
  }

  internal JwtTokenValidator PlainValidator(JsonWebKeySet keys) => new(Issuer, new StaticJwksProvider(keys), Time);

  public void Dispose()
  {
    Server.Dispose();
    foreach (var d in _disposables)
    {
      d.Dispose();
    }
  }
}

public sealed class AuthorizationServerTests : IDisposable
{
  private readonly AsHarness _h = new();

  public void Dispose() => _h.Dispose();

  private static string BasicHeader(string id, string secret) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.EscapeDataString(id) + ":" + Uri.EscapeDataString(secret)));

  // ------------------------------------------------------------------------------------------------ metadata

  [Fact]
  public async Task Metadata_advertises_pkce_iss_and_metadata_documents_and_the_endpoints()
  {
    foreach (var path in new[] { "/.well-known/oauth-authorization-server", "/.well-known/openid-configuration" })
    {
      var response = await _h.SendAsync(new AsExchange("GET", path));
      var doc = response.Json;

      Assert.Equal(200, response.Status);
      Assert.Equal(AsHarness.Issuer, doc.GetProperty("issuer").GetString());
      Assert.Equal("https://as.test/authorize", doc.GetProperty("authorization_endpoint").GetString());
      Assert.Equal("https://as.test/token", doc.GetProperty("token_endpoint").GetString());
      Assert.Equal("https://as.test/jwks", doc.GetProperty("jwks_uri").GetString());
      Assert.Equal("https://as.test/revoke", doc.GetProperty("revocation_endpoint").GetString());
      Assert.False(doc.TryGetProperty("registration_endpoint", out _));
      Assert.Equal(["S256"], doc.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(e => e.GetString()));
      Assert.True(doc.GetProperty("authorization_response_iss_parameter_supported").GetBoolean());
      Assert.True(doc.GetProperty("client_id_metadata_document_supported").GetBoolean());
      Assert.Contains("private_key_jwt", doc.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray().Select(e => e.GetString()));
      Assert.Contains("client_credentials", doc.GetProperty("grant_types_supported").EnumerateArray().Select(e => e.GetString()));
    }
  }

  [Fact]
  public async Task Requests_for_other_paths_are_not_handled_and_unsupported_methods_are_refused()
  {
    Assert.False(await _h.Server.HandleAsync(new AsExchange("GET", "/mcp")));
    Assert.False(_h.Server.IsServerPath("/register"));

    var token = await _h.SendAsync(new AsExchange("GET", "/token"));
    var authorize = await _h.SendAsync(new AsExchange("PUT", "/authorize"));
    Assert.Equal(405, token.Status);
    Assert.Equal(405, authorize.Status);
  }

  // ------------------------------------------------------------------------------------------------ PKCE

  [Fact]
  public async Task A_missing_code_challenge_is_refused_with_an_error_redirect_that_carries_state_and_iss()
  {
    var response = await _h.AuthorizeAsync("unused", ("code_challenge", null), ("code_challenge_method", null));

    Assert.Equal(302, response.Status);
    var q = response.RedirectQuery;
    Assert.Equal("invalid_request", q["error"]);
    Assert.Equal("st-1", q["state"]);
    Assert.Equal(AsHarness.Issuer, q["iss"]);
    Assert.False(q.ContainsKey("code"));
    Assert.Empty(_h.Requests);
  }

  [Theory]
  [InlineData("plain")]
  [InlineData("s256")]
  [InlineData(null)]
  public async Task Only_the_S256_method_is_accepted(string? method)
  {
    var (_, challenge) = AsHarness.Pkce();
    var response = await _h.AuthorizeAsync(challenge, ("code_challenge_method", method));

    Assert.Equal(302, response.Status);
    Assert.Equal("invalid_request", response.RedirectQuery["error"]);
    Assert.Empty(_h.Requests);
  }

  [Fact]
  public async Task A_wrong_or_malformed_code_verifier_is_refused_and_the_code_is_spent()
  {
    var (code, verifier) = await _h.CodeAsync();
    var wrong = await _h.TokenAsync([("grant_type", "authorization_code"), ("code", code), ("code_verifier", AsHarness.Pkce().Verifier), ("client_id", "app"), ("redirect_uri", AsHarness.RedirectUri)]);
    var right = await _h.TokenAsync([("grant_type", "authorization_code"), ("code", code), ("code_verifier", verifier), ("client_id", "app"), ("redirect_uri", AsHarness.RedirectUri)]);

    Assert.Equal(400, wrong.Status);
    Assert.Equal("invalid_grant", wrong.Json.GetProperty("error").GetString());
    Assert.Equal(400, right.Status);

    var (code2, _) = await _h.CodeAsync();
    var shortVerifier = await _h.TokenAsync([("grant_type", "authorization_code"), ("code", code2), ("code_verifier", "short"), ("client_id", "app"), ("redirect_uri", AsHarness.RedirectUri)]);
    Assert.Equal("invalid_grant", shortVerifier.Json.GetProperty("error").GetString());

    var missing = await _h.TokenAsync([("grant_type", "authorization_code"), ("code", code2), ("client_id", "app")]);
    Assert.Equal("invalid_request", missing.Json.GetProperty("error").GetString());
  }

  // ------------------------------------------------------------------------------------------------ redirect URIs

  [Theory]
  [InlineData("https://app.test/cb/")]
  [InlineData("https://app.test/cb?x=1")]
  [InlineData("https://app.test/CB")]
  [InlineData("HTTPS://app.test/cb")]
  [InlineData("https://app.test:443/cb")]
  [InlineData("https://app.test.evil.test/cb")]
  [InlineData("https://app.test/cb#frag")]
  [InlineData("https://app.test/cb/../cb")]
  [InlineData("https://evil.test/cb")]
  [InlineData("http://app.test/cb")]
  [InlineData("http://127.0.0.1:9999/other")]
  [InlineData("http://localhost:9999/cb")]
  [InlineData("javascript:alert(1)")]
  public async Task A_redirect_uri_that_is_not_exactly_registered_is_refused_without_redirecting(string redirect)
  {
    var (_, challenge) = AsHarness.Pkce();
    var response = await _h.AuthorizeAsync(challenge, ("redirect_uri", redirect));

    Assert.Equal(400, response.Status);
    Assert.Null(response.ResponseHeaders.GetFirst("Location"));
    Assert.Empty(_h.Requests);
  }

  [Fact]
  public async Task A_loopback_redirect_may_use_any_port_and_nothing_else_may_differ()
  {
    var (code, verifier) = await _h.CodeAsync(redirect: "http://127.0.0.1:53124/cb");
    var tokens = await _h.RedeemAsync(code, verifier, redirect: "http://127.0.0.1:53124/cb");
    Assert.True(tokens.TryGetProperty("access_token", out _));

    var (_, challenge) = AsHarness.Pkce();
    var otherPath = await _h.AuthorizeAsync(challenge, ("redirect_uri", "http://127.0.0.1:53124/cb2"));
    var otherHost = await _h.AuthorizeAsync(challenge, ("redirect_uri", "http://[::1]:53124/cb"));
    Assert.Equal(400, otherPath.Status);
    Assert.Equal(400, otherHost.Status);
  }

  [Fact]
  public async Task The_token_request_must_repeat_the_redirect_uri_of_the_authorization_request()
  {
    var (code, verifier) = await _h.CodeAsync();
    var response = await _h.TokenAsync([("grant_type", "authorization_code"), ("code", code), ("code_verifier", verifier), ("client_id", "app"), ("redirect_uri", "http://127.0.0.1/cb")]);

    Assert.Equal(400, response.Status);
    Assert.Equal("invalid_grant", response.Json.GetProperty("error").GetString());
  }

  [Fact]
  public async Task An_unknown_or_missing_client_is_never_redirected_to()
  {
    var (_, challenge) = AsHarness.Pkce();
    var unknown = await _h.AuthorizeAsync(challenge, ("client_id", "nobody"));
    var missing = await _h.AuthorizeAsync(challenge, ("client_id", null));
    var twice = await _h.SendAsync(new AsExchange("GET", "/authorize", "client_id=app&client_id=svc&response_type=code"));

    Assert.Equal(400, unknown.Status);
    Assert.Equal(400, missing.Status);
    Assert.Equal(400, twice.Status);
    Assert.Null(unknown.ResponseHeaders.GetFirst("Location"));
  }

  // ------------------------------------------------------------------------------------------------ iss and errors

  [Fact]
  public async Task Iss_and_state_are_on_the_success_redirect()
  {
    var (_, challenge) = AsHarness.Pkce();
    var response = await _h.AuthorizeAsync(challenge);

    Assert.Equal(302, response.Status);
    var q = response.RedirectQuery;
    Assert.Equal(AsHarness.Issuer, q["iss"]);
    Assert.Equal("st-1", q["state"]);
    Assert.False(string.IsNullOrEmpty(q["code"]));
    Assert.Equal("no-store", response.ResponseHeaders.GetFirst("Cache-Control"));
    Assert.StartsWith(AsHarness.RedirectUri + "?", response.ResponseHeaders.GetFirst("Location"), StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("denied")]
  [InlineData("response_type")]
  [InlineData("scope")]
  [InlineData("resource")]
  [InlineData("no_resource")]
  public async Task Iss_and_state_are_on_every_error_redirect(string kind)
  {
    var (_, challenge) = AsHarness.Pkce();
    (string, string?)[] overrides = kind switch
    {
      "response_type" => [("response_type", "token")],
      "scope" => [("scope", "unknown:scope")],
      "resource" => [("resource", "https://evil.test/mcp")],
      "no_resource" => [("resource", null)],
      _ => [],
    };
    if (kind == "denied")
    {
      _h.Decide = _ => AuthorizationDecision.Deny("no thanks");
    }

    var response = await _h.AuthorizeAsync(challenge, overrides);

    Assert.Equal(302, response.Status);
    var q = response.RedirectQuery;
    Assert.True(q.ContainsKey("error"));
    Assert.False(q.ContainsKey("code"));
    Assert.Equal(AsHarness.Issuer, q["iss"]);
    Assert.Equal("st-1", q["state"]);
    Assert.Equal(kind switch { "denied" => "access_denied", "response_type" => "unsupported_response_type", "scope" => "invalid_scope", _ => "invalid_target" }, q["error"]);
  }

  [Fact]
  public async Task A_repeated_parameter_is_refused()
  {
    var (_, challenge) = AsHarness.Pkce();
    var query = AsHarness.Encode([("response_type", "code"), ("client_id", "app"), ("redirect_uri", AsHarness.RedirectUri), ("code_challenge", challenge), ("code_challenge_method", "S256"), ("resource", AsHarness.Resource), ("resource", AsHarness.OtherResource)]);
    var response = await _h.SendAsync(new AsExchange("GET", "/authorize", query));

    Assert.Equal(302, response.Status);
    Assert.Equal("invalid_request", response.RedirectQuery["error"]);
  }

  [Fact]
  public async Task A_missing_resource_is_accepted_only_when_the_server_is_told_to_bind_the_single_resource()
  {
    using var lenient = new AsHarness(allowMissingResource: true);
    var (_, challenge) = AsHarness.Pkce();

    // Two resources are configured, so there is no single one to bind to.
    Assert.Equal("invalid_target", (await lenient.AuthorizeAsync(challenge, ("resource", null))).RedirectQuery["error"]);
  }

  // ------------------------------------------------------------------------------------------------ tokens and resource binding

  [Fact]
  public async Task The_access_token_is_a_jwt_for_the_resource_that_the_unchanged_validator_accepts()
  {
    var tokens = await _h.FullFlowAsync();
    var access = tokens.GetProperty("access_token").GetString()!;
    var keys = await _h.PublishedKeysAsync();
    var validator = _h.PlainValidator(keys);

    var result = await validator.ValidateAsync(access, new TokenValidationRequest(AsHarness.Resource), TestContext.Current.CancellationToken);

    Assert.True(result.IsValid, result.Description);
    Assert.Equal("Bearer", tokens.GetProperty("token_type").GetString());
    Assert.Equal(600, tokens.GetProperty("expires_in").GetInt32());
    Assert.Equal("files:read", tokens.GetProperty("scope").GetString());
    Assert.Equal("alice", result.Principal!.FindFirst("sub")!.Value);
    Assert.Equal("app", result.Principal.FindFirst("client_id")!.Value);
    Assert.Equal(["files:read"], result.Scopes);
    var jwt = JsonWebToken.Parse(access, 8192, out _)!;
    Assert.Equal("at+jwt", jwt.Type);
    Assert.Equal("k1", jwt.KeyId);
    Assert.Equal(AsHarness.Resource, jwt.Payload.GetProperty("aud").GetString());

    var wrongResource = await validator.ValidateAsync(access, new TokenValidationRequest(AsHarness.OtherResource), TestContext.Current.CancellationToken);
    Assert.False(wrongResource.IsValid);
  }

  [Fact]
  public async Task A_token_for_another_resource_is_rejected_by_the_resource_server()
  {
    var (code, verifier) = await _h.CodeAsync(overrides: ("resource", AsHarness.OtherResource));
    var tokens = await _h.RedeemAsync(code, verifier);
    var access = tokens.GetProperty("access_token").GetString()!;
    var resourceServer = new ResourceServer(new ResourceServerOptions(new Uri(AsHarness.Resource), _h.PlainValidator(await _h.PublishedKeysAsync())) { AuthorizationServers = [AsHarness.Issuer] });

    var outcome = await resourceServer.AuthenticateAsync(["Bearer " + access], TestContext.Current.CancellationToken);

    Assert.False(outcome.Succeeded);
    Assert.Equal(401, outcome.StatusCode);
  }

  [Fact]
  public async Task Redeeming_with_a_different_resource_is_refused()
  {
    var (code, verifier) = await _h.CodeAsync();
    var response = await _h.TokenAsync([("grant_type", "authorization_code"), ("code", code), ("code_verifier", verifier), ("client_id", "app"), ("redirect_uri", AsHarness.RedirectUri), ("resource", AsHarness.OtherResource)]);

    Assert.Equal("invalid_target", response.Json.GetProperty("error").GetString());
  }

  [Fact]
  public async Task Token_responses_are_not_cacheable()
  {
    var (code, verifier) = await _h.CodeAsync();
    var response = await _h.TokenAsync([("grant_type", "authorization_code"), ("code", code), ("code_verifier", verifier), ("client_id", "app"), ("redirect_uri", AsHarness.RedirectUri)]);

    Assert.Equal("no-store", response.ResponseHeaders.GetFirst("Cache-Control"));
    Assert.Equal("no-cache", response.ResponseHeaders.GetFirst("Pragma"));
  }

  [Fact]
  public async Task A_code_works_once_and_a_replay_revokes_what_it_produced()
  {
    var (code, verifier) = await _h.CodeAsync();
    var tokens = await _h.RedeemAsync(code, verifier);
    var validator = _h.Server.CreateTokenValidator();
    Assert.True((await validator.ValidateAsync(tokens.GetProperty("access_token").GetString()!, new TokenValidationRequest(AsHarness.Resource), TestContext.Current.CancellationToken)).IsValid);

    var replay = await _h.TokenAsync([("grant_type", "authorization_code"), ("code", code), ("code_verifier", verifier), ("client_id", "app"), ("redirect_uri", AsHarness.RedirectUri)]);

    Assert.Equal("invalid_grant", replay.Json.GetProperty("error").GetString());
    Assert.False((await validator.ValidateAsync(tokens.GetProperty("access_token").GetString()!, new TokenValidationRequest(AsHarness.Resource), TestContext.Current.CancellationToken)).IsValid);
    var refresh = await _h.TokenAsync([("grant_type", "refresh_token"), ("refresh_token", tokens.GetProperty("refresh_token").GetString()), ("client_id", "app")]);
    Assert.Equal("invalid_grant", refresh.Json.GetProperty("error").GetString());
    Assert.Contains(_h.SecurityEvents, e => e.Contains("replayed", StringComparison.Ordinal));
  }

  [Fact]
  public async Task An_expired_code_is_refused()
  {
    var (code, verifier) = await _h.CodeAsync();
    _h.Time.Advance(TimeSpan.FromSeconds(61));
    var response = await _h.TokenAsync([("grant_type", "authorization_code"), ("code", code), ("code_verifier", verifier), ("client_id", "app"), ("redirect_uri", AsHarness.RedirectUri)]);

    Assert.Equal("invalid_grant", response.Json.GetProperty("error").GetString());
  }

  [Fact]
  public async Task A_code_cannot_be_redeemed_by_another_client()
  {
    var (code, verifier) = await _h.CodeAsync();
    var response = await _h.TokenAsync([("grant_type", "authorization_code"), ("code", code), ("code_verifier", verifier), ("redirect_uri", AsHarness.RedirectUri)], ("Authorization", BasicHeader("svc", AsHarness.ClientSecretValue)));

    Assert.Equal("invalid_grant", response.Json.GetProperty("error").GetString());
  }

  [Fact]
  public async Task The_host_can_narrow_the_scope_but_not_widen_it()
  {
    _h.Decide = _ => AuthorizationDecision.Approve("alice", ["files:read", "admin"], new Dictionary<string, string> { ["email"] = "a@example.test" });
    var (code, verifier) = await _h.CodeAsync(overrides: ("scope", "files:read files:write"));
    var tokens = await _h.RedeemAsync(code, verifier);

    Assert.Equal("files:read", tokens.GetProperty("scope").GetString());
    var jwt = JsonWebToken.Parse(tokens.GetProperty("access_token").GetString()!, 8192, out _)!;
    Assert.Equal("a@example.test", jwt.Payload.GetProperty("email").GetString());
    Assert.Throws<ArgumentException>(() => AuthorizationDecision.Approve("alice", null, new Dictionary<string, string> { ["aud"] = "x" }));
  }

  // ------------------------------------------------------------------------------------------------ refresh tokens

  [Fact]
  public async Task Refresh_tokens_rotate_and_reuse_of_a_spent_one_revokes_the_whole_family()
  {
    var first = await _h.FullFlowAsync();
    var r1 = first.GetProperty("refresh_token").GetString()!;
    var validator = _h.Server.CreateTokenValidator();

    var second = (await _h.TokenAsync([("grant_type", "refresh_token"), ("refresh_token", r1), ("client_id", "app")])).Json;
    var r2 = second.GetProperty("refresh_token").GetString()!;
    Assert.NotEqual(r1, r2);
    Assert.True((await validator.ValidateAsync(second.GetProperty("access_token").GetString()!, new TokenValidationRequest(AsHarness.Resource), TestContext.Current.CancellationToken)).IsValid);

    // The spent token shown again: two parties hold it.
    var reuse = await _h.TokenAsync([("grant_type", "refresh_token"), ("refresh_token", r1), ("client_id", "app")]);
    Assert.Equal(400, reuse.Status);
    Assert.Equal("invalid_grant", reuse.Json.GetProperty("error").GetString());
    Assert.Contains(_h.SecurityEvents, e => e.Contains("reused", StringComparison.Ordinal));

    // The legitimate newest token and the access tokens of the family are dead too.
    var latest = await _h.TokenAsync([("grant_type", "refresh_token"), ("refresh_token", r2), ("client_id", "app")]);
    Assert.Equal("invalid_grant", latest.Json.GetProperty("error").GetString());
    Assert.False((await validator.ValidateAsync(second.GetProperty("access_token").GetString()!, new TokenValidationRequest(AsHarness.Resource), TestContext.Current.CancellationToken)).IsValid);
    Assert.False((await validator.ValidateAsync(first.GetProperty("access_token").GetString()!, new TokenValidationRequest(AsHarness.Resource), TestContext.Current.CancellationToken)).IsValid);
  }

  [Fact]
  public async Task Concurrent_refreshes_of_one_token_give_one_winner_and_revoke_the_family()
  {
    var tokens = await _h.FullFlowAsync();
    var refresh = tokens.GetProperty("refresh_token").GetString()!;

    var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _h.TokenAsync([("grant_type", "refresh_token"), ("refresh_token", refresh), ("client_id", "app")])));

    Assert.Equal(1, results.Count(r => r.Status == 200));
    Assert.Equal(7, results.Count(r => r.Status == 400));
    var winner = results.Single(r => r.Status == 200).Json;
    var next = await _h.TokenAsync([("grant_type", "refresh_token"), ("refresh_token", winner.GetProperty("refresh_token").GetString()), ("client_id", "app")]);
    Assert.Equal(400, next.Status);
  }

  [Fact]
  public async Task A_refresh_may_narrow_the_scope_and_never_widen_it()
  {
    var (code, verifier) = await _h.CodeAsync(overrides: ("scope", "files:read files:write"));
    var tokens = await _h.RedeemAsync(code, verifier);
    var refresh = tokens.GetProperty("refresh_token").GetString()!;

    var wider = await _h.TokenAsync([("grant_type", "refresh_token"), ("refresh_token", refresh), ("client_id", "app"), ("scope", "files:read admin")]);
    var narrower = await _h.TokenAsync([("grant_type", "refresh_token"), ("refresh_token", refresh), ("client_id", "app"), ("scope", "files:read")]);

    Assert.Equal("invalid_scope", wider.Json.GetProperty("error").GetString());
    Assert.Equal(200, narrower.Status);
    Assert.Equal("files:read", narrower.Json.GetProperty("scope").GetString());
  }

  [Fact]
  public async Task A_refresh_token_is_bound_to_its_client_and_is_not_spent_by_a_stranger()
  {
    var tokens = await _h.FullFlowAsync();
    var refresh = tokens.GetProperty("refresh_token").GetString()!;

    var stranger = await _h.TokenAsync([("grant_type", "refresh_token"), ("refresh_token", refresh)], ("Authorization", BasicHeader("svc", AsHarness.ClientSecretValue)));
    var owner = await _h.TokenAsync([("grant_type", "refresh_token"), ("refresh_token", refresh), ("client_id", "app")]);

    Assert.Equal("invalid_grant", stranger.Json.GetProperty("error").GetString());
    Assert.Equal(200, owner.Status);
  }

  [Fact]
  public async Task Refresh_tokens_expire_and_the_family_has_an_absolute_limit()
  {
    var tokens = await _h.FullFlowAsync();
    var refresh = tokens.GetProperty("refresh_token").GetString()!;
    _h.Time.Advance(TimeSpan.FromDays(31));
    var expired = await _h.TokenAsync([("grant_type", "refresh_token"), ("refresh_token", refresh), ("client_id", "app")]);
    Assert.Equal("invalid_grant", expired.Json.GetProperty("error").GetString());

    var fresh = await _h.FullFlowAsync();
    var current = fresh.GetProperty("refresh_token").GetString()!;
    for (var i = 0; i < 4; i++)
    {
      _h.Time.Advance(TimeSpan.FromDays(25));
      var next = await _h.TokenAsync([("grant_type", "refresh_token"), ("refresh_token", current), ("client_id", "app")]);
      if (next.Status != 200)
      {
        return;
      }

      current = next.Json.GetProperty("refresh_token").GetString()!;
    }

    Assert.Fail("The family should have ended within 90 days.");
  }

  // ------------------------------------------------------------------------------------------------ revocation

  [Fact]
  public async Task A_revoked_access_token_is_refused_by_the_servers_validator()
  {
    var tokens = await _h.FullFlowAsync();
    var access = tokens.GetProperty("access_token").GetString()!;
    var own = _h.Server.CreateTokenValidator();
    var plain = _h.PlainValidator(await _h.PublishedKeysAsync());
    Assert.True((await own.ValidateAsync(access, new TokenValidationRequest(AsHarness.Resource), TestContext.Current.CancellationToken)).IsValid);

    var revoke = await _h.SendAsync(new AsExchange("POST", "/revoke", string.Empty, AsHarness.Encode([("token", access), ("client_id", "app")])));

    Assert.Equal(200, revoke.Status);
    Assert.False((await own.ValidateAsync(access, new TokenValidationRequest(AsHarness.Resource), TestContext.Current.CancellationToken)).IsValid);

    // A resource server elsewhere sees only the signed token and lives with its short lifetime.
    Assert.True((await plain.ValidateAsync(access, new TokenValidationRequest(AsHarness.Resource), TestContext.Current.CancellationToken)).IsValid);
  }

  [Fact]
  public async Task A_revoked_refresh_token_ends_its_family()
  {
    var tokens = await _h.FullFlowAsync();
    var refresh = tokens.GetProperty("refresh_token").GetString()!;
    var access = tokens.GetProperty("access_token").GetString()!;

    var revoke = await _h.SendAsync(new AsExchange("POST", "/revoke", string.Empty, AsHarness.Encode([("token", refresh), ("token_type_hint", "refresh_token"), ("client_id", "app")])));
    var again = await _h.TokenAsync([("grant_type", "refresh_token"), ("refresh_token", refresh), ("client_id", "app")]);

    Assert.Equal(200, revoke.Status);
    Assert.Equal("invalid_grant", again.Json.GetProperty("error").GetString());
    Assert.False((await _h.Server.CreateTokenValidator().ValidateAsync(access, new TokenValidationRequest(AsHarness.Resource), TestContext.Current.CancellationToken)).IsValid);
  }

  [Fact]
  public async Task Revocation_by_another_client_or_of_unknown_tokens_changes_nothing_and_succeeds_quietly()
  {
    var tokens = await _h.FullFlowAsync();
    var refresh = tokens.GetProperty("refresh_token").GetString()!;

    var foreign = await _h.SendAsync(new AsExchange("POST", "/revoke", string.Empty, AsHarness.Encode([("token", refresh)]), headers: ("Authorization", BasicHeader("svc", AsHarness.ClientSecretValue))));
    var unknown = await _h.SendAsync(new AsExchange("POST", "/revoke", string.Empty, AsHarness.Encode([("token", "nonsense"), ("client_id", "app")])));
    var badClient = await _h.SendAsync(new AsExchange("POST", "/revoke", string.Empty, AsHarness.Encode([("token", refresh)]), headers: ("Authorization", BasicHeader("svc", "wrong"))));

    Assert.Equal(200, foreign.Status);
    Assert.Equal(200, unknown.Status);
    Assert.Equal(401, badClient.Status);
    var stillWorks = await _h.TokenAsync([("grant_type", "refresh_token"), ("refresh_token", refresh), ("client_id", "app")]);
    Assert.Equal(200, stillWorks.Status);
  }

  // ------------------------------------------------------------------------------------------------ key rotation

  [Fact]
  public async Task Rotation_keeps_old_tokens_valid_until_they_expire_and_publishes_both_keys_meanwhile()
  {
    var before = await _h.FullFlowAsync();
    var oldAccess = before.GetProperty("access_token").GetString()!;

    _h.Key1.RetiredAt = _h.Time.GetUtcNow();
    _h.Build([_h.Key2, _h.Key1]);
    var keys = await _h.PublishedKeysAsync();
    var kids = keys.Keys.Select(k => k.KeyId).ToList();

    Assert.Equal(["k2", "k1"], kids);
    var validator = _h.PlainValidator(keys);
    Assert.True((await validator.ValidateAsync(oldAccess, new TokenValidationRequest(AsHarness.Resource), TestContext.Current.CancellationToken)).IsValid);

    var after = await _h.FullFlowAsync();
    var newAccess = JsonWebToken.Parse(after.GetProperty("access_token").GetString()!, 8192, out _)!;
    Assert.Equal("k2", newAccess.KeyId);
    Assert.Equal("ES256", newAccess.Algorithm);
    Assert.True((await validator.ValidateAsync(after.GetProperty("access_token").GetString()!, new TokenValidationRequest(AsHarness.Resource), TestContext.Current.CancellationToken)).IsValid);

    // Once a token signed by the old key can no longer be valid, the key leaves the set.
    _h.Time.Advance(TimeSpan.FromMinutes(12));
    Assert.Equal(["k2"], (await _h.PublishedKeysAsync()).Keys.Select(k => k.KeyId));
  }
}
