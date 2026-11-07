using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.Jose;

namespace RecursiveAI.OneModelContext.Auth.Server;

/// <summary>
/// An OAuth 2.1 authorization server (draft-ietf-oauth-v2-1-13) for an MCP endpoint, hosted on the same request abstraction as the
/// Streamable HTTP endpoint so that the built-in listener and ASP.NET Core both serve it. Users are never seen here: the host signs them
/// in and obtains consent through <see cref="AuthorizationServerOptions.AuthorizeUser"/>.
/// </summary>
/// <remarks>
/// Endpoints: RFC 8414 metadata (also as OpenID Connect discovery), <c>authorize</c>, <c>token</c>, <c>revoke</c> (RFC 7009), the key set,
/// and, when switched on, registration (RFC 7591). Rules enforced: PKCE with S256 only (RFC 7636); exact redirect URI match with the
/// loopback port exception (RFC 8252 section 7.3); <c>iss</c> on every authorization response, errors included (RFC 9207); a required
/// <c>resource</c> that becomes the token's audience (RFC 8707); single-use authorization codes whose replay revokes what they produced;
/// refresh tokens that rotate on every use, where reuse of a spent token revokes its whole family. Access tokens are JWTs in the
/// RFC 9068 shape, signed with the host's keys, and validate with <see cref="JwtTokenValidator"/> unchanged; use
/// <see cref="CreateTokenValidator"/> to also honour revocation when the resource server runs in the same process.
/// </remarks>
public sealed partial class AuthorizationServer : IDisposable
{
  private const string JwtBearerGrant = "urn:ietf:params:oauth:grant-type:jwt-bearer";
  private const string ClientAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
  private const string IdJagProfile = "urn:ietf:params:oauth:grant-profile:id-jag";

  internal static readonly HashSet<string> ReservedClaims = new(StringComparer.Ordinal)
  {
    "iss", "sub", "aud", "exp", "nbf", "iat", "jti", "scope", "client_id", "sid",
  };

  private static readonly string[] s_signingAlgorithms = ["RS256", "RS384", "RS512", "PS256", "PS384", "PS512", "ES256", "ES384", "ES512"];

  private readonly AuthorizationServerOptions _options;
  private readonly TimeProvider _time;
  private readonly IAuthorizationStore _store;
  private readonly ClientMetadataResolver _clients;
  private readonly string _issuer;
  private readonly string _issuerPath;
  private readonly HashSet<string> _resources;
  private readonly byte[] _resumeKey;
  private readonly byte[] _metadataJson;
  private readonly HashSet<string> _routes = new(StringComparer.Ordinal);
  private long _lastPurgeTicks;

  /// <summary>Creates the server.</summary>
  /// <param name="options">Its settings.</param>
  /// <exception cref="ArgumentException">An option is invalid: the issuer is not HTTPS (or loopback HTTP) or has a query or fragment, no resource or signing key is given, a resource is not an absolute URI, key ids repeat, or the resume key is shorter than 32 bytes.</exception>
  public AuthorizationServer(AuthorizationServerOptions options)
  {
    ArgumentNullException.ThrowIfNull(options);
    _options = options;
    _time = options.TimeProvider ?? TimeProvider.System;
    _store = options.Store;
    var issuer = options.Issuer;
    if (!issuer.IsAbsoluteUri || issuer.Query.Length != 0 || issuer.Fragment.Length != 0 || issuer.UserInfo.Length != 0
      || !(issuer.Scheme == Uri.UriSchemeHttps || (issuer.Scheme == Uri.UriSchemeHttp && RedirectUriRules.IsLoopbackHost(issuer))))
    {
      throw new ArgumentException("The issuer must be an absolute https URL without query or fragment (http only on a loopback host).", nameof(options));
    }

    // RFC 8414 section 2: the issuer has no trailing slash when it has no path.
    _issuer = issuer.GetLeftPart(UriPartial.Path).TrimEnd('/');
    _issuerPath = issuer.AbsolutePath.TrimEnd('/');
    if (options.Resources.Count == 0)
    {
      throw new ArgumentException("At least one resource is required.", nameof(options));
    }

    _resources = [];
    foreach (var resource in options.Resources)
    {
      _resources.Add(ResourceIdentifier.Canonicalize(resource) ?? throw new ArgumentException("A resource is not an absolute URI.", nameof(options)));
    }

    if (options.SigningKeys.Count == 0 || options.SigningKeys.Select(k => k.KeyId).Distinct(StringComparer.Ordinal).Count() != options.SigningKeys.Count)
    {
      throw new ArgumentException("Give at least one signing key; key ids must be unique.", nameof(options));
    }

    if (options.ResumeKey is { Length: < 32 })
    {
      throw new ArgumentException("The resume key must be at least 32 bytes.", nameof(options));
    }

    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.AccessTokenLifetime, TimeSpan.Zero);
    _resumeKey = options.ResumeKey is { } key ? [.. key] : RandomNumberGenerator.GetBytes(32);
    _clients = new ClientMetadataResolver(options.ClientMetadataDocuments, options.ClientKeyFetch, _time);
    _metadataJson = BuildMetadata();

    _routes.Add(_issuerPath + options.AuthorizePath);
    _routes.Add(_issuerPath + options.TokenPath);
    _routes.Add(_issuerPath + options.RevocationPath);
    _routes.Add(_issuerPath + options.JwksPath);
    _routes.Add(_issuerPath + "/.well-known/openid-configuration");
    _routes.Add("/.well-known/oauth-authorization-server" + _issuerPath);
    _routes.Add("/.well-known/openid-configuration" + _issuerPath);
    if (_issuerPath.Length == 0 || options.ServeMetadataAtOrigin)
    {
      _routes.Add("/.well-known/oauth-authorization-server");
    }

#pragma warning disable CS0618 // OMC0004: the registration endpoint is this library's own implementation of a deprecated feature.
    if (options.DynamicClientRegistration)
    {
      _routes.Add(_issuerPath + options.RegistrationPath);
    }
#pragma warning restore CS0618
  }

  /// <summary>The issuer identifier published in the metadata and placed in every token and authorization response.</summary>
  public string Issuer => _issuer;

  /// <summary>The URL of the authorization endpoint.</summary>
  public Uri AuthorizationEndpoint => new(_issuer + _options.AuthorizePath);

  /// <summary>The URL of the token endpoint.</summary>
  public Uri TokenEndpoint => new(_issuer + _options.TokenPath);

  /// <summary>The URL of the key set.</summary>
  public Uri JwksUri => new(_issuer + _options.JwksPath);

  /// <summary>The store the server was configured with, for registering clients.</summary>
  public IAuthorizationStore Store => _store;

  /// <summary>Every path the server answers, so a host can route exactly these to <see cref="HandleAsync"/>. Paths are absolute, from the origin.</summary>
  public IReadOnlyCollection<string> Paths => _routes;

  /// <summary>Whether the request path belongs to this server, so a host can route to <see cref="HandleAsync"/> before its own handlers.</summary>
  /// <param name="path">The request path without the query.</param>
  public bool IsServerPath(string path) => path is not null && _routes.Contains(path);

  /// <summary>
  /// Answers a request if its path is one of the server's endpoints. It never throws for bad input: malformed requests get the
  /// OAuth error response, and an internal fault gets a bare 500 with no detail.
  /// </summary>
  /// <param name="exchange">The request and response.</param>
  /// <returns>True when the request was handled; false when the path is not the server's and nothing was written.</returns>
  public async ValueTask<bool> HandleAsync(IHttpExchange exchange)
  {
    ArgumentNullException.ThrowIfNull(exchange);
    var path = exchange.Request.Path;
    if (!_routes.Contains(path))
    {
      return false;
    }

    try
    {
      var ct = exchange.Aborted;
      if (path == _issuerPath + _options.AuthorizePath)
      {
        await AuthorizeAsync(exchange, ct).ConfigureAwait(false);
      }
      else if (path == _issuerPath + _options.TokenPath)
      {
        await TokenAsync(exchange, ct).ConfigureAwait(false);
      }
      else if (path == _issuerPath + _options.RevocationPath)
      {
        await RevokeAsync(exchange, ct).ConfigureAwait(false);
      }
      else if (path == _issuerPath + _options.JwksPath)
      {
        await WriteDocumentAsync(exchange, PublishedKeys(), ct).ConfigureAwait(false);
      }
      else if (path == _issuerPath + _options.RegistrationPath && _routes.Contains(path))
      {
        await RegisterAsync(exchange, ct).ConfigureAwait(false);
      }
      else
      {
        await WriteDocumentAsync(exchange, _metadataJson, ct).ConfigureAwait(false);
      }
    }
    catch (OperationCanceledException) when (exchange.Aborted.IsCancellationRequested)
    {
      // The peer went away; there is nobody to answer.
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
      // Only the exception type is reported: its message could carry request content.
      ContextDiagnostics.Publish(DiagnosticKind.HandlerFault, "Authorization server fault: " + ex.GetType().Name);
      if (!exchange.Response.Body.CanWrite || exchange.Response.StatusCode == 200)
      {
        try
        {
          await WriteOAuthErrorAsync(exchange, 500, "server_error", "The server could not complete the request.", null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception inner) when (inner is InvalidOperationException or IOException or ObjectDisposedException)
        {
          // The response had already started.
        }
      }
    }

    return true;
  }

  /// <summary>
  /// Creates a validator for this server's own access tokens: <see cref="JwtTokenValidator"/> over the published keys, which also refuses
  /// tokens that were revoked, or whose refresh family was revoked, in the store. For a resource server in the same process.
  /// </summary>
  public ITokenValidator CreateTokenValidator() => new OwnTokenValidator(this);

  /// <inheritdoc />
  public void Dispose() => _clients.Dispose();

  // ------------------------------------------------------------------------------------------------ metadata and keys

  private byte[] BuildMetadata()
  {
    var buffer = new ArrayBufferWriter<byte>();
    using (var w = new Utf8JsonWriter(buffer))
    {
      w.WriteStartObject();
      w.WriteString("issuer", _issuer);
      w.WriteString("authorization_endpoint", _issuer + _options.AuthorizePath);
      w.WriteString("token_endpoint", _issuer + _options.TokenPath);
      w.WriteString("jwks_uri", _issuer + _options.JwksPath);
      w.WriteString("revocation_endpoint", _issuer + _options.RevocationPath);
#pragma warning disable CS0618 // OMC0004
      if (_options.DynamicClientRegistration)
      {
        w.WriteString("registration_endpoint", _issuer + _options.RegistrationPath);
      }
#pragma warning restore CS0618

      if (_options.ScopesSupported.Count > 0)
      {
        WriteStrings(w, "scopes_supported", _options.ScopesSupported);
      }

      WriteStrings(w, "response_types_supported", ["code"]);
      WriteStrings(w, "response_modes_supported", ["query"]);
      var grants = new List<string> { "authorization_code" };
      if (_options.IssueRefreshTokens)
      {
        grants.Add("refresh_token");
      }

      grants.Add("client_credentials");
      if (_options.EnterpriseAuthorization is not null)
      {
        grants.Add(JwtBearerGrant);
      }

      WriteStrings(w, "grant_types_supported", grants);
      WriteStrings(w, "token_endpoint_auth_methods_supported", ["none", "client_secret_basic", "client_secret_post", "private_key_jwt"]);
      WriteStrings(w, "token_endpoint_auth_signing_alg_values_supported", s_signingAlgorithms);
      WriteStrings(w, "revocation_endpoint_auth_methods_supported", ["none", "client_secret_basic", "client_secret_post", "private_key_jwt"]);

      // Draft OAuth 2.1 and the MCP authorization page: clients refuse to proceed unless S256 is advertised.
      WriteStrings(w, "code_challenge_methods_supported", ["S256"]);

      // RFC 9207 section 3: advertised because every authorization response carries iss.
      w.WriteBoolean("authorization_response_iss_parameter_supported", true);
      if (_options.ClientMetadataDocuments is not null)
      {
        w.WriteBoolean("client_id_metadata_document_supported", true);
      }

      if (_options.EnterpriseAuthorization is not null)
      {
        // draft-ietf-oauth-identity-assertion-authz-grant section 7.2.
        WriteStrings(w, "authorization_grant_profiles_supported", [IdJagProfile]);
      }

      w.WriteEndObject();
    }

    return buffer.WrittenSpan.ToArray();
  }

  private static void WriteStrings(Utf8JsonWriter w, string name, IEnumerable<string> values)
  {
    w.WriteStartArray(name);
    foreach (var value in values)
    {
      w.WriteStringValue(value);
    }

    w.WriteEndArray();
  }

  private JsonWebSigningKey? ActiveKey(DateTimeOffset now) => _options.SigningKeys.FirstOrDefault(k => k.RetiredAt is null || k.RetiredAt > now);

  // A retired key stays published until the last token it signed (lifetime plus a minute of clock skew) can no longer be valid.
  private IEnumerable<JsonWebSigningKey> PublishedSigningKeys(DateTimeOffset now) =>
    _options.SigningKeys.Where(k => k.RetiredAt is null || k.RetiredAt + _options.AccessTokenLifetime + TimeSpan.FromMinutes(1) > now);

  private byte[] PublishedKeys()
  {
    var buffer = new ArrayBufferWriter<byte>();
    using (var w = new Utf8JsonWriter(buffer))
    {
      w.WriteStartObject();
      w.WriteStartArray("keys");
      foreach (var key in PublishedSigningKeys(_time.GetUtcNow()))
      {
        key.WritePublicJwk(w);
      }

      w.WriteEndArray();
      w.WriteEndObject();
    }

    return buffer.WrittenSpan.ToArray();
  }

  private static async Task WriteDocumentAsync(IHttpExchange exchange, byte[] json, CancellationToken cancellationToken)
  {
    var request = exchange.Request;
    if (request.Method is not ("GET" or "HEAD"))
    {
      exchange.Response.StatusCode = 405;
      exchange.Response.SetHeader("Allow", "GET, HEAD");
      return;
    }

    var response = exchange.Response;
    response.StatusCode = 200;
    response.SetHeader("Content-Type", "application/json");
    response.SetHeader("Cache-Control", "public, max-age=300");
    response.SetHeader("X-Content-Type-Options", "nosniff");
    if (request.Method == "GET")
    {
      await response.Body.WriteAsync(json, cancellationToken).ConfigureAwait(false);
    }
  }

  // ------------------------------------------------------------------------------------------------ response helpers

  private static async Task WriteJsonAsync(IHttpExchange exchange, int status, Action<Utf8JsonWriter> write, CancellationToken cancellationToken)
  {
    var buffer = new ArrayBufferWriter<byte>();
    using (var w = new Utf8JsonWriter(buffer))
    {
      write(w);
    }

    var response = exchange.Response;
    response.StatusCode = status;
    response.SetHeader("Content-Type", "application/json");

    // RFC 6749 section 5.1: responses with tokens must not be cached.
    response.SetHeader("Cache-Control", "no-store");
    response.SetHeader("Pragma", "no-cache");
    response.SetHeader("X-Content-Type-Options", "nosniff");
    await response.Body.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
  }

  private static async Task WriteOAuthErrorAsync(IHttpExchange exchange, int status, string error, string description, string? wwwAuthenticate, CancellationToken cancellationToken)
  {
    if (wwwAuthenticate is not null)
    {
      exchange.Response.SetHeader("WWW-Authenticate", wwwAuthenticate);
    }

    // RFC 6749 section 5.2. The description is always one of this class's fixed strings, never request content.
    await WriteJsonAsync(exchange, status, w =>
    {
      w.WriteStartObject();
      w.WriteString("error", error);
      w.WriteString("error_description", description);
      w.WriteEndObject();
    }, cancellationToken).ConfigureAwait(false);
  }

  private static async Task WriteTextErrorAsync(IHttpExchange exchange, int status, string message, CancellationToken cancellationToken)
  {
    var response = exchange.Response;
    response.StatusCode = status;
    response.SetHeader("Content-Type", "text/plain; charset=utf-8");
    response.SetHeader("Cache-Control", "no-store");
    response.SetHeader("X-Content-Type-Options", "nosniff");
    await response.Body.WriteAsync(Encoding.UTF8.GetBytes(message), cancellationToken).ConfigureAwait(false);
  }

  // ------------------------------------------------------------------------------------------------ request parsing

  /// <summary>Parameters of a form or query string. A name that occurs twice is remembered as a duplicate (RFC 6749 section 3.1: parameters must not repeat).</summary>
  private sealed class Parameters
  {
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _duplicates = new(StringComparer.Ordinal);

    internal static Parameters Parse(string encoded)
    {
      var result = new Parameters();
      var count = 0;
      foreach (var pair in encoded.Split('&', StringSplitOptions.RemoveEmptyEntries))
      {
        if (++count > 64)
        {
          break;
        }

        var eq = pair.IndexOf('=', StringComparison.Ordinal);
        var name = Decode(eq < 0 ? pair : pair[..eq]);
        var value = eq < 0 ? string.Empty : Decode(pair[(eq + 1)..]);
        if (!result._values.TryAdd(name, value))
        {
          result._duplicates.Add(name);
        }
      }

      return result;
    }

    internal bool HasDuplicates => _duplicates.Count > 0;

    internal bool IsDuplicate(string name) => _duplicates.Contains(name);

    internal bool Has(string name) => _values.ContainsKey(name);

    internal string? Get(string name) => _values.GetValueOrDefault(name);

    internal static string Decode(string value) => Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal));
  }

  private async Task<Parameters?> ReadFormAsync(IHttpExchange exchange, CancellationToken cancellationToken)
  {
    var request = exchange.Request;
    var contentType = request.Headers.GetFirst("Content-Type");
    if (contentType is null || !contentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
    {
      return null;
    }

    var body = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
    return body is null ? null : Parameters.Parse(Encoding.UTF8.GetString(body));
  }

  private async Task<byte[]?> ReadBodyAsync(IHttpRequest request, CancellationToken cancellationToken)
  {
    var max = _options.MaxRequestBodyBytes;
    if (request.ContentLength is { } declared && declared > max)
    {
      return null;
    }

    using var ms = new MemoryStream();
    var buffer = new byte[4096];
    int n;
    while ((n = await request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
    {
      if (ms.Length + n > max)
      {
        return null;
      }

      ms.Write(buffer, 0, n);
    }

    return ms.ToArray();
  }

  private static string RandomToken() => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

  private static string Hash(string value) => System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

  private void SecurityEvent(string text)
  {
    // The texts are fixed phrases; they never include a code, token, secret or client-supplied value.
    ContextDiagnostics.Publish(DiagnosticKind.AuthRejected, "Authorization server: " + text);
    _options.OnSecurityEvent?.Invoke(text);
  }

  private async Task MaybePurgeAsync(DateTimeOffset now, CancellationToken cancellationToken)
  {
    var last = Interlocked.Read(ref _lastPurgeTicks);
    if (now.UtcTicks - last < TimeSpan.FromMinutes(5).Ticks || Interlocked.CompareExchange(ref _lastPurgeTicks, now.UtcTicks, last) != last)
    {
      return;
    }

    await _store.PurgeAsync(now, cancellationToken).ConfigureAwait(false);
  }

  // ------------------------------------------------------------------------------------------------ scopes and resources

  private static List<string> SplitScopes(string? scope) =>
    string.IsNullOrWhiteSpace(scope) ? [] : scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();

  /// <summary>
  /// Whether a scope allow-list applies to the client: the server's <see cref="AuthorizationServerOptions.ScopesSupported"/> or the
  /// client's own <see cref="RegisteredClient.Scopes"/>. Without one every well-formed scope string would be granted.
  /// </summary>
  private bool HasScopeAllowList(RegisteredClient client) => _options.ScopesSupported.Count > 0 || client.Scopes is not null;

  // RFC 6749 section 3.3: scope-token = 1*( %x21 / %x23-5B / %x5D-7E ), so no quote, backslash, control character or non-ASCII.
  private static bool IsScopeToken(string scope)
  {
    if (scope.Length is 0 or > 256)
    {
      return false;
    }

    foreach (var c in scope)
    {
      if (!(c is '\u0021' or (>= '\u0023' and <= '\u005b') or (>= '\u005d' and <= '\u007e')))
      {
        return false;
      }
    }

    return true;
  }

  private static bool AreScopeTokens(List<string> scopes) => scopes.Count <= 64 && scopes.All(IsScopeToken);

  /// <summary>Restricts requested scopes to well-formed ones that the server supports and the client may have (RFC 6749 section 3.3 lets the server ignore the rest).</summary>
  private List<string> AllowedScopes(IEnumerable<string> requested, RegisteredClient client)
  {
    var list = new List<string>();
    foreach (var scope in requested)
    {
      if (IsScopeToken(scope)
        && (_options.ScopesSupported.Count == 0 || _options.ScopesSupported.Contains(scope, StringComparer.Ordinal))
        && (client.Scopes is null || client.Scopes.Contains(scope, StringComparer.Ordinal)))
      {
        list.Add(scope);
      }
    }

    return list;
  }

  /// <summary>Picks the resource a request is for (RFC 8707). Returns null with an error text when it is missing or not one of ours.</summary>
  private string? ChooseResource(string? requested, bool duplicated, out string error)
  {
    error = string.Empty;
    if (duplicated)
    {
      error = "Only one resource may be requested.";
      return null;
    }

    if (string.IsNullOrEmpty(requested))
    {
      if (_options.AllowMissingResource && _resources.Count == 1)
      {
        return _resources.First();
      }

      error = "The resource parameter is required.";
      return null;
    }

    var canonical = ResourceIdentifier.Canonicalize(requested);
    if (canonical is null || !_resources.Contains(canonical))
    {
      error = "This server issues no tokens for that resource.";
      return null;
    }

    return canonical;
  }

  // ------------------------------------------------------------------------------------------------ tokens

  private string IssueAccessToken(string subject, string clientId, string scope, string resource, string? familyId, IReadOnlyDictionary<string, string> claims, DateTimeOffset now)
  {
    var key = ActiveKey(now) ?? throw new InvalidOperationException("No signing key is active.");
    var buffer = new ArrayBufferWriter<byte>();
    using (var w = new Utf8JsonWriter(buffer))
    {
      w.WriteStartObject();
      foreach (var (name, value) in claims)
      {
        if (!ReservedClaims.Contains(name))
        {
          w.WriteString(name, value);
        }
      }

      w.WriteString("iss", _issuer);
      w.WriteString("sub", subject);

      // RFC 9068 section 2.2: aud is the resource the token is for; RFC 8707 section 2.
      w.WriteString("aud", resource);
      w.WriteString("client_id", clientId);
      if (scope.Length > 0)
      {
        w.WriteString("scope", scope);
      }

      w.WriteNumber("iat", now.ToUnixTimeSeconds());
      w.WriteNumber("nbf", now.ToUnixTimeSeconds());
      w.WriteNumber("exp", (now + _options.AccessTokenLifetime).ToUnixTimeSeconds());
      w.WriteString("jti", RandomToken());
      if (familyId is not null)
      {
        w.WriteString("sid", familyId);
      }

      w.WriteEndObject();
    }

    return key.SignJwt("at+jwt", buffer.WrittenSpan);
  }

  private Task WriteTokenResponseAsync(IHttpExchange exchange, string accessToken, string scope, string? refreshToken, string? resource, CancellationToken cancellationToken) =>
    WriteJsonAsync(exchange, 200, w =>
    {
      w.WriteStartObject();
      w.WriteString("access_token", accessToken);
      w.WriteString("token_type", "Bearer");
      w.WriteNumber("expires_in", (long)_options.AccessTokenLifetime.TotalSeconds);
      if (scope.Length > 0)
      {
        w.WriteString("scope", scope);
      }

      if (refreshToken is not null)
      {
        w.WriteString("refresh_token", refreshToken);
      }

      if (resource is not null)
      {
        w.WriteString("resource", resource);
      }

      w.WriteEndObject();
    }, cancellationToken);

  private async Task RevokeFamilyAsync(string familyId, CancellationToken cancellationToken)
  {
    // The record has to outlive every token of the family: the longest a family can live plus one access token.
    var until = _time.GetUtcNow() + _options.MaxRefreshFamilyLifetime + _options.AccessTokenLifetime + TimeSpan.FromMinutes(1);
    await _store.RevokeAsync("family:" + familyId, until, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Checks this server's own access tokens, refusing revoked ones.</summary>
  private sealed class OwnTokenValidator : ITokenValidator
  {
    private readonly AuthorizationServer _server;
    private readonly JwtTokenValidator _inner;

    internal OwnTokenValidator(AuthorizationServer server)
    {
      _server = server;
      _inner = new JwtTokenValidator(server._issuer, new OwnKeys(server), server._time) { RequireAccessTokenType = true };
    }

    public async ValueTask<TokenValidationResult> ValidateAsync(string accessToken, TokenValidationRequest request, CancellationToken cancellationToken)
    {
      var result = await _inner.ValidateAsync(accessToken, request, cancellationToken).ConfigureAwait(false);
      if (!result.IsValid)
      {
        return result;
      }

      var token = JsonWebToken.Parse(accessToken, _inner.MaxTokenLength, out _);
      if (token is null)
      {
        return TokenValidationResult.Failure("The token is not valid.");
      }

      if (token.Payload.TryGetProperty("jti", out var jti) && jti.ValueKind == JsonValueKind.String
        && await _server._store.IsRevokedAsync("jti:" + jti.GetString(), cancellationToken).ConfigureAwait(false))
      {
        return TokenValidationResult.Failure("The token has been revoked.");
      }

      if (token.Payload.TryGetProperty("sid", out var sid) && sid.ValueKind == JsonValueKind.String
        && await _server._store.IsRevokedAsync("family:" + sid.GetString(), cancellationToken).ConfigureAwait(false))
      {
        return TokenValidationResult.Failure("The token has been revoked.");
      }

      return result;
    }
  }

  private sealed class OwnKeys(AuthorizationServer server) : IJwksProvider
  {
    public ValueTask<JsonWebKeySet?> GetKeysAsync(bool forceRefresh, CancellationToken cancellationToken) =>
      ValueTask.FromResult<JsonWebKeySet?>(new JsonWebKeySet([.. server.PublishedSigningKeys(server._time.GetUtcNow()).Select(k => k.ToPublicKey())]));
  }
}
