using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Diagnostics;

namespace RecursiveAI.OneModelContext.Http;

/// <summary>One accepted connection: reads requests one at a time, runs the handler, writes the response, repeats while keep-alive holds.</summary>
internal sealed class HttpConnection : IDisposable, IRequestBodyOwner
{
  private const int DrainLimit = 64 * 1024;
  private const int LingerLimit = 64 * 1024;
  private static readonly TimeSpan LingerTimeout = TimeSpan.FromSeconds(2);
  private static readonly byte[] ContinueResponse = "HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray();

  private readonly Socket _socket;
  private readonly IPAddress? _peer;
  private readonly bool _tls;
  private Stream _stream;
  private ConnectionReader? _reader;
  private (int Status, string Reason)? _error;
  private bool _continuePending;
  private bool _broken;

  internal HttpConnection(HttpServer server, Socket socket)
  {
    Server = server;
    _socket = socket;
    _stream = new NetworkStream(socket, ownsSocket: false);
    _peer = (socket.RemoteEndPoint as IPEndPoint)?.Address;
    _tls = server.Options.Certificate is not null;
  }

  internal HttpServer Server { get; }

  /// <summary>Serialises the writers of one response (a keep-alive comment and an event can race).</summary>
  internal SemaphoreSlim ResponseGate { get; } = new(1, 1);

  /// <summary>Bytes of the response body held back until the head is committed.</summary>
  internal ArrayBufferWriter<byte> BodyBuffer { get; } = new();

  /// <summary>Scratch space for a response head and the chunk that follows it.</summary>
  internal ArrayBufferWriter<byte> Output { get; } = new();

  public void RecordError(int status, string reason) => _error ??= (status, reason);

  public async ValueTask SendContinueAsync()
  {
    if (_continuePending)
    {
      _continuePending = false;
      await WriteAsync(ContinueResponse, Server.Shutdown).ConfigureAwait(false);
    }
  }

  /// <summary>Called when the request body has been read to its end: from here on the reader only watches for a hang-up.</summary>
  public void BodyCompleted() => StartWatching();

  private CancellationTokenSource? _abort;

  private void StartWatching()
  {
    if (_abort is not { } abort || _reader?.StartMonitor() is not { } monitor)
    {
      return;
    }

    _ = WatchAsync(monitor, abort);
  }

  private static async Task WatchAsync(Task<int> monitor, CancellationTokenSource abort)
  {
    var gone = false;
    try
    {
      gone = await monitor.ConfigureAwait(false) == 0;
    }
    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
    {
      gone = true;
    }

    if (gone)
    {
      try
      {
        await abort.CancelAsync().ConfigureAwait(false);
      }
      catch (ObjectDisposedException)
      {
        // The request is over already.
      }
    }
  }

  /// <summary>Writes to the peer under the write timeout. A failed or timed-out write ends the connection.</summary>
  internal async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
  {
    using var timeout = new CancellationTokenSource(Server.Options.WriteTimeout, Server.Time);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken, Server.Shutdown);
    try
    {
      await _stream.WriteAsync(data, linked.Token).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
    {
      Break();
      if (ex is OperationCanceledException && timeout.IsCancellationRequested)
      {
        throw new IOException("The peer did not read the response in time.", ex);
      }

      throw;
    }
  }

  private void Break()
  {
    _broken = true;
    try
    {
      _socket.Dispose();
    }
    catch (ObjectDisposedException)
    {
      // Already closed.
    }
  }

  internal async Task RunAsync()
  {
    try
    {
      if (Server.Options.Certificate is { } certificate)
      {
        var ssl = new SslStream(_stream, leaveInnerStreamOpen: true);
        _stream = ssl;
        using var handshake = new CancellationTokenSource(Server.Options.HeaderReadTimeout, Server.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(handshake.Token, Server.Shutdown);
        await ssl.AuthenticateAsServerAsync(
          new SslServerAuthenticationOptions
          {
            ServerCertificate = certificate,
            EnabledSslProtocols = Server.Options.SslProtocols,
            ClientCertificateRequired = false,
            ApplicationProtocols = [SslApplicationProtocol.Http11],
          },
          linked.Token).ConfigureAwait(false);
      }

      _reader = new ConnectionReader(_stream, Server.Options.MaxHeaderBytes);
      while (!_broken && !Server.Shutdown.IsCancellationRequested && await ServeOneAsync().ConfigureAwait(false))
      {
      }

      await LingerAsync().ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or AuthenticationException)
    {
      // The peer left, a handshake failed or the server is stopping: the connection simply ends.
    }
    catch (Exception ex)
    {
      ContextDiagnostics.Publish(DiagnosticKind.HandlerFault, "The HTTP connection failed with " + ex.GetType().Name + ".", null, ex);
    }
    finally
    {
      Dispose();
    }
  }

  // Closing a socket that still has unread bytes in its receive queue sends a reset, and a reset can destroy a response the peer has
  // not read yet (an error sent to a client that was still sending). So: finish our side, then let the peer finish theirs.
  private async ValueTask LingerAsync()
  {
    if (_broken || Server.Shutdown.IsCancellationRequested || _reader is null)
    {
      return;
    }

    using var timeout = new CancellationTokenSource(LingerTimeout, Server.Time);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, Server.Shutdown);
    try
    {
      if (_stream is SslStream ssl)
      {
        await ssl.ShutdownAsync().ConfigureAwait(false);
      }

      _socket.Shutdown(SocketShutdown.Send);
      await _reader.DiscardUntilEofAsync(LingerLimit, linked.Token).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or InvalidOperationException)
    {
      // The peer is gone or slow; close anyway.
    }
  }

  public void Dispose()
  {
    _reader?.Dispose();
    ResponseGate.Dispose();
    try
    {
      _stream.Dispose();
    }
    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
    {
      // Closing a dead connection.
    }

    _socket.Dispose();
  }

  private async ValueTask<bool> ServeOneAsync()
  {
    var options = Server.Options;
    var reader = _reader!;
    _error = null;
    _continuePending = false;

    RequestHead? head;
    using (var timer = new CancellationTokenSource(options.IdleTimeout, Server.Time))
    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, Server.Shutdown))
    {
      try
      {
        if (!await reader.WaitForDataAsync(linked.Token).ConfigureAwait(false))
        {
          return false;
        }
      }
      catch (OperationCanceledException)
      {
        // Idle too long, or shutting down: close without a word.
        return false;
      }

      // Slowloris: from the first byte on, the whole head must arrive within the header timeout (RFC 9112 section 9.5 leaves the
      // value to the server).
      timer.CancelAfter(options.HeaderReadTimeout);
      HeadError? error;
      try
      {
        (head, error) = await ReadHeadAsync(linked.Token).ConfigureAwait(false);
      }
      catch (OperationCanceledException) when (timer.IsCancellationRequested)
      {
        await SendErrorAsync(408, "The request head took too long to arrive.").ConfigureAwait(false);
        return false;
      }

      if (error is { } failure)
      {
        await SendErrorAsync(failure.Status, failure.Reason).ConfigureAwait(false);
        return false;
      }

      if (head is null)
      {
        return false;
      }
    }

    if (head.ContentLength > options.MaxRequestBodyBytes)
    {
      await SendErrorAsync(413, "The request body is too large.").ConfigureAwait(false);
      return false;
    }

    var (remote, scheme) = ApplyForwardedHeaders(head);
    using var deadline = new CancellationTokenSource(options.RequestTimeout, Server.Time);
    using var abort = CancellationTokenSource.CreateLinkedTokenSource(Server.Shutdown);
    _abort = abort;
    var hasBody = head.Chunked || head.ContentLength > 0;
    RequestBodyStream? body = null;
    if (head.Chunked)
    {
      body = new ChunkedBodyStream(this, reader, options.MaxRequestBodyBytes, deadline.Token);
    }
    else if (head.ContentLength > 0)
    {
      body = new LengthBodyStream(this, reader, head.ContentLength.Value, deadline.Token);
    }

    _continuePending = head.ExpectContinue && hasBody;
    var isHead = head.Method == "HEAD";
    using var response = new ListenerResponse(this, head.Http11, isHead, head.Close || !head.Http11);
    var request = new ListenerRequest(head, body, remote, scheme);
    var exchange = new ListenerExchange(request, response, abort.Token);
    BodyBuffer.ResetWrittenCount();
    Output.ResetWrittenCount();

    if (!hasBody)
    {
      StartWatching();
    }

    var faulted = false;
    try
    {
      await Server.Handler(exchange).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      // The request was aborted, by the peer or the server, or its body could not be read; the error, if any, is answered below.
    }
    catch (Exception ex)
    {
      faulted = true;
      ContextDiagnostics.Publish(DiagnosticKind.HandlerFault, "The HTTP handler failed with " + ex.GetType().Name + ".", null, ex);
    }

    if (_broken)
    {
      return false;
    }

    if (!response.Committed && (_error is not null || faulted))
    {
      var (status, reason) = _error ?? (500, "Internal error.");
      await SendErrorAsync(status, reason).ConfigureAwait(false);
      return false;
    }

    var reusable = await response.CompleteAsync(Server.Shutdown).ConfigureAwait(false);
    if (!reusable || _error is not null || faulted || Server.Shutdown.IsCancellationRequested)
    {
      return false;
    }

    if (_continuePending)
    {
      // The client is still waiting for permission to send a body we never asked for.
      return false;
    }

    return body is null || await DrainAsync(body, deadline.Token).ConfigureAwait(false);
  }

  // A handler may answer without reading the body (a 4xx). The connection can carry on only if the rest of the body is small enough
  // to skip; otherwise the next request would be parsed out of the middle of it.
  private static async ValueTask<bool> DrainAsync(RequestBodyStream body, CancellationToken deadline)
  {
    if (body.Complete)
    {
      return true;
    }

    var scratch = ArrayPool<byte>.Shared.Rent(4096);
    try
    {
      var skipped = 0;
      while (skipped <= DrainLimit)
      {
        var read = await body.ReadAsync(scratch.AsMemory(), deadline).ConfigureAwait(false);
        if (read == 0)
        {
          return body.Complete;
        }

        skipped += read;
      }

      return false;
    }
    catch (OperationCanceledException)
    {
      return false;
    }
    finally
    {
      ArrayPool<byte>.Shared.Return(scratch);
    }
  }

  private async ValueTask<(RequestHead? Head, HeadError? Error)> ReadHeadAsync(CancellationToken cancellationToken)
  {
    var reader = _reader!;
    var options = Server.Options;
    while (true)
    {
      var window = reader.Window;
      var end = window.IndexOf("\r\n\r\n"u8);
      if (end >= 0)
      {
        var error = RequestHeadParser.TryParse(window[..end], options, out var head);
        reader.Consume(end + 4);
        return (head, error);
      }

      // No complete head yet. Refuse early what can no longer become a valid one, so a sender cannot make us buffer it.
      var firstLine = window.IndexOf("\r\n"u8);
      if ((firstLine < 0 ? window.Length : firstLine) > options.MaxRequestLineBytes)
      {
        return (null, new HeadError(414, "The request line is too long."));
      }

      if (window.Length >= reader.Capacity)
      {
        return (null, new HeadError(431, "The request head is too large."));
      }

      if (window.IndexOf((byte)'\n') is var lf and >= 0 && (lf == 0 || window[lf - 1] != (byte)'\r'))
      {
        return (null, new HeadError(400, "Bare LF in the request head."));
      }

      if (await reader.FillAsync(cancellationToken).ConfigureAwait(false) == 0)
      {
        // The peer hung up in the middle of a head.
        return (null, null);
      }
    }
  }

  private (IPAddress? Remote, string Scheme) ApplyForwardedHeaders(RequestHead head)
  {
    var headers = head.Headers;
    var scheme = _tls ? "https" : "http";
    var remote = _peer;
    var trusted = Server.IsTrustedProxy(_peer);
    if (trusted)
    {
      // Only the proxy's own appendage is believed: walk X-Forwarded-For from the right, past other trusted proxies.
      var forwardedFor = headers.GetAll("X-Forwarded-For");
      for (var i = forwardedFor.Count - 1; i >= 0; i--)
      {
        var parts = forwardedFor[i].Split(',');
        for (var j = parts.Length - 1; j >= 0; j--)
        {
          if (!IPAddress.TryParse(parts[j].Trim(), out var address))
          {
            goto done;
          }

          remote = address;
          if (!Server.IsTrustedProxy(address))
          {
            goto done;
          }
        }
      }

    done:
      if (LastToken(headers.GetAll("X-Forwarded-Proto")) is { } proto && (proto.Equals("https", StringComparison.OrdinalIgnoreCase) || proto.Equals("http", StringComparison.OrdinalIgnoreCase)))
      {
        scheme = proto.ToLowerInvariant();
      }

      if (LastToken(headers.GetAll("X-Forwarded-Host")) is { Length: > 0 } host && RequestHeadParser.IsHostLike(host))
      {
        headers.Set("Host", host);
      }

      return (remote, scheme);
    }

    // An untrusted peer can write anything in these; none of it may reach a handler that might believe it.
    foreach (var name in new List<string>(headers.Names))
    {
      if (name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) || name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase))
      {
        headers.Remove(name);
      }
    }

    return (remote, scheme);
  }

  private static string? LastToken(IReadOnlyList<string> values)
  {
    if (values.Count == 0)
    {
      return null;
    }

    var last = values[^1];
    var comma = last.LastIndexOf(',');
    return (comma < 0 ? last : last[(comma + 1)..]).Trim();
  }

  /// <summary>Answers a request the server will not process, with a short fixed text, and the connection is closed afterwards.</summary>
  private async ValueTask SendErrorAsync(int status, string reason)
  {
    var text = reason + "\n";
    var head = string.Create(
      System.Globalization.CultureInfo.InvariantCulture,
      $"HTTP/1.1 {status} {ErrorReason(status)}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(text)}\r\nConnection: close\r\n\r\n{text}");
    try
    {
      await WriteAsync(Encoding.UTF8.GetBytes(head), Server.Shutdown).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
    {
      // The peer is gone; the error had nowhere to go.
    }
  }

  private static string ErrorReason(int status) => status switch
  {
    400 => "Bad Request",
    408 => "Request Timeout",
    413 => "Content Too Large",
    414 => "URI Too Long",
    417 => "Expectation Failed",
    431 => "Request Header Fields Too Large",
    500 => "Internal Server Error",
    501 => "Not Implemented",
    503 => "Service Unavailable",
    505 => "HTTP Version Not Supported",
    _ => "Error",
  };
}
