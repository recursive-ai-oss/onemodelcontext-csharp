using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.JsonRpc;

/// <summary>What a channel read produced.</summary>
public enum JsonRpcChannelStatus
{
  /// <summary>A complete message is available.</summary>
  Message,

  /// <summary>The peer closed its side; no more messages will arrive.</summary>
  EndOfStream,

  /// <summary>A message exceeded the channel's size bound. The channel is finished: nothing more can be read from it.</summary>
  MessageTooLong,
}

/// <summary>One read from an <see cref="IJsonRpcChannel"/>.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Message">The message bytes when <paramref name="Status"/> is <see cref="JsonRpcChannelStatus.Message"/>; valid until the next read.</param>
public readonly record struct JsonRpcChannelRead(JsonRpcChannelStatus Status, ReadOnlyMemory<byte> Message);

/// <summary>
/// A bidirectional channel of whole JSON-RPC messages: newline-delimited streams, and later HTTP exchanges, implement it.
/// One reader and one writer at a time; <see cref="JsonRpcConnection"/> serializes writes.
/// </summary>
public interface IJsonRpcChannel : IAsyncDisposable
{
  /// <summary>Reads the next message.</summary>
  /// <param name="cancellationToken">Cancels the wait.</param>
  ValueTask<JsonRpcChannelRead> ReadAsync(CancellationToken cancellationToken);

  /// <summary>Sends one message.</summary>
  /// <param name="message">The message bytes, without framing.</param>
  /// <param name="cancellationToken">Cancels the write.</param>
  ValueTask WriteAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken);
}

/// <summary>
/// Marks a channel whose <see cref="IJsonRpcChannel.WriteAsync"/> may be called from several threads at once, so the connection need not
/// serialise sends. The HTTP client is one: each message is its own request.
/// </summary>
internal interface IConcurrentWriteChannel
{
}

/// <summary>
/// Newline-delimited JSON-RPC over a pair of streams: each message is one line of UTF-8 JSON that contains no raw newline,
/// terminated by <c>\n</c> (the MCP stdio framing). A trailing <c>\r</c> is ignored and empty lines are skipped.
/// </summary>
/// <remarks>
/// Overrun: a line longer than the bound ends the channel with <see cref="JsonRpcChannelStatus.MessageTooLong"/>; the bytes are
/// discarded and the caller closes the connection, because the stream cannot be resynchronised safely. Memory use is bounded by
/// the line bound plus one read chunk. Buffers come from <see cref="ArrayPool{T}"/> and are cleared when returned.
/// Writes are queued and sent by one background drain, so a caller that is also the reader does not wait for the stream, and messages that
/// pile up while a write is in flight go out together in the next one. Order is kept. At most <see cref="MaxQueuedBytes"/> wait in the
/// queue: a writer past that waits until the stream has taken some (backpressure). A stream failure is reported by the next write.
/// </remarks>
public sealed class NewlineChannel : IJsonRpcChannel
{
  /// <summary>The default longest line accepted, 8 MiB.</summary>
  public const int DefaultMaxLineBytes = 8 * 1024 * 1024;

  private const int InitialBuffer = 16 * 1024;

  /// <summary>Most bytes queued for the stream before a writer waits: 1 MiB (a single larger message is still accepted whole).</summary>
  public const int MaxQueuedBytes = 1024 * 1024;

  private readonly Stream _input;
  private readonly Stream _output;
  private readonly bool _leaveOpen;
  private readonly int _maxLineBytes;
  private byte[] _buffer;
  private int _start;
  private int _end;
  private int _scanned;
  private bool _finished;
  private bool _disposed;
  private readonly object _gate = new();
  private readonly CancellationTokenSource _stopWriting = new();
  private byte[] _queue = [];
  private int _queued;
  private bool _draining;
  private Task _drain = Task.CompletedTask;
  private TaskCompletionSource? _space;
  private Exception? _writeFault;

  /// <summary>Creates a channel over a duplex stream.</summary>
  /// <param name="duplex">A stream that can read and write.</param>
  /// <param name="maxLineBytes">The longest line accepted, excluding the newline. Default 8 MiB.</param>
  /// <param name="leaveOpen">Leave the stream open when the channel is disposed.</param>
  public NewlineChannel(Stream duplex, int maxLineBytes = DefaultMaxLineBytes, bool leaveOpen = false)
    : this(duplex, duplex, maxLineBytes, leaveOpen)
  {
  }

  /// <summary>Creates a channel over separate input and output streams (a child process's pipes, stdin and stdout).</summary>
  /// <param name="input">Where messages are read from.</param>
  /// <param name="output">Where messages are written.</param>
  /// <param name="maxLineBytes">The longest line accepted, excluding the newline. Default 8 MiB.</param>
  /// <param name="leaveOpen">Leave the streams open when the channel is disposed.</param>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLineBytes"/> is below 1.</exception>
  public NewlineChannel(Stream input, Stream output, int maxLineBytes = DefaultMaxLineBytes, bool leaveOpen = false)
  {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentOutOfRangeException.ThrowIfLessThan(maxLineBytes, 1);
    _input = input;
    _output = output;
    _maxLineBytes = maxLineBytes;
    _leaveOpen = leaveOpen;
    _buffer = ArrayPool<byte>.Shared.Rent(Math.Min(InitialBuffer, maxLineBytes + 2));
  }

  /// <summary>The longest line accepted, in bytes.</summary>
  public int MaxLineBytes => _maxLineBytes;

  /// <inheritdoc />
  public async ValueTask<JsonRpcChannelRead> ReadAsync(CancellationToken cancellationToken)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    while (true)
    {
      if (_finished)
      {
        return new JsonRpcChannelRead(JsonRpcChannelStatus.EndOfStream, default);
      }

      // Scan only the bytes not yet looked at; IndexOf is vectorised by the runtime.
      var pending = _buffer.AsSpan(_start + _scanned, _end - _start - _scanned);
      var newline = pending.IndexOf((byte)'\n');
      if (newline >= 0)
      {
        var lineLength = _scanned + newline;
        var line = _buffer.AsMemory(_start, lineLength);
        _start += lineLength + 1;
        _scanned = 0;
        if (line.Length > 0 && line.Span[^1] == (byte)'\r')
        {
          line = line[..^1];
        }

        if (line.Length > _maxLineBytes)
        {
          return Overrun();
        }

        if (line.Length == 0)
        {
          continue;
        }

        return new JsonRpcChannelRead(JsonRpcChannelStatus.Message, line);
      }

      _scanned = _end - _start;
      if (_scanned > _maxLineBytes + 1)
      {
        // More than a full line (plus a possible \r) without a newline.
        return Overrun();
      }

      MakeRoom();
      var read = await _input.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
      if (read == 0)
      {
        _finished = true;
        var rest = _buffer.AsMemory(_start, _end - _start);
        _start = _end = _scanned = 0;
        if (rest.Length > 0 && rest.Span[^1] == (byte)'\r')
        {
          rest = rest[..^1];
        }

        // A last line without a newline is still a message.
        if (rest.Length > _maxLineBytes)
        {
          return Overrun();
        }

        return rest.Length == 0
          ? new JsonRpcChannelRead(JsonRpcChannelStatus.EndOfStream, default)
          : new JsonRpcChannelRead(JsonRpcChannelStatus.Message, rest);
      }

      _end += read;
    }
  }

  /// <inheritdoc />
  /// <exception cref="IOException">An earlier write to the stream failed.</exception>
  public ValueTask WriteAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    Task? space = null;
    lock (_gate)
    {
      if (_writeFault is { } fault)
      {
        throw new IOException("An earlier write failed: " + fault.Message, fault);
      }

      var needed = _queued + message.Length + 1;
      if (needed > _queue.Length)
      {
        var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(needed, Math.Min(_queue.Length * 2, MaxQueuedBytes)));
        _queue.AsSpan(0, _queued).CopyTo(bigger);
        ArrayPool<byte>.Shared.Return(_queue, clearArray: true);
        _queue = bigger;
      }

      message.Span.CopyTo(_queue.AsSpan(_queued));
      _queue[_queued + message.Length] = (byte)'\n';
      _queued += message.Length + 1;
      if (!_draining)
      {
        _draining = true;
        _drain = Task.Run(DrainAsync, CancellationToken.None);
      }

      if (_queued > MaxQueuedBytes)
      {
        space = (_space ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
      }
    }

    return space is null ? ValueTask.CompletedTask : new ValueTask(space.WaitAsync(cancellationToken));
  }

  // The single writer to the stream: takes everything queued, writes it as one block, repeats until nothing is left.
  private async Task DrainAsync()
  {
    var spare = Array.Empty<byte>();
    while (true)
    {
      byte[] block;
      int length;
      var spin = new SpinWait();
      while (Volatile.Read(ref _queued) == 0 && spin.Count < 20)
      {
        // New messages usually follow within microseconds under load; waiting briefly saves a thread hand-off.
        spin.SpinOnce(sleep1Threshold: -1);
      }

      lock (_gate)
      {
        if (_queued == 0)
        {
          _draining = false;
          ArrayPool<byte>.Shared.Return(spare, clearArray: true);
          return;
        }

        block = _queue;
        length = _queued;
        _queue = spare;
        _queued = 0;
        _space?.TrySetResult();
        _space = null;
      }

      try
      {
        await _output.WriteAsync(block.AsMemory(0, length), _stopWriting.Token).ConfigureAwait(false);
        await _output.FlushAsync(_stopWriting.Token).ConfigureAwait(false);
      }
      catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException or OperationCanceledException)
      {
        lock (_gate)
        {
          _writeFault = ex;
          _draining = false;
          _queued = 0;
          _space?.TrySetException(new IOException("The stream failed: " + ex.Message, ex));
          _space = null;
        }

        ArrayPool<byte>.Shared.Return(block, clearArray: true);
        return;
      }

      spare = block;
    }
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync()
  {
    if (_disposed)
    {
      return;
    }

    _disposed = true;
    try
    {
      // Queued output still goes out, unless the stream is stuck on a peer that stopped reading.
      await _drain.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
    }
    catch (TimeoutException)
    {
      await _stopWriting.CancelAsync().ConfigureAwait(false);
    }

    lock (_gate)
    {
      ArrayPool<byte>.Shared.Return(_queue, clearArray: true);
      _queue = [];
      _queued = 0;
    }

    ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
    _buffer = [];
    if (!_leaveOpen)
    {
      await _output.DisposeAsync().ConfigureAwait(false);
      if (!ReferenceEquals(_input, _output))
      {
        await _input.DisposeAsync().ConfigureAwait(false);
      }
    }
  }

  private JsonRpcChannelRead Overrun()
  {
    _finished = true;
    _start = _end = _scanned = 0;
    return new JsonRpcChannelRead(JsonRpcChannelStatus.MessageTooLong, default);
  }

  // Keeps the unread bytes at the front of the buffer and grows it (never beyond the line bound plus a newline and a \r).
  private void MakeRoom()
  {
    if (_start > 0 && _end == _buffer.Length)
    {
      Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
      _end -= _start;
      _start = 0;
    }

    if (_end < _buffer.Length)
    {
      return;
    }

    var limit = _maxLineBytes + 2;
    var next = (int)Math.Min((long)_buffer.Length * 2, limit);
    if (next <= _buffer.Length)
    {
      // Full at the bound with no newline: the next scan reports the overrun.
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
