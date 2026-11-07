using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Wire;

namespace RecursiveAI.OneModelContext.Hosting;

/// <summary>
/// One legacy session on this instance (2025-03-26 to 2025-11-25 Streamable HTTP, "Session Management"): the codec's state, the event
/// streams that can be resumed, the requests the server sent and is waiting on, and the handlers still running. Sessions are pinned to
/// the instance that made them.
/// </summary>
internal sealed class HttpSession : IServerSink, IDisposable
{
  private readonly StreamableHttpOptions _options;
  private readonly TimeProvider _time;
  private readonly TimeSpan _requestTimeout;
  private readonly ConcurrentDictionary<JsonRpcId, TaskCompletionSource<JsonRpcMessage>> _pending = new();
  private readonly ConcurrentDictionary<JsonRpcId, CancellationTokenSource> _inFlight = new();
  private readonly ConcurrentDictionary<string, SseStream> _streams = new(StringComparer.Ordinal);
  private readonly CancellationTokenSource _closed = new();
  private long _nextRequestId;
  private long _nextStreamId;
  private long _lastActivity;
  private SseStream? _standalone;

  internal HttpSession(StreamableHttpOptions options, TimeProvider time, TimeSpan requestTimeout)
  {
    _options = options;
    _time = time;
    _requestTimeout = requestTimeout;
    Id = NewId();
    State = new ServerSessionState { SessionId = Id };
    Touch();
  }

  internal string Id { get; }

  internal ServerSessionState State { get; }

  /// <summary>Cancelled when the session ends (DELETE, expiry or shutdown); linked into every handler of the session.</summary>
  internal CancellationToken Closed => _closed.Token;

  internal int ActiveHandlers => _inFlight.Count;

  internal long LastActivity => Volatile.Read(ref _lastActivity);

  internal void Touch() => Volatile.Write(ref _lastActivity, _time.GetTimestamp());

  internal bool HasOpenStreams()
  {
    foreach (var stream in _streams.Values)
    {
      if (!stream.IsComplete)
      {
        return true;
      }
    }

    return false;
  }

  // 128 random bits as hex: unguessable, and only visible ASCII (2025-11-25 Streamable HTTP, "Session Management", item 1).
  private static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

  internal SseStream NewStream(bool standalone)
  {
    var id = Interlocked.Increment(ref _nextStreamId).ToString(System.Globalization.CultureInfo.InvariantCulture);
    var stream = new SseStream(id, record: true, _options, _time);
    _streams[id] = stream;
    if (standalone)
    {
      _standalone = stream;
    }

    TrimStreams();
    return stream;
  }

  internal bool TryGetStream(string id, out SseStream stream) => _streams.TryGetValue(id, out stream!);

  // Keep the newest streams; finished streams past the limit are the ones to forget.
  private void TrimStreams()
  {
    if (_streams.Count <= _options.MaxStreamsPerSession)
    {
      return;
    }

    var finished = new List<SseStream>();
    foreach (var stream in _streams.Values)
    {
      if (stream.IsComplete)
      {
        finished.Add(stream);
      }
    }

    finished.Sort(static (a, b) => a.LastActivity.CompareTo(b.LastActivity));
    var excess = _streams.Count - _options.MaxStreamsPerSession;
    for (var i = 0; i < excess && i < finished.Count; i++)
    {
      _streams.TryRemove(finished[i].Id, out _);
    }
  }

  internal CancellationTokenSource RegisterHandler(JsonRpcId id, CancellationToken linkTo)
  {
    var cts = CancellationTokenSource.CreateLinkedTokenSource(linkTo, _closed.Token);
    _inFlight[id] = cts;
    return cts;
  }

  internal void UnregisterHandler(JsonRpcId id) => _inFlight.TryRemove(id, out _);

  /// <summary><c>notifications/cancelled</c>: stop the handler of the named request.</summary>
  internal void CancelHandler(JsonRpcId id)
  {
    if (_inFlight.TryGetValue(id, out var cts))
    {
      try
      {
        cts.Cancel();
      }
      catch (ObjectDisposedException)
      {
        // The handler finished first.
      }
    }
  }

  /// <summary>Delivers the client's answer to a request the server sent. Returns false when no such request is waiting.</summary>
  internal bool TryResolve(JsonRpcMessage response)
  {
    var id = response switch
    {
      JsonRpcResultResponse result => result.Id,
      JsonRpcErrorResponse error => error.Id,
      _ => default,
    };

    return id.HasValue && _pending.TryRemove(id, out var waiter) && waiter.TrySetResult(response);
  }

  // The session-level sink: messages not tied to a request go to the standalone GET stream, if one is open.
  public async ValueTask SendNotificationAsync(JsonRpcNotification notification, CancellationToken cancellationToken)
  {
    if (_standalone is { } stream)
    {
      await stream.SendAsync(JsonRpcWriter.ToUtf8Bytes(notification), cancellationToken).ConfigureAwait(false);
    }
  }

  public ValueTask<JsonElement> SendRequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken) =>
    SendRequestAsync(_standalone, method, parameters, cancellationToken);

  /// <summary>Sends a request to the client on <paramref name="stream"/> and waits for its answer.</summary>
  internal async ValueTask<JsonElement> SendRequestAsync(SseStream? stream, string method, JsonElement parameters, CancellationToken cancellationToken)
  {
    if (stream is null)
    {
      throw new NotSupportedException("No open stream reaches the client.");
    }

    var id = JsonRpcId.FromNumber(Interlocked.Increment(ref _nextRequestId));
    var waiter = new TaskCompletionSource<JsonRpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    _pending[id] = waiter;
    try
    {
      await stream.SendAsync(JsonRpcWriter.ToUtf8Bytes(new JsonRpcRequest(id, method, parameters)), cancellationToken).ConfigureAwait(false);
      using var timeout = new CancellationTokenSource(_requestTimeout, _time);
      using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, _closed.Token);
      var response = await waiter.Task.WaitAsync(linked.Token).ConfigureAwait(false);
      if (response is JsonRpcErrorResponse error)
      {
        throw new JsonRpcRemoteException(error.Error);
      }

      return ((JsonRpcResultResponse)response).Result;
    }
    finally
    {
      _pending.TryRemove(id, out _);
    }
  }

  public void Dispose()
  {
    try
    {
      _closed.Cancel();
    }
    catch (ObjectDisposedException)
    {
      // Already disposed.
    }

    foreach (var stream in _streams.Values)
    {
      _ = stream.CompleteAsync().AsTask();
    }

    foreach (var cts in _inFlight.Values)
    {
      try
      {
        cts.Cancel();
      }
      catch (ObjectDisposedException)
      {
        // The handler finished first.
      }
    }
  }
}
