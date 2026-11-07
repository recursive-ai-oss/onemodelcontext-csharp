using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.Http;

/// <summary>
/// The read side of one connection: a pooled buffer holding bytes that arrived but are not yet consumed (the rest of a head, a
/// pipelined request). At most one socket read is in flight at any time.
/// </summary>
internal sealed class ConnectionReader : IDisposable
{
  private readonly Stream _stream;
  private readonly int _capacity;
  private byte[]? _buffer;
  private int _start;
  private int _end;
  private bool _eof;
  private Task<int>? _monitor;

  internal ConnectionReader(Stream stream, int capacity)
  {
    _stream = stream;
    _capacity = capacity;
    _buffer = ArrayPool<byte>.Shared.Rent(capacity);
  }

  /// <summary>The most bytes the buffer holds; a request head must fit.</summary>
  internal int Capacity => _capacity;

  /// <summary>The bytes received and not yet consumed.</summary>
  internal ReadOnlySpan<byte> Window => _buffer.AsSpan(_start, _end - _start);

  internal int Buffered => _end - _start;

  /// <summary>Marks bytes of the window as used.</summary>
  internal void Consume(int count)
  {
    _start += count;
    if (_start == _end)
    {
      _start = 0;
      _end = 0;
    }
  }

  /// <summary>
  /// Waits until a byte is available. Returns false when the peer closed the connection with nothing pending. If a monitor read is
  /// in flight it is the read that is waited for, so no byte is ever lost between a request and the next.
  /// </summary>
  internal async ValueTask<bool> WaitForDataAsync(CancellationToken cancellationToken)
  {
    if (_monitor is { } monitor)
    {
      await monitor.WaitAsync(cancellationToken).ConfigureAwait(false);
      _monitor = null;
    }

    if (Buffered > 0)
    {
      return true;
    }

    return !_eof && await FillAsync(cancellationToken).ConfigureAwait(false) > 0;
  }

  /// <summary>Reads more bytes behind the window. Returns 0 at end of stream. The window must not be full.</summary>
  internal async ValueTask<int> FillAsync(CancellationToken cancellationToken)
  {
    if (_eof)
    {
      return 0;
    }

    Compact();
    if (_end == _capacity)
    {
      throw new InvalidOperationException("The read buffer is full.");
    }

    var read = await _stream.ReadAsync(_buffer.AsMemory(_end, _capacity - _end), cancellationToken).ConfigureAwait(false);
    if (read == 0)
    {
      _eof = true;
    }
    else
    {
      _end += read;
    }

    return read;
  }

  /// <summary>
  /// Reads body bytes: buffered ones first, then straight from the connection into <paramref name="destination"/>. Returns 0 at end
  /// of stream.
  /// </summary>
  internal async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
  {
    if (Buffered > 0)
    {
      var count = Math.Min(Buffered, destination.Length);
      _buffer.AsSpan(_start, count).CopyTo(destination.Span);
      Consume(count);
      return count;
    }

    if (_eof || destination.IsEmpty)
    {
      return 0;
    }

    var read = await _stream.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
    if (read == 0)
    {
      _eof = true;
    }

    return read;
  }

  /// <summary>
  /// Starts one background read so that a peer that hangs up is noticed while a handler runs. The bytes it returns, if any, are a
  /// pipelined request and stay buffered. Returns null when a read is already in flight or the buffer has no room.
  /// </summary>
  internal Task<int>? StartMonitor()
  {
    // A pipelined request is already waiting in the buffer: the next read belongs to the head parser, not to a hang-up watch.
    if (_monitor is not null || _eof || Buffered > 0)
    {
      return null;
    }

    Compact();
    if (_end == _capacity)
    {
      return null;
    }

    _monitor = MonitorAsync();
    return _monitor;
  }

  /// <summary>Reads and discards until the peer closes its side, up to <paramref name="limit"/> bytes. Used after our side has been shut down.</summary>
  internal async ValueTask DiscardUntilEofAsync(int limit, CancellationToken cancellationToken)
  {
    if (_monitor is { } monitor)
    {
      var first = await monitor.WaitAsync(cancellationToken).ConfigureAwait(false);
      _monitor = null;
      if (first == 0)
      {
        return;
      }
    }

    var scratch = ArrayPool<byte>.Shared.Rent(4096);
    try
    {
      var total = 0;
      while (total <= limit && !_eof)
      {
        var read = await _stream.ReadAsync(scratch.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
          return;
        }

        total += read;
      }
    }
    finally
    {
      ArrayPool<byte>.Shared.Return(scratch);
    }
  }

  private async Task<int> MonitorAsync()
  {
    try
    {
      var read = await _stream.ReadAsync(_buffer.AsMemory(_end, _capacity - _end), CancellationToken.None).ConfigureAwait(false);
      if (read == 0)
      {
        _eof = true;
      }
      else
      {
        _end += read;
      }

      return read;
    }
    catch
    {
      _eof = true;
      throw;
    }
  }

  private void Compact()
  {
    if (_start > 0)
    {
      Buffer.BlockCopy(_buffer!, _start, _buffer!, 0, _end - _start);
      _end -= _start;
      _start = 0;
    }
  }

  public void Dispose()
  {
    var buffer = _buffer;
    _buffer = null;
    // A read still in flight owns the buffer until the connection is closed under it; leave that one to the collector.
    if (buffer is not null && (_monitor is null || _monitor.IsCompleted))
    {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }
}
