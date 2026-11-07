using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.OAuth;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Sse;

namespace RecursiveAI.OneModelContext.Transports;

/// <summary>
/// The Streamable HTTP client as a message channel. Each message the connection writes becomes its own POST; what comes back (a JSON
/// body, or an event stream scoped to the request) is queued for the connection to read. For legacy revisions the channel also keeps the
/// session id, opens the standalone GET stream and resumes broken streams, and when a server only speaks the 2024-11-05 HTTP+SSE
/// transport it switches to that.
/// </summary>
/// <remarks>
/// Sources: 2026-07-28 basic/transports (Streamable HTTP: sending messages, receiving messages, request metadata, backward
/// compatibility); 2025-11-25, 2025-06-18 and 2025-03-26 basic/transports (sessions, the GET stream, resumability, backwards
/// compatibility); 2024-11-05 basic/transports (HTTP with SSE); the W3C Server-Sent Events specification.
/// </remarks>
internal sealed class HttpChannel : IJsonRpcChannel, IConcurrentWriteChannel
{
  // JSON-RPC reserves -32000 to -32099 for the implementation. The data member marks errors that come from HTTP, not from the server's
  // protocol layer, so the client can raise them as transport errors.
  internal const int HttpErrorCode = -32000;
  private const int MaxErrorBodyBytes = 64 * 1024;

  private readonly Uri _endpoint;
  private readonly HttpClient _http;
  private readonly bool _ownsClient;
  private readonly HttpTransportOptions _options;
  private readonly Channel<Inbound> _inbound;
  private readonly CancellationTokenSource _lifetime = new();
  private readonly ConcurrentDictionary<JsonRpcId, Call> _calls = new();
  private readonly TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
  private int _running;
  private readonly ConcurrentDictionary<string, HeaderParameter[]> _toolHeaders = new(StringComparer.Ordinal);
  private readonly SemaphoreSlim _modeGate = new(1, 1);
  private byte[]? _lastRead;
  private long _reissueCounter;
  private volatile TransportMode _mode;
  private volatile string? _sessionId;
  private volatile string? _legacyVersion;
  private Uri _postUri;
  private int _listening;
  private int _disposed;

  internal HttpChannel(Uri endpoint, HttpClient http, bool ownsClient, HttpTransportOptions options)
  {
    _endpoint = endpoint;
    _postUri = endpoint;
    _http = http;
    _ownsClient = ownsClient;
    _options = options;
    _inbound = Channel.CreateBounded<Inbound>(new BoundedChannelOptions(Math.Max(1, options.MaxQueuedMessages))
    {
      SingleReader = true,
      FullMode = BoundedChannelFullMode.Wait,

      // The connection's read loop only classifies a message and hands it on (callers resume on the pool), so it can run on the thread that
      // delivered the message instead of waiting for a pool thread of its own.
      AllowSynchronousContinuations = true,
    });
  }

  private enum TransportMode
  {
    /// <summary>No request has been answered yet.</summary>
    Unknown,

    /// <summary>The server speaks Streamable HTTP (either era).</summary>
    Streamable,

    /// <summary>The server speaks the deprecated 2024-11-05 HTTP+SSE transport.</summary>
    SseLegacy,
  }

  private enum Outcome
  {
    /// <summary>The request has been answered, or its failure reported.</summary>
    Done,

    /// <summary>The response stream ended before the response arrived.</summary>
    Broken,

    /// <summary>The transport changed; send the request again.</summary>
    Retry,
  }

  private enum PumpEnd
  {
    Responded,
    Ended,
    TooLarge,
    Idle,
    Faulted,
  }

  private readonly record struct Inbound(byte[] Buffer, int Length);

  /// <summary>Registers the header-mirrored parameters of a tool, so its calls carry them (2026-07-28 "Custom Headers from Tool Parameters").</summary>
  internal void SetToolHeaders(string tool, HeaderParameter[] parameters)
  {
    if (parameters.Length == 0)
    {
      _toolHeaders.TryRemove(tool, out _);
    }
    else
    {
      _toolHeaders[tool] = parameters;
    }
  }

  public async ValueTask<JsonRpcChannelRead> ReadAsync(CancellationToken cancellationToken)
  {
    ReleaseLastRead();
    while (await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
    {
      if (_inbound.Reader.TryRead(out var item))
      {
        _lastRead = item.Buffer;
        return new JsonRpcChannelRead(JsonRpcChannelStatus.Message, item.Buffer.AsMemory(0, item.Length));
      }
    }

    return new JsonRpcChannelRead(JsonRpcChannelStatus.EndOfStream, default);
  }

  public ValueTask WriteAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
  {
    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    var body = message.ToArray();
    var shape = MessageShape.Inspect(body);
    switch (shape.Kind)
    {
      case MessageKind.Request:
        var call = NewCall(body, shape);
        _calls[call.Id] = call;
        StartCall(call);
        break;
      case MessageKind.Notification when shape.Method == Methods.NotificationsCancelled:
        Cancel(body, shape);
        break;
      case MessageKind.Notification or MessageKind.Response or MessageKind.Batch:
        var other = NewCall(body, shape);
        StartCall(other);
        break;
      default:
        throw new IOException("The message is not a JSON-RPC request, notification or response.");
    }

    return ValueTask.CompletedTask;
  }

  public async ValueTask DisposeAsync()
  {
    if (Interlocked.Exchange(ref _disposed, 1) != 0)
    {
      return;
    }

    try
    {
      await _lifetime.CancelAsync().ConfigureAwait(false);
      if (Volatile.Read(ref _running) != 0)
      {
        try
        {
          await _idle.Task.WaitAsync(TimeSpan.FromSeconds(3), _options.TimeProvider).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
          // Streams that do not stop are abandoned; the connections are closed with the client.
        }
      }

      await CloseSessionAsync().ConfigureAwait(false);
    }
    finally
    {
      _inbound.Writer.TryComplete();
      ReleaseLastRead();
      while (_inbound.Reader.TryRead(out var item))
      {
        ArrayPool<byte>.Shared.Return(item.Buffer, clearArray: true);
      }

      _lifetime.Dispose();
      if (_ownsClient)
      {
        _http.Dispose();
      }
    }
  }

  // ---- building and running requests -------------------------------------------------------------------------------------------

  private Call NewCall(byte[] body, MessageShape shape)
  {
    var call = new Call(body, shape.Kind, shape.Id, shape.Method ?? string.Empty, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
    if (shape.Kind != MessageKind.Request && shape.Kind != MessageKind.Notification)
    {
      return call;
    }

    // 2026-07-28 requests carry the version in params._meta; that is what makes a message modern (the headers mirror it).
    try
    {
      var wantsHeaders = shape.Method == Methods.ToolsCall && !_toolHeaders.IsEmpty;
      var scan = ScanParams(body);
      call.ModernVersion = scan.Version;
      if (call.ModernVersion is not null)
      {
        call.Name = shape.Method switch
        {
          Methods.ToolsCall or Methods.PromptsGet => scan.Name,
          Methods.ResourcesRead => scan.Uri,
          _ when shape.Method is { } taskMethod && Extensions.Tasks.TasksExtension.IsTaskMethod(taskMethod) => scan.TaskId,
          _ => null,
        };

        if (wantsHeaders && call.Name is not null && _toolHeaders.TryGetValue(call.Name, out var mirrored))
        {
          using var document = JsonDocument.Parse(body);
          if (document.RootElement.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("arguments", out var arguments))
          {
            foreach (var parameter in mirrored)
            {
              if (HeaderMirroring.ValueOf(arguments, parameter) is { } value)
              {
                (call.ParameterHeaders ??= []).Add(("Mcp-Param-" + parameter.Name, value));
              }
            }
          }
        }
      }
    }
    catch (JsonException)
    {
      // The connection wrote it, so it parses; if it somehow does not, the server rejects it.
    }

    return call;
  }

  private readonly record struct ParamsScan(string? Version, string? Name, string? Uri, string? TaskId);

  // Reads the few params members a transport needs (the _meta version and the name, uri or taskId that the headers mirror) with a
  // forward-only reader, so no document is built for every message.
  private static ParamsScan ScanParams(ReadOnlySpan<byte> utf8)
  {
    string? version = null, name = null, uri = null, taskId = null;
    var reader = new Utf8JsonReader(utf8, isFinalBlock: true, new JsonReaderState(new JsonReaderOptions { MaxDepth = 64 }));
    if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
    {
      return default;
    }

    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
    {
      var isParams = reader.ValueTextEquals("params"u8);
      reader.Read();
      if (!isParams || reader.TokenType != JsonTokenType.StartObject)
      {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
          reader.Skip();
        }

        continue;
      }

      while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
      {
        var isMeta = reader.ValueTextEquals("_meta"u8);
        var isName = reader.ValueTextEquals("name"u8);
        var isUri = reader.ValueTextEquals("uri"u8);
        var isTask = reader.ValueTextEquals("taskId"u8);
        reader.Read();
        if (isMeta && reader.TokenType == JsonTokenType.StartObject)
        {
          while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
          {
            var isVersion = reader.ValueTextEquals(WireNames.MetaProtocolVersion);
            reader.Read();
            if (isVersion && reader.TokenType == JsonTokenType.String)
            {
              version = reader.GetString();
            }
            else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
              reader.Skip();
            }
          }
        }
        else if (reader.TokenType == JsonTokenType.String && (isName || isUri || isTask))
        {
          var text = reader.GetString();
          if (isName)
          {
            name = text;
          }
          else if (isUri)
          {
            uri = text;
          }
          else
          {
            taskId = text;
          }
        }
        else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
          reader.Skip();
        }
      }
    }

    return new ParamsScan(version, name, uri, taskId);
  }

  // Starts one request on the caller's thread and returns when it first has to wait (for the network). Sends from several threads are
  // independent exchanges, so there is no reason to hop to the pool first. Dispose waits for the count to reach zero.
  private void StartCall(Call call)
  {
    Interlocked.Increment(ref _running);
    _ = RunTrackedAsync(call);
  }

  private async Task RunTrackedAsync(Call call)
  {
    try
    {
      await RunCallAsync(call).ConfigureAwait(false);
    }
    finally
    {
      if (Interlocked.Decrement(ref _running) == 0 && Volatile.Read(ref _disposed) != 0)
      {
        _idle.TrySetResult();
      }
    }
  }

  // Background work of the channel: the listening stream and the HTTP+SSE session and the listening stream. Dispose waits for the count to reach zero.
  private void Run(Func<Task> work)
  {
    Interlocked.Increment(ref _running);
    _ = Task.Run(async () =>
    {
      try
      {
        await work().ConfigureAwait(false);
      }
      finally
      {
        if (Interlocked.Decrement(ref _running) == 0 && Volatile.Read(ref _disposed) != 0)
        {
          _idle.TrySetResult();
        }
      }
    });
  }

  private async Task RunCallAsync(Call call)
  {
    var ct = call.Cts.Token;
    try
    {
      var reissues = 0;
      var switched = false;
      while (true)
      {
        var outcome = await SendOnceAsync(call, ct).ConfigureAwait(false);
        if (outcome == Outcome.Retry && !switched)
        {
          switched = true;
          continue;
        }

        if (outcome == Outcome.Broken)
        {
          // 2026-07-28 transports: a broken stream is a cancelled request and nothing resumes; the client asks again as a new request.
          // A subscription is not repeated here: the caller opened it and decides whether to open it again.
          if (call.ModernVersion is not null && call.Method != Methods.SubscriptionsListen && reissues < _options.MaxReissues)
          {
            reissues++;
            call.WireId = JsonRpcId.FromString("reissue-" + Interlocked.Increment(ref _reissueCounter).ToString(System.Globalization.CultureInfo.InvariantCulture));
            call.Body = MessageShape.WithId(call.Body, call.WireId);
            call.Responded = false;
            continue;
          }

          await FailAsync(call, "The server's response stream ended before the response arrived.", null, null, ct).ConfigureAwait(false);
        }

        break;
      }
    }
    catch (OperationCanceledException) when (call.Cancelled || Volatile.Read(ref _disposed) != 0)
    {
      // The caller cancelled it, or the client is closing: nobody waits for an answer.
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
      ContextDiagnostics.Publish(DiagnosticKind.HandlerFault, "The HTTP transport failed a request (" + ex.GetType().Name + ").", call.Method, ex);
      try
      {
        await FailAsync(call, "The request failed (" + ex.GetType().Name + ").", null, null, CancellationToken.None).ConfigureAwait(false);
      }
      catch (Exception inner) when (inner is OperationCanceledException or ChannelClosedException or ObjectDisposedException)
      {
        // Closing.
      }
    }
    finally
    {
      _calls.TryRemove(call.Id, out _);
      call.Cts.Dispose();
    }
  }

  private async Task<Outcome> SendOnceAsync(Call call, CancellationToken ct)
  {
    using var request = await BuildAsync(HttpMethod.Post, _postUri, call, null, ct).ConfigureAwait(false);
    HttpResponseMessage response;
    try
    {
      response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }
    catch (HttpRequestException ex)
    {
      await FailAsync(call, "Could not reach the server: " + ex.Message, 0, null, ct).ConfigureAwait(false);
      return Outcome.Done;
    }
    catch (OAuthException ex)
    {
      // A handler that signs requests in (OAuthHttpHandler) could not get a token. Its error says why and never holds a token, so it
      // reaches the caller as it is rather than as a dropped connection.
      await FailAsync(call, ex.Message, 0, null, ct, ex).ConfigureAwait(false);
      return Outcome.Done;
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
      await FailAsync(call, "The server did not answer in time.", 0, null, ct).ConfigureAwait(false);
      return Outcome.Done;
    }

    using (response)
    {
      var status = (int)response.StatusCode;
      if (status is >= 200 and < 300)
      {
        return await HandleSuccessAsync(call, response, status, ct).ConfigureAwait(false);
      }

      return await HandleFailureAsync(call, response, status, ct).ConfigureAwait(false);
    }
  }

  private async Task<Outcome> HandleSuccessAsync(Call call, HttpResponseMessage response, int status, CancellationToken ct)
  {
    if (_mode == TransportMode.Unknown)
    {
      _mode = TransportMode.Streamable;
    }

    if (call.Method == Methods.Initialize && response.Headers.TryGetValues(WireNames.HeaderSessionId, out var ids))
    {
      foreach (var id in ids)
      {
        // 2025-03-26 transports: the id is visible ASCII only.
        if (id.Length is > 0 and <= 512 && IsVisibleAscii(id))
        {
          _sessionId = id;
        }

        break;
      }
    }

    if (call.Kind != MessageKind.Request)
    {
      // Notifications and responses are answered 202 Accepted with no body.
      if (call.Method == Methods.Initialized)
      {
        StartListeningStream();
      }

      return Outcome.Done;
    }

    if (_mode == TransportMode.SseLegacy)
    {
      // 2024-11-05: the POST only acknowledges; the response arrives on the event stream.
      return Outcome.Done;
    }

    var mediaType = response.Content.Headers.ContentType?.MediaType;
    if (status == 200 && string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
    {
      var (body, length) = await ReadBodyAsync(response.Content, _options.MaxResponseBodyBytes, ct).ConfigureAwait(false);
      if (body is null)
      {
        await FailAsync(call, "The server's response is larger than " + _options.MaxResponseBodyBytes + " bytes.", null, null, ct).ConfigureAwait(false);
        return Outcome.Done;
      }

      try
      {
        await DeliverAsync(call, body.AsMemory(0, length), ct).ConfigureAwait(false);
      }
      finally
      {
        ArrayPool<byte>.Shared.Return(body, clearArray: true);
      }

      return Outcome.Done;
    }

    if (status == 200 && string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
    {
      return await ConsumePostStreamAsync(call, response, ct).ConfigureAwait(false);
    }

    await FailAsync(call, "The server answered a request with HTTP " + status + " and content type '" + Printable(mediaType) + "'.", status, null, ct).ConfigureAwait(false);
    return Outcome.Done;
  }

  private async Task<Outcome> HandleFailureAsync(Call call, HttpResponseMessage response, int status, CancellationToken ct)
  {
    var body = await ReadBoundedAsync(response.Content, MaxErrorBodyBytes, ct, truncate: true).ConfigureAwait(false) ?? [];
    if (call.Kind != MessageKind.Request)
    {
      ContextDiagnostics.Publish(DiagnosticKind.InvalidMessage, "The server refused a " + call.Method + " message with HTTP " + status + ".", call.Method);
      return Outcome.Done;
    }

    // A 401 or 403 is about authorization whatever its body says: it becomes an AuthorizationException with the challenge, so a host (or the
    // OAuth client) can authorize and try again.
    if (status is 401 or 403)
    {
      var header = response.Headers.WwwAuthenticate.Count > 0 ? string.Join(", ", response.Headers.WwwAuthenticate) : null;
      await FailAsync(call, status == 401 ? "The server requires authorization (HTTP 401)." : "The server refused the request (HTTP 403).", status, header, ct).ConfigureAwait(false);
      return Outcome.Done;
    }

    // A body that is a JSON-RPC error is the server's own answer (modern servers use 400 and 404 for protocol errors, 2026-07-28
    // transports "Backward Compatibility"): pass it on with the request's id.
    if (TryReadErrorResponse(body))
    {
      if (_mode == TransportMode.Unknown)
      {
        _mode = TransportMode.Streamable;
      }

      await DeliverAsync(call, body, ct).ConfigureAwait(false);
      return Outcome.Done;
    }

    // 2025-03-26 transports, "Backwards compatibility": 400, 404 or 405 without a JSON-RPC error may be an HTTP+SSE server.
    if (status is 400 or 404 or 405 && _mode == TransportMode.Unknown && _options.AllowSseFallback && await TryEnterSseModeAsync(ct).ConfigureAwait(false))
    {
      return Outcome.Retry;
    }

    string? challenge = null;
    if (status is 401 or 403 && response.Headers.WwwAuthenticate.Count > 0)
    {
      challenge = string.Join(", ", response.Headers.WwwAuthenticate);
    }

    var message = status switch
    {
      401 => "The server requires authorization (HTTP 401).",
      403 => "The server refused the request (HTTP 403).",
      404 when _sessionId is not null => "The server no longer knows this session (HTTP 404). Connect again.",
      >= 300 and < 400 => "The server redirected the request (HTTP " + status + "). Redirects are not followed, because a credential could travel with them; use the final URL.",
      _ => "The server answered HTTP " + status + ".",
    };

    await FailAsync(call, message, status, challenge, ct).ConfigureAwait(false);
    return Outcome.Done;
  }

  // Reports a failure for one request. Before anything has worked, a failure that is not a protocol answer ends the connection so that
  // connecting fails with a transport error; afterwards it becomes a JSON-RPC error for that request only.
  private async ValueTask FailAsync(Call call, string message, int? httpStatus, string? challenge, CancellationToken ct, Exception? cause = null)
  {
    if (call.Kind != MessageKind.Request)
    {
      return;
    }

    if (_mode == TransportMode.Unknown && (httpStatus is null or 0 or 401 or 403 or >= 500 || call.Method == Methods.Initialize))
    {
      _inbound.Writer.TryComplete(httpStatus is 401 or 403 ? new HttpAuthFailure(message, httpStatus.Value, challenge) : new IOException(message, cause));
      return;
    }

    var data = ErrorData(httpStatus ?? 0, challenge);
    var error = new JsonRpcErrorResponse(call.Id, new JsonRpcError(HttpErrorCode, message, data));
    var buffer = new ArrayBufferWriter<byte>(256);
    JsonRpcWriter.Write(buffer, error);
    await PushAsync(buffer.WrittenMemory, ct).ConfigureAwait(false);
  }

  private static JsonElement ErrorData(int httpStatus, string? challenge)
  {
    var buffer = new ArrayBufferWriter<byte>(64);
    using (var writer = new Utf8JsonWriter(buffer))
    {
      writer.WriteStartObject();
      writer.WriteBoolean("transport", true);
      writer.WriteNumber("httpStatus", httpStatus);
      if (challenge is not null)
      {
        writer.WriteString("wwwAuthenticate", challenge);
      }

      writer.WriteEndObject();
    }

    using var document = JsonDocument.Parse(buffer.WrittenMemory);
    return document.RootElement.Clone();
  }

  private static bool TryReadErrorResponse(byte[] body)
  {
    if (body.Length == 0)
    {
      return false;
    }

    try
    {
      using var document = JsonDocument.Parse(body);
      return document.RootElement.ValueKind == JsonValueKind.Object
        && document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
        && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number;
    }
    catch (JsonException)
    {
      return false;
    }
  }

  // Hands a message that answers this request to the connection. The id is the request's own: an HTTP error answer may carry a null id,
  // and a re-issued request used a different one on the wire.
  private async ValueTask DeliverAsync(Call call, ReadOnlyMemory<byte> bytes, CancellationToken ct)
  {
    var shape = MessageShape.Inspect(bytes.Span);
    if (shape.Kind == MessageKind.Response)
    {
      call.Responded = true;
      if (call.Method == Methods.Initialize)
      {
        NoteInitializeResult(bytes);
      }

      if (shape.Id != call.Id)
      {
        bytes = MessageShape.WithId(bytes.Span, call.Id);
      }
    }

    await PushAsync(bytes, ct).ConfigureAwait(false);
  }

  // Legacy revisions: after initialize the version in force goes in the MCP-Protocol-Version header of every request (2025-06-18 transports).
  private void NoteInitializeResult(ReadOnlyMemory<byte> bytes)
  {
    try
    {
      using var document = JsonDocument.Parse(bytes);
      if (document.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
        && result.TryGetProperty("protocolVersion", out var version) && version.ValueKind == JsonValueKind.String
        && version.GetString() is { Length: > 0 and <= 32 } text && IsVisibleAscii(text))
      {
        _legacyVersion = text;
      }
    }
    catch (JsonException)
    {
      // The codec reports an unusable result.
    }
  }

  private async ValueTask PushAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
  {
    var rented = ArrayPool<byte>.Shared.Rent(message.Length);
    message.CopyTo(rented);
    try
    {
      await _inbound.Writer.WriteAsync(new Inbound(rented, message.Length), ct).ConfigureAwait(false);
    }
    catch (ChannelClosedException)
    {
      ArrayPool<byte>.Shared.Return(rented, clearArray: true);
    }
    catch
    {
      ArrayPool<byte>.Shared.Return(rented, clearArray: true);
      throw;
    }
  }

  private void ReleaseLastRead()
  {
    if (_lastRead is { } last)
    {
      _lastRead = null;
      ArrayPool<byte>.Shared.Return(last, clearArray: true);
    }
  }

  // ---- cancellation -----------------------------------------------------------------------------------------------------------

  // 2026-07-28 transports, "Cancellation": closing the response stream is the cancellation; no notification is sent. Legacy revisions send
  // notifications/cancelled as a POST, and the stream of the request is dropped as well.
  private void Cancel(byte[] body, MessageShape shape)
  {
    JsonRpcId target = default;
    try
    {
      using var document = JsonDocument.Parse(body);
      if (document.RootElement.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object
        && parameters.TryGetProperty("requestId", out var id))
      {
        JsonRpcId.TryFromElement(id, out target);
      }
    }
    catch (JsonException)
    {
      return;
    }

    if (target.HasValue && _calls.TryGetValue(target, out var running))
    {
      var modern = running.ModernVersion is not null;
      running.Cancelled = true;
      try
      {
        running.Cts.Cancel();
      }
      catch (ObjectDisposedException)
      {
        // It finished meanwhile.
      }

      if (modern)
      {
        return;
      }
    }
    else if (_mode == TransportMode.Streamable && _legacyVersion is null)
    {
      return;
    }

    var notification = NewCall(body, shape);
    StartCall(notification);
  }

  // ---- event streams -----------------------------------------------------------------------------------------------------------

  private async Task<Outcome> ConsumePostStreamAsync(Call call, HttpResponseMessage response, CancellationToken ct)
  {
    var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    using var reader = new SseEventReader(stream, _options.MaxEventBytes, leaveOpen: true);
    var end = await PumpAsync(call, reader, ct).ConfigureAwait(false);

    // 2025-11-25 transports, "Resumability and Redelivery": a stream that ended without the response is resumed with a GET carrying
    // Last-Event-ID, after the wait the server asked for with retry. Only legacy streams have event ids worth resuming.
    var attempts = 0;
    var lastId = reader.LastEventId;
    var retry = reader.RetryMilliseconds;
    while (!call.Responded && end is PumpEnd.Ended or PumpEnd.Idle or PumpEnd.Faulted && call.ModernVersion is null && lastId is not null
      && _mode != TransportMode.SseLegacy && attempts < _options.MaxResumeAttempts)
    {
      attempts++;
      await Task.Delay(RetryDelay(retry), _options.TimeProvider, ct).ConfigureAwait(false);
      using var resumed = await OpenGetStreamAsync(lastId, ct).ConfigureAwait(false);
      if (resumed is null)
      {
        break;
      }

      var resumedStream = await resumed.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
      using var resumedReader = new SseEventReader(resumedStream, _options.MaxEventBytes, leaveOpen: true);
      end = await PumpAsync(call, resumedReader, ct).ConfigureAwait(false);
      if (resumedReader.LastEventId is { } newer && newer != lastId)
      {
        attempts = 0;
      }

      lastId = resumedReader.LastEventId ?? lastId;
      retry = resumedReader.RetryMilliseconds ?? retry;
    }

    if (call.Responded)
    {
      return Outcome.Done;
    }

    if (end == PumpEnd.TooLarge)
    {
      await FailAsync(call, "An event from the server is larger than " + _options.MaxEventBytes + " bytes.", null, null, ct).ConfigureAwait(false);
      return Outcome.Done;
    }

    return Outcome.Broken;
  }

  private TimeSpan RetryDelay(int? serverMilliseconds)
  {
    var delay = serverMilliseconds is { } ms ? TimeSpan.FromMilliseconds(ms) : _options.DefaultRetryDelay;
    return delay > _options.MaxRetryDelay ? _options.MaxRetryDelay : delay;
  }

  // Reads events until the request's response arrives (when there is a call), the stream ends, an event is too large, or nothing arrives
  // for the idle timeout. Each "message" event goes to the connection; other event types are ignored.
  private async Task<PumpEnd> PumpAsync(Call? call, SseEventReader reader, CancellationToken ct)
  {
    using var idle = new CancellationTokenSource(Timeout.InfiniteTimeSpan, _options.TimeProvider);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);
    while (true)
    {
      SseRead read;
      try
      {
        idle.CancelAfter(_options.StreamIdleTimeout);
        read = await reader.ReadAsync(linked.Token).ConfigureAwait(false);
      }
      catch (OperationCanceledException) when (idle.IsCancellationRequested && !ct.IsCancellationRequested)
      {
        return PumpEnd.Idle;
      }
      catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException)
      {
        return PumpEnd.Faulted;
      }

      switch (read.Status)
      {
        case SseReadStatus.EndOfStream:
          return PumpEnd.Ended;
        case SseReadStatus.TooLarge:
          ContextDiagnostics.Publish(DiagnosticKind.InvalidMessage, "An event stream sent an event larger than the limit; the stream was dropped.");
          return PumpEnd.TooLarge;
      }

      if (read.Event.EventName != "message" || read.Event.Data.IsEmpty)
      {
        continue;
      }

      if (call is not null)
      {
        await DeliverAsync(call, read.Event.Data, ct).ConfigureAwait(false);
        if (call.Responded)
        {
          return PumpEnd.Responded;
        }
      }
      else
      {
        await PushAsync(read.Event.Data, ct).ConfigureAwait(false);
      }
    }
  }

  // The standalone GET stream of a legacy session (2025-03-26 transports, "Listening for Messages from the Server").
  private void StartListeningStream()
  {
    if (!_options.OpenListeningStream || _mode == TransportMode.SseLegacy || _legacyVersion is null || Interlocked.Exchange(ref _listening, 1) != 0)
    {
      return;
    }

    Run(RunListeningStreamAsync);
  }

  private async Task RunListeningStreamAsync()
  {
    var ct = _lifetime.Token;
    string? lastId = null;
    int? retry = null;
    var failures = 0;
    try
    {
      while (!ct.IsCancellationRequested && failures <= _options.MaxResumeAttempts)
      {
        using var response = await OpenGetStreamAsync(lastId, ct).ConfigureAwait(false);
        if (response is null)
        {
          // 405 means the server has no stream for the client; other answers are not worth hammering.
          if (lastId is null || ++failures > _options.MaxResumeAttempts)
          {
            return;
          }

          await Task.Delay(RetryDelay(retry), _options.TimeProvider, ct).ConfigureAwait(false);
          continue;
        }

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new SseEventReader(stream, _options.MaxEventBytes, leaveOpen: true);
        var before = lastId;
        var end = await PumpAsync(null, reader, ct).ConfigureAwait(false);
        lastId = reader.LastEventId ?? lastId;
        retry = reader.RetryMilliseconds ?? retry;
        failures = lastId != before ? 0 : failures + 1;
        if (end == PumpEnd.TooLarge)
        {
          return;
        }

        await Task.Delay(RetryDelay(retry), _options.TimeProvider, ct).ConfigureAwait(false);
      }
    }
    catch (OperationCanceledException)
    {
      // Closing.
    }
    catch (Exception ex) when (ex is HttpRequestException or IOException or ChannelClosedException)
    {
      ContextDiagnostics.Publish(DiagnosticKind.HandlerFault, "The listening stream ended (" + ex.GetType().Name + ").");
    }
  }

  // GET with Accept: text/event-stream. Returns the response when it is an event stream, otherwise null (and disposes it).
  private async Task<HttpResponseMessage?> OpenGetStreamAsync(string? lastEventId, CancellationToken ct)
  {
    using var request = await BuildAsync(HttpMethod.Get, _endpoint, null, lastEventId, ct).ConfigureAwait(false);
    HttpResponseMessage response;
    try
    {
      response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is HttpRequestException or OAuthException)
    {
      return null;
    }

    if (response.StatusCode == HttpStatusCode.OK
      && string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
    {
      return response;
    }

    response.Dispose();
    return null;
  }

  // ---- the 2024-11-05 HTTP+SSE transport ---------------------------------------------------------------------------------------

  // 2024-11-05 transports: GET the URL, the first event is "endpoint" with the URI to POST messages to; responses come back as
  // "message" events on this stream. Returns false when the URL is not such a server.
  private async Task<bool> TryEnterSseModeAsync(CancellationToken ct)
  {
    await _modeGate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      if (_mode == TransportMode.SseLegacy)
      {
        return true;
      }

      if (_mode != TransportMode.Unknown)
      {
        return false;
      }

      using var request = await BuildAsync(HttpMethod.Get, _endpoint, null, null, ct).ConfigureAwait(false);
      HttpResponseMessage response;
      try
      {
        using var timeout = new CancellationTokenSource(_options.SseFallbackTimeout, _options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
      }
      catch (Exception ex) when (ex is HttpRequestException or OAuthException or OperationCanceledException && !ct.IsCancellationRequested)
      {
        return false;
      }

      var keep = false;
      try
      {
        if (response.StatusCode != HttpStatusCode.OK
          || !string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
          return false;
        }

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var reader = new SseEventReader(stream, _options.MaxEventBytes, leaveOpen: true);
        Uri? target = null;
        try
        {
          using var timeout = new CancellationTokenSource(_options.SseFallbackTimeout, _options.TimeProvider);
          using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
          while (target is null)
          {
            var read = await reader.ReadAsync(linked.Token).ConfigureAwait(false);
            if (read.Status != SseReadStatus.Event)
            {
              break;
            }

            if (read.Event.EventName == "endpoint")
            {
              target = ResolveEndpoint(Encoding.UTF8.GetString(read.Event.Data.Span));
              break;
            }

            if (read.Event.EventName == "message")
            {
              // A Streamable HTTP server's stream: not the old transport.
              break;
            }
          }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
        {
          if (ct.IsCancellationRequested)
          {
            throw;
          }
        }

        if (target is null)
        {
          reader.Dispose();
          return false;
        }

        _postUri = target;
        _mode = TransportMode.SseLegacy;
        keep = true;
        var owned = response;
        Run(() => RunSseSessionAsync(owned, reader));
        return true;
      }
      finally
      {
        if (!keep)
        {
          response.Dispose();
        }
      }
    }
    finally
    {
      _modeGate.Release();
    }
  }

  // The endpoint event's data is a URI, relative to the stream's URL or absolute. Messages carry whatever credentials the application
  // adds, so a target on another origin is refused.
  private Uri? ResolveEndpoint(string data)
  {
    if (!Uri.TryCreate(_endpoint, data.Trim(), out var target) || !target.IsAbsoluteUri)
    {
      return null;
    }

    var sameOrigin = string.Equals(target.Scheme, _endpoint.Scheme, StringComparison.OrdinalIgnoreCase)
      && string.Equals(target.Host, _endpoint.Host, StringComparison.OrdinalIgnoreCase)
      && target.Port == _endpoint.Port;
    if (!sameOrigin)
    {
      ContextDiagnostics.Publish(DiagnosticKind.InvalidMessage, "The server's endpoint event named another origin; it was refused.");
      return null;
    }

    return target;
  }

  private async Task RunSseSessionAsync(HttpResponseMessage response, SseEventReader reader)
  {
    using (response)
    using (reader)
    {
      try
      {
        await PumpAsync(null, reader, _lifetime.Token).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        return;
      }

      // 2024-11-05 has no resumption: when the stream is gone the session is.
      _inbound.Writer.TryComplete();
    }
  }

  // ---- requests --------------------------------------------------------------------------------------------------------------

  private async ValueTask<HttpRequestMessage> BuildAsync(HttpMethod method, Uri uri, Call? call, string? lastEventId, CancellationToken ct)
  {
    var request = new HttpRequestMessage(method, uri);
    try
    {
      if (call is not null)
      {
        request.Content = new ByteArrayContent(call.Body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
      }
      else
      {
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
      }

      if (call?.ModernVersion is { } modern)
      {
        // 2026-07-28 transports, "Request Metadata": the version, method and name mirror the body.
        request.Headers.TryAddWithoutValidation(WireNames.HeaderProtocolVersion, modern);
        request.Headers.TryAddWithoutValidation(WireNames.HeaderMethod, call.Method);
        if (call.Name is { } name)
        {
          request.Headers.TryAddWithoutValidation(WireNames.HeaderName, HeaderMirroring.Encode(name));
        }

        if (call.ParameterHeaders is { } mirrored)
        {
          foreach (var (header, value) in mirrored)
          {
            request.Headers.TryAddWithoutValidation(header, value);
          }
        }
      }
      else
      {
        if (_sessionId is { } session && call?.Method != Methods.Initialize)
        {
          request.Headers.TryAddWithoutValidation(WireNames.HeaderSessionId, session);
        }

        if (_legacyVersion is { } version && call?.Method != Methods.Initialize)
        {
          request.Headers.TryAddWithoutValidation(WireNames.HeaderProtocolVersion, version);
        }
      }

      if (lastEventId is not null)
      {
        request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
      }

      if (_options.Headers is { } extra)
      {
        foreach (var (name, value) in extra)
        {
          request.Headers.TryAddWithoutValidation(name, value);
        }
      }

      if (_options.AuthorizeRequest is { } authorize)
      {
        await authorize(request, ct).ConfigureAwait(false);
      }

      return request;
    }
    catch
    {
      request.Dispose();
      throw;
    }
  }

  // 2025-03-26 transports, "Session Management": a client that is done SHOULD DELETE the session; a 405 means the server does not allow it.
  private async ValueTask CloseSessionAsync()
  {
    if (_sessionId is null || _mode != TransportMode.Streamable)
    {
      return;
    }

    try
    {
      using var timeout = new CancellationTokenSource(_options.CloseTimeout, _options.TimeProvider);
      using var request = await BuildAsync(HttpMethod.Delete, _endpoint, null, null, timeout.Token).ConfigureAwait(false);
      request.Headers.Remove("Accept");
      using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is HttpRequestException or OAuthException or OperationCanceledException or ObjectDisposedException)
    {
      // Best effort: the server drops idle sessions anyway.
    }
  }

  // ---- helpers ---------------------------------------------------------------------------------------------------------------

  // Reads a whole body up to a bound into a pooled buffer the caller returns. A null buffer means the body is larger than the bound.
  private static async Task<(byte[]? Buffer, int Length)> ReadBodyAsync(HttpContent content, int limit, CancellationToken ct)
  {
    var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    var announced = content.Headers.ContentLength;
    var rented = ArrayPool<byte>.Shared.Rent(announced is > 0 and <= 16 * 1024 ? (int)announced.Value + 1 : Math.Min(limit + 1, 16 * 1024));
    var length = 0;
    try
    {
      while (true)
      {
        if (length == rented.Length)
        {
          if (length > limit)
          {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            return (null, 0);
          }

          var bigger = ArrayPool<byte>.Shared.Rent((int)Math.Min((long)rented.Length * 2, (long)limit + 1));
          Buffer.BlockCopy(rented, 0, bigger, 0, length);
          ArrayPool<byte>.Shared.Return(rented, clearArray: true);
          rented = bigger;
        }

        var room = Math.Min(rented.Length, limit + 1) - length;
        if (room <= 0)
        {
          ArrayPool<byte>.Shared.Return(rented, clearArray: true);
          return (null, 0);
        }

        int read;
        try
        {
          read = await stream.ReadAsync(rented.AsMemory(length, room), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
          ArrayPool<byte>.Shared.Return(rented, clearArray: true);
          return (null, 0);
        }

        if (read == 0)
        {
          return (rented, length);
        }

        length += read;
      }
    }
    catch
    {
      ArrayPool<byte>.Shared.Return(rented, clearArray: true);
      throw;
    }
  }

  // Reads a body up to a bound. Returns null when it is larger (or the truncated bytes with truncate).
  private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int limit, CancellationToken ct, bool truncate = false)
  {
    var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    var rented = ArrayPool<byte>.Shared.Rent(Math.Min(limit + 1, 16 * 1024));
    var length = 0;
    try
    {
      while (true)
      {
        if (length == rented.Length)
        {
          if (length > limit)
          {
            break;
          }

          var bigger = ArrayPool<byte>.Shared.Rent((int)Math.Min((long)rented.Length * 2, (long)limit + 1));
          Buffer.BlockCopy(rented, 0, bigger, 0, length);
          ArrayPool<byte>.Shared.Return(rented, clearArray: true);
          rented = bigger;
        }

        var room = Math.Min(rented.Length, limit + 1) - length;
        if (room <= 0)
        {
          break;
        }

        int read;
        try
        {
          read = await stream.ReadAsync(rented.AsMemory(length, room), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
          return truncate ? rented.AsSpan(0, length).ToArray() : null;
        }

        if (read == 0)
        {
          return rented.AsSpan(0, length).ToArray();
        }

        length += read;
      }

      return truncate ? rented.AsSpan(0, Math.Min(length, limit)).ToArray() : null;
    }
    finally
    {
      ArrayPool<byte>.Shared.Return(rented, clearArray: true);
    }
  }

  private static bool IsVisibleAscii(string text)
  {
    foreach (var c in text)
    {
      if (c is < (char)0x21 or > (char)0x7E)
      {
        return false;
      }
    }

    return true;
  }

  private static string Printable(string? text)
  {
    if (text is null)
    {
      return "none";
    }

    var builder = new StringBuilder(Math.Min(text.Length, 60));
    foreach (var c in text.Length > 60 ? text[..60] : text)
    {
      builder.Append(c is >= ' ' and <= '~' ? c : '?');
    }

    return builder.ToString();
  }

  private sealed class Call(byte[] body, MessageKind kind, JsonRpcId id, string method, CancellationTokenSource cts)
  {
    internal byte[] Body { get; set; } = body;

    internal MessageKind Kind { get; } = kind;

    /// <summary>The id the connection knows the request by.</summary>
    internal JsonRpcId Id { get; } = id;

    /// <summary>The id on the wire; differs from <see cref="Id"/> after a re-issue.</summary>
    internal JsonRpcId WireId { get; set; } = id;

    internal string Method { get; } = method;

    internal CancellationTokenSource Cts { get; } = cts;

    internal volatile bool Cancelled;

    internal volatile bool Responded;

    /// <summary>The 2026-07-28 version from <c>_meta</c>, or null for a legacy message.</summary>
    internal string? ModernVersion { get; set; }

    /// <summary>The value of <c>Mcp-Name</c> before encoding.</summary>
    internal string? Name { get; set; }

    internal List<(string Header, string Value)>? ParameterHeaders { get; set; }
  }
}

/// <summary>A 401 or 403 that ended the connection before negotiation finished; the client turns it into an <see cref="AuthorizationException"/>.</summary>
internal sealed class HttpAuthFailure(string message, int status, string? challenge) : IOException(message)
{
  internal int Status => status;

  internal string? Challenge => challenge;
}
