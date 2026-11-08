using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.JsonRpc;

/// <summary>Why <see cref="JsonRpcConnection.RunAsync"/> returned.</summary>
public enum JsonRpcCloseReason
{
  /// <summary>The peer closed its side.</summary>
  EndOfStream,

  /// <summary>A message exceeded the channel's size bound, so the connection was closed.</summary>
  MessageTooLong,

  /// <summary>The run was cancelled or the connection was disposed.</summary>
  Cancelled,

  /// <summary>Reading or writing failed; see <see cref="JsonRpcConnection.Fault"/>.</summary>
  ChannelFault,
}

/// <summary>Callbacks and limits of a <see cref="JsonRpcConnection"/>.</summary>
public sealed class JsonRpcConnectionOptions
{
  // A protocol handshake is a barrier: later admissions must observe its completed state transition.
  internal Predicate<JsonRpcRequest>? RequestBarrier { get; init; }

  /// <summary>
  /// Answers an incoming request: return the response message (result or error) or <c>null</c> to send none.
  /// Called concurrently, one call per request. When absent every request gets -32601.
  /// </summary>
  public Func<JsonRpcRequest, CancellationToken, ValueTask<JsonRpcMessage?>>? RequestHandler { get; init; }

  /// <summary>
  /// Receives incoming notifications, one at a time and in arrival order. It runs on the read loop, so it must return quickly.
  /// </summary>
  public Func<JsonRpcNotification, CancellationToken, ValueTask>? NotificationHandler { get; init; }

  /// <summary>
  /// Called when the caller of <see cref="JsonRpcConnection.SendRequestAsync(string, JsonElement, CancellationToken)"/> cancels while waiting, so a protocol layer can send its
  /// cancellation notification. Exceptions it throws are ignored.
  /// </summary>
  public Func<JsonRpcId, ValueTask>? OutgoingRequestCancelled { get; init; }

  /// <summary>Called with an exception a request handler threw (the peer only sees "Internal error."). Exceptions it throws are ignored.</summary>
  public Action<Exception>? HandlerFaulted { get; init; }

  /// <summary>Most incoming requests handled at once; more get error -32000. Default 256.</summary>
  public int MaxConcurrentRequests { get; init; } = 256;

  /// <summary>How long <see cref="JsonRpcConnection.SendRequestAsync(string, JsonElement, CancellationToken)"/> waits for a response. Default 5 minutes.</summary>
  public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);

  /// <summary>How long shutdown waits for cancelled handlers to finish. Default 5 seconds.</summary>
  public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

  /// <summary>
  /// Grace after input EOF for admitted requests to finish and queue their responses before cancellation. Default 5 seconds; zero cancels immediately.
  /// Explicit cancellation or a channel fault skips the grace. Remaining handlers are then cancelled and subject to
  /// <see cref="ShutdownTimeout"/>; channel disposal drains queued writes separately.
  /// </summary>
  public TimeSpan EndOfStreamTimeout { get; init; } = TimeSpan.FromSeconds(5);

  /// <summary>Limits applied to every incoming message.</summary>
  public JsonRpcReaderOptions ReaderOptions { get; init; } = new();

  /// <summary>The clock for timeouts. Default <see cref="TimeProvider.System"/>.</summary>
  public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>Per-request settings for <see cref="JsonRpcConnection.SendRequestAsync(string, JsonElement, JsonRpcRequestOptions, CancellationToken)"/>.</summary>
public sealed class JsonRpcRequestOptions
{
  /// <summary>Called with the request's id after it was chosen and before the request is sent, so a caller can match later messages to it.</summary>
  public Action<JsonRpcId>? IdAssigned { get; init; }

  /// <summary>How long to wait for the response; <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> for no limit. Default: the connection's request timeout.</summary>
  public TimeSpan? Timeout { get; init; }
}

/// <summary>
/// A JSON-RPC 2.0 peer over an <see cref="IJsonRpcChannel"/>. It sends requests and correlates their responses, dispatches incoming
/// requests concurrently, delivers notifications in order, and serialises sends. It knows no protocol methods.
/// </summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
  private readonly IJsonRpcChannel _channel;
  private readonly JsonRpcConnectionOptions _options;
  private readonly SemaphoreSlim _writeLock = new(1, 1);
  private readonly bool _channelWritesConcurrently;
  private readonly ConcurrentDictionary<JsonRpcId, TaskCompletionSource<JsonRpcMessage>> _pending = new();
  private readonly ConcurrentDictionary<JsonRpcId, IncomingRequest> _incoming = new();
  private readonly ConcurrentDictionary<Task, byte> _active = new();
  private readonly CancellationTokenSource _closed = new();
  private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
  private long _nextId;
  private int _started;
  private int _closing;

  /// <summary>Creates a connection. Nothing is read until <see cref="RunAsync"/>.</summary>
  /// <param name="channel">The message channel; disposed with the connection.</param>
  /// <param name="options">Handlers and limits; <c>null</c> for none.</param>
  public JsonRpcConnection(IJsonRpcChannel channel, JsonRpcConnectionOptions? options = null)
  {
    ArgumentNullException.ThrowIfNull(channel);
    _channel = channel;
    _channelWritesConcurrently = channel is IConcurrentWriteChannel;
    _options = options ?? new JsonRpcConnectionOptions();
    if (channel is NewlineChannel newline)
    {
      newline.ConfigureShutdown(_options.ShutdownTimeout, _options.EndOfStreamTimeout + _options.ShutdownTimeout, _options.TimeProvider);
    }
  }

  /// <summary>
  /// Whether JSON array batches are accepted. Default false: a batch gets one -32600 error. Only the 2025-03-26 revision allows
  /// batches, so the protocol layer turns this on when it negotiates that revision.
  /// </summary>
  public bool AcceptBatches { get; set; }

  /// <summary>The exception that ended the connection when the reason was <see cref="JsonRpcCloseReason.ChannelFault"/>.</summary>
  public Exception? Fault { get; private set; }

  /// <summary>True once shutdown has closed sending, after any input-EOF grace period.</summary>
  public bool IsClosed => Volatile.Read(ref _closing) != 0;

  /// <summary>Cancelled when the connection closes.</summary>
  public CancellationToken Closed => _closed.Token;

  /// <summary>
  /// Reads and dispatches messages until the peer closes, a message is too long, a channel fault occurs or <paramref name="cancellationToken"/>
  /// is cancelled. On return all pending requests have failed with <see cref="JsonRpcClosedException"/> and running handlers were cancelled.
  /// </summary>
  /// <param name="cancellationToken">Stops the read loop.</param>
  /// <exception cref="InvalidOperationException">Already running.</exception>
  public async Task<JsonRpcCloseReason> RunAsync(CancellationToken cancellationToken = default)
  {
    if (Interlocked.Exchange(ref _started, 1) != 0)
    {
      throw new InvalidOperationException("The connection is already running.");
    }

    var reason = JsonRpcCloseReason.EndOfStream;
    try
    {
      using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
      while (true)
      {
        if (_channel is NewlineChannel newline)
        {
          await newline.WaitForWriteCapacityAsync(linked.Token).ConfigureAwait(false);
        }

        var read = await _channel.ReadAsync(linked.Token).ConfigureAwait(false);
        if (read.Status == JsonRpcChannelStatus.EndOfStream)
        {
          if (_channel is NewlineChannel ending)
          {
            ending.BeginShutdown();
          }

          break;
        }

        if (read.Status == JsonRpcChannelStatus.MessageTooLong)
        {
          reason = JsonRpcCloseReason.MessageTooLong;
          await TrySendAsync(new JsonRpcErrorResponse(default, new JsonRpcError(JsonRpcErrorCodes.InvalidRequest, "Invalid request: the message is larger than the limit; closing the connection."))).ConfigureAwait(false);
          break;
        }

        await DispatchAsync(JsonRpcReader.Read(read.Message, _options.ReaderOptions), linked.Token).ConfigureAwait(false);
      }
    }
    catch (OperationCanceledException)
    {
      reason = JsonRpcCloseReason.Cancelled;
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
    {
      Fault = ex;
      reason = JsonRpcCloseReason.ChannelFault;
    }

    if (reason == JsonRpcCloseReason.EndOfStream)
    {
      // Input EOF is a half-close: the peer may still be reading our output. Let admitted requests queue their replies before closing
      // the write side. A peer that cannot answer a nested request, or a hung handler, must not hold shutdown open indefinitely.
      var running = _active.Keys.ToArray();
      if (running.Length > 0)
      {
        using var draining = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        try
        {
          await Task.WhenAll(running).WaitAsync(_options.EndOfStreamTimeout, _options.TimeProvider, draining.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
          // The grace elapsed or the caller stopped the connection; shutdown now cancels any remaining handlers.
        }
      }
    }

    await ShutdownAsync().ConfigureAwait(false);
    _stopped.TrySetResult();
    return reason;
  }

  /// <summary>Sends a request and waits for its result.</summary>
  /// <param name="method">The method name.</param>
  /// <param name="parameters">The params, or <c>default</c> for none.</param>
  /// <param name="cancellationToken">Cancels the wait; the peer is then told through <see cref="JsonRpcConnectionOptions.OutgoingRequestCancelled"/>.</param>
  /// <returns>The result value (a standalone clone).</returns>
  /// <exception cref="JsonRpcRemoteException">The peer answered with an error.</exception>
  /// <exception cref="JsonRpcClosedException">The connection closed before the response arrived.</exception>
  /// <exception cref="TimeoutException">No response within <see cref="JsonRpcConnectionOptions.RequestTimeout"/>.</exception>
  /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
  public ValueTask<JsonElement> SendRequestAsync(string method, JsonElement parameters = default, CancellationToken cancellationToken = default) =>
    SendRequestCoreAsync(method, parameters, null, _options.RequestTimeout, cancellationToken);

  /// <summary>Sends a request that needs the id the connection chose or a wait other than the connection's default (a long-lived request).</summary>
  /// <param name="method">The method name.</param>
  /// <param name="parameters">The params.</param>
  /// <param name="requestOptions">The id callback and the wait.</param>
  /// <param name="cancellationToken">Cancels the wait; the peer is then told through <see cref="JsonRpcConnectionOptions.OutgoingRequestCancelled"/>.</param>
  /// <returns>The result value (a standalone clone).</returns>
  /// <exception cref="JsonRpcRemoteException">The peer answered with an error.</exception>
  /// <exception cref="JsonRpcClosedException">The connection closed before the response arrived.</exception>
  /// <exception cref="TimeoutException">No response within the request's wait.</exception>
  /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
  public ValueTask<JsonElement> SendRequestAsync(string method, JsonElement parameters, JsonRpcRequestOptions requestOptions, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(requestOptions);
    return SendRequestCoreAsync(method, parameters, requestOptions.IdAssigned, requestOptions.Timeout ?? _options.RequestTimeout, cancellationToken);
  }

  private async ValueTask<JsonElement> SendRequestCoreAsync(string method, JsonElement parameters, Action<JsonRpcId>? idAssigned, TimeSpan requestTimeout, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(method);
    if (IsClosed)
    {
      throw new JsonRpcClosedException("The connection is closed.");
    }

    var id = JsonRpcId.FromNumber(Interlocked.Increment(ref _nextId));
    var tcs = new TaskCompletionSource<JsonRpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    _pending[id] = tcs;
    idAssigned?.Invoke(id);
    using var timeout = new CancellationTokenSource(requestTimeout, _options.TimeProvider);

    // A caller that cannot cancel needs only the timeout's token; a second source would just add registrations and allocations.
    using var linkedSource = cancellationToken.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token) : null;
    var linked = linkedSource?.Token ?? timeout.Token;
    try
    {
      await WriteAsync(new JsonRpcRequest(id, method, parameters), linked).ConfigureAwait(false);
      JsonRpcMessage response;
      try
      {
        response = await tcs.Task.WaitAsync(linked).ConfigureAwait(false);
      }
      catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
      {
        throw new TimeoutException($"No response to '{method}' within {requestTimeout}.");
      }

      return response switch
      {
        JsonRpcResultResponse ok => ok.Result,
        JsonRpcErrorResponse error => throw new JsonRpcRemoteException(error.Error),
        _ => throw new InvalidOperationException("Unexpected response type."),
      };
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      _pending.TryRemove(id, out _);
      await NotifyCancelledAsync(id).ConfigureAwait(false);
      throw;
    }
    catch (TimeoutException)
    {
      _pending.TryRemove(id, out _);
      await NotifyCancelledAsync(id).ConfigureAwait(false);
      throw;
    }
    finally
    {
      _pending.TryRemove(id, out _);
    }
  }

  /// <summary>Sends a notification.</summary>
  /// <param name="method">The method name.</param>
  /// <param name="parameters">The params, or <c>default</c> for none.</param>
  /// <param name="cancellationToken">Cancels the send.</param>
  /// <exception cref="JsonRpcClosedException">The connection is closed.</exception>
  public ValueTask SendNotificationAsync(string method, JsonElement parameters = default, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(method);
    return WriteAsync(new JsonRpcNotification(method, parameters), cancellationToken);
  }

  /// <summary>Sends any message (a response built by a protocol layer, for instance). Sends are serialised.</summary>
  /// <param name="message">The message.</param>
  /// <param name="cancellationToken">Cancels the send.</param>
  /// <exception cref="JsonRpcClosedException">The connection is closed or the channel failed.</exception>
  public ValueTask SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(message);
    return WriteAsync(message, cancellationToken);
  }

  /// <summary>
  /// Cancels the handler of an incoming request, as <c>notifications/cancelled</c> asks. No response is sent for it.
  /// </summary>
  /// <param name="id">The id of the incoming request.</param>
  /// <returns>True when the request was still running.</returns>
  public bool CancelIncomingRequest(JsonRpcId id)
  {
    if (!_incoming.TryGetValue(id, out var request))
    {
      return false;
    }

    request.CancelledByPeer = true;
    try
    {
      request.Cts.Cancel();
    }
    catch (ObjectDisposedException)
    {
      return false;
    }

    return true;
  }

  /// <summary>Stops reading, fails pending requests, cancels handlers and disposes the channel.</summary>
  public async ValueTask DisposeAsync()
  {
    await _closed.CancelAsync().ConfigureAwait(false);
    if (Volatile.Read(ref _started) == 0)
    {
      await ShutdownAsync().ConfigureAwait(false);
    }
    else
    {
      // RunAsync performs the shutdown; wait for it, but not forever.
      try
      {
        await _stopped.Task.WaitAsync(_options.ShutdownTimeout, _options.TimeProvider).ConfigureAwait(false);
      }
      catch (TimeoutException)
      {
        // Handlers that ignore cancellation are abandoned after the shutdown timeout.
      }
    }

    await _channel.DisposeAsync().ConfigureAwait(false);
    _closed.Dispose();
  }

  private async Task DispatchAsync(JsonRpcReadResult read, CancellationToken cancellationToken)
  {
    switch (read.Kind)
    {
      case JsonRpcReadKind.Invalid:
        await TrySendAsync(read.Error!).ConfigureAwait(false);
        break;
      case JsonRpcReadKind.Message:
        await DispatchMessageAsync(read.Message!, cancellationToken).ConfigureAwait(false);
        break;
      case JsonRpcReadKind.Batch:
        if (!AcceptBatches)
        {
          await TrySendAsync(new JsonRpcErrorResponse(default, new JsonRpcError(JsonRpcErrorCodes.InvalidRequest, "Invalid request: batches are not supported."))).ConfigureAwait(false);
          break;
        }

        Track(HandleBatchAsync(read.Items!, cancellationToken));
        break;
    }
  }

  private async Task DispatchMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken)
  {
    switch (message)
    {
      case JsonRpcRequest request:
        await HandleAndRespondAsync(request, cancellationToken).ConfigureAwait(false);
        break;
      case JsonRpcNotification notification:
        await DeliverNotificationAsync(notification, cancellationToken).ConfigureAwait(false);
        break;
      case JsonRpcResultResponse or JsonRpcErrorResponse:
        CompleteResponse(message);
        break;
    }
  }

  private async Task HandleBatchAsync(IReadOnlyList<JsonRpcReadResult> items, CancellationToken cancellationToken)
  {
    var responses = new List<Task<JsonRpcMessage?>>();
    foreach (var item in items)
    {
      switch (item.Kind)
      {
        case JsonRpcReadKind.Invalid:
          responses.Add(Task.FromResult<JsonRpcMessage?>(item.Error));
          break;
        case JsonRpcReadKind.Message when item.Message is JsonRpcRequest request:
          responses.Add(HandleRequestAsync(request).AsTask());
          break;
        case JsonRpcReadKind.Message when item.Message is JsonRpcNotification notification:
          await DeliverNotificationAsync(notification, cancellationToken).ConfigureAwait(false);
          break;
        case JsonRpcReadKind.Message:
          CompleteResponse(item.Message!);
          break;
      }
    }

    var done = await Task.WhenAll(responses).ConfigureAwait(false);
    var toSend = new List<JsonRpcMessage>();
    foreach (var response in done)
    {
      if (response is not null)
      {
        toSend.Add(response);
      }
    }

    if (toSend.Count == 0)
    {
      return;
    }

    var buffer = new ArrayBufferWriter<byte>();
    JsonRpcWriter.WriteBatch(buffer, toSend);
    await TrySendBytesAsync(buffer.WrittenMemory).ConfigureAwait(false);
  }

  // The request is admitted and registered on the read loop, so a cancellation that arrives next finds it; the handler itself runs on the
  // thread pool, so the read loop goes straight on to the next message instead of waiting for the handler and the reply to be written.
  private async ValueTask HandleAndRespondAsync(JsonRpcRequest request, CancellationToken cancellationToken)
  {
    var (entry, rejection) = Admit(request);
    if (entry is null)
    {
      // Rejections also consume output capacity. Await them so a peer that never reads cannot accumulate unbounded tasks.
      await TrySendAsync(rejection!).ConfigureAwait(false);
      return;
    }

    var work = Task.Run(() => RunAndRespondAsync(request, entry), CancellationToken.None);
    Track(work);
    if (_options.RequestBarrier?.Invoke(request) == true)
    {
      await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
  }

  private async Task RunAndRespondAsync(JsonRpcRequest request, IncomingRequest entry)
  {
    var response = await RunHandlerAsync(request, entry).ConfigureAwait(false);
    if (response is not null)
    {
      await TrySendAsync(response).ConfigureAwait(false);
    }
  }

  private ValueTask<JsonRpcMessage?> HandleRequestAsync(JsonRpcRequest request)
  {
    var (entry, rejection) = Admit(request);
    return entry is null ? ValueTask.FromResult<JsonRpcMessage?>(rejection) : RunHandlerAsync(request, entry);
  }

  // Counts the request against the limits and registers it as in flight. Returns the entry, or the error to answer with.
  private (IncomingRequest? Entry, JsonRpcMessage? Rejection) Admit(JsonRpcRequest request)
  {
    if (_incoming.Count >= _options.MaxConcurrentRequests)
    {
      return (null, new JsonRpcErrorResponse(request.Id, new JsonRpcError(JsonRpcErrorCodes.ServerBusy, "Too many requests in flight.")));
    }

    var entry = new IncomingRequest(CancellationTokenSource.CreateLinkedTokenSource(_closed.Token));
    if (!_incoming.TryAdd(request.Id, entry))
    {
      entry.Cts.Dispose();
      return (null, new JsonRpcErrorResponse(request.Id, new JsonRpcError(JsonRpcErrorCodes.InvalidRequest, "Invalid request: a request with this id is already in flight.")));
    }

    return (entry, null);
  }

  private async ValueTask<JsonRpcMessage?> RunHandlerAsync(JsonRpcRequest request, IncomingRequest entry)
  {
    try
    {
      if (_options.RequestHandler is null)
      {
        return new JsonRpcErrorResponse(request.Id, new JsonRpcError(JsonRpcErrorCodes.MethodNotFound, "Method not found: " + request.Method + "."));
      }

      var response = await _options.RequestHandler(request, entry.Cts.Token).ConfigureAwait(false);
      return entry.CancelledByPeer ? null : response;
    }
    catch (OperationCanceledException) when (entry.Cts.IsCancellationRequested)
    {
      // Cancelled by the peer (no response is sent) or by shutdown (nobody to send it to).
      return null;
    }
    catch (Exception ex)
    {
      // The peer sees a generic error: exception messages can carry secrets and internals.
      ReportFault(ex);
      return new JsonRpcErrorResponse(request.Id, new JsonRpcError(JsonRpcErrorCodes.InternalError, "Internal error."));
    }
    finally
    {
      _incoming.TryRemove(request.Id, out _);
      entry.Cts.Dispose();
    }
  }

  private async ValueTask DeliverNotificationAsync(JsonRpcNotification notification, CancellationToken cancellationToken)
  {
    if (_options.NotificationHandler is null)
    {
      return;
    }

    try
    {
      await _options.NotificationHandler(notification, cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception ex)
    {
      // A notification has no response to carry an error; a faulty handler must not end the connection.
      ReportFault(ex);
    }
  }

  private void CompleteResponse(JsonRpcMessage response)
  {
    var id = response is JsonRpcResultResponse ok ? ok.Id : ((JsonRpcErrorResponse)response).Id;
    if (id.HasValue && _pending.TryRemove(id, out var tcs))
    {
      tcs.TrySetResult(response);
    }

    // A response nobody waits for (late, duplicate or unsolicited) is dropped.
  }

  private void ReportFault(Exception ex)
  {
    try
    {
      _options.HandlerFaulted?.Invoke(ex);
    }
    catch (Exception)
    {
      // The fault reporter must not take the connection down.
    }
  }

  private void Track(Task task)
  {
    if (task.IsCompleted)
    {
      // Handlers that finished without waiting need no tracking.
      return;
    }

    _active[task] = 0;
    _ = task.ContinueWith(static (t, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(t, out _), _active, TaskScheduler.Default);
  }

  private async ValueTask NotifyCancelledAsync(JsonRpcId id)
  {
    if (_options.OutgoingRequestCancelled is null || IsClosed)
    {
      return;
    }

    try
    {
      await _options.OutgoingRequestCancelled(id).ConfigureAwait(false);
    }
    catch (Exception)
    {
      // Best effort: the caller is already being told about the cancellation.
    }
  }

  private async ValueTask WriteAsync(JsonRpcMessage message, CancellationToken cancellationToken)
  {
    var buffer = new ArrayBufferWriter<byte>(256);
    JsonRpcWriter.Write(buffer, message);
    await WriteBytesAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
  }

  private async ValueTask WriteBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
  {
    if (IsClosed)
    {
      throw new JsonRpcClosedException("The connection is closed.");
    }

    var serialise = !_channelWritesConcurrently;
    if (serialise)
    {
      await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    try
    {
      await _channel.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
    {
      throw new JsonRpcClosedException("The connection failed while writing.", ex);
    }
    finally
    {
      if (serialise)
      {
        _writeLock.Release();
      }
    }
  }

  // Responses and error replies on the read path: a failed write means the peer is gone, which the read loop will see too.
  private async ValueTask TrySendAsync(JsonRpcMessage message)
  {
    try
    {
      await WriteAsync(message, _closed.Token).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is JsonRpcClosedException or OperationCanceledException)
    {
      // Nobody left to answer.
    }
  }

  private async ValueTask TrySendBytesAsync(ReadOnlyMemory<byte> bytes)
  {
    try
    {
      await WriteBytesAsync(bytes, _closed.Token).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is JsonRpcClosedException or OperationCanceledException)
    {
      // Nobody left to answer.
    }
  }

  private async Task ShutdownAsync()
  {
    if (Interlocked.Exchange(ref _closing, 1) != 0)
    {
      return;
    }

    if (_channel is NewlineChannel ending)
    {
      ending.BeginShutdown();
    }

    foreach (var pair in _pending)
    {
      pair.Value.TrySetException(new JsonRpcClosedException("The connection closed before a response arrived.", Fault));
    }

    _pending.Clear();
    await _closed.CancelAsync().ConfigureAwait(false);
    var running = _active.Keys.ToArray();
    if (running.Length > 0)
    {
      try
      {
        await Task.WhenAll(running).WaitAsync(_options.ShutdownTimeout, _options.TimeProvider).ConfigureAwait(false);
      }
      catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
      {
        // Handlers that ignore cancellation are abandoned after the shutdown timeout.
      }
    }
  }

  private sealed class IncomingRequest(CancellationTokenSource cts)
  {
    public CancellationTokenSource Cts { get; } = cts;

    public volatile bool CancelledByPeer;
  }
}
