using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.Jose;
using RecursiveAI.OneModelContext.OAuth;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>One request the test authorization server received at its token endpoint.</summary>
internal sealed record TokenCall(Dictionary<string, string> Form, string? Authorization);

/// <summary>
/// A small authorization server for the OAuth client tests, on the library's own listener. Keys, codes and tokens are generated for the
/// run. It issues RS256 JWT access tokens whose audience is the <c>resource</c> parameter, so a resource server can validate them.
/// </summary>
internal sealed class TestAuthServer : IAsyncDisposable
{
  private readonly HttpServer _http;
  private readonly Dictionary<string, (string Challenge, string Scope, string Resource, string RedirectUri)> _codes = [];
  private readonly Dictionary<string, string> _refresh = [];
  private readonly object _lock = new();
  private int _registrations;

  internal TestAuthServer(TimeProvider? time = null)
  {
    Time = time ?? TimeProvider.System;
    _http = new HttpServer(HandleAsync);
    _http.Start();
    Issuer = _http.BaseAddress!.GetLeftPart(UriPartial.Authority);
    Keys = new JsonWebKeySet([JsonWebKey.FromRsa(Rsa, "k1")]);
  }

  internal TimeProvider Time { get; }

  internal RSA Rsa { get; } = RSA.Create(2048);

  internal JsonWebKeySet Keys { get; }

  internal string Issuer { get; }

  internal List<TokenCall> TokenCalls { get; } = [];

  internal int Registrations => _registrations;

  internal Dictionary<string, string>? ClientAssertionKeyOwner { get; set; }

  // Knobs.
  internal bool IssParameterSupported { get; set; } = true;

  internal string[]? PkceMethods { get; set; } = ["S256"];

  internal string? MetadataIssuer { get; set; }

  internal string[]? ScopesSupported { get; set; }

  internal bool Register { get; set; } = true;

  internal int AccessSeconds { get; set; } = 3600;

  internal string? ErrorDescriptionEcho { get; set; }

  internal string? RedirectTo { get; set; }

  internal string[] AuthMethods { get; set; } = ["none", "client_secret_basic", "client_secret_post", "private_key_jwt"];

  internal string? ExpectedClientSecret { get; set; }

  internal RSA? ClientAssertionKey { get; set; }

  internal string ScopeGranted(string requested) => requested;

  /// <summary>What a browser would do: records the request and returns the redirect the authorization server would send.</summary>
  internal Uri Authorize(Uri authorizationUrl, string? iss = "default", string? state = "default")
  {
    var query = AuthorizationResponse.ParseQuery(authorizationUrl.Query, out _);
    var code = "code-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    lock (_lock)
    {
      _codes[code] = (query["code_challenge"], query.GetValueOrDefault("scope", string.Empty), query["resource"], query["redirect_uri"]);
    }

    var redirect = query["redirect_uri"] + "?code=" + code;
    var effectiveState = state == "default" ? query["state"] : state;
    if (effectiveState is not null)
    {
      redirect += "&state=" + Uri.EscapeDataString(effectiveState);
    }

    var effectiveIss = iss == "default" ? Issuer : iss;
    if (effectiveIss is not null)
    {
      redirect += "&iss=" + Uri.EscapeDataString(effectiveIss);
    }

    return new Uri(redirect);
  }

  internal string Mint(string scope, string resource)
  {
    var now = Time.GetUtcNow().ToUnixTimeSeconds();
    var header = Encoding.UTF8.GetBytes("""{"alg":"RS256","kid":"k1","typ":"at+jwt"}""");
    var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
    {
      ["iss"] = Issuer,
      ["aud"] = resource,
      ["sub"] = "user-1",
      ["scope"] = scope,
      ["iat"] = now,
      ["exp"] = now + AccessSeconds,
      ["jti"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)),
    });
    var input = TestIssuer.B64(header) + "." + TestIssuer.B64(payload);
    return input + "." + TestIssuer.B64(Rsa.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
  }

  public async ValueTask DisposeAsync()
  {
    await _http.DisposeAsync();
    Rsa.Dispose();
  }

  private async ValueTask HandleAsync(IHttpExchange exchange)
  {
    var path = exchange.Request.Path;
    if (path == "/.well-known/oauth-authorization-server")
    {
      await WriteJsonAsync(exchange, 200, Metadata());
    }
    else if (path == "/token" && exchange.Request.Method == "POST")
    {
      await TokenAsync(exchange);
    }
    else if (path == "/register" && exchange.Request.Method == "POST" && Register)
    {
      var id = "dyn-" + Interlocked.Increment(ref _registrations).ToString(System.Globalization.CultureInfo.InvariantCulture);
      await WriteJsonAsync(exchange, 201, $$"""{"client_id":"{{id}}","token_endpoint_auth_method":"none"}""");
    }
    else
    {
      await WriteJsonAsync(exchange, 404, "{}");
    }
  }

  private string Metadata()
  {
    var doc = new Dictionary<string, object>
    {
      ["issuer"] = MetadataIssuer ?? Issuer,
      ["authorization_endpoint"] = Issuer + "/authorize",
      ["token_endpoint"] = Issuer + "/token",
      ["token_endpoint_auth_methods_supported"] = AuthMethods,
      ["grant_types_supported"] = new[] { "authorization_code", "refresh_token", "client_credentials" },
      ["authorization_response_iss_parameter_supported"] = IssParameterSupported,
    };
    if (Register)
    {
      doc["registration_endpoint"] = Issuer + "/register";
    }

    if (PkceMethods is not null)
    {
      doc["code_challenge_methods_supported"] = PkceMethods;
    }

    if (ScopesSupported is not null)
    {
      doc["scopes_supported"] = ScopesSupported;
    }

    return JsonSerializer.Serialize(doc);
  }

  private async ValueTask TokenAsync(IHttpExchange exchange)
  {
    using var buffer = new System.IO.MemoryStream();
    await exchange.Request.Body.CopyToAsync(buffer);
    var form = AuthorizationResponse.ParseQuery(Encoding.UTF8.GetString(buffer.ToArray()), out _);
    var call = new TokenCall(form, exchange.Request.Headers.GetFirst("Authorization"));
    lock (_lock)
    {
      TokenCalls.Add(call);
    }

    if (RedirectTo is { } elsewhere)
    {
      exchange.Response.SetHeader("Location", elsewhere);
      await WriteJsonAsync(exchange, 302, "{}");
      return;
    }

    if (ErrorDescriptionEcho is { } echoKey && form.TryGetValue(echoKey, out var echoed))
    {
      await WriteJsonAsync(exchange, 400, JsonSerializer.Serialize(new Dictionary<string, string> { ["error"] = "invalid_grant", ["error_description"] = "bad value " + echoed }));
      return;
    }

    var grant = form.GetValueOrDefault("grant_type");
    switch (grant)
    {
      case "authorization_code":
        await CodeGrantAsync(exchange, form);
        break;
      case "refresh_token":
        await RefreshGrantAsync(exchange, form);
        break;
      case "client_credentials":
        await ClientCredentialsGrantAsync(exchange, form, call);
        break;
      case "urn:ietf:params:oauth:grant-type:token-exchange":
        await WriteJsonAsync(exchange, 200, """{"access_token":"idjag-0123456789","issued_token_type":"urn:ietf:params:oauth:token-type:id-jag","token_type":"N_A","expires_in":300}""");
        break;
      case "urn:ietf:params:oauth:grant-type:jwt-bearer" when form.GetValueOrDefault("assertion") == "idjag-0123456789":
        await IssueAsync(exchange, form.GetValueOrDefault("scope", string.Empty), form["resource"], withRefresh: false);
        break;
      default:
        await WriteJsonAsync(exchange, 400, """{"error":"unsupported_grant_type"}""");
        break;
    }
  }

  private async ValueTask CodeGrantAsync(IHttpExchange exchange, Dictionary<string, string> form)
  {
    (string Challenge, string Scope, string Resource, string RedirectUri)? found;
    lock (_lock)
    {
      found = _codes.Remove(form.GetValueOrDefault("code", string.Empty), out var entry) ? entry : null;
    }

    var verifier = form.GetValueOrDefault("code_verifier", string.Empty);
    var computed = TestIssuer.B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    if (found is not { } pending || computed != pending.Challenge || form.GetValueOrDefault("redirect_uri") != pending.RedirectUri || form.GetValueOrDefault("resource") != pending.Resource)
    {
      await WriteJsonAsync(exchange, 400, """{"error":"invalid_grant"}""");
      return;
    }

    await IssueAsync(exchange, pending.Scope, pending.Resource, withRefresh: true);
  }

  private async ValueTask RefreshGrantAsync(IHttpExchange exchange, Dictionary<string, string> form)
  {
    string? scope;
    lock (_lock)
    {
      if (!_refresh.Remove(form.GetValueOrDefault("refresh_token", string.Empty), out scope))
      {
        scope = null;
      }
    }

    if (scope is null)
    {
      await WriteJsonAsync(exchange, 400, """{"error":"invalid_grant"}""");
      return;
    }

    await IssueAsync(exchange, scope, form["resource"], withRefresh: true);
  }

  private async ValueTask ClientCredentialsGrantAsync(IHttpExchange exchange, Dictionary<string, string> form, TokenCall call)
  {
    var ok = false;
    if (form.TryGetValue("client_assertion", out var assertion) && ClientAssertionKey is { } key)
    {
      var parts = assertion.Split('.');
      ok = parts.Length == 3 && key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), System.Buffers.Text.Base64Url.DecodeFromChars(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
    else if (call.Authorization is { } basic && basic.StartsWith("Basic ", StringComparison.Ordinal) && ExpectedClientSecret is { } secret)
    {
      ok = Encoding.UTF8.GetString(Convert.FromBase64String(basic[6..])).EndsWith(":" + secret, StringComparison.Ordinal);
    }
    else if (form.GetValueOrDefault("client_secret") is { } posted && ExpectedClientSecret is { } expected)
    {
      ok = posted == expected;
    }

    if (!ok)
    {
      await WriteJsonAsync(exchange, 401, """{"error":"invalid_client"}""");
      return;
    }

    await IssueAsync(exchange, form.GetValueOrDefault("scope", string.Empty), form["resource"], withRefresh: false);
  }

  private async ValueTask IssueAsync(IHttpExchange exchange, string scope, string resource, bool withRefresh)
  {
    var doc = new Dictionary<string, object>
    {
      ["access_token"] = Mint(scope, resource),
      ["token_type"] = "Bearer",
      ["expires_in"] = AccessSeconds,
    };
    if (withRefresh)
    {
      var refresh = "refresh-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
      lock (_lock)
      {
        _refresh[refresh] = scope;
      }

      doc["refresh_token"] = refresh;
    }

    await WriteJsonAsync(exchange, 200, JsonSerializer.Serialize(doc));
  }

  internal static async ValueTask WriteJsonAsync(IHttpExchange exchange, int status, string json)
  {
    exchange.Response.StatusCode = status;
    exchange.Response.SetHeader("Content-Type", "application/json");
    await exchange.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(json));
  }
}
