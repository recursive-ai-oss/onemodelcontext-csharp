using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.Http;

/// <summary>What a guarded fetch returned. A failure never carries a body.</summary>
public sealed class GuardedFetchResult
{
  private GuardedFetchResult()
  {
  }

  /// <summary>Whether the server answered 200 with a body inside the limits.</summary>
  public bool Succeeded { get; private init; }

  /// <summary>The body, or empty after a failure.</summary>
  public byte[] Body { get; private init; } = [];

  /// <summary>The media type of the response without parameters (lower case), or null.</summary>
  public string? ContentType { get; private init; }

  /// <summary>
  /// How long the response may be cached from <c>Cache-Control</c> (<c>max-age</c>, RFC 9111 section 5.2.2.1): zero for
  /// <c>no-store</c>, <c>no-cache</c> or <c>max-age=0</c>; null when the response gave no lifetime.
  /// </summary>
  public TimeSpan? CacheLifetime { get; private init; }

  /// <summary>Why the fetch failed, in words safe to log (never the body); null on success.</summary>
  public string? Failure { get; private init; }

  internal static GuardedFetchResult Ok(byte[] body, string? contentType, TimeSpan? lifetime) =>
    new() { Succeeded = true, Body = body, ContentType = contentType, CacheLifetime = lifetime };

  internal static GuardedFetchResult Fail(string reason) => new() { Failure = reason };
}

/// <summary>Limits and the network policy for <see cref="GuardedHttpFetcher"/>.</summary>
public sealed class GuardedFetchOptions
{
  /// <summary>Which resolved addresses may be connected to. Default: public addresses only.</summary>
  public AddressPolicy Addresses { get; init; } = new();

  /// <summary>Largest response body accepted, in bytes. Default 16 KiB; a longer body is a failure. Must be positive.</summary>
  public int MaxBytes { get; init; } = 16 * 1024;

  /// <summary>Time allowed for the whole fetch, connection to last byte. Default 5 seconds. Must be positive.</summary>
  public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

  /// <summary>
  /// Whether <c>http</c> URLs are fetched when every address the host resolves to was allow-listed in <see cref="Addresses"/>. Default
  /// false: only <c>https</c>. It exists so that a loopback fixture can be fetched without a certificate.
  /// </summary>
  public bool AllowHttpToAllowListedAddresses { get; init; }

  /// <summary>
  /// Replaces name resolution, for tests and for hosts with their own resolver. Receives the host name and returns its addresses.
  /// Default: the system resolver. IP literals are never resolved.
  /// </summary>
  public Func<string, CancellationToken, ValueTask<IPAddress[]>>? Resolver { get; init; }

  /// <summary>Lets tests trust a throwaway certificate on a loopback fixture. Not public: production fetches always use the platform's trust store.</summary>
  internal System.Net.Security.RemoteCertificateValidationCallback? CertificateValidation { get; init; }
}

/// <summary>
/// Fetches a URL on behalf of a remote party without letting that party aim the request at the server's own network (SSRF). The host
/// name is resolved once, in the connect step, every address is checked against the <see cref="AddressPolicy"/>, and the socket is
/// opened to one of the checked addresses, so a name that resolves differently a second time cannot redirect the connection. Redirects
/// are never followed (a 3xx is a failure), proxies are not used, no cookies or credentials are sent, the body is capped and the whole
/// fetch is time-limited.
/// </summary>
public sealed class GuardedHttpFetcher : IDisposable
{
  private readonly GuardedFetchOptions _options;
  private readonly TimeProvider _time;
  private readonly HttpClient _client;

  /// <summary>Creates a fetcher.</summary>
  /// <param name="options">Limits and policy; null for the defaults.</param>
  /// <param name="timeProvider">The clock that drives the timeout; null for the system clock.</param>
  /// <exception cref="ArgumentOutOfRangeException">A limit is not positive.</exception>
  public GuardedHttpFetcher(GuardedFetchOptions? options = null, TimeProvider? timeProvider = null)
  {
    _options = options ?? new GuardedFetchOptions();
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxBytes);
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.Timeout, TimeSpan.Zero);
    _time = timeProvider ?? TimeProvider.System;
    var handler = new SocketsHttpHandler
    {
      AllowAutoRedirect = false,
      UseProxy = false,
      UseCookies = false,
      AutomaticDecompression = DecompressionMethods.None,
      ConnectTimeout = _options.Timeout,
      PooledConnectionLifetime = TimeSpan.FromMinutes(1),
      ConnectCallback = ConnectAsync,
      MaxResponseHeadersLength = 16,
    };
    if (_options.CertificateValidation is { } validation)
    {
      handler.SslOptions.RemoteCertificateValidationCallback = validation;
    }

    _client = new HttpClient(handler, disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
  }

  /// <summary>Whether a URL is one this fetcher would try: absolute, <c>https</c> (or <c>http</c> when allowed), without credentials or fragment.</summary>
  /// <param name="uri">The URL.</param>
  public bool IsAcceptableUrl(Uri uri)
  {
    ArgumentNullException.ThrowIfNull(uri);
    return uri.IsAbsoluteUri
      && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && _options.AllowHttpToAllowListedAddresses))
      && uri.UserInfo.Length == 0
      && uri.Fragment.Length == 0
      && uri.Host.Length > 0;
  }

  /// <summary>Fetches a URL with GET. Failures are results, not exceptions; only cancellation by the caller throws.</summary>
  /// <param name="uri">The URL.</param>
  /// <param name="cancellationToken">Cancels the fetch.</param>
  /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
  public async Task<GuardedFetchResult> GetAsync(Uri uri, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(uri);
    if (!IsAcceptableUrl(uri))
    {
      return GuardedFetchResult.Fail("The URL is not an https URL without credentials or fragment.");
    }

    using var deadline = new CancellationTokenSource(_options.Timeout, _time);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
    try
    {
      using var request = new HttpRequestMessage(HttpMethod.Get, uri);
      request.Headers.Accept.ParseAdd("application/json");
      using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);

      // Draft CIMD section 4: only 200 counts, and a redirect is never followed.
      if (response.StatusCode != HttpStatusCode.OK)
      {
        return GuardedFetchResult.Fail($"The server answered {(int)response.StatusCode}.");
      }

      if (response.Content.Headers.ContentLength is { } declared && declared > _options.MaxBytes)
      {
        return GuardedFetchResult.Fail("The response is larger than the limit.");
      }

      await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
      var body = await ReadBoundedAsync(stream, _options.MaxBytes, linked.Token).ConfigureAwait(false);
      if (body is null)
      {
        return GuardedFetchResult.Fail("The response is larger than the limit.");
      }

      var media = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
      return GuardedFetchResult.Ok(body, media, ParseLifetime(response));
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
      return GuardedFetchResult.Fail("The fetch took too long.");
    }
    catch (HttpRequestException ex)
    {
      // The inner message names the refused address class, never a body.
      return GuardedFetchResult.Fail(ex.InnerException is BlockedAddressException blocked ? blocked.Message : "The server could not be reached.");
    }
    catch (IOException)
    {
      return GuardedFetchResult.Fail("The connection failed.");
    }
  }

  /// <inheritdoc />
  public void Dispose() => _client.Dispose();

  private static async Task<byte[]?> ReadBoundedAsync(Stream stream, int max, CancellationToken cancellationToken)
  {
    var buffer = new byte[Math.Min(max, 4096) + 1];
    using var ms = new MemoryStream();
    int n;
    while ((n = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
    {
      if (ms.Length + n > max)
      {
        return null;
      }

      ms.Write(buffer, 0, n);
    }

    return ms.ToArray();
  }

  private static TimeSpan? ParseLifetime(HttpResponseMessage response)
  {
    var cc = response.Headers.CacheControl;
    if (cc is null)
    {
      return null;
    }

    if (cc.NoStore || cc.NoCache)
    {
      return TimeSpan.Zero;
    }

    return cc.SharedMaxAge ?? cc.MaxAge;
  }

  private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
  {
    var host = context.DnsEndPoint.Host.TrimEnd('.');
    var port = context.DnsEndPoint.Port;
    IPAddress[] addresses;
    if (IPAddress.TryParse(host, out var literal))
    {
      addresses = [literal];
    }
    else
    {
      try
      {
        addresses = _options.Resolver is { } resolver
          ? await resolver(host, cancellationToken).ConfigureAwait(false)
          : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
      }
      catch (SocketException)
      {
        throw new BlockedAddressException("The host name did not resolve.");
      }
    }

    if (addresses.Length == 0)
    {
      throw new BlockedAddressException("The host name did not resolve.");
    }

    // One refused address refuses the host: a name that also resolves to a private address is how rebinding attacks are staged.
    var allAllowListed = true;
    foreach (var address in addresses)
    {
      if (!_options.Addresses.IsAllowed(address))
      {
        throw new BlockedAddressException("The host resolves to an address that is not public.");
      }

      allAllowListed &= _options.Addresses.IsAllowListed(address);
    }

    if (context.InitialRequestMessage.RequestUri?.Scheme == Uri.UriSchemeHttp && !(_options.AllowHttpToAllowListedAddresses && allAllowListed))
    {
      throw new BlockedAddressException("Plain http is not allowed for this host.");
    }

    foreach (var address in addresses)
    {
      var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
      try
      {
        await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
      }
      catch (SocketException)
      {
        socket.Dispose();
      }
    }

    throw new BlockedAddressException("The server could not be reached.");
  }

  /// <summary>Raised inside the connect step so that the caller can tell a policy refusal from a network failure.</summary>
  private sealed class BlockedAddressException(string message) : IOException(message)
  {
  }
}
