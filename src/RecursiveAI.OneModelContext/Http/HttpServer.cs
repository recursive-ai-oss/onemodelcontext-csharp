using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Diagnostics;

namespace RecursiveAI.OneModelContext.Http;

/// <summary>
/// A small HTTP/1.1 server on <see cref="Socket"/> for local tools and command-line programs, with optional TLS through
/// <see cref="System.Net.Security.SslStream"/>. It hands each request to a handler as an <see cref="IHttpExchange"/>, so the same handler
/// runs on this server and on a web framework. For a server on the internet, host the handler in a web framework instead.
/// </summary>
/// <remarks>
/// <para>The server is a security boundary even on loopback, so it is strict by default: it listens on the loopback address; the request
/// line, the header block, the number of headers and the body are bounded; every wait (first byte, head, body, write) has a timeout; the
/// number of connections is bounded; and a request whose framing is ambiguous (<c>Content-Length</c> with <c>Transfer-Encoding</c>, a
/// repeated <c>Content-Length</c>, a bare line feed, obsolete line folding, a malformed chunk size) is refused and the connection closed
/// without reading anything further. <c>X-Forwarded-*</c> headers are believed only from <see cref="HttpServerOptions.TrustedProxies"/>
/// and removed from everyone else's requests. It does not check <c>Origin</c> or <c>Host</c>: the handler does (the Streamable HTTP
/// endpoint does).</para>
/// <para>Requests on one connection are served one at a time, in order. A handler runs until it returns; its
/// <see cref="IHttpExchange.Aborted"/> token is cancelled when the peer closes the connection (including closing only its sending side) or
/// the server stops. Request bodies and response bodies are read and written asynchronously only.</para>
/// </remarks>
public sealed class HttpServer : IAsyncDisposable
{
  private readonly ConcurrentDictionary<long, (Socket Socket, Task Task)> _connections = new();
  private readonly CancellationTokenSource _shutdown = new();
  private readonly SemaphoreSlim _slots;
  private Socket? _listener;
  private Task? _acceptLoop;
  private long _nextId;

  /// <summary>Creates a server. Nothing listens until <see cref="Start"/>.</summary>
  /// <param name="handler">Called once per request. It must finish the response before it returns. An exception it throws becomes a 500 when nothing has been sent yet.</param>
  /// <param name="options">Address, limits and timeouts; null for the defaults.</param>
  /// <param name="timeProvider">The clock behind every timeout; null for the system clock.</param>
  /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
  /// <exception cref="ArgumentException">A limit in <paramref name="options"/> is out of range.</exception>
  public HttpServer(Func<IHttpExchange, ValueTask> handler, HttpServerOptions? options = null, TimeProvider? timeProvider = null)
  {
    ArgumentNullException.ThrowIfNull(handler);
    Handler = handler;
    Shutdown = _shutdown.Token;
    Options = options ?? new HttpServerOptions();
    Time = timeProvider ?? TimeProvider.System;
    if (Options.MaxConnections < 1 || Options.MaxHeaderBytes < 1024 || Options.MaxRequestLineBytes < 16 || Options.MaxHeaderCount < 1 || Options.MaxRequestBodyBytes < 0 || Options.Port is < 0 or > 65535)
    {
      throw new ArgumentException("A limit is out of range.", nameof(options));
    }

    _slots = new SemaphoreSlim(Options.MaxConnections);
  }

  /// <summary>The endpoint being listened on; the port is the real one when <see cref="HttpServerOptions.Port"/> was 0. Null before <see cref="Start"/>.</summary>
  public IPEndPoint? LocalEndPoint => _listener?.LocalEndPoint as IPEndPoint;

  /// <summary>The base address clients use, such as <c>http://127.0.0.1:5000/</c>. Null before <see cref="Start"/>.</summary>
  public Uri? BaseAddress
  {
    get
    {
      if (LocalEndPoint is not { } endpoint)
      {
        return null;
      }

      var host = endpoint.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + endpoint.Address + "]" : endpoint.Address.ToString();
      return new Uri((Options.Certificate is null ? "http://" : "https://") + host + ":" + endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/");
    }
  }

  internal Func<IHttpExchange, ValueTask> Handler { get; }

  internal HttpServerOptions Options { get; }

  internal TimeProvider Time { get; }

  internal CancellationToken Shutdown { get; }

  /// <summary>Binds the address and starts accepting connections.</summary>
  /// <exception cref="InvalidOperationException">The server was started already, or disposed.</exception>
  /// <exception cref="SocketException">The address cannot be bound, for example because the port is in use.</exception>
  public void Start()
  {
    ObjectDisposedException.ThrowIf(_shutdown.IsCancellationRequested, this);
    if (_listener is not null)
    {
      throw new InvalidOperationException("The server was started already.");
    }

    if (ShouldWarn(Options))
    {
      ContextDiagnostics.Publish(
        DiagnosticKind.Warning,
        "The HTTP server listens on " + Options.Address + ", which is not a loopback address, without TLS or a trusted proxy. Anyone who can reach the port can use the server.");
    }

    var listener = new Socket(Options.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
    try
    {
      listener.Bind(new IPEndPoint(Options.Address, Options.Port));
      listener.Listen(128);
    }
    catch
    {
      listener.Dispose();
      throw;
    }

    _listener = listener;
    _acceptLoop = Task.Run(AcceptLoopAsync);
  }

  /// <summary>Whether starting with these options deserves the non-loopback warning.</summary>
  internal static bool ShouldWarn(HttpServerOptions options) =>
    !IPAddress.IsLoopback(options.Address) && options.Certificate is null && options.TrustedProxies.Count == 0;

  internal bool IsTrustedProxy(IPAddress? address)
  {
    if (address is null)
    {
      return false;
    }

    if (address.IsIPv4MappedToIPv6)
    {
      address = address.MapToIPv4();
    }

    foreach (var proxy in Options.TrustedProxies)
    {
      if ((proxy.IsIPv4MappedToIPv6 ? proxy.MapToIPv4() : proxy).Equals(address))
      {
        return true;
      }
    }

    return false;
  }

  private async Task AcceptLoopAsync()
  {
    var listener = _listener!;
    var token = _shutdown.Token;
    while (!token.IsCancellationRequested)
    {
      try
      {
        // At the limit, wait here: new connections queue in the operating system instead of being served.
        await _slots.WaitAsync(token).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        return;
      }

      Socket socket;
      try
      {
        socket = await listener.AcceptAsync(token).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        return;
      }
      catch (ObjectDisposedException)
      {
        return;
      }
      catch (SocketException)
      {
        // A connection that died before it was accepted, or no descriptors left: back off and carry on.
        _slots.Release();
        try
        {
          await Task.Delay(TimeSpan.FromMilliseconds(50), Time, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
          return;
        }

        continue;
      }

      socket.NoDelay = true;
      var id = Interlocked.Increment(ref _nextId);
      var connection = new HttpConnection(this, socket);
      var task = Task.Run(async () =>
      {
        try
        {
          await connection.RunAsync().ConfigureAwait(false);
        }
        finally
        {
          _connections.TryRemove(id, out _);
          _slots.Release();
        }
      });
      _connections[id] = (socket, task);
    }
  }

  /// <summary>
  /// Stops accepting, cancels the running requests, closes every connection and waits up to
  /// <see cref="HttpServerOptions.ShutdownGracePeriod"/> for handlers to finish.
  /// </summary>
  public async ValueTask DisposeAsync()
  {
    if (_shutdown.IsCancellationRequested)
    {
      return;
    }

    await _shutdown.CancelAsync().ConfigureAwait(false);
    _listener?.Dispose();
    foreach (var (socket, _) in _connections.Values)
    {
      try
      {
        socket.Dispose();
      }
      catch (ObjectDisposedException)
      {
        // Closed already.
      }
    }

    var pending = new System.Collections.Generic.List<Task>();
    if (_acceptLoop is { } loop)
    {
      pending.Add(loop);
    }

    foreach (var (_, task) in _connections.Values)
    {
      pending.Add(task);
    }

    try
    {
      await Task.WhenAll(pending).WaitAsync(Options.ShutdownGracePeriod, Time).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
    {
      // Handlers that ignore cancellation are left behind; their sockets are closed.
    }

  }
}
