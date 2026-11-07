using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.Sse;

/// <summary>What <see cref="SseEventReader.ReadAsync"/> found.</summary>
public enum SseReadStatus
{
  /// <summary>An event with data is available.</summary>
  Event,

  /// <summary>The stream ended. An event the stream stopped in the middle of is discarded, as the W3C specification says.</summary>
  EndOfStream,

  /// <summary>An event or a line was larger than the bound. The reader is finished: the stream cannot be resynchronised.</summary>
  TooLarge,
}

/// <summary>One dispatched Server-Sent Event.</summary>
/// <param name="EventName">The <c>event</c> field, or <c>message</c> when the event had none.</param>
/// <param name="Data">The <c>data</c> lines joined with a line feed, as UTF-8; valid until the next read.</param>
public readonly record struct SseEvent(string EventName, ReadOnlyMemory<byte> Data);

/// <summary>One read from an <see cref="SseEventReader"/>.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Event">The event when <paramref name="Status"/> is <see cref="SseReadStatus.Event"/>.</param>
public readonly record struct SseRead(SseReadStatus Status, SseEvent Event);

/// <summary>
/// Reads a Server-Sent Events stream (W3C HTML Living Standard, "Server-sent events", the event stream interpretation) from a stream.
/// Lines end in CRLF, LF or CR; a leading byte order mark is skipped; comment lines and unknown fields are ignored; one space after the
/// colon is dropped. An event without <c>data</c> is not returned, but its <c>id</c> and <c>retry</c> still take effect, which is how a
/// server primes a client with an event id before closing a stream.
/// </summary>
/// <remarks>
/// Memory is bounded: an event larger than the bound ends the reader with <see cref="SseReadStatus.TooLarge"/>. Buffers are rented from
/// <see cref="ArrayPool{T}"/> and cleared when returned. One reader, one read at a time.
/// </remarks>
public sealed class SseEventReader : IDisposable
{
  /// <summary>The default largest event (data bytes) or line accepted, 8 MiB.</summary>
  public const int DefaultMaxEventBytes = 8 * 1024 * 1024;

  private const int InitialBuffer = 16 * 1024;
  private static readonly string DefaultEventName = "message";

  private readonly Stream _stream;
  private readonly int _maxEventBytes;
  private readonly bool _leaveOpen;
  private byte[] _buffer;
  private int _start;
  private int _end;
  private byte[] _data = [];
  private int _dataLength;
  private bool _hasData;
  private string? _eventName;
  private string? _pendingId;
  private bool _skipLineFeed;
  private bool _checkedBom;
  private bool _finished;

  /// <summary>Creates a reader.</summary>
  /// <param name="stream">The response stream.</param>
  /// <param name="maxEventBytes">The largest event data or line, in bytes. Default 8 MiB.</param>
  /// <param name="leaveOpen">Leave <paramref name="stream"/> open when the reader is disposed.</param>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxEventBytes"/> is below 1.</exception>
  public SseEventReader(Stream stream, int maxEventBytes = DefaultMaxEventBytes, bool leaveOpen = false)
  {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentOutOfRangeException.ThrowIfLessThan(maxEventBytes, 1);
    _stream = stream;
    _maxEventBytes = maxEventBytes;
    _leaveOpen = leaveOpen;
    _buffer = ArrayPool<byte>.Shared.Rent(Math.Min(InitialBuffer, maxEventBytes + 2));
  }

  /// <summary>The id of the last event the stream dispatched or primed (the <c>id</c> field), for <c>Last-Event-ID</c>. Null if none was sent.</summary>
  public string? LastEventId { get; private set; }

  /// <summary>The last <c>retry</c> value the server sent: how long to wait before reconnecting, in milliseconds. Null if none was sent.</summary>
  public int? RetryMilliseconds { get; private set; }

  /// <summary>Reads until the next event with data.</summary>
  /// <param name="cancellationToken">Cancels the wait.</param>
  public async ValueTask<SseRead> ReadAsync(CancellationToken cancellationToken)
  {
    ObjectDisposedException.ThrowIf(_buffer.Length == 0, this);
    while (true)
    {
      if (_finished)
      {
        return new SseRead(SseReadStatus.EndOfStream, default);
      }

      var outcome = ScanBuffered(out var dispatched);
      if (outcome == SseReadStatus.TooLarge)
      {
        _finished = true;
        return new SseRead(SseReadStatus.TooLarge, default);
      }

      if (dispatched)
      {
        var name = _eventName ?? DefaultEventName;
        _eventName = null;
        return new SseRead(SseReadStatus.Event, new SseEvent(name, _data.AsMemory(0, _dataLength)));
      }

      MakeRoom();
      var read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
      if (read == 0)
      {
        _finished = true;
        return new SseRead(SseReadStatus.EndOfStream, default);
      }

      _end += read;
    }
  }

  /// <inheritdoc />
  public void Dispose()
  {
    if (_buffer.Length == 0)
    {
      return;
    }

    ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
    _buffer = [];
    if (_data.Length > 0)
    {
      ArrayPool<byte>.Shared.Return(_data, clearArray: true);
      _data = [];
    }

    if (!_leaveOpen)
    {
      _stream.Dispose();
    }
  }

  // Consumes complete lines from the buffer. Returns TooLarge on overrun; sets dispatched when an event with data is ready.
  private SseReadStatus ScanBuffered(out bool dispatched)
  {
    dispatched = false;
    if (!_checkedBom && _end - _start >= 3)
    {
      _checkedBom = true;
      if (_buffer[_start] == 0xEF && _buffer[_start + 1] == 0xBB && _buffer[_start + 2] == 0xBF)
      {
        _start += 3;
      }
    }

    // A previous line ended in CR at the very end of the buffer: a following LF belongs to it.
    if (_skipLineFeed && _end > _start)
    {
      _skipLineFeed = false;
      if (_buffer[_start] == (byte)'\n')
      {
        _start++;
      }
    }

    while (true)
    {
      var pending = _buffer.AsSpan(_start, _end - _start);
      var eol = pending.IndexOfAny((byte)'\n', (byte)'\r');
      if (eol < 0)
      {
        // No complete line yet. A line this long can never fit in an event.
        return pending.Length > _maxEventBytes + 1 ? SseReadStatus.TooLarge : SseReadStatus.Event;
      }

      var line = pending[..eol];
      var consumed = eol + 1;
      if (pending[eol] == (byte)'\r')
      {
        if (eol + 1 < pending.Length)
        {
          if (pending[eol + 1] == (byte)'\n')
          {
            consumed++;
          }
        }
        else
        {
          _skipLineFeed = true;
        }
      }

      if (line.Length > _maxEventBytes)
      {
        return SseReadStatus.TooLarge;
      }

      var status = ProcessLine(line, out dispatched);
      _start += consumed;
      if (status != SseReadStatus.Event)
      {
        return status;
      }

      if (dispatched)
      {
        return SseReadStatus.Event;
      }
    }
  }

  private SseReadStatus ProcessLine(ReadOnlySpan<byte> line, out bool dispatched)
  {
    dispatched = false;
    if (line.IsEmpty)
    {
      // A blank line dispatches the event being built. The id takes effect even when there is no data to dispatch.
      if (_pendingId is not null)
      {
        LastEventId = _pendingId;
        _pendingId = null;
      }

      if (_hasData)
      {
        // Drop the line feed that ended the last data line (W3C: "If the data buffer's last character is a LF, remove it").
        _dataLength--;
        dispatched = true;
        _hasData = false;
        return SseReadStatus.Event;
      }

      _eventName = null;
      _dataLength = 0;
      return SseReadStatus.Event;
    }

    if (line[0] == (byte)':')
    {
      return SseReadStatus.Event;
    }

    ReadOnlySpan<byte> name;
    ReadOnlySpan<byte> value;
    var colon = line.IndexOf((byte)':');
    if (colon < 0)
    {
      name = line;
      value = default;
    }
    else
    {
      name = line[..colon];
      value = line[(colon + 1)..];
      if (!value.IsEmpty && value[0] == (byte)' ')
      {
        value = value[1..];
      }
    }

    if (name.SequenceEqual("data"u8))
    {
      if ((long)_dataLength + value.Length + 1 > _maxEventBytes)
      {
        return SseReadStatus.TooLarge;
      }

      // Reuse the data buffer of the previous event, but only start counting from zero after it was handed out.
      if (!_hasData && _dataLength != 0)
      {
        _dataLength = 0;
      }

      EnsureData(_dataLength + value.Length + 1);
      value.CopyTo(_data.AsSpan(_dataLength));
      _dataLength += value.Length;
      _data[_dataLength++] = (byte)'\n';
      _hasData = true;
    }
    else if (name.SequenceEqual("event"u8))
    {
      _eventName = value.SequenceEqual("message"u8) ? DefaultEventName : System.Text.Encoding.UTF8.GetString(value);
    }
    else if (name.SequenceEqual("id"u8))
    {
      // An id with a NUL is ignored (W3C).
      if (value.IndexOf((byte)0) < 0)
      {
        _pendingId = System.Text.Encoding.UTF8.GetString(value);
      }
    }
    else if (name.SequenceEqual("retry"u8))
    {
      // Only ASCII digits make a valid retry value.
      if (!value.IsEmpty && value.Length <= 9 && TryParseDigits(value, out var milliseconds))
      {
        RetryMilliseconds = milliseconds;
      }
    }

    return SseReadStatus.Event;
  }

  private static bool TryParseDigits(ReadOnlySpan<byte> digits, out int value)
  {
    value = 0;
    foreach (var d in digits)
    {
      if (d is < (byte)'0' or > (byte)'9')
      {
        return false;
      }

      value = (value * 10) + (d - '0');
    }

    return true;
  }

  private void EnsureData(int needed)
  {
    if (_data.Length >= needed)
    {
      return;
    }

    var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(needed, Math.Min(Math.Max(_data.Length * 2, 1024), _maxEventBytes + 1)));
    _data.AsSpan(0, _dataLength).CopyTo(bigger);
    if (_data.Length > 0)
    {
      ArrayPool<byte>.Shared.Return(_data, clearArray: true);
    }

    _data = bigger;
  }

  // Keeps the unread bytes at the front of the buffer and grows it, never beyond the event bound plus a line ending.
  private void MakeRoom()
  {
    if (_start == _end)
    {
      _start = _end = 0;
    }

    if (_end < _buffer.Length)
    {
      return;
    }

    if (_start > 0)
    {
      Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
      _end -= _start;
      _start = 0;
      return;
    }

    var limit = _maxEventBytes + 3;
    var next = (int)Math.Min((long)_buffer.Length * 2, limit);
    if (next <= _buffer.Length)
    {
      next = _buffer.Length + 1;
    }

    var bigger = ArrayPool<byte>.Shared.Rent(next);
    Buffer.BlockCopy(_buffer, _start, bigger, 0, _end - _start);
    ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
    _end -= _start;
    _start = 0;
    _buffer = bigger;
  }
}
