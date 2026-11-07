using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.Auth;

/// <summary>How this server authenticates itself to an introspection endpoint (RFC 7662 section 2.1).</summary>
public sealed class IntrospectionClientAuthentication
{
  private readonly AuthenticationHeaderValue _header;

  private IntrospectionClientAuthentication(AuthenticationHeaderValue header) => _header = header;

  /// <summary>HTTP Basic with a client id and secret (RFC 6749 section 2.3.1: both are form-urlencoded first).</summary>
  /// <param name="clientId">The client id.</param>
  /// <param name="clientSecret">The client secret. It is sent on every introspection call and never logged.</param>
  public static IntrospectionClientAuthentication Basic(string clientId, string clientSecret)
  {
    ArgumentException.ThrowIfNullOrEmpty(clientId);
    ArgumentNullException.ThrowIfNull(clientSecret);
    var raw = Uri.EscapeDataString(clientId).Replace("%20", "+", StringComparison.Ordinal) + ":" + Uri.EscapeDataString(clientSecret).Replace("%20", "+", StringComparison.Ordinal);
    return new IntrospectionClientAuthentication(new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))));
  }

  /// <summary>A bearer token that authorizes this server to introspect (RFC 7662 section 2.1 allows it).</summary>
  /// <param name="token">The token.</param>
  public static IntrospectionClientAuthentication Bearer(string token)
  {
    ArgumentException.ThrowIfNullOrEmpty(token);
    return new IntrospectionClientAuthentication(new AuthenticationHeaderValue("Bearer", token));
  }

  internal void Apply(HttpRequestMessage request) => request.Headers.Authorization = _header;

  /// <inheritdoc />
  public override string ToString() => "IntrospectionClientAuthentication";
}

/// <summary>
/// Validates opaque tokens by asking the authorization server (RFC 7662). A token is accepted only when the answer says
/// <c>active</c> and its <c>aud</c> names this resource; an answer without <c>aud</c> is refused because the audience cannot be confirmed
/// (RFC 8707). Active answers are cached, never beyond the token's <c>exp</c> and never beyond <see cref="MaxCacheLifetime"/>; inactive
/// answers are not cached. The cache is keyed by a hash of the token, so tokens are not retained in memory.
/// </summary>
public sealed class IntrospectionTokenValidator : ITokenValidator, IDisposable
{
  private readonly Uri _endpoint;
  private readonly IntrospectionClientAuthentication _auth;
  private readonly HttpClient _http;
  private readonly bool _ownsClient;
  private readonly TimeProvider _time;
  private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

  /// <summary>Creates a validator.</summary>
  /// <param name="introspectionEndpoint">The endpoint. HTTPS, or HTTP on a loopback host, because credentials and tokens travel to it.</param>
  /// <param name="authentication">How to authenticate to it.</param>
  /// <param name="httpClient">The client to call with, or null for a private one that never follows redirects. A client you supply is not disposed, and a redirect response is a failure either way.</param>
  /// <param name="timeProvider">The clock; null for the system clock.</param>
  public IntrospectionTokenValidator(Uri introspectionEndpoint, IntrospectionClientAuthentication authentication, HttpClient? httpClient = null, TimeProvider? timeProvider = null)
  {
    ArgumentNullException.ThrowIfNull(introspectionEndpoint);
    ArgumentNullException.ThrowIfNull(authentication);
    if (!introspectionEndpoint.IsAbsoluteUri || !(introspectionEndpoint.Scheme == Uri.UriSchemeHttps || (introspectionEndpoint.Scheme == Uri.UriSchemeHttp && introspectionEndpoint.IsLoopback)) || introspectionEndpoint.UserInfo.Length != 0)
    {
      throw new ArgumentException("The introspection endpoint must be an absolute https URI (http only on a loopback host) without credentials.", nameof(introspectionEndpoint));
    }

    _endpoint = introspectionEndpoint;
    _auth = authentication;
    _time = timeProvider ?? TimeProvider.System;
    if (httpClient is null)
    {
      _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
      _ownsClient = true;
    }
    else
    {
      _http = httpClient;
    }
  }

  /// <summary>If set, the answer's <c>iss</c> must equal this exactly. Default none.</summary>
  public string? Issuer { get; init; }

  /// <summary>Longest an active answer is cached. Default 60 seconds; zero turns caching off.</summary>
  public TimeSpan MaxCacheLifetime { get; init; } = TimeSpan.FromSeconds(60);

  /// <summary>Most cached answers; the cache is emptied when it fills. Default 1000.</summary>
  public int MaxCacheEntries { get; init; } = 1000;

  /// <summary>Longest token sent for introspection, in characters. Default 8192.</summary>
  public int MaxTokenLength { get; init; } = 8192;

  /// <summary>Largest introspection response accepted, in bytes. Default 64 KiB.</summary>
  public int MaxResponseBytes { get; init; } = 64 * 1024;

  /// <summary>How long one introspection call may take. Default 10 seconds.</summary>
  public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

  /// <inheritdoc />
  public async ValueTask<TokenValidationResult> ValidateAsync(string accessToken, TokenValidationRequest request, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(accessToken);
    if (accessToken.Length == 0 || accessToken.Length > MaxTokenLength)
    {
      return TokenValidationResult.Failure("The token is not valid.");
    }

    var now = _time.GetUtcNow();
    var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Resource + "\n" + accessToken)));
    if (_cache.TryGetValue(key, out var cached))
    {
      if (cached.ValidUntil > now)
      {
        return TokenValidationResult.Success(new ClaimsPrincipal(cached.Principal.Clone()), cached.Scopes, cached.ExpiresAt);
      }

      _cache.TryRemove(key, out _);
    }

    JsonDocument? document;
    try
    {
      document = await IntrospectAsync(accessToken, cancellationToken).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
    {
      cancellationToken.ThrowIfCancellationRequested();
      return TokenValidationResult.TemporarilyUnavailable("The token could not be checked now.");
    }

    if (document is null)
    {
      return TokenValidationResult.TemporarilyUnavailable("The token could not be checked now.");
    }

    using (document)
    {
      var claims = document.RootElement;
      if (claims.ValueKind != JsonValueKind.Object)
      {
        return TokenValidationResult.TemporarilyUnavailable("The token could not be checked now.");
      }

      // RFC 7662 section 2.2: only "active": true is a yes; anything else, a missing member included, is a no.
      if (!claims.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.True)
      {
        return TokenValidationResult.Failure("The token is not active.");
      }

      if (Issuer is not null && !(claims.TryGetProperty("iss", out var iss) && iss.ValueKind == JsonValueKind.String && string.Equals(iss.GetString(), Issuer, StringComparison.Ordinal)))
      {
        return TokenValidationResult.Failure("The token was not issued by the expected issuer.");
      }

      if (!claims.TryGetProperty("aud", out var aud) || !ResourceIdentifier.AudienceMatches(aud, request.Resource))
      {
        return TokenValidationResult.Failure("The token was not issued for this resource.");
      }

      DateTimeOffset? expiresAt = null;
      if (claims.TryGetProperty("exp", out var exp))
      {
        if (!TryDate(exp, out var expiry) || expiry <= now)
        {
          return TokenValidationResult.Failure("The token has expired.");
        }

        expiresAt = expiry;
      }

      if (claims.TryGetProperty("nbf", out var nbf) && (!TryDate(nbf, out var notBefore) || notBefore > now))
      {
        return TokenValidationResult.Failure("The token is not valid yet.");
      }

      var scopes = new List<string>();
      if (claims.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.String)
      {
        scopes.AddRange(scope.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries));
      }

      var identity = new ClaimsIdentity("Bearer", AuthClaimTypes.Subject, null);
      var subject = Str(claims, "sub") ?? Str(claims, "username");
      if (subject is not null)
      {
        identity.AddClaim(new Claim(AuthClaimTypes.Subject, subject));
      }

      if (Str(claims, "iss") is { } issuer)
      {
        identity.AddClaim(new Claim(AuthClaimTypes.Issuer, issuer));
      }

      if (Str(claims, "client_id") is { } client)
      {
        identity.AddClaim(new Claim(AuthClaimTypes.ClientId, client));
      }

      foreach (var s in scopes)
      {
        identity.AddClaim(new Claim(AuthClaimTypes.Scope, s));
      }

      Remember(key, identity, scopes, expiresAt, now);
      return TokenValidationResult.Success(new ClaimsPrincipal(identity), scopes, expiresAt);
    }
  }

  /// <summary>Releases the private HTTP client, if this validator created one.</summary>
  public void Dispose()
  {
    if (_ownsClient)
    {
      _http.Dispose();
    }
  }

  private void Remember(string key, ClaimsIdentity identity, List<string> scopes, DateTimeOffset? expiresAt, DateTimeOffset now)
  {
    if (MaxCacheLifetime <= TimeSpan.Zero || MaxCacheEntries <= 0)
    {
      return;
    }

    // Bounded by the shorter of the configured limit and the token's own expiry.
    var until = now + MaxCacheLifetime;
    if (expiresAt is { } expiry && expiry < until)
    {
      until = expiry;
    }

    if (_cache.Count >= MaxCacheEntries)
    {
      _cache.Clear();
    }

    _cache[key] = new CacheEntry(identity, scopes, expiresAt, until);
  }

  private async Task<JsonDocument?> IntrospectAsync(string token, CancellationToken cancellationToken)
  {
    using var timeout = new CancellationTokenSource(Timeout, _time);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

    // RFC 7662 section 2.1: form-encoded POST. The token goes in the body, never in the URL.
    using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
    {
      Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("token", token), new KeyValuePair<string, string>("token_type_hint", "access_token")]),
    };
    message.Headers.TryAddWithoutValidation("Accept", "application/json");
    _auth.Apply(message);
    using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
    if (response.StatusCode != System.Net.HttpStatusCode.OK || response.Content.Headers.ContentLength > MaxResponseBytes)
    {
      return null;
    }

    await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
    using var buffer = new MemoryStream();
    var chunk = new byte[4096];
    int read;
    while ((read = await stream.ReadAsync(chunk, linked.Token).ConfigureAwait(false)) > 0)
    {
      if (buffer.Length + read > MaxResponseBytes)
      {
        return null;
      }

      buffer.Write(chunk, 0, read);
    }

    try
    {
      return JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), new JsonDocumentOptions { MaxDepth = 8 });
    }
    catch (JsonException)
    {
      return null;
    }
  }

  private static string? Str(JsonElement claims, string name) =>
    claims.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

  private static bool TryDate(JsonElement value, out DateTimeOffset date)
  {
    date = default;
    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var seconds) || double.IsNaN(seconds) || seconds < 0 || seconds > 253_402_300_799)
    {
      return false;
    }

    date = DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
    return true;
  }

  private sealed record CacheEntry(ClaimsIdentity Principal, IReadOnlyList<string> Scopes, DateTimeOffset? ExpiresAt, DateTimeOffset ValidUntil);
}
