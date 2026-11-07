using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Sse;

namespace RecursiveAI.OneModelContext.Hosting;

/// <summary>One HTTP response currently carrying an <see cref="SseStream"/>. It ends when the stream finishes, the stream drops it or a write fails.</summary>
internal sealed class SseConnection
{
  private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

  internal SseConnection(SseWriter writer)
  {
    Writer = writer;
  }

  internal SseWriter Writer { get; }

  /// <summary>Completes when the connection should end.</summary>
  internal Task Closed => _closed.Task;

  internal void Close() => _closed.TrySetResult();
}

/// <summary>
/// A stream of server-to-client messages on one request or one GET. A legacy stream keeps recent events with ids so that a client can
/// reconnect with <c>Last-Event-ID</c> (2025-03-26 and later, Streamable HTTP "Resumability and Redelivery"); a modern stream keeps nothing,
/// because 2026-07-28 streams are not resumable.
/// </summary>
internal sealed class SseStream
{
  private readonly AsyncGate _gate = new();
  private readonly bool _record;
  private readonly StreamableHttpOptions _options;
  private readonly TimeProvider _time;
  private readonly LinkedList<(long Seq, byte[] Data)> _log = new();
  private long _seq;
  private int _logBytes;
  private SseConnection? _current;
  private bool _complete;

  internal SseStream(string id, bool record, StreamableHttpOptions options, TimeProvider time)
  {
    Id = id;
    _record = record;
    _options = options;
    _time = time;
  }

  internal string Id { get; }

  internal bool IsComplete => Volatile.Read(ref _complete);

  /// <summary>When the stream last carried a message or a connection, for expiring finished streams.</summary>
  internal long LastActivity { get; private set; }

  internal string EventId(long seq) => Id + "-" + seq.ToString(System.Globalization.CultureInfo.InvariantCulture);

  /// <summary>Parses an event id made by <see cref="EventId"/> into its stream id and sequence number.</summary>
  internal static bool TryParseEventId(string value, out string streamId, out long seq)
  {
    var dash = value.LastIndexOf('-');
    if (dash > 0 && long.TryParse(value.AsSpan(dash + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out seq))
    {
      streamId = value[..dash];
      return true;
    }

    streamId = string.Empty;
    seq = 0;
    return false;
  }

  /// <summary>Sends one message (UTF-8 JSON). A recording stream logs it first, so a dropped connection can fetch it later.</summary>
  internal async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
  {
    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      if (_complete)
      {
        return;
      }

      LastActivity = _time.GetTimestamp();
      long seq = 0;
      if (_record)
      {
        seq = ++_seq;
        Record(seq, data.ToArray());
      }

      if (_current is { } connection)
      {
        await WriteAsync(connection, data, _record ? EventId(seq) : null).ConfigureAwait(false);
      }
    }
    finally
    {
      _gate.Release();
    }
  }

  /// <summary>
  /// Sends an event with an id and no data, which tells a client that can reconnect where to resume from
  /// (2025-11-25 Streamable HTTP, "Sending Messages to the Server", item 6).
  /// </summary>
  internal ValueTask SendPrimingAsync(CancellationToken cancellationToken) => SendAsync(ReadOnlyMemory<byte>.Empty, cancellationToken);

  /// <summary>
  /// Attaches a response to the stream and replays what the client missed. A connection already attached is closed: a message goes to
  /// one stream only, so the newest connection takes over.
  /// </summary>
  /// <param name="writer">The response's event writer.</param>
  /// <param name="afterSeq">Replay events after this sequence number, or -1 to replay nothing.</param>
  /// <param name="cancellationToken">Cancels the attach.</param>
  internal async ValueTask<SseConnection> AttachAsync(SseWriter writer, long afterSeq, CancellationToken cancellationToken)
  {
    var connection = new SseConnection(writer);
    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      LastActivity = _time.GetTimestamp();
      _current?.Close();
      _current = null;
      if (afterSeq >= 0)
      {
        // Copy the tail first so a write failure cannot leave the log half-walked.
        foreach (var (seq, data) in _log)
        {
          if (seq > afterSeq)
          {
            await WriteAsync(connection, data, EventId(seq)).ConfigureAwait(false);
          }
        }
      }

      if (_complete)
      {
        connection.Close();
      }
      else
      {
        _current = connection;
      }
    }
    finally
    {
      _gate.Release();
    }

    if (_options.KeepAliveInterval > TimeSpan.Zero)
    {
      _ = KeepAliveAsync(connection);
    }

    return connection;
  }

  /// <summary>Detaches a connection (the response ended). The stream keeps recording for a later reconnect.</summary>
  internal async ValueTask DetachAsync(SseConnection connection)
  {
    await _gate.WaitAsync().ConfigureAwait(false);
    try
    {
      if (ReferenceEquals(_current, connection))
      {
        _current = null;
      }

      connection.Close();
    }
    finally
    {
      _gate.Release();
    }
  }

  /// <summary>Marks the stream finished: no more messages, and the attached connection ends.</summary>
  internal async ValueTask CompleteAsync()
  {
    await _gate.WaitAsync().ConfigureAwait(false);
    try
    {
      _complete = true;
      LastActivity = _time.GetTimestamp();
      _current?.Close();
      _current = null;
    }
    finally
    {
      _gate.Release();
    }
  }

  /// <summary>
  /// Ends the connection without ending the stream, after telling the client when to come back (<c>retry</c>). The client then polls
  /// with <c>Last-Event-ID</c> (2025-11-25 Streamable HTTP, "Sending Messages to the Server", item 6).
  /// </summary>
  internal async ValueTask CloseConnectionAsync(TimeSpan retryAfter)
  {
    await _gate.WaitAsync().ConfigureAwait(false);
    try
    {
      if (_current is { } connection)
      {
        try
        {
          using var timeout = new CancellationTokenSource(_options.SseWriteTimeout, _time);
          await connection.Writer.WriteRetryAsync((int)Math.Min(retryAfter.TotalMilliseconds, int.MaxValue), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
          // The client is already gone; closing is what was asked for.
        }

        connection.Close();
        _current = null;
      }
    }
    finally
    {
      _gate.Release();
    }
  }

  private async Task KeepAliveAsync(SseConnection connection)
  {
    using var stop = new CancellationTokenSource();
    _ = connection.Closed.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Cancel(), stop, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    try
    {
      while (!stop.IsCancellationRequested)
      {
        await Task.Delay(_options.KeepAliveInterval, _time, stop.Token).ConfigureAwait(false);
        await _gate.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
          if (ReferenceEquals(_current, connection))
          {
            using var timeout = new CancellationTokenSource(_options.SseWriteTimeout, _time);
            await connection.Writer.WriteCommentAsync(timeout.Token).ConfigureAwait(false);
          }
        }
        finally
        {
          _gate.Release();
        }
      }
    }
    catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
    {
      connection.Close();
    }
  }

  // Called with the gate held. A failed or too-slow write closes the connection; a recording stream keeps the event for the reconnect.
  private async ValueTask WriteAsync(SseConnection connection, ReadOnlyMemory<byte> data, string? id)
  {
    try
    {
      using var timeout = new CancellationTokenSource(_options.SseWriteTimeout, _time);
      await connection.Writer.WriteEventAsync(data, id, cancellationToken: timeout.Token).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
    {
      connection.Close();
      if (ReferenceEquals(_current, connection))
      {
        _current = null;
      }
    }
  }

  private void Record(long seq, byte[] data)
  {
    _log.AddLast((seq, data));
    _logBytes += data.Length;
    while (_log.Count > _options.MaxReplayEvents || (_logBytes > _options.MaxReplayBytes && _log.Count > 1))
    {
      _logBytes -= _log.First!.Value.Data.Length;
      _log.RemoveFirst();
    }
  }
}
