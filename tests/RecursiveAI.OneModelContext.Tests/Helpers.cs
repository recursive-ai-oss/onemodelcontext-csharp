using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>One end of an in-memory duplex stream: what one end writes, the other reads.</summary>
internal sealed class PipeEnd : Stream
{
  private readonly Channel<byte[]> _incoming;
  private readonly Channel<byte[]> _outgoing;
  private byte[] _left = [];
  private int _leftOffset;

  private PipeEnd(Channel<byte[]> incoming, Channel<byte[]> outgoing)
  {
    _incoming = incoming;
    _outgoing = outgoing;
  }

  public static (PipeEnd A, PipeEnd B) CreatePair()
  {
    var ab = Channel.CreateUnbounded<byte[]>();
    var ba = Channel.CreateUnbounded<byte[]>();
    return (new PipeEnd(ba, ab), new PipeEnd(ab, ba));
  }

  public override bool CanRead => true;

  public override bool CanSeek => false;

  public override bool CanWrite => true;

  public override long Length => throw new NotSupportedException();

  public override long Position
  {
    get => throw new NotSupportedException();
    set => throw new NotSupportedException();
  }

  public override void Flush()
  {
  }

  public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

  public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
  {
    while (_leftOffset >= _left.Length)
    {
      if (!await _incoming.Reader.WaitToReadAsync(cancellationToken))
      {
        return 0;
      }

      if (_incoming.Reader.TryRead(out var chunk))
      {
        _left = chunk;
        _leftOffset = 0;
      }
    }

    var n = Math.Min(buffer.Length, _left.Length - _leftOffset);
    _left.AsMemory(_leftOffset, n).CopyTo(buffer);
    _leftOffset += n;
    return n;
  }

  public override void Write(byte[] buffer, int offset, int count) => _outgoing.Writer.TryWrite(buffer.AsSpan(offset, count).ToArray());

  public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
  {
    if (!_outgoing.Writer.TryWrite(buffer.ToArray()))
    {
      throw new IOException("The pipe is closed.");
    }

    return ValueTask.CompletedTask;
  }

  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

  public override void SetLength(long value) => throw new NotSupportedException();

  /// <summary>Closing an end completes what it writes, so the other end reads end of stream.</summary>
  protected override void Dispose(bool disposing)
  {
    if (disposing)
    {
      _outgoing.Writer.TryComplete();
    }

    base.Dispose(disposing);
  }
}

/// <summary>Reads whole lines the other end wrote. For tests that play the peer by hand.</summary>
internal sealed class LineReader(Stream stream)
{
  private readonly StringBuilder _pending = new();
  private readonly byte[] _buffer = new byte[4096];

  public async Task<string?> ReadLineAsync(TimeSpan? timeout = null)
  {
    using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
    while (true)
    {
      var text = _pending.ToString();
      var newline = text.IndexOf('\n', StringComparison.Ordinal);
      if (newline >= 0)
      {
        _pending.Clear().Append(text.AsSpan(newline + 1));
        return text[..newline];
      }

      var n = await stream.ReadAsync(_buffer, cts.Token);
      if (n == 0)
      {
        return null;
      }

      _pending.Append(Encoding.UTF8.GetString(_buffer, 0, n));
    }
  }

  public async Task<JsonElement> ReadJsonAsync()
  {
    var line = await ReadLineAsync() ?? throw new InvalidOperationException("The stream ended.");
    return JsonDocument.Parse(line).RootElement.Clone();
  }
}

/// <summary>A clock tests advance by hand.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
  private readonly object _gate = new();
  private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
  private readonly System.Collections.Generic.List<ManualTimer> _timers = [];

  public override DateTimeOffset GetUtcNow() => _now;

  public override long GetTimestamp() => _now.Ticks;

  public override long TimestampFrequency => TimeSpan.TicksPerSecond;

  public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
  {
    var timer = new ManualTimer(this, callback, state, dueTime);
    lock (_gate)
    {
      _timers.Add(timer);
    }

    return timer;
  }

  public void Advance(TimeSpan by)
  {
    ManualTimer[] due;
    lock (_gate)
    {
      _now += by;
      due = _timers.FindAll(t => t.Due <= _now).ToArray();
      foreach (var t in due)
      {
        _timers.Remove(t);
      }
    }

    foreach (var t in due)
    {
      t.Fire();
    }
  }

  private sealed class ManualTimer : ITimer
  {
    private readonly TimerCallback _callback;
    private readonly object? _state;

    public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime)
    {
      _callback = callback;
      _state = state;
      Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner._now + dueTime;
    }

    public DateTimeOffset Due { get; }

    public void Fire() => _callback(_state);

    public bool Change(TimeSpan dueTime, TimeSpan period) => false;

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }
}

internal static class Json
{
  public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

  /// <summary>True when two JSON values mean the same: objects compare by member regardless of order.</summary>
  public static bool SameMeaning(JsonElement a, JsonElement b)
  {
    if (a.ValueKind != b.ValueKind)
    {
      return false;
    }

    switch (a.ValueKind)
    {
      case JsonValueKind.Object:
        var left = new System.Collections.Generic.Dictionary<string, JsonElement>();
        foreach (var p in a.EnumerateObject())
        {
          left[p.Name] = p.Value;
        }

        var count = 0;
        foreach (var p in b.EnumerateObject())
        {
          count++;
          if (!left.TryGetValue(p.Name, out var other) || !SameMeaning(other, p.Value))
          {
            return false;
          }
        }

        return count == left.Count;
      case JsonValueKind.Array:
        if (a.GetArrayLength() != b.GetArrayLength())
        {
          return false;
        }

        var ia = a.EnumerateArray();
        var ib = b.EnumerateArray();
        while (ia.MoveNext() && ib.MoveNext())
        {
          if (!SameMeaning(ia.Current, ib.Current))
          {
            return false;
          }
        }

        return true;
      case JsonValueKind.String:
        return a.GetString() == b.GetString();
      case JsonValueKind.Number:
        return a.GetRawText() == b.GetRawText();
      default:
        return true;
    }
  }
}
