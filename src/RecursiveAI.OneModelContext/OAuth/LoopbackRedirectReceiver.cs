using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Http;

namespace RecursiveAI.OneModelContext.OAuth;

/// <summary>
/// The redirect receiver for desktop and command-line programs: listens on the loopback interface on a port the operating system picks,
/// asks the host to open the browser, and returns the redirect the authorization server sends back (RFC 8252 section 7.3). It binds
/// <c>127.0.0.1</c> only, answers only <c>GET</c> on its callback path, reads at most 8 KiB of a request within 10 seconds, and shuts
/// down with the object. Register <see cref="RedirectUri"/> with the authorization server (or list it in the Client ID Metadata
/// Document).
/// </summary>
public sealed class LoopbackRedirectReceiver : IAuthorizationUserAgent, IDisposable
{
  private readonly HttpServer _server;
  private readonly Channel<string> _callbacks = Channel.CreateBounded<string>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropWrite });
  private readonly Func<Uri, CancellationToken, ValueTask> _openBrowser;
  private readonly string _path;
  private readonly TimeProvider _time;

  /// <summary>Starts listening.</summary>
  /// <param name="openBrowser">Opens the authorization URL for the user, for example by starting the system browser. It must return once the browser is launched, not when the user is done. The URL holds no secret.</param>
  /// <param name="port">The port; 0 (the default) lets the system choose.</param>
  /// <param name="path">The callback path. Default <c>/callback</c>.</param>
  /// <param name="timeProvider">The clock for <see cref="Timeout"/>; null for the system clock.</param>
  /// <exception cref="System.Net.Sockets.SocketException">The port is in use.</exception>
  public LoopbackRedirectReceiver(Func<Uri, CancellationToken, ValueTask> openBrowser, int port = 0, string path = "/callback", TimeProvider? timeProvider = null)
  {
    ArgumentNullException.ThrowIfNull(openBrowser);
    ArgumentException.ThrowIfNullOrEmpty(path);
    if (path[0] != '/')
    {
      throw new ArgumentException("The path must start with '/'.", nameof(path));
    }

    _openBrowser = openBrowser;
    _path = path;
    _time = timeProvider ?? TimeProvider.System;
    _server = new HttpServer(
      HandleAsync,
      new HttpServerOptions
      {
        Address = IPAddress.Loopback,
        Port = port,
        MaxRequestLineBytes = 8 * 1024,
        MaxHeaderBytes = 8 * 1024,
        MaxRequestBodyBytes = 0,
        MaxConnections = 8,
        HeaderReadTimeout = TimeSpan.FromSeconds(10),
        ShutdownGracePeriod = TimeSpan.FromSeconds(1),
      },
      _time);
    _server.Start();
    RedirectUri = new Uri("http://127.0.0.1:" + _server.LocalEndPoint!.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + path);
  }

  /// <inheritdoc />
  public Uri RedirectUri { get; }

  /// <summary>How long to wait for the user to finish. Default 5 minutes.</summary>
  public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

  /// <inheritdoc />
  public async ValueTask<Uri> AuthorizeAsync(Uri authorizationUrl, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(authorizationUrl);
    while (_callbacks.Reader.TryRead(out _))
    {
      // A redirect left over from an earlier attempt belongs to a different state value.
    }

    using var timeout = new CancellationTokenSource(Timeout, _time);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
    try
    {
      await _openBrowser(authorizationUrl, linked.Token).ConfigureAwait(false);
      var query = await _callbacks.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
      return new Uri(RedirectUri, query.Length == 0 ? "" : "?" + query);
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
      throw new OAuthException("Authorization was not completed in time.", "timeout");
    }
  }

  /// <summary>Stops listening.</summary>
  public void Dispose() => _server.DisposeAsync().AsTask().GetAwaiter().GetResult();

  // Anything but GET on the callback path with a loopback Host (which also refuses DNS-rebinding names) gets a 404 and is ignored.
  private async ValueTask HandleAsync(IHttpExchange exchange)
  {
    var request = exchange.Request;
    var host = request.Headers.GetFirst("Host");
    var port = RedirectUri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    var ours = request.Method == "GET" && request.Path == _path
      && (string.Equals(host, "127.0.0.1:" + port, StringComparison.Ordinal) || string.Equals(host, "localhost:" + port, StringComparison.OrdinalIgnoreCase));
    if (ours)
    {
      _callbacks.Writer.TryWrite(request.Query);
    }

    var body = ours ? "You can close this window and return to the application."u8.ToArray() : "Not found."u8.ToArray();
    var response = exchange.Response;
    response.StatusCode = ours ? 200 : 404;
    response.SetHeader("Content-Type", "text/plain; charset=utf-8");
    response.SetHeader("Cache-Control", "no-store");
    response.SetHeader("Referrer-Policy", "no-referrer");
    await response.Body.WriteAsync(body, exchange.Aborted).ConfigureAwait(false);
  }
}
