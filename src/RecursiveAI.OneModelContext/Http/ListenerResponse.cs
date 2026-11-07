using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.Http;

/// <summary>
/// The response of one exchange. Writes are buffered until the handler flushes or finishes: a response that was never flushed goes out
/// with a <c>Content-Length</c>, one that was flushed (an event stream) goes out chunked, and one whose handler declared a length
/// keeps it (RFC 9112 section 6). Every wait on the peer is bounded by the connection's write timeout.
/// </summary>
internal sealed class ListenerResponse : IHttpResponse, IDisposable
{
  // Bytes held back before the head is committed. A small answer then leaves in one write; a larger one switches to chunked.
  private const int BufferLimit = 16 * 1024;
  private static readonly byte[] LastChunk = "0\r\n\r\n"u8.ToArray();
  private static readonly byte[] Crlf = "\r\n"u8.ToArray();

  private readonly HttpConnection _connection;
  private readonly bool _http11;
  private readonly bool _isHead;
  private readonly HttpHeaders _headers = new();
  private readonly BodyStream _body;
  private Mode _mode;
  private int _status = 200;
  private long _declared = -1;
  private long _sent;
  private bool _close;

  private enum Mode
  {
    Unsent,
    Chunked,
    Fixed,
    CloseDelimited,
    NoBody,
  }

  internal ListenerResponse(HttpConnection connection, bool http11, bool isHead, bool close)
  {
    _connection = connection;
    _http11 = http11;
    _isHead = isHead;
    _close = close;
    _body = new BodyStream(this);
  }

  public void Dispose() => _body.Dispose();

  internal bool Committed => _mode != Mode.Unsent;

  public int StatusCode
  {
    get => _status;
    set
    {
      if (Committed)
      {
        throw new InvalidOperationException("The response head has been sent.");
      }

      if (value is < 200 or > 599)
      {
        throw new ArgumentOutOfRangeException(nameof(value), "A final status code is 200 to 599.");
      }

      _status = value;
    }
  }

  public Stream Body => _body;

  public void SetHeader(string name, string value)
  {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(value);
    if (Committed)
    {
      throw new InvalidOperationException("The response head has been sent.");
    }

    // Response splitting: a name must be a token and a value may hold no line break or NUL (RFC 9110 section 5.5).
    if (!RequestHeadParser.IsToken(name))
    {
      throw new ArgumentException("Not a valid header name.", nameof(name));
    }

    foreach (var c in value)
    {
      if (c > 0xFF || (c < 0x20 && c != '\t') || c == 0x7F)
      {
        throw new ArgumentException("A header value may hold no control characters.", nameof(value));
      }
    }

    if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
    {
      throw new ArgumentException("The server chooses the transfer coding.", nameof(name));
    }

    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
    {
      if (!RequestHeadParser.TryParseLength(value, out var length))
      {
        throw new ArgumentException("Content-Length is a number of bytes.", nameof(value));
      }

      _declared = length;
      return;
    }

    if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
    {
      if (value.Contains("close", StringComparison.OrdinalIgnoreCase))
      {
        _close = true;
      }

      return;
    }

    _headers.Set(name, value.Trim());
  }

  /// <summary>Finishes the response. Returns whether the connection may carry another request.</summary>
  internal async ValueTask<bool> CompleteAsync(CancellationToken cancellationToken)
  {
    await _connection.ResponseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      switch (_mode)
      {
        case Mode.Unsent:
          await CommitAsync(final: true, cancellationToken).ConfigureAwait(false);
          break;
        case Mode.Chunked:
          await _connection.WriteAsync(LastChunk, cancellationToken).ConfigureAwait(false);
          break;
        case Mode.Fixed when _sent != _declared:
          // The handler promised more bytes than it wrote; the peer would wait for them, so the connection cannot be reused.
          _close = true;
          break;
      }

      return !_close && _mode != Mode.CloseDelimited;
    }
    finally
    {
      _connection.ResponseGate.Release();
    }
  }

  private bool BodyAllowed => !_isHead && _status is not (204 or 304);

  private async ValueTask CommitAsync(bool final, CancellationToken cancellationToken)
  {
    var buffer = _connection.BodyBuffer;
    var output = _connection.Output;
    output.ResetWrittenCount();
    long? contentLength = null;
    var chunked = false;
    if (!BodyAllowed)
    {
      _mode = Mode.NoBody;
      if (_isHead && _declared >= 0)
      {
        contentLength = _declared;
      }
    }
    else if (_declared >= 0)
    {
      _mode = Mode.Fixed;
      contentLength = _declared;
    }
    else if (final)
    {
      _mode = Mode.Fixed;
      _declared = buffer.WrittenCount;
      contentLength = _declared;
    }
    else if (_http11)
    {
      _mode = Mode.Chunked;
      chunked = true;
    }
    else
    {
      // HTTP/1.0 has no chunked coding; the end of the body is the end of the connection (RFC 9112 section 6.3).
      _mode = Mode.CloseDelimited;
      _close = true;
    }

    WriteHead(output, contentLength, chunked);
    if (_mode == Mode.Fixed)
    {
      if (buffer.WrittenCount > _declared)
      {
        _close = true;
        buffer.ResetWrittenCount();
        throw new InvalidOperationException("The handler wrote more than the Content-Length it declared.");
      }

      output.Write(buffer.WrittenSpan);
      _sent += buffer.WrittenCount;
    }
    else if (_mode == Mode.Chunked && buffer.WrittenCount > 0)
    {
      AppendChunk(output, buffer.WrittenSpan);
    }
    else if (_mode == Mode.CloseDelimited)
    {
      output.Write(buffer.WrittenSpan);
    }

    buffer.ResetWrittenCount();
    await _connection.WriteAsync(output.WrittenMemory, cancellationToken).ConfigureAwait(false);
  }

  private void WriteHead(ArrayBufferWriter<byte> output, long? contentLength, bool chunked)
  {
    var line = string.Create(CultureInfo.InvariantCulture, $"HTTP/1.1 {_status} {Reason(_status)}\r\n");
    Append(output, line);
    foreach (var name in _headers.Names)
    {
      foreach (var value in _headers.GetAll(name))
      {
        Append(output, name);
        Append(output, ": ");
        Append(output, value);
        Append(output, "\r\n");
      }
    }

    Append(output, "Date: ");
    Append(output, _connection.Server.Time.GetUtcNow().ToString("r", CultureInfo.InvariantCulture));
    Append(output, "\r\n");
    if (contentLength is { } length)
    {
      Append(output, string.Create(CultureInfo.InvariantCulture, $"Content-Length: {length}\r\n"));
    }
    else if (chunked)
    {
      Append(output, "Transfer-Encoding: chunked\r\n");
    }

    if (_close)
    {
      Append(output, "Connection: close\r\n");
    }

    Append(output, "\r\n");
  }

  private static void Append(ArrayBufferWriter<byte> output, string text)
  {
    var span = output.GetSpan(text.Length);
    var written = Encoding.Latin1.GetBytes(text, span);
    output.Advance(written);
  }

  private static void AppendChunk(ArrayBufferWriter<byte> output, ReadOnlySpan<byte> data)
  {
    var span = output.GetSpan(10);
    data.Length.TryFormat(span, out var written, "x", CultureInfo.InvariantCulture);
    output.Advance(written);
    output.Write(Crlf);
    output.Write(data);
    output.Write(Crlf);
  }

  private static string Reason(int status) => status switch
  {
    200 => "OK",
    201 => "Created",
    202 => "Accepted",
    204 => "No Content",
    301 => "Moved Permanently",
    302 => "Found",
    304 => "Not Modified",
    400 => "Bad Request",
    401 => "Unauthorized",
    403 => "Forbidden",
    404 => "Not Found",
    405 => "Method Not Allowed",
    406 => "Not Acceptable",
    408 => "Request Timeout",
    409 => "Conflict",
    413 => "Content Too Large",
    414 => "URI Too Long",
    415 => "Unsupported Media Type",
    417 => "Expectation Failed",
    429 => "Too Many Requests",
    431 => "Request Header Fields Too Large",
    500 => "Internal Server Error",
    501 => "Not Implemented",
    503 => "Service Unavailable",
    505 => "HTTP Version Not Supported",
    _ => status < 400 ? "OK" : "Error",
  };

  private sealed class BodyStream(ListenerResponse owner) : Stream
  {
    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
      get => throw new NotSupportedException();
      set => throw new NotSupportedException();
    }

    public override void Flush() => throw new NotSupportedException("Response bodies are written asynchronously.");

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Response bodies are written asynchronously.");

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
      WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
      var connection = owner._connection;
      await connection.ResponseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
        await owner.WriteLockedAsync(buffer, cancellationToken).ConfigureAwait(false);
      }
      finally
      {
        connection.ResponseGate.Release();
      }
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
      var connection = owner._connection;
      await connection.ResponseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
        if (owner._mode == Mode.Unsent)
        {
          await owner.CommitAsync(final: false, cancellationToken).ConfigureAwait(false);
        }
      }
      finally
      {
        connection.ResponseGate.Release();
      }
    }
  }

  private async ValueTask WriteLockedAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
  {
    if (_mode == Mode.Unsent)
    {
      if (!BodyAllowed)
      {
        return;
      }

      if (_connection.BodyBuffer.WrittenCount + data.Length <= BufferLimit)
      {
        _connection.BodyBuffer.Write(data.Span);
        return;
      }

      await CommitAsync(final: false, cancellationToken).ConfigureAwait(false);
    }

    if (data.IsEmpty || _mode == Mode.NoBody)
    {
      return;
    }

    switch (_mode)
    {
      case Mode.Chunked when data.Length <= BufferLimit:
        // One write for size, bytes and CRLF; a zero-length chunk would end the body, which is why empty writes returned above.
        _connection.Output.ResetWrittenCount();
        AppendChunk(_connection.Output, data.Span);
        await _connection.WriteAsync(_connection.Output.WrittenMemory, cancellationToken).ConfigureAwait(false);
        break;
      case Mode.Chunked:
        _connection.Output.ResetWrittenCount();
        var span = _connection.Output.GetSpan(10);
        data.Length.TryFormat(span, out var written, "x", CultureInfo.InvariantCulture);
        _connection.Output.Advance(written);
        _connection.Output.Write(Crlf);
        await _connection.WriteAsync(_connection.Output.WrittenMemory, cancellationToken).ConfigureAwait(false);
        await _connection.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await _connection.WriteAsync(Crlf, cancellationToken).ConfigureAwait(false);
        break;
      case Mode.Fixed:
        if (_sent + data.Length > _declared)
        {
          _close = true;
          throw new InvalidOperationException("The handler wrote more than the Content-Length it declared.");
        }

        _sent += data.Length;
        await _connection.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        break;
      default:
        await _connection.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        break;
    }
  }
}
