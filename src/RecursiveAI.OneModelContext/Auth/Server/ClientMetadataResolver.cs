using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.Jose;

namespace RecursiveAI.OneModelContext.Auth.Server;

/// <summary>Rules for redirect URIs: what may be registered and what counts as the same URI.</summary>
internal static class RedirectUriRules
{
  /// <summary>
  /// Whether a URI may be registered: HTTPS; HTTP only on a loopback host (RFC 8252 section 7.3); or a private-use scheme named for a
  /// domain the client controls, so it contains a dot (RFC 8252 section 7.1). Fragments and credentials are never allowed (RFC 6749 section 3.1.2).
  /// </summary>
  internal static bool IsValidRegistration(string value)
  {
    if (value.Length is 0 or > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Fragment.Length != 0 || value.Contains('#', StringComparison.Ordinal) || uri.UserInfo.Length != 0)
    {
      return false;
    }

    if (uri.Scheme == Uri.UriSchemeHttps)
    {
      return uri.Host.Length > 0;
    }

    if (uri.Scheme == Uri.UriSchemeHttp)
    {
      return IsLoopbackHost(uri);
    }

    return uri.Scheme.Contains('.', StringComparison.Ordinal);
  }

  internal static bool IsLoopbackHost(Uri uri) =>
    uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
      ? IPAddress.TryParse(uri.IdnHost, out var ip) && IPAddress.IsLoopback(ip)
      : string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

  /// <summary>
  /// Whether the requested URI is the registered one. The comparison is exact, character for character (OAuth 2.1 section 7.5.2), except
  /// that a loopback HTTP redirect may use any port: native clients cannot know theirs in advance (RFC 8252 section 7.3).
  /// </summary>
  internal static bool Matches(string registered, string requested)
  {
    if (string.Equals(registered, requested, StringComparison.Ordinal))
    {
      return true;
    }

    if (!Uri.TryCreate(registered, UriKind.Absolute, out var a) || !Uri.TryCreate(requested, UriKind.Absolute, out var b))
    {
      return false;
    }

    if (a.Scheme != Uri.UriSchemeHttp || b.Scheme != Uri.UriSchemeHttp || !IsLoopbackHost(a) || !IsLoopbackHost(b))
    {
      return false;
    }

    // Everything but the port must still be identical: same host spelling, same path and query.
    return string.Equals(a.Host, b.Host, StringComparison.Ordinal)
      && string.Equals(StripPort(registered), StripPort(requested), StringComparison.Ordinal);
  }

  // "http://127.0.0.1:8080/cb?x=1" -> "http://127.0.0.1/cb?x=1"; operates on the text so no normalisation can hide a difference.
  private static string StripPort(string value)
  {
    var start = value.IndexOf("://", StringComparison.Ordinal) + 3;
    var end = value.IndexOfAny(['/', '?'], start);
    if (end < 0)
    {
      end = value.Length;
    }

    var authority = value[start..end];
    var colon = authority.LastIndexOf(':');
    if (colon >= 0 && authority.IndexOf(']', StringComparison.Ordinal) < colon)
    {
      authority = authority[..colon];
    }

    return value[..start] + authority + value[end..];
  }
}

/// <summary>
/// Turns a <c>client_id</c> that is an HTTPS URL into a client by fetching its metadata document under the SSRF guard
/// (draft-ietf-oauth-client-id-metadata-document sections 3, 4 and 6), and fetches <c>jwks_uri</c> key sets the same way. Results are
/// cached per <c>Cache-Control</c>, bounded, and failures are remembered briefly.
/// </summary>
internal sealed class ClientMetadataResolver : IDisposable
{
  private static readonly string[] s_allowedGrants = ["authorization_code", "refresh_token", "urn:ietf:params:oauth:grant-type:jwt-bearer"];

  private readonly ClientMetadataDocumentOptions _options;
  private readonly GuardedHttpFetcher _fetcher;
  private readonly GuardedHttpFetcher _keyFetcher;
  private readonly TimeProvider _time;
  private readonly SemaphoreSlim _slots;
  private readonly ConcurrentDictionary<string, (RegisteredClient? Client, DateTimeOffset Until)> _clients = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, (JsonWebKeySet? Keys, DateTimeOffset Until)> _keys = new(StringComparer.Ordinal);

  internal ClientMetadataResolver(ClientMetadataDocumentOptions? options, GuardedFetchOptions keyFetch, TimeProvider time)
  {
    DocumentsEnabled = options is not null;
    _options = options ?? new ClientMetadataDocumentOptions();
    _time = time;
    _fetcher = new GuardedHttpFetcher(_options.Fetch, time);
    _keyFetcher = new GuardedHttpFetcher(keyFetch, time);
    _slots = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentFetches));
  }

  internal bool DocumentsEnabled { get; }

  /// <summary>Whether a client_id has the shape of a metadata document URL (so a lookup is worth trying).</summary>
  internal bool LooksLikeUrl(string clientId) => DocumentsEnabled && clientId.StartsWith("https://", StringComparison.Ordinal);

  internal async Task<RegisteredClient?> ResolveAsync(string clientId, CancellationToken cancellationToken)
  {
    var now = _time.GetUtcNow();
    if (_clients.TryGetValue(clientId, out var cached) && cached.Until > now)
    {
      return cached.Client;
    }

    var client = await FetchAsync(clientId, cancellationToken).ConfigureAwait(false);
    // A valid result lives as the response allowed; a failed one briefly. Past the cap nothing new is cached, which only costs a refetch.
    if (_clients.Count < _options.MaxCacheEntries || _clients.ContainsKey(clientId))
    {
      Cache(_clients, clientId, client.Client, client.Lifetime, now);
    }

    return client.Client;
  }

  internal async Task<JsonWebKeySet?> GetKeysAsync(string jwksUri, bool metadataDocumentClient, CancellationToken cancellationToken)
  {
    var now = _time.GetUtcNow();
    if (_keys.TryGetValue(jwksUri, out var cached) && cached.Until > now)
    {
      return cached.Keys;
    }

    JsonWebKeySet? set = null;
    TimeSpan lifetime = _options.FailureCacheLifetime;
    if (Uri.TryCreate(jwksUri, UriKind.Absolute, out var uri) && await EnterAsync(cancellationToken).ConfigureAwait(false))
    {
      try
      {
        var result = await (metadataDocumentClient ? _fetcher : _keyFetcher).GetAsync(uri, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
          set = JsonWebKeySet.Parse(result.Body);
          lifetime = set is null ? _options.FailureCacheLifetime : Clamp(result.CacheLifetime);
        }
      }
      finally
      {
        _slots.Release();
      }
    }

    if (_keys.Count < _options.MaxCacheEntries || _keys.ContainsKey(jwksUri))
    {
      Cache(_keys, jwksUri, set, lifetime, now);
    }

    return set;
  }

  private static void Cache<T>(ConcurrentDictionary<string, (T? Value, DateTimeOffset Until)> map, string key, T? value, TimeSpan lifetime, DateTimeOffset now)
    where T : class
  {
    if (lifetime > TimeSpan.Zero)
    {
      map[key] = (value, now + lifetime);
    }
    else
    {
      map.TryRemove(key, out _);
    }
  }

  private TimeSpan Clamp(TimeSpan? fromResponse)
  {
    var lifetime = fromResponse ?? _options.DefaultCacheLifetime;
    return lifetime > _options.MaxCacheLifetime ? _options.MaxCacheLifetime : lifetime;
  }

  private async ValueTask<bool> EnterAsync(CancellationToken cancellationToken)
  {
    // Waiting is bounded by the fetch timeout: a flood of distinct client ids cannot queue unbounded work behind slow fetches.
    using var wait = new CancellationTokenSource(_options.Fetch.Timeout, _time);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, wait.Token);
    try
    {
      await _slots.WaitAsync(linked.Token).ConfigureAwait(false);
      return true;
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
      return false;
    }
  }

  private async Task<(RegisteredClient? Client, TimeSpan Lifetime)> FetchAsync(string clientId, CancellationToken cancellationToken)
  {
    var failure = (Client: (RegisteredClient?)null, Lifetime: _options.FailureCacheLifetime);
    if (!IsValidClientIdUrl(clientId, out var uri) || (_options.AcceptClientId is { } accept && !accept(uri)))
    {
      return failure;
    }

    if (!await EnterAsync(cancellationToken).ConfigureAwait(false))
    {
      return (null, TimeSpan.Zero);
    }

    GuardedFetchResult result;
    try
    {
      result = await _fetcher.GetAsync(uri, cancellationToken).ConfigureAwait(false);
    }
    finally
    {
      _slots.Release();
    }

    var ct = result.ContentType;
    if (!result.Succeeded || ct is null || !(ct == "application/json" || ct.EndsWith("+json", StringComparison.Ordinal)))
    {
      return failure;
    }

    var client = Parse(clientId, result.Body, _time.GetUtcNow());
    return client is null ? failure : (client, Clamp(result.CacheLifetime));
  }

  /// <summary>
  /// Draft CIMD section 3: HTTPS, a path, no fragment, no credentials, no single-dot or double-dot segments. The segments are checked on the
  /// text because <see cref="Uri"/> removes them while parsing.
  /// </summary>
  internal static bool IsValidClientIdUrl(string value, out Uri uri)
  {
    uri = null!;
    if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps
      || parsed.UserInfo.Length != 0 || value.Contains('#', StringComparison.Ordinal) || parsed.AbsolutePath.Length == 0 || parsed.Host.Length == 0)
    {
      return false;
    }

    var afterAuthority = value.IndexOf('/', "https://".Length);
    if (afterAuthority < 0)
    {
      return false;
    }

    var path = value[afterAuthority..];
    var query = path.IndexOf('?', StringComparison.Ordinal);
    if (query >= 0)
    {
      path = path[..query];
    }

    foreach (var segment in path.Split('/'))
    {
      var decoded = segment.Replace("%2e", ".", StringComparison.OrdinalIgnoreCase);
      if (decoded is "." or "..")
      {
        return false;
      }
    }

    uri = parsed;
    return true;
  }

  internal static RegisteredClient? Parse(string clientId, byte[] body, DateTimeOffset now)
  {
    try
    {
      using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
      var root = doc.RootElement;
      if (root.ValueKind != JsonValueKind.Object)
      {
        return null;
      }

      // Section 4.1: client_id must equal the URL by simple string comparison; secrets cannot exist for these clients.
      if (!root.TryGetProperty("client_id", out var id) || id.ValueKind != JsonValueKind.String || !string.Equals(id.GetString(), clientId, StringComparison.Ordinal))
      {
        return null;
      }

      if (root.TryGetProperty("client_secret", out _) || root.TryGetProperty("client_secret_expires_at", out _))
      {
        return null;
      }

      var method = "none";
      if (root.TryGetProperty("token_endpoint_auth_method", out var m))
      {
        method = m.ValueKind == JsonValueKind.String ? m.GetString()! : string.Empty;
        if (method is not ("none" or "private_key_jwt"))
        {
          return null;
        }
      }

      var redirects = StringArray(root, "redirect_uris", 10);
      if (redirects is not { Count: > 0 } || !redirects.All(RedirectUriRules.IsValidRegistration))
      {
        return null;
      }

      var grants = StringArray(root, "grant_types", 8) ?? ["authorization_code", "refresh_token"];
      if (grants.Count == 0 || grants.Any(g => !s_allowedGrants.Contains(g, StringComparer.Ordinal)))
      {
        return null;
      }

      if (StringArray(root, "response_types", 4) is { } types && (types.Count == 0 || types.Any(t => t != "code")))
      {
        return null;
      }

      string? jwks = null;
      string? jwksUri = null;
      if (method == "private_key_jwt")
      {
        if (root.TryGetProperty("jwks", out var keys) && keys.ValueKind == JsonValueKind.Object)
        {
          jwks = keys.GetRawText();
        }
        else if (root.TryGetProperty("jwks_uri", out var uri) && uri.ValueKind == JsonValueKind.String && uri.GetString() is { Length: <= 2048 } text && IsValidClientIdUrl(text, out _))
        {
          jwksUri = text;
        }
        else
        {
          return null;
        }
      }

      var name = root.TryGetProperty("client_name", out var n) && n.ValueKind == JsonValueKind.String ? Sanitize(n.GetString()!) : null;
      return new RegisteredClient
      {
        ClientId = clientId,
        Name = name,
        RedirectUris = redirects,
        TokenEndpointAuthMethod = method,
        JwksJson = jwks,
        JwksUri = jwksUri,
        GrantTypes = grants,
        CreatedAt = now,
        IsMetadataDocument = true,
      };
    }
    catch (JsonException)
    {
      return null;
    }
  }

  // A name is shown to a user, so it is trimmed and stripped of control characters and line breaks.
  private static string? Sanitize(string name)
  {
    var sb = new StringBuilder(Math.Min(name.Length, 100));
    foreach (var c in name)
    {
      if (!char.IsControl(c) && sb.Length < 100)
      {
        sb.Append(c);
      }
    }

    var text = sb.ToString().Trim();
    return text.Length == 0 ? null : text;
  }

  private static List<string>? StringArray(JsonElement root, string name, int max)
  {
    if (!root.TryGetProperty(name, out var array))
    {
      return null;
    }

    if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > max)
    {
      return [];
    }

    var list = new List<string>();
    foreach (var item in array.EnumerateArray())
    {
      if (item.ValueKind != JsonValueKind.String)
      {
        return [];
      }

      list.Add(item.GetString()!);
    }

    return list;
  }

  public void Dispose()
  {
    _fetcher.Dispose();
    _keyFetcher.Dispose();
    _slots.Dispose();
  }
}
