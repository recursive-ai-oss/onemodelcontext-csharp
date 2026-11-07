using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.Http;

/// <summary>
/// Thrown to a handler reading a request body that cannot be completed (malformed framing, too large, too slow, the peer left). The
/// connection has already recorded the status to answer with; handlers treat it like any cancelled read.
/// </summary>
internal sealed class RequestAbortedException : OperationCanceledException
{
  internal RequestAbortedException()
    : base("The request body could not be read.")
  {
  }
}

/// <summary>What a request body reports to the connection that carries it.</summary>
internal interface IRequestBodyOwner
{
  /// <summary>Records the status to answer with when the body cannot be completed; the first one wins.</summary>
  void RecordError(int status, string reason);

  /// <summary>Sends <c>100 Continue</c> if the client asked for it and it has not been sent yet.</summary>
  ValueTask SendContinueAsync();

  /// <summary>The body has been read to its end.</summary>
  void BodyCompleted();
}

/// <summary>The request body of one exchange, read from the connection under the request deadline and the body size limit.</summary>
internal abstract class RequestBodyStream : Stream
{
  private readonly IRequestBodyOwner _connection;
  private readonly CancellationToken _deadline;
  private bool _started;

  protected RequestBodyStream(IRequestBodyOwner connection, ConnectionReader reader, long limit, CancellationToken deadline)
  {
    _connection = connection;
    Reader = reader;
    Limit = limit;
    _deadline = deadline;
  }

  protected ConnectionReader Reader { get; }

  protected long Limit { get; }

  /// <summary>True once the whole body, including a chunked body's trailer, has been consumed.</summary>
  internal bool Complete { get; private protected set; }

  internal bool Failed { get; private set; }

  public override bool CanRead => true;

  public override bool CanSeek => false;

  public override bool CanWrite => false;

  public override long Length => throw new NotSupportedException();

  public override long Position
  {
    get => throw new NotSupportedException();
    set => throw new NotSupportedException();
  }

  public override void Flush()
  {
  }

  public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Request bodies are read asynchronously.");

  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

  public override void SetLength(long value) => throw new NotSupportedException();

  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

  public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
  {
    if (Complete || buffer.IsEmpty)
    {
      return 0;
    }

    if (Failed)
    {
      throw new RequestAbortedException();
    }

    if (!_started)
    {
      _started = true;
      await _connection.SendContinueAsync().ConfigureAwait(false);
    }

    using var linked = cancellationToken.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline) : null;
    var token = linked?.Token ?? _deadline;
    try
    {
      var read = await ReadCoreAsync(buffer, token).ConfigureAwait(false);
      if (Complete)
      {
        _connection.BodyCompleted();
      }

      return read;
    }
    catch (OperationCanceledException) when (_deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
    {
      throw Fail(408, "The request took too long to arrive.");
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
      // The server is shutting down.
      throw Fail(503, "The server is shutting down.");
    }
    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
    {
      throw Fail(400, "The connection ended before the request body did.");
    }
  }

  public override int Read(Span<byte> buffer) => throw new NotSupportedException("Request bodies are read asynchronously.");

  public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
    ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

  protected abstract ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken);

  protected RequestAbortedException Fail(int status, string reason)
  {
    Failed = true;
    _connection.RecordError(status, reason);
    return new RequestAbortedException();
  }
}

/// <summary>A body with a declared length (RFC 9112 section 6.2).</summary>
internal sealed class LengthBodyStream(IRequestBodyOwner connection, ConnectionReader reader, long length, CancellationToken deadline)
  : RequestBodyStream(connection, reader, length, deadline)
{
  private long _remaining = length;

  protected override async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
  {
    var want = (int)Math.Min(buffer.Length, _remaining);
    var read = await Reader.ReadAsync(buffer[..want], cancellationToken).ConfigureAwait(false);
    if (read == 0)
    {
      throw Fail(400, "The request body ended early.");
    }

    _remaining -= read;
    Complete = _remaining == 0;
    return read;
  }
}

/// <summary>
/// A chunked body (RFC 9112 section 7.1). Chunk sizes are hexadecimal with no sign, space or overflow; chunk extensions are skipped
/// (section 7.1.1: a recipient ignores extensions it does not know) but the line stays bounded; trailers are read and dropped under the
/// same bound; every line ends in CRLF.
/// </summary>
internal sealed class ChunkedBodyStream(IRequestBodyOwner connection, ConnectionReader reader, long limit, CancellationToken deadline)
  : RequestBodyStream(connection, reader, limit, deadline)
{
  private const int MaxLineBytes = 4096;
  private const int MaxTrailerBytes = 8192;
  private long _chunkRemaining;
  private long _total;
  private bool _inChunk;

  protected override async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
  {
    while (true)
    {
      if (!_inChunk)
      {
        var size = await ReadChunkSizeAsync(cancellationToken).ConfigureAwait(false);
        if (size == 0)
        {
          await SkipTrailerAsync(cancellationToken).ConfigureAwait(false);
          Complete = true;
          return 0;
        }

        if (size > Limit - _total)
        {
          throw Fail(413, "The request body is too large.");
        }

        _chunkRemaining = size;
        _inChunk = true;
      }

      var want = (int)Math.Min(buffer.Length, _chunkRemaining);
      var read = await Reader.ReadAsync(buffer[..want], cancellationToken).ConfigureAwait(false);
      if (read == 0)
      {
        throw Fail(400, "The request body ended early.");
      }

      _chunkRemaining -= read;
      _total += read;
      if (_chunkRemaining == 0)
      {
        await ExpectCrlfAsync(cancellationToken).ConfigureAwait(false);
        _inChunk = false;
      }

      return read;
    }
  }

  private async ValueTask<long> ReadChunkSizeAsync(CancellationToken cancellationToken)
  {
    var length = await ReadLineAsync(MaxLineBytes, cancellationToken).ConfigureAwait(false);
    var line = Reader.Window[..length];
    var digits = line.IndexOf((byte)';');
    var hex = digits < 0 ? line : line[..digits];
    if (hex.IsEmpty || hex.Length > 15)
    {
      throw Fail(400, "Malformed chunk size.");
    }

    long size = 0;
    foreach (var b in hex)
    {
      int digit = b switch
      {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
      };
      if (digit < 0)
      {
        throw Fail(400, "Malformed chunk size.");
      }

      size = (size << 4) | (uint)digit;
    }

    Reader.Consume(length + 2);
    return size;
  }

  private async ValueTask ExpectCrlfAsync(CancellationToken cancellationToken)
  {
    while (Reader.Buffered < 2)
    {
      if (await Reader.FillAsync(cancellationToken).ConfigureAwait(false) == 0)
      {
        throw Fail(400, "The request body ended early.");
      }
    }

    if (Reader.Window[0] != (byte)'\r' || Reader.Window[1] != (byte)'\n')
    {
      throw Fail(400, "A chunk is not followed by CRLF.");
    }

    Reader.Consume(2);
  }

  private async ValueTask SkipTrailerAsync(CancellationToken cancellationToken)
  {
    var budget = MaxTrailerBytes;
    while (true)
    {
      var length = await ReadLineAsync(Math.Min(MaxLineBytes, budget), cancellationToken).ConfigureAwait(false);
      var first = length > 0 ? Reader.Window[0] : (byte)0;
      Reader.Consume(length + 2);
      if (length == 0)
      {
        return;
      }

      // A folded trailer line is as ambiguous as a folded header.
      if (first is (byte)' ' or (byte)'\t')
      {
        throw Fail(400, "Obsolete line folding in a trailer.");
      }

      budget -= length + 2;
      if (budget <= 0)
      {
        throw Fail(431, "The trailer is too large.");
      }
    }
  }

  // Makes the window start with a complete line (up to a CRLF) of at most maxLength bytes and returns its length without the CRLF.
  private async ValueTask<int> ReadLineAsync(int maxLength, CancellationToken cancellationToken)
  {
    var scanned = 0;
    while (true)
    {
      var window = Reader.Window;
      for (; scanned < window.Length; scanned++)
      {
        var b = window[scanned];
        if (b == (byte)'\n')
        {
          if (scanned == 0 || window[scanned - 1] != (byte)'\r')
          {
            throw Fail(400, "Bare LF in the request body framing.");
          }

          if (scanned - 1 > maxLength)
          {
            throw Fail(400, "A chunk line is too long.");
          }

          return scanned - 1;
        }

        if (b == (byte)'\r' && scanned + 1 < window.Length && window[scanned + 1] != (byte)'\n')
        {
          throw Fail(400, "Bare CR in the request body framing.");
        }
      }

      if (window.Length > maxLength + 1 || window.Length >= Reader.Capacity)
      {
        throw Fail(400, "A chunk line is too long.");
      }

      if (await Reader.FillAsync(cancellationToken).ConfigureAwait(false) == 0)
      {
        throw Fail(400, "The request body ended early.");
      }
    }
  }
}
