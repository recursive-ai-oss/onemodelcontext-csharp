using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.Jose;

/// <summary>Supplies the verification keys of a token issuer.</summary>
public interface IJwksProvider
{
  /// <summary>Gets the current key set.</summary>
  /// <param name="forceRefresh">True when a token named a <c>kid</c> the cached set lacks: the issuer may have rotated keys. A provider may still decline to refetch too often.</param>
  /// <param name="cancellationToken">Cancels the wait.</param>
  /// <returns>The set, or null when none is available (the issuer is unreachable and nothing is cached).</returns>
  ValueTask<JsonWebKeySet?> GetKeysAsync(bool forceRefresh, CancellationToken cancellationToken);
}

/// <summary>A fixed key set, for keys the host holds itself.</summary>
/// <param name="keys">The keys.</param>
public sealed class StaticJwksProvider(JsonWebKeySet keys) : IJwksProvider
{
  /// <inheritdoc />
  public ValueTask<JsonWebKeySet?> GetKeysAsync(bool forceRefresh, CancellationToken cancellationToken) => ValueTask.FromResult<JsonWebKeySet?>(keys);
}

/// <summary>
/// Fetches a JWKS document (RFC 7517) and caches it. The document is read over HTTPS (plain HTTP only for a loopback host), at most
/// <see cref="MaxBytes"/> long, within <see cref="Timeout"/>, and redirects are never followed. A key id the cache does not know triggers
/// one refetch, but never more often than <see cref="MinRefreshInterval"/>, so a stream of forged <c>kid</c> values cannot turn this
/// server into a request amplifier against the issuer.
/// </summary>
public sealed class HttpJwksProvider : IJwksProvider, IDisposable
{
  private readonly Uri _uri;
  private readonly HttpClient _http;
  private readonly bool _ownsClient;
  private readonly TimeProvider _time;
  private readonly SemaphoreSlim _gate = new(1, 1);
  private JsonWebKeySet? _keys;
  private long _fetchedAt;
  private long _attemptedAt;
  private bool _hasAttempt;

  /// <summary>Creates a provider.</summary>
  /// <param name="jwksUri">The <c>jwks_uri</c>. HTTPS, or HTTP on a loopback host.</param>
  /// <param name="httpClient">The client to fetch with, or null for a private one that does not follow redirects. A client you supply is not disposed; a 3xx response is treated as a failure either way.</param>
  /// <param name="timeProvider">The clock for cache lifetimes; null for the system clock.</param>
  /// <exception cref="ArgumentException">The URI is not absolute HTTPS (or loopback HTTP).</exception>
  public HttpJwksProvider(Uri jwksUri, HttpClient? httpClient = null, TimeProvider? timeProvider = null)
  {
    ArgumentNullException.ThrowIfNull(jwksUri);
    if (!jwksUri.IsAbsoluteUri || !(jwksUri.Scheme == Uri.UriSchemeHttps || (jwksUri.Scheme == Uri.UriSchemeHttp && jwksUri.IsLoopback)) || jwksUri.UserInfo.Length != 0)
    {
      throw new ArgumentException("The JWKS URI must be an absolute https URI (http only on a loopback host) without credentials.", nameof(jwksUri));
    }

    _uri = jwksUri;
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

  /// <summary>How long a fetched set is used before it is fetched again. Default 10 minutes.</summary>
  public TimeSpan CacheLifetime { get; init; } = TimeSpan.FromMinutes(10);

  /// <summary>The least time between two fetches, however many unknown key ids arrive. Default 30 seconds.</summary>
  public TimeSpan MinRefreshInterval { get; init; } = TimeSpan.FromSeconds(30);

  /// <summary>Largest document accepted, in bytes. Default 256 KiB.</summary>
  public int MaxBytes { get; init; } = 256 * 1024;

  /// <summary>How long one fetch may take. Default 10 seconds.</summary>
  public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

  /// <inheritdoc />
  public async ValueTask<JsonWebKeySet?> GetKeysAsync(bool forceRefresh, CancellationToken cancellationToken)
  {
    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      var now = _time.GetTimestamp();
      var fresh = _keys is not null && _time.GetElapsedTime(_fetchedAt, now) < CacheLifetime;
      if (fresh && !forceRefresh)
      {
        return _keys;
      }

      // Rate limit every fetch, failed ones too, so an unreachable issuer is not hammered either.
      if (_hasAttempt && _time.GetElapsedTime(_attemptedAt, now) < MinRefreshInterval)
      {
        return _keys;
      }

      _hasAttempt = true;
      _attemptedAt = now;
      var fetched = await FetchAsync(cancellationToken).ConfigureAwait(false);
      if (fetched is not null)
      {
        _keys = fetched;
        _fetchedAt = _time.GetTimestamp();
      }

      // On failure the previous set keeps serving: a brief outage at the issuer must not lock every caller out.
      return _keys;
    }
    finally
    {
      _gate.Release();
    }
  }

  /// <summary>Releases the private HTTP client, if this provider created one.</summary>
  public void Dispose()
  {
    if (_ownsClient)
    {
      _http.Dispose();
    }

    _gate.Dispose();
  }

  private async Task<JsonWebKeySet?> FetchAsync(CancellationToken cancellationToken)
  {
    using var timeout = new CancellationTokenSource(Timeout, _time);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
    try
    {
      using var request = new HttpRequestMessage(HttpMethod.Get, _uri);
      request.Headers.TryAddWithoutValidation("Accept", "application/json, application/jwk-set+json");
      using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
      if (response.StatusCode != System.Net.HttpStatusCode.OK)
      {
        return null;
      }

      if (response.Content.Headers.ContentLength > MaxBytes)
      {
        return null;
      }

      await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
      using var buffer = new MemoryStream();
      var chunk = new byte[8192];
      int read;
      while ((read = await stream.ReadAsync(chunk, linked.Token).ConfigureAwait(false)) > 0)
      {
        if (buffer.Length + read > MaxBytes)
        {
          return null;
        }

        buffer.Write(chunk, 0, read);
      }

      return JsonWebKeySet.Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }
    catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
    {
      // The caller's own cancellation is not an issuer failure.
      cancellationToken.ThrowIfCancellationRequested();
      return null;
    }
  }
}
