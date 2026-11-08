using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Auth;
using RecursiveAI.OneModelContext.Jose;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

public sealed class JwtValidatorTests : IDisposable
{
  private static readonly Uri JwksUri = new("https://issuer.test/jwks");
  private static readonly string[] MixedAudience = ["https://other.test", "HTTPS://MCP.test/mcp"];
  private readonly TestIssuer _issuer = new();
  private readonly ManualTimeProvider _time = new();
  private string _jwks;
  private StubHandler _handler;
  private HttpJwksProvider _provider;
  private JwtTokenValidator _validator;

  public JwtValidatorTests()
  {
    _jwks = _issuer.Jwks();
    _handler = new StubHandler(_ => StubHandler.Json(_jwks));
    _provider = new HttpJwksProvider(JwksUri, new HttpClient(_handler), _time);
    _validator = new JwtTokenValidator(TestIssuer.Issuer, _provider, _time);
  }

  public void Dispose()
  {
    _issuer.Dispose();
    _provider.Dispose();
  }

  private ValueTask<TokenValidationResult> Validate(string token) =>
    _validator.ValidateAsync(token, new TokenValidationRequest(TestIssuer.Resource), TestContext.Current.CancellationToken);

  private static void AssertRefused(TokenValidationResult result, string? mentions = null)
  {
    Assert.False(result.IsValid);
    Assert.False(result.Unavailable);
    if (mentions is not null)
    {
      Assert.Contains(mentions, result.Description, StringComparison.Ordinal);
    }
  }

  [Theory]
  [InlineData("RS256")]
  [InlineData("PS256")]
  [InlineData("ES256")]
  [InlineData("ES384")]
  [InlineData("ES512")]
  public async Task A_valid_token_is_accepted_and_yields_a_principal(string alg)
  {
    var result = await Validate(_issuer.Mint(alg, time: _time));

    Assert.True(result.IsValid, result.Description);
    Assert.Equal("user-1", result.Principal!.FindFirst("sub")!.Value);
    Assert.Equal(["files:read"], result.Scopes);
    Assert.Equal(["files:read"], AuthClaimTypes.GetScopes(result.Principal));
  }

  [Fact]
  public async Task The_audience_may_be_an_array_and_is_compared_after_canonicalising_scheme_and_host()
  {
    var ok = await Validate(_issuer.Mint(time: _time, claims: new() { ["aud"] = MixedAudience }));
    Assert.True(ok.IsValid, ok.Description);
  }

  [Theory]
  [InlineData("https://mcp.test/other")]
  [InlineData("https://mcp.test")]
  [InlineData("https://mcp.test/mcp/")]
  [InlineData("http://mcp.test/mcp")]
  [InlineData("https://mcp.test:8443/mcp")]
  public async Task A_token_for_another_resource_is_refused(string audience)
  {
    AssertRefused(await Validate(_issuer.Mint(time: _time, claims: new() { ["aud"] = audience })), "resource");
  }

  [Fact]
  public async Task A_missing_audience_is_refused()
  {
    AssertRefused(await Validate(_issuer.Mint(time: _time, claims: new() { ["aud"] = null })), "resource");
  }

  [Theory]
  [InlineData("https://issuer.test/")]
  [InlineData("https://ISSUER.test")]
  [InlineData("https://evil.test")]
  public async Task The_issuer_is_compared_exactly(string issuer)
  {
    AssertRefused(await Validate(_issuer.Mint(time: _time, claims: new() { ["iss"] = issuer })), "issuer");
  }

  [Fact]
  public async Task Expiry_not_before_and_issued_at_are_checked_with_bounded_skew()
  {
    var now = _time.GetUtcNow().ToUnixTimeSeconds();

    AssertRefused(await Validate(_issuer.Mint(time: _time, claims: new() { ["exp"] = now - 120 })), "expired");
    Assert.True((await Validate(_issuer.Mint(time: _time, claims: new() { ["exp"] = now - 30 }))).IsValid);
    AssertRefused(await Validate(_issuer.Mint(time: _time, claims: new() { ["nbf"] = now + 300 })), "not valid yet");
    Assert.True((await Validate(_issuer.Mint(time: _time, claims: new() { ["nbf"] = now + 30 }))).IsValid);
    AssertRefused(await Validate(_issuer.Mint(time: _time, claims: new() { ["iat"] = now + 3600 })), "future");
    AssertRefused(await Validate(_issuer.Mint(time: _time, claims: new() { ["exp"] = null })), "expiry");
  }

  [Fact]
  public async Task A_token_expires_as_the_clock_advances()
  {
    var token = _issuer.Mint(time: _time);
    Assert.True((await Validate(token)).IsValid);

    _time.Advance(TimeSpan.FromMinutes(12));

    AssertRefused(await Validate(token), "expired");
  }

  [Fact]
  public async Task A_signature_from_another_key_is_refused()
  {
    using var other = new TestIssuer();
    AssertRefused(await Validate(other.Mint(time: _time)), "signature");
  }

  [Fact]
  public async Task A_tampered_payload_is_refused()
  {
    var parts = _issuer.Mint(time: _time).Split('.');
    var forged = TestIssuer.B64(Encoding.UTF8.GetBytes("""{"iss":"https://issuer.test","aud":"https://mcp.test/mcp","sub":"admin","scope":"admin","exp":4102444800}"""));

    AssertRefused(await Validate(parts[0] + "." + forged + "." + parts[2]), "signature");
  }

  [Fact]
  public async Task Alg_none_is_refused()
  {
    AssertRefused(await Validate(_issuer.Mint("none", time: _time)));
    Assert.Equal(0, _handler.Calls);
  }

  [Fact]
  public async Task An_hmac_token_signed_with_the_public_key_as_secret_is_refused()
  {
    // The classic algorithm confusion: the public key is not secret, so an HS256 MAC keyed with it proves nothing.
    var secret = _issuer.Rsa.ExportSubjectPublicKeyInfo();
    var token = _issuer.Mint("HS256", kid: "rsa-1", time: _time, signWith: secret);

    AssertRefused(await Validate(token), "not supported");
  }

  [Fact]
  public async Task A_token_that_names_an_algorithm_the_key_cannot_have_made_is_refused()
  {
    // Header says ES256 but the kid points at the RSA key.
    var token = _issuer.Mint("ES256", kid: "rsa-1", time: _time);

    AssertRefused(await Validate(token));
  }

  [Fact]
  public async Task An_unknown_kid_refetches_at_most_once_per_interval_so_forged_kids_cannot_hammer_the_issuer()
  {
    var token = _issuer.Mint(kid: "rotated", time: _time);

    AssertRefused(await Validate(token), "known key");
    Assert.Equal(1, _handler.Calls); // the keys were loaded a moment ago; fetching again would learn nothing

    _time.Advance(TimeSpan.FromSeconds(31));
    AssertRefused(await Validate(token), "known key");
    Assert.Equal(2, _handler.Calls); // one refetch for the unknown kid

    AssertRefused(await Validate(token), "known key");
    Assert.Equal(2, _handler.Calls);
  }

  [Fact]
  public async Task A_rotated_key_is_picked_up_after_the_refresh_interval()
  {
    var token = _issuer.Mint(kid: "rsa-2", time: _time);
    AssertRefused(await Validate(token));

    _jwks = _issuer.Jwks(_issuer.RsaJwk("rsa-2"));
    _time.Advance(TimeSpan.FromSeconds(31));

    Assert.True((await Validate(token)).IsValid);
  }

  [Fact]
  public async Task The_key_set_is_cached_until_it_expires()
  {
    await Validate(_issuer.Mint(time: _time));
    await Validate(_issuer.Mint(time: _time));
    Assert.Equal(1, _handler.Calls);

    _time.Advance(TimeSpan.FromMinutes(11));
    await Validate(_issuer.Mint(time: _time));
    Assert.Equal(2, _handler.Calls);
  }

  [Fact]
  public async Task An_issuer_outage_serves_the_cached_keys_and_without_a_cache_is_unavailable()
  {
    var down = false;
    _handler = new StubHandler(_ => down ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : StubHandler.Json(_jwks));
    _provider = new HttpJwksProvider(JwksUri, new HttpClient(_handler), _time);
    _validator = new JwtTokenValidator(TestIssuer.Issuer, _provider, _time);
    down = true;

    var cold = await Validate(_issuer.Mint(time: _time));
    Assert.True(cold.Unavailable);

    down = false;
    _time.Advance(TimeSpan.FromMinutes(1));
    Assert.True((await Validate(_issuer.Mint(time: _time))).IsValid);
    down = true;
    _time.Advance(TimeSpan.FromMinutes(11));
    Assert.True((await Validate(_issuer.Mint(time: _time))).IsValid);
  }

  [Fact]
  public async Task A_jwks_with_only_eddsa_keys_names_the_adapter_that_handles_them()
  {
    _jwks = "{\"keys\":[" + TestIssuer.EdwardsJwk("ed-1") + "]}";
    _handler = new StubHandler(_ => StubHandler.Json(_jwks));
    _provider = new HttpJwksProvider(JwksUri, new HttpClient(_handler), _time);
    _validator = new JwtTokenValidator(TestIssuer.Issuer, _provider, _time);

    var signedWithRsaByAnotherParty = await Validate(_issuer.Mint(kid: "ed-1", time: _time));
    AssertRefused(signedWithRsaByAnotherParty, "RecursiveAI.OneModelContext.OneCrypto");

    // A token that says EdDSA gets the same pointer, however the JWKS looks.
    var header = TestIssuer.B64(Encoding.UTF8.GetBytes("""{"alg":"EdDSA","kid":"ed-1"}"""));
    var payload = TestIssuer.B64(Encoding.UTF8.GetBytes("""{"iss":"https://issuer.test"}"""));
    AssertRefused(await Validate(header + "." + payload + "." + TestIssuer.B64(new byte[64])), "RecursiveAI.OneModelContext.OneCrypto");
  }

  [Theory]
  [InlineData("")]
  [InlineData("abc")]
  [InlineData("a.b")]
  [InlineData("a.b.c.d")]
  [InlineData("..")]
  [InlineData("e30.e30.")]
  [InlineData("!!!.!!!.!!!")]
  public async Task Garbage_is_refused_without_throwing(string token)
  {
    AssertRefused(await Validate(token));
  }

  [Fact]
  public async Task A_token_longer_than_the_limit_is_refused_before_it_is_parsed()
  {
    AssertRefused(await Validate(new string('a', 9000)));
    Assert.Equal(0, _handler.Calls);
  }

  [Fact]
  public async Task A_critical_header_extension_is_refused()
  {
    var header = TestIssuer.B64(Encoding.UTF8.GetBytes("""{"alg":"RS256","kid":"rsa-1","crit":["exp"],"exp":1}"""));
    var payload = TestIssuer.B64(Encoding.UTF8.GetBytes("""{"iss":"https://issuer.test"}"""));

    AssertRefused(await Validate(header + "." + payload + "." + TestIssuer.B64(new byte[256])), "critical");
  }

  [Fact]
  public void The_jwks_uri_must_be_https_or_loopback()
  {
    Assert.Throws<ArgumentException>(() => new HttpJwksProvider(new Uri("http://issuer.test/jwks")));
    Assert.Throws<ArgumentException>(() => new HttpJwksProvider(new Uri("https://user:pw@issuer.test/jwks")));
    new HttpJwksProvider(new Uri("http://127.0.0.1:1/jwks")).Dispose();
  }

  [Fact]
  public async Task An_oversized_or_redirected_jwks_response_is_not_used()
  {
    _handler = new StubHandler(_ => StubHandler.Json("{\"keys\":[],\"pad\":\"" + new string('x', 300_000) + "\"}"));
    _provider = new HttpJwksProvider(JwksUri, new HttpClient(_handler), _time);
    _validator = new JwtTokenValidator(TestIssuer.Issuer, _provider, _time);
    Assert.True((await Validate(_issuer.Mint(time: _time))).Unavailable);

    _handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Found));
    _provider = new HttpJwksProvider(JwksUri, new HttpClient(_handler), _time);
    _validator = new JwtTokenValidator(TestIssuer.Issuer, _provider, _time);
    Assert.True((await Validate(_issuer.Mint(time: _time))).Unavailable);
  }

  [Fact]
  public void Weak_and_malformed_keys_are_skipped_when_a_jwks_is_read()
  {
    using var weak = System.Security.Cryptography.RSA.Create(1024);
    var p = weak.ExportParameters(false);
    var json = "{\"keys\":[{\"kty\":\"RSA\",\"kid\":\"weak\",\"n\":\"" + TestIssuer.B64(p.Modulus!) + "\",\"e\":\"" + TestIssuer.B64(p.Exponent!) + "\"},{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"AA\",\"y\":\"AA\"},{\"kty\":\"oct\",\"k\":\"AAAA\"}," + _issuer.RsaJwk("good") + "]}";

    var set = JsonWebKeySet.Parse(Encoding.UTF8.GetBytes(json))!;

    Assert.Equal(["good"], set.Keys.Select(k => k.KeyId));
  }

  [Theory]
  [InlineData("P-256", 32)]
  [InlineData("P-384", 48)]
  [InlineData("P-521", 66)]
  public void Off_curve_points_are_skipped_without_discarding_valid_keys(string curve, int coordinateBytes)
  {
    var coordinate = TestIssuer.B64(new byte[coordinateBytes]);
    var json = "{\"keys\":[{\"kty\":\"EC\",\"crv\":\"" + curve + "\",\"x\":\"" + coordinate + "\",\"y\":\"" + coordinate + "\"}," + _issuer.RsaJwk("good") + "]}";
    var set = JsonWebKeySet.Parse(Encoding.UTF8.GetBytes(json))!;
    Assert.Equal(["good"], set.Keys.Select(k => k.KeyId));
  }

}

public sealed class IntrospectionValidatorTests
{
  private static readonly Uri Endpoint = new("https://issuer.test/introspect");
  private readonly ManualTimeProvider _time = new();

  private (IntrospectionTokenValidator Validator, StubHandler Handler) Create(Func<string, string> answer, int status = 200)
  {
    var handler = new StubHandler(request =>
    {
      var token = Uri.UnescapeDataString(request.Content!.ReadAsStringAsync().Result.Split('&')[0].Split('=')[1]);
      return StubHandler.Json(answer(token), (HttpStatusCode)status);
    });
    var validator = new IntrospectionTokenValidator(Endpoint, IntrospectionClientAuthentication.Basic("mcp server", "s3cret+/"), new HttpClient(handler), _time);
    return (validator, handler);
  }

  private static ValueTask<TokenValidationResult> Validate(IntrospectionTokenValidator v, string token) =>
    v.ValidateAsync(token, new TokenValidationRequest(TestIssuer.Resource), TestContext.Current.CancellationToken);

  private string Active(long? exp = null, string aud = TestIssuer.Resource, string scope = "files:read files:write") =>
    "{\"active\":true,\"sub\":\"u1\",\"client_id\":\"c1\",\"scope\":\"" + scope + "\",\"aud\":\"" + aud + "\",\"exp\":" + (exp ?? _time.GetUtcNow().ToUnixTimeSeconds() + 300) + "}";

  [Fact]
  public async Task An_active_token_for_this_resource_is_accepted_and_the_request_is_a_form_post_with_client_authentication()
  {
    var (validator, handler) = Create(_ => Active());

    var result = await Validate(validator, "opaque-token-value");

    Assert.True(result.IsValid, result.Description);
    Assert.Equal(["files:read", "files:write"], result.Scopes);
    var (request, body) = handler.Seen[0];
    Assert.Equal(HttpMethod.Post, request.Method);
    Assert.DoesNotContain("opaque-token-value", request.RequestUri!.ToString(), StringComparison.Ordinal);
    Assert.Contains("token=opaque-token-value", body, StringComparison.Ordinal);
    Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
    // RFC 6749 2.3.1: id and secret are form-urlencoded before the Basic encoding.
    Assert.Equal("mcp+server:s3cret%2B%2F", Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
  }

  [Fact]
  public async Task An_inactive_token_is_refused_and_not_cached()
  {
    var (validator, handler) = Create(_ => """{"active":false}""");

    Assert.False((await Validate(validator, "opaque-token-value")).IsValid);
    Assert.False((await Validate(validator, "opaque-token-value")).IsValid);
    Assert.Equal(2, handler.Calls);
  }

  [Theory]
  [InlineData("""{"sub":"u1"}""")]
  [InlineData("""{"active":"true"}""")]
  [InlineData("""{"active":true,"aud":"https://mcp.test/other"}""")]
  [InlineData("""{"active":true}""")]
  [InlineData("""{"active":true,"aud":"https://mcp.test/mcp","exp":1}""")]
  public async Task Anything_but_an_active_answer_for_this_audience_is_refused(string answer)
  {
    var (validator, _) = Create(_ => answer);

    var result = await Validate(validator, "opaque-token-value");

    Assert.False(result.IsValid);
    Assert.False(result.Unavailable);
  }

  [Fact]
  public async Task Active_answers_are_cached_but_never_past_exp()
  {
    var exp = _time.GetUtcNow().ToUnixTimeSeconds() + 20;
    var (validator, handler) = Create(_ => Active(exp));

    await Validate(validator, "opaque-token-value");
    _time.Advance(TimeSpan.FromSeconds(10));
    Assert.True((await Validate(validator, "opaque-token-value")).IsValid);
    Assert.Equal(1, handler.Calls);

    _time.Advance(TimeSpan.FromSeconds(15)); // past exp, inside the 60 s cache limit
    Assert.False((await Validate(validator, "opaque-token-value")).IsValid);
    Assert.Equal(2, handler.Calls);
  }

  [Fact]
  public async Task The_cache_is_bounded_by_its_own_limit_too()
  {
    var (validator, handler) = Create(_ => Active());

    await Validate(validator, "opaque-token-value");
    _time.Advance(TimeSpan.FromSeconds(61));
    await Validate(validator, "opaque-token-value");

    Assert.Equal(2, handler.Calls);
  }

  [Fact]
  public async Task An_endpoint_failure_is_unavailable_not_invalid()
  {
    var (validator, _) = Create(_ => "oops", status: 500);

    var result = await Validate(validator, "opaque-token-value");

    Assert.True(result.Unavailable);
  }

  [Fact]
  public void The_endpoint_must_be_https_or_loopback()
  {
    Assert.Throws<ArgumentException>(() => new IntrospectionTokenValidator(new Uri("http://issuer.test/i"), IntrospectionClientAuthentication.Bearer("x")));
  }
}

public sealed class StaticTokenValidatorTests
{
  private const string Token = "tok_0123456789abcdef0123456789abcdef";
  private readonly ManualTimeProvider _time = new();

  private static ValueTask<TokenValidationResult> Validate(StaticTokenValidator v, string token) =>
    v.ValidateAsync(token, new TokenValidationRequest(TestIssuer.Resource), TestContext.Current.CancellationToken);

  [Fact]
  public async Task A_known_token_yields_its_user_and_scopes_and_an_unknown_one_fails()
  {
    var validator = new StaticTokenValidator(_time).Add(Token, "alice", ["files:read"]).Add("tok_ffffffffffffffffffffffffffffffff", "bob");

    var hit = await Validate(validator, Token);
    Assert.True(hit.IsValid);
    Assert.Equal("alice", hit.Principal!.FindFirst("sub")!.Value);
    Assert.Equal("alice", hit.Principal.Identity!.Name);
    Assert.Equal(["files:read"], hit.Scopes);
    Assert.Empty((await Validate(validator, "tok_ffffffffffffffffffffffffffffffff")).Scopes);

    Assert.False((await Validate(validator, Token + "x")).IsValid);
    Assert.False((await Validate(validator, string.Empty)).IsValid);
  }

  [Fact]
  public async Task Only_hashes_are_kept_and_hashed_registration_works()
  {
    var validator = new StaticTokenValidator(_time).AddHashed(StaticTokenValidator.Hash(Token), "carol", ["a"]);

    Assert.True((await Validate(validator, Token)).IsValid);
  }

  [Fact]
  public async Task A_token_can_expire()
  {
    var validator = new StaticTokenValidator(_time).Add(Token, "alice", null, _time.GetUtcNow().AddMinutes(5));
    Assert.True((await Validate(validator, Token)).IsValid);

    _time.Advance(TimeSpan.FromMinutes(6));

    Assert.False((await Validate(validator, Token)).IsValid);
  }

  [Fact]
  public void A_short_token_is_refused_at_registration()
  {
    Assert.Throws<ArgumentException>(() => new StaticTokenValidator().Add("short", "alice"));
    Assert.Throws<ArgumentException>(() => new StaticTokenValidator().AddHashed(new byte[5], "alice"));
  }
}
