using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
  private const int MaxRequestBytes = 8 * 1024;
  private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(10);

  private readonly TcpListener _listener;
  private readonly Func<Uri, CancellationToken, ValueTask> _openBrowser;
  private readonly string _path;
  private readonly TimeProvider _time;

  /// <summary>Starts listening.</summary>
  /// <param name="openBrowser">Opens the authorization URL for the user, for example by starting the system browser. It must return once the browser is launched, not when the user is done. The URL holds no secret.</param>
  /// <param name="port">The port; 0 (the default) lets the system choose.</param>
  /// <param name="path">The callback path. Default <c>/callback</c>.</param>
  /// <param name="timeProvider">The clock for <see cref="Timeout"/>; null for the system clock.</param>
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
    _listener = new TcpListener(IPAddress.Loopback, port);
    _listener.Start();
    RedirectUri = new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + path);
  }

  /// <inheritdoc />
  public Uri RedirectUri { get; }

  /// <summary>How long to wait for the user to finish. Default 5 minutes.</summary>
  public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

  /// <inheritdoc />
  public async ValueTask<Uri> AuthorizeAsync(Uri authorizationUrl, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(authorizationUrl);
    using var timeout = new CancellationTokenSource(Timeout, _time);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
    try
    {
      await _openBrowser(authorizationUrl, linked.Token).ConfigureAwait(false);
      while (true)
      {
        using var connection = await _listener.AcceptTcpClientAsync(linked.Token).ConfigureAwait(false);
        var target = await ReadCallbackAsync(connection, linked.Token).ConfigureAwait(false);
        if (target is not null)
        {
          return new Uri(RedirectUri, target);
        }
      }
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
      throw new OAuthException("Authorization was not completed in time.", "timeout");
    }
  }

  /// <summary>Stops listening.</summary>
  public void Dispose() => _listener.Stop();

  // Returns the request target when the connection is a callback, null for anything else (which gets a 404 and is ignored).
  private async Task<string?> ReadCallbackAsync(TcpClient connection, CancellationToken cancellationToken)
  {
    using var limit = new CancellationTokenSource(RequestReadTimeout, _time);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
    var stream = connection.GetStream();
    var buffer = new byte[MaxRequestBytes];
    var used = 0;
    string? line = null;
    try
    {
      while (used < buffer.Length)
      {
        var n = await stream.ReadAsync(buffer.AsMemory(used), linked.Token).ConfigureAwait(false);
        if (n == 0)
        {
          break;
        }

        used += n;
        var end = Array.IndexOf(buffer, (byte)'\n', 0, used);
        if (end >= 0)
        {
          line = Encoding.ASCII.GetString(buffer, 0, end).TrimEnd('\r');
          break;
        }
      }
    }
    catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
    {
      return null;
    }

    string? target = null;
    var parts = line?.Split(' ');
    if (parts is { Length: 3 } && parts[0] == "GET" && parts[1].StartsWith(_path, StringComparison.Ordinal)
      && (parts[1].Length == _path.Length || parts[1][_path.Length] == '?'))
    {
      target = parts[1];
    }

    var body = target is null ? "Not found." : "You can close this window and return to the application.";
    var response = Encoding.ASCII.GetBytes(
      (target is null ? "HTTP/1.1 404 Not Found\r\n" : "HTTP/1.1 200 OK\r\n")
      + "Content-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\nContent-Length: "
      + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n\r\n" + body);
    try
    {
      await stream.WriteAsync(response, linked.Token).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
    {
      // The browser went away after sending the redirect; the redirect is still good.
    }

    return target;
  }
}
