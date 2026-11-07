using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Sse;
using RecursiveAI.OneModelContext.Wire;

namespace RecursiveAI.OneModelContext.Hosting;

/// <summary>
/// The Streamable HTTP transport for a <see cref="ContextServer"/>, written against <see cref="IHttpExchange"/> so that the built-in
/// listener, ASP.NET Core and tests all use the same code. One endpoint answers both protocol eras on one path.
/// </summary>
/// <remarks>
/// <para>Modern (2026-07-28): every POST is a request; the answer is one JSON object or an event stream scoped to that request. The
/// <c>MCP-Protocol-Version</c>, <c>Mcp-Method</c> and <c>Mcp-Name</c> headers (and <c>Mcp-Param-*</c> for <c>x-mcp-header</c> parameters) must
/// agree with the body or the request gets 400 with error -32020. There are no sessions, no GET stream and no resumption; closing a
/// response stream cancels its request.</para>
/// <para>Legacy (2025-11-25 and earlier): <c>initialize</c> creates a session, whose id travels in <c>Mcp-Session-Id</c>. The session lives in
/// this instance's memory, so a deployment with several instances must pin a client to one. Streams carry event ids and can be resumed
/// with <c>Last-Event-ID</c>; the server may send requests to the client on them.</para>
/// <para>Safe defaults: <c>Host</c> and <c>Origin</c> are validated against loopback names and the configured allow-lists (DNS rebinding,
/// Streamable HTTP "Security Warning"), bodies and sessions are bounded and every wait has a timeout. The endpoint does not
/// authenticate; the host does, and passes the caller in <see cref="IHttpExchange.User"/>.</para>
/// </remarks>
public sealed class StreamableHttpEndpoint : IDisposable
{
  private readonly ContextServer _server;
  private readonly StreamableHttpOptions _options;
  private readonly TimeProvider _time;
  private readonly ConcurrentDictionary<string, HttpSession> _sessions = new(StringComparer.Ordinal);
  private readonly CancellationTokenSource _shutdown = new();
  private long _lastSweep;

  /// <summary>Creates the endpoint for a server.</summary>
  /// <param name="server">The server whose requests this endpoint carries.</param>
  /// <param name="options">Limits and allow-lists; <c>null</c> for the defaults.</param>
  public StreamableHttpEndpoint(ContextServer server, StreamableHttpOptions? options = null)
  {
    ArgumentNullException.ThrowIfNull(server);
    _server = server;
    _options = options ?? new StreamableHttpOptions();
    _time = server.Options.TimeProvider;
    _lastSweep = _time.GetTimestamp();
  }

  /// <summary>How many legacy sessions this instance holds.</summary>
  public int SessionCount => _sessions.Count;

  /// <summary>
  /// Handles one HTTP exchange: validates it, runs the JSON-RPC message through the server and writes the answer. It never throws on bad
  /// input; a failure becomes an HTTP status and a JSON-RPC error. It returns when the response is finished, which for an event
  /// stream is when the stream ends.
  /// </summary>
  /// <param name="exchange">The request and response.</param>
  public async ValueTask HandleAsync(IHttpExchange exchange)
  {
    ArgumentNullException.ThrowIfNull(exchange);
    var call = new Call(exchange);
    try
    {
      await RouteAsync(call).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      // The client left or the server is shutting down: nothing more to say.
    }
    catch (Exception ex)
    {
      ContextDiagnostics.Publish(DiagnosticKind.HandlerFault, "The HTTP endpoint failed with " + ex.GetType().Name + ".", null, ex);
      if (!call.ResponseStarted)
      {
        try
        {
          await WriteErrorAsync(call, 500, JsonRpcErrorCodes.InternalError, "Internal error.").ConfigureAwait(false);
        }
        catch (Exception inner) when (inner is IOException or ObjectDisposedException or InvalidOperationException)
        {
          // The response is already unusable.
        }
      }
    }
  }

  /// <summary>Ends every session and cancels every running handler.</summary>
  public void Dispose()
  {
    try
    {
      _shutdown.Cancel();
    }
    catch (ObjectDisposedException)
    {
      // Already disposed.
    }

    foreach (var session in _sessions.Values)
    {
      session.Dispose();
    }

    _sessions.Clear();
  }

  private async ValueTask RouteAsync(Call call)
  {
    if (!HostAllowed(call.Request) || !OriginAllowed(call.Request))
    {
      // Streamable HTTP "Security Warning": an Origin that is present and invalid gets 403. A Host we do not serve gets the same.
      await WriteErrorAsync(call, 403, JsonRpcErrorCodes.InvalidRequest, "Forbidden: the Host or Origin header is not allowed.").ConfigureAwait(false);
      return;
    }

    switch (call.Request.Method)
    {
      case "POST":
        await PostAsync(call).ConfigureAwait(false);
        break;
      case "GET":
        await GetAsync(call).ConfigureAwait(false);
        break;
      case "DELETE":
        await DeleteAsync(call).ConfigureAwait(false);
        break;
      default:
        call.Response.SetHeader("Allow", "POST, GET, DELETE");
        await WriteErrorAsync(call, 405, JsonRpcErrorCodes.InvalidRequest, "Method not allowed.").ConfigureAwait(false);
        break;
    }
  }

  // ---- Host and Origin -------------------------------------------------------------------------------------------------------

  private bool HostAllowed(IHttpRequest request)
  {
    if (!_options.ValidateHost || request.Headers.GetFirst("Host") is not { } host)
    {
      return true;
    }

    return HostMatches(host, _options.AllowedHosts);
  }

  private static bool HostMatches(string hostHeader, IReadOnlyList<string> allowed)
  {
    SplitHostPort(hostHeader, out var host, out _);
    if (IsLoopbackName(host))
    {
      return true;
    }

    foreach (var entry in allowed)
    {
      if (entry == "*" || string.Equals(entry, hostHeader, StringComparison.OrdinalIgnoreCase))
      {
        return true;
      }

      // An entry without a port matches the host on any port.
      SplitHostPort(entry, out var entryHost, out var entryPort);
      if (entryPort is null && string.Equals(entryHost, host, StringComparison.OrdinalIgnoreCase))
      {
        return true;
      }
    }

    return false;
  }

  private bool OriginAllowed(IHttpRequest request)
  {
    var origins = request.Headers.GetAll("Origin");
    if (origins.Count == 0)
    {
      return true;
    }

    if (origins.Count > 1)
    {
      return false;
    }

    var origin = origins[0];
    foreach (var entry in _options.AllowedOrigins)
    {
      if (entry == "*" || string.Equals(entry, origin, StringComparison.OrdinalIgnoreCase))
      {
        return true;
      }
    }

    // "null" (a sandboxed or file page) and anything that is not an absolute http(s) origin fall through to a refusal.
    return _options.AllowLoopbackOrigins
      && Uri.TryCreate(origin, UriKind.Absolute, out var uri)
      && uri.Scheme is "http" or "https"
      && uri.AbsolutePath == "/"
      && origin.AsSpan().IndexOfAny('?', '#') < 0
      && !origin.EndsWith('/')
      && IsLoopbackName(uri.IdnHost);
  }

  private static void SplitHostPort(string value, out string host, out string? port)
  {
    value = value.Trim();
    if (value.StartsWith('['))
    {
      var close = value.IndexOf(']', StringComparison.Ordinal);
      if (close > 0)
      {
        host = value[..(close + 1)];
        port = close + 2 < value.Length && value[close + 1] == ':' ? value[(close + 2)..] : null;
        return;
      }
    }

    var colon = value.LastIndexOf(':');
    if (colon > 0 && value.IndexOf(':', StringComparison.Ordinal) == colon)
    {
      host = value[..colon];
      port = value[(colon + 1)..];
      return;
    }

    host = value;
    port = null;
  }

  private static bool IsLoopbackName(string host)
  {
    host = host.TrimEnd('.');
    if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
    {
      return true;
    }

    var bare = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
    return IPAddress.TryParse(bare, out var address) && IPAddress.IsLoopback(address);
  }

  // ---- POST ------------------------------------------------------------------------------------------------------------------

  private async ValueTask PostAsync(Call call)
  {
    var request = call.Request;
    if (request.Headers.GetFirst("Content-Type") is { } contentType
      && !contentType.AsSpan().TrimStart().StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
    {
      await WriteErrorAsync(call, 415, JsonRpcErrorCodes.InvalidRequest, "Unsupported media type: send application/json.").ConfigureAwait(false);
      return;
    }

    var accept = string.Join(',', request.Headers.GetAll("Accept"));
    var acceptsAny = accept.Length == 0 || accept.Contains("*/*", StringComparison.Ordinal);
    var acceptsSse = acceptsAny || accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) || accept.Contains("text/*", StringComparison.Ordinal);
    var acceptsJson = acceptsAny || accept.Contains("application/json", StringComparison.OrdinalIgnoreCase) || accept.Contains("application/*", StringComparison.Ordinal);
    if (!acceptsSse && !acceptsJson)
    {
      await WriteErrorAsync(call, 406, JsonRpcErrorCodes.InvalidRequest, "Not acceptable: the client must accept application/json or text/event-stream.").ConfigureAwait(false);
      return;
    }

    var body = await ReadBodyAsync(call).ConfigureAwait(false);
    if (body is null)
    {
      return;
    }

    var read = JsonRpcReader.Read(body.WrittenMemory, _server.Options.ReaderOptions);
    switch (read.Kind)
    {
      case JsonRpcReadKind.Invalid:
        await WriteMessageAsync(call, 400, read.Error!).ConfigureAwait(false);
        return;
      case JsonRpcReadKind.Batch:
        await PostBatchAsync(call, body.WrittenMemory).ConfigureAwait(false);
        return;
    }

    // An unsupported version header is refused with 400 and -32022 (2026-07-28 Streamable HTTP, "Protocol Version Header"). The error carries the
    // request's id when the body is a request, because every error answers the request it belongs to (JSON-RPC 2.0 section 5).
    if (request.Headers.GetFirst(WireNames.HeaderProtocolVersion) is { } versionHeader && !IsSupported(versionHeader))
    {
      await WriteProtocolErrorAsync(call, 400, UnsupportedVersion(versionHeader), (read.Message as JsonRpcRequest)?.Id ?? default).ConfigureAwait(false);
      return;
    }

    switch (read.Message)
    {
      case JsonRpcRequest rpcRequest:
        await PostRequestAsync(call, rpcRequest, acceptsSse).ConfigureAwait(false);
        break;
      case JsonRpcNotification notification:
        await PostNotificationAsync(call, notification).ConfigureAwait(false);
        break;
      default:
        await PostResponseAsync(call, read.Message!).ConfigureAwait(false);
        break;
    }
  }

  private async ValueTask PostRequestAsync(Call call, JsonRpcRequest rpcRequest, bool acceptsSse)
  {
    var sessionHeader = call.Request.Headers.GetFirst(WireNames.HeaderSessionId);

    // 2026-07-28 Streamable HTTP, "Protocol Version Header": a method the server does not implement is 404 with -32601. The handshake was
    // removed in that revision, so an initialize that names it is such a method; a legacy initialize names no version header yet.
    if (rpcRequest.Method == Methods.Initialize && call.Request.Headers.GetFirst(WireNames.HeaderProtocolVersion) == ProtocolVersions.V20260728)
    {
      await WriteErrorAsync(call, 404, JsonRpcErrorCodes.MethodNotFound, "Method not found: initialize was removed in 2026-07-28; send server/discover.", rpcRequest.Id).ConfigureAwait(false);
      return;
    }

    var era = CodecSelector.Select(rpcRequest.Method, rpcRequest.Params, new ServerSessionState(), sessionHeader is not null);
    HttpSession? session = null;
    var created = false;
    ServerSessionState state;
    if (era == ProtocolEra.Modern)
    {
      // 2026-07-28: no sessions. A stray Mcp-Session-Id is ignored, never echoed (Streamable HTTP, "Earlier Streamable HTTP Revisions").
      if (ModernHeaderError(call.Request, rpcRequest) is { } mismatch)
      {
        await WriteProtocolErrorAsync(call, 400, mismatch, rpcRequest.Id).ConfigureAwait(false);
        return;
      }

      state = new ServerSessionState();
    }
    else if (rpcRequest.Method == Methods.Initialize)
    {
      Sweep();
      if (_sessions.Count >= _options.MaxSessions)
      {
        call.Response.SetHeader("Retry-After", "5");
        await WriteErrorAsync(call, 503, JsonRpcErrorCodes.ServerBusy, "Too many sessions.", rpcRequest.Id).ConfigureAwait(false);
        return;
      }

      session = new HttpSession(_options, _time, _server.Options.RequestTimeout);
      created = true;
      state = session.State;
    }
    else if (sessionHeader is null)
    {
      // Legacy requests other than initialize need the session id (2025-11-25 Streamable HTTP, "Session Management", item 2).
      await WriteErrorAsync(call, 400, JsonRpcErrorCodes.InvalidRequest, "Bad request: the Mcp-Session-Id header is required.", rpcRequest.Id).ConfigureAwait(false);
      return;
    }
    else if (!_sessions.TryGetValue(sessionHeader, out session))
    {
      await WriteErrorAsync(call, 404, JsonRpcErrorCodes.InvalidRequest, "Not found: the session does not exist or has ended.", rpcRequest.Id).ConfigureAwait(false);
      return;
    }
    else
    {
      state = session.State;
    }

    session?.Touch();
    var legacy = era == ProtocolEra.Legacy;
    var channel = new PostChannel(this, call, session, legacy, acceptsSse, created);
    CancellationTokenSource handlerCts = legacy
      ? session!.RegisterHandler(rpcRequest.Id, _shutdown.Token)
      : CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);

    // Modern: the client closing the stream is the cancellation (Streamable HTTP, "Cancellation"). Legacy: a dropped connection is not
    // a cancellation (2025-11-25, "Sending Messages to the Server"); the handler runs on and the client may resume the stream.
    var abortRegistration = call.Exchange.Aborted.Register(() =>
    {
      if (!legacy)
      {
        try
        {
          handlerCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
          // The handler finished first.
        }
      }

      channel.Released.TrySetResult();
    });

    var work = Task.Run(async () =>
    {
      JsonRpcMessage? response = null;
      try
      {
        using (HttpRequestScope.Enter(new HttpRequestScope(call.Exchange, channel.CloseConnectionAsync)))
        {
          response = await _server.DispatchRequestAsync(rpcRequest, state, channel, call.Exchange.User, session is not null, handlerCts.Token).ConfigureAwait(false);
        }

        if (created && response is JsonRpcResultResponse && session is not null)
        {
          _sessions[session.Id] = session;
          session.Attachment = _server.Attach(session.State, session);
        }
        else if (created)
        {
          session?.Dispose();
        }
      }
      catch (OperationCanceledException)
      {
        response = null;
      }
      catch (Exception ex)
      {
        ContextDiagnostics.Publish(DiagnosticKind.HandlerFault, "The HTTP endpoint failed with " + ex.GetType().Name + ".", rpcRequest.Method, ex);
        response = new JsonRpcErrorResponse(rpcRequest.Id, new JsonRpcError(JsonRpcErrorCodes.InternalError, "Internal error."));
      }

      try
      {
        await channel.FinishAsync(response, StatusFor(response, era, HasRequiredMeta(rpcRequest.Params))).ConfigureAwait(false);
      }
      catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
      {
        // The connection broke while the answer was being written.
      }
      finally
      {
        session?.UnregisterHandler(rpcRequest.Id);
        handlerCts.Dispose();
      }
    });

    try
    {
      await Task.WhenAny(work, channel.Released.Task).ConfigureAwait(false);
    }
    finally
    {
      await abortRegistration.DisposeAsync().ConfigureAwait(false);
    }

    if (!work.IsCompleted)
    {
      // The connection ended while the handler is still running (legacy). Stop writing to it; the answer is kept for a resume.
      await channel.ReleaseAsync().ConfigureAwait(false);
    }
    else
    {
      await work.ConfigureAwait(false);
    }
  }

  private async ValueTask PostNotificationAsync(Call call, JsonRpcNotification notification)
  {
    var sessionHeader = call.Request.Headers.GetFirst(WireNames.HeaderSessionId);

    var era = CodecSelector.Select(notification.Method, notification.Params, new ServerSessionState(), sessionHeader is not null);
    ServerSessionState state;
    if (era == ProtocolEra.Modern)
    {
      state = new ServerSessionState();
    }
    else if (sessionHeader is null)
    {
      await WriteErrorAsync(call, 400, JsonRpcErrorCodes.InvalidRequest, "Bad request: the Mcp-Session-Id header is required.").ConfigureAwait(false);
      return;
    }
    else if (!_sessions.TryGetValue(sessionHeader, out var session))
    {
      await WriteErrorAsync(call, 404, JsonRpcErrorCodes.InvalidRequest, "Not found: the session does not exist or has ended.").ConfigureAwait(false);
      return;
    }
    else
    {
      session.Touch();
      state = session.State;
      if (notification.Method == Methods.NotificationsCancelled
        && notification.Params.ValueKind == JsonValueKind.Object
        && notification.Params.TryGetProperty("requestId", out var idElement)
        && JsonRpcId.TryFromElement(idElement, out var id))
      {
        session.CancelHandler(id);
      }
    }

    await _server.DispatchNotificationAsync(notification, state, call.Exchange.Aborted).ConfigureAwait(false);
    await WriteAcceptedAsync(call).ConfigureAwait(false);
  }

  // A JSON-RPC response from the client answers a request the server sent on a legacy stream.
  private async ValueTask PostResponseAsync(Call call, JsonRpcMessage response)
  {
    var sessionHeader = call.Request.Headers.GetFirst(WireNames.HeaderSessionId);
    if (sessionHeader is null)
    {
      // 2026-07-28: clients never send responses; and a legacy response needs its session.
      await WriteErrorAsync(call, 400, JsonRpcErrorCodes.InvalidRequest, "Bad request: unexpected JSON-RPC response.").ConfigureAwait(false);
      return;
    }

    if (!_sessions.TryGetValue(sessionHeader, out var session))
    {
      await WriteErrorAsync(call, 404, JsonRpcErrorCodes.InvalidRequest, "Not found: the session does not exist or has ended.").ConfigureAwait(false);
      return;
    }

    session.Touch();
    if (!session.TryResolve(response))
    {
      await WriteErrorAsync(call, 400, JsonRpcErrorCodes.InvalidRequest, "Bad request: no request is waiting for that response.").ConfigureAwait(false);
      return;
    }

    await WriteAcceptedAsync(call).ConfigureAwait(false);
  }

  // 2025-03-26 only: a POST body may be a JSON-RPC batch (the codec decides; later revisions removed batches).
  private async ValueTask PostBatchAsync(Call call, ReadOnlyMemory<byte> body)
  {
    var sessionHeader = call.Request.Headers.GetFirst(WireNames.HeaderSessionId);
    if (sessionHeader is null || !_sessions.TryGetValue(sessionHeader, out var session))
    {
      await WriteErrorAsync(call, sessionHeader is null ? 400 : 404, JsonRpcErrorCodes.InvalidRequest, "Invalid request: batches need a session.").ConfigureAwait(false);
      return;
    }

    session.Touch();
    var answer = await _server.HandleMessageAsync(Encoding.UTF8.GetString(body.Span), session.State, sink: null, call.Exchange.User, call.Exchange.Aborted).ConfigureAwait(false);
    if (answer is null)
    {
      await WriteAcceptedAsync(call).ConfigureAwait(false);
      return;
    }

    await WriteBytesAsync(call, answer.StartsWith('[') ? 200 : 400, Encoding.UTF8.GetBytes(answer)).ConfigureAwait(false);
  }

  // ---- GET and DELETE (legacy sessions) -------------------------------------------------------------------------------------

  private async ValueTask GetAsync(Call call)
  {
    // 2026-07-28 removed the GET stream (405). On legacy sessions GET opens the standalone stream or resumes one.
    var sessionHeader = call.Request.Headers.GetFirst(WireNames.HeaderSessionId);
    if (sessionHeader is null)
    {
      call.Response.SetHeader("Allow", "POST");
      await WriteErrorAsync(call, 405, JsonRpcErrorCodes.InvalidRequest, "Method not allowed: there is no GET stream without a session.").ConfigureAwait(false);
      return;
    }

    if (!_sessions.TryGetValue(sessionHeader, out var session))
    {
      await WriteErrorAsync(call, 404, JsonRpcErrorCodes.InvalidRequest, "Not found: the session does not exist or has ended.").ConfigureAwait(false);
      return;
    }

    var accept = string.Join(',', call.Request.Headers.GetAll("Accept"));
    if (!accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) && !accept.Contains("*/*", StringComparison.Ordinal))
    {
      await WriteErrorAsync(call, 406, JsonRpcErrorCodes.InvalidRequest, "Not acceptable: a GET stream needs Accept: text/event-stream.").ConfigureAwait(false);
      return;
    }

    session.Touch();
    SseStream stream;
    long after = -1;
    if (call.Request.Headers.GetFirst("Last-Event-ID") is { Length: > 0 } lastEventId)
    {
      // Resume the stream the id belongs to, and only that one (2025-11-25 Streamable HTTP, "Resumability and Redelivery", item 2).
      if (!SseStream.TryParseEventId(lastEventId, out var streamId, out after) || !session.TryGetStream(streamId, out stream!))
      {
        await WriteErrorAsync(call, 400, JsonRpcErrorCodes.InvalidRequest, "Bad request: unknown Last-Event-ID.").ConfigureAwait(false);
        return;
      }
    }
    else
    {
      stream = session.NewStream(standalone: true);
    }

    var response = call.Response;
    response.StatusCode = 200;
    ApplySseHeaders(response);
    call.ResponseStarted = true;
    await response.Body.FlushAsync(call.Exchange.Aborted).ConfigureAwait(false);
    using var writer = new SseWriter(response.Body);
    var connection = await stream.AttachAsync(writer, after, call.Exchange.Aborted).ConfigureAwait(false);
    try
    {
      using var linked = CancellationTokenSource.CreateLinkedTokenSource(call.Exchange.Aborted, _shutdown.Token, session.Closed);
      await connection.Closed.WaitAsync(linked.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      // The client disconnected, the session ended or the server is stopping.
    }
    finally
    {
      await stream.DetachAsync(connection).ConfigureAwait(false);
    }
  }

  private async ValueTask DeleteAsync(Call call)
  {
    var sessionHeader = call.Request.Headers.GetFirst(WireNames.HeaderSessionId);
    if (sessionHeader is null)
    {
      call.Response.SetHeader("Allow", "POST");
      await WriteErrorAsync(call, 405, JsonRpcErrorCodes.InvalidRequest, "Method not allowed: there are no sessions to end.").ConfigureAwait(false);
      return;
    }

    if (!_sessions.TryRemove(sessionHeader, out var session))
    {
      await WriteErrorAsync(call, 404, JsonRpcErrorCodes.InvalidRequest, "Not found: the session does not exist or has ended.").ConfigureAwait(false);
      return;
    }

    session.Dispose();
    call.Response.StatusCode = 200;
    call.ResponseStarted = true;
    await call.Response.Body.FlushAsync(call.Exchange.Aborted).ConfigureAwait(false);
  }

  // ---- Header checks (2026-07-28 Streamable HTTP, "Request Metadata" and "Server Validation") --------------------------------

  private ProtocolException? ModernHeaderError(IHttpRequest request, JsonRpcRequest rpcRequest)
  {
    var headers = request.Headers;
    var versionHeaders = headers.GetAll(WireNames.HeaderProtocolVersion);
    if (versionHeaders.Count != 1)
    {
      return new HeaderMismatchException("Header mismatch: exactly one " + WireNames.HeaderProtocolVersion + " header is required.");
    }

    var metaVersion = ReadMetaVersion(rpcRequest.Params);
    if (metaVersion is not null && !string.Equals(metaVersion, versionHeaders[0], StringComparison.Ordinal))
    {
      return new HeaderMismatchException("Header mismatch: " + WireNames.HeaderProtocolVersion + " does not match the protocol version in the request body.");
    }

    var methodHeaders = headers.GetAll(WireNames.HeaderMethod);
    if (methodHeaders.Count != 1)
    {
      return new HeaderMismatchException("Header mismatch: exactly one " + WireNames.HeaderMethod + " header is required.");
    }

    if (!HeaderValue.TryDecode(methodHeaders[0], out var method) || !string.Equals(method, rpcRequest.Method, StringComparison.Ordinal))
    {
      return new HeaderMismatchException("Header mismatch: " + WireNames.HeaderMethod + " does not match the method in the request body.");
    }

    var nameProperty = rpcRequest.Method switch
    {
      Methods.ToolsCall or Methods.PromptsGet => "name",
      Methods.ResourcesRead => "uri",
      _ => null,
    };

    if (nameProperty is not null)
    {
      var nameHeaders = headers.GetAll(WireNames.HeaderName);
      if (nameHeaders.Count != 1)
      {
        return new HeaderMismatchException("Header mismatch: exactly one " + WireNames.HeaderName + " header is required for " + rpcRequest.Method + ".");
      }

      if (rpcRequest.Params.ValueKind == JsonValueKind.Object
        && rpcRequest.Params.TryGetProperty(nameProperty, out var bodyName)
        && bodyName.ValueKind == JsonValueKind.String
        && (!HeaderValue.TryDecode(nameHeaders[0], out var name) || !string.Equals(name, bodyName.GetString(), StringComparison.Ordinal)))
      {
        return new HeaderMismatchException("Header mismatch: " + WireNames.HeaderName + " does not match the request body.");
      }
    }

    if (rpcRequest.Method == Methods.ToolsCall
      && rpcRequest.Params.ValueKind == JsonValueKind.Object
      && rpcRequest.Params.TryGetProperty("name", out var toolName)
      && toolName.ValueKind == JsonValueKind.String
      && _server.Tools.TryGet(toolName.GetString()!, out var tool, out _))
    {
      rpcRequest.Params.TryGetProperty("arguments", out var arguments);
      return HeaderValue.CheckParameterHeaders(headers, tool.InputSchema, arguments);
    }

    return null;
  }

  private static string? ReadMetaVersion(JsonElement parameters) =>
    parameters.ValueKind == JsonValueKind.Object
    && parameters.TryGetProperty("_meta", out var meta)
    && meta.ValueKind == JsonValueKind.Object
    && meta.TryGetProperty(WireNames.MetaProtocolVersion, out var version)
    && version.ValueKind == JsonValueKind.String
      ? version.GetString()
      : null;

  // SEP-2575: a request missing the per-request protocol fields is rejected with 400 (the suite checks both fields).
  private static bool HasRequiredMeta(JsonElement parameters) =>
    ReadMetaVersion(parameters) is not null
    && parameters.GetProperty("_meta").TryGetProperty(WireNames.MetaClientCapabilities, out var capabilities)
    && capabilities.ValueKind == JsonValueKind.Object;

  private bool IsSupported(string version)
  {
    foreach (var supported in _server.SupportedVersions)
    {
      if (string.Equals(supported, version, StringComparison.Ordinal))
      {
        return true;
      }
    }

    return false;
  }

  private ProtocolException UnsupportedVersion(string requested)
  {
    var data = new UnsupportedProtocolVersionData { Supported = [.. _server.SupportedVersions], Requested = requested };
    return new ProtocolException(
      ProtocolErrorCodes.UnsupportedProtocolVersion,
      "Unsupported protocol version.",
      JsonSerializer.SerializeToElement(data, WireJsonContext.Default.UnsupportedProtocolVersionData));
  }

  // Envelope errors are 400; an unknown method on a modern request is 404 so that it can be told from a legacy server's 404
  // (2026-07-28 Streamable HTTP, "Protocol Version Header"). Everything else a handler says stays a 200 with the error in the body.
  private static int StatusFor(JsonRpcMessage? response, ProtocolEra era, bool hadRequiredMeta)
  {
    if (response is not JsonRpcErrorResponse error)
    {
      return 200;
    }

    return error.Error.Code switch
    {
      JsonRpcErrorCodes.ParseError or JsonRpcErrorCodes.InvalidRequest => 400,
      ProtocolErrorCodes.HeaderMismatch or ProtocolErrorCodes.MissingRequiredClientCapability or ProtocolErrorCodes.UnsupportedProtocolVersion => 400,
      JsonRpcErrorCodes.MethodNotFound when era == ProtocolEra.Modern => 404,
      JsonRpcErrorCodes.InvalidParams when era == ProtocolEra.Modern && !hadRequiredMeta => 400,
      _ => 200,
    };
  }

  // ---- Sessions --------------------------------------------------------------------------------------------------------------

  private void Sweep()
  {
    var now = _time.GetTimestamp();
    if (_time.GetElapsedTime(_lastSweep, now) < TimeSpan.FromSeconds(30))
    {
      return;
    }

    _lastSweep = now;
    foreach (var (id, session) in _sessions)
    {
      if (session.ActiveHandlers == 0 && !session.HasOpenStreams() && _time.GetElapsedTime(session.LastActivity, now) > _options.SessionIdleTimeout
        && _sessions.TryRemove(id, out _))
      {
        session.Dispose();
      }
    }
  }

  // ---- Reading and writing ---------------------------------------------------------------------------------------------------

  private async ValueTask<ArrayBufferWriter<byte>?> ReadBodyAsync(Call call)
  {
    var request = call.Request;
    var max = _options.MaxRequestBodyBytes;
    if (request.ContentLength > max)
    {
      await WriteErrorAsync(call, 413, JsonRpcErrorCodes.InvalidRequest, "Payload too large.").ConfigureAwait(false);
      return null;
    }

    var buffer = new ArrayBufferWriter<byte>((int)Math.Min(Math.Max(request.ContentLength ?? 4096, 256), 1 << 20));
    using var timeout = new CancellationTokenSource(_options.BodyReadTimeout, _time);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, call.Exchange.Aborted, _shutdown.Token);
    try
    {
      while (true)
      {
        var memory = buffer.GetMemory(4096);
        var read = await request.Body.ReadAsync(memory, linked.Token).ConfigureAwait(false);
        if (read == 0)
        {
          return buffer;
        }

        buffer.Advance(read);
        if (buffer.WrittenCount > max)
        {
          await WriteErrorAsync(call, 413, JsonRpcErrorCodes.InvalidRequest, "Payload too large.").ConfigureAwait(false);
          return null;
        }
      }
    }
    catch (OperationCanceledException) when (timeout.IsCancellationRequested && !call.Exchange.Aborted.IsCancellationRequested)
    {
      await WriteErrorAsync(call, 408, JsonRpcErrorCodes.InvalidRequest, "Request timeout: the body took too long.").ConfigureAwait(false);
      return null;
    }
  }

  private static void ApplySseHeaders(IHttpResponse response)
  {
    response.SetHeader("Content-Type", "text/event-stream");
    response.SetHeader("Cache-Control", "no-cache, no-transform");
    // 2026-07-28 Streamable HTTP, "Receiving Messages": keep reverse proxies from buffering the stream.
    response.SetHeader("X-Accel-Buffering", "no");
  }

  private static ValueTask WriteErrorAsync(Call call, int status, int code, string message, JsonRpcId id = default) =>
    WriteMessageAsync(call, status, new JsonRpcErrorResponse(id, new JsonRpcError(code, message)));

  private static ValueTask WriteProtocolErrorAsync(Call call, int status, ProtocolException error, JsonRpcId id) =>
    WriteMessageAsync(call, status, new JsonRpcErrorResponse(id, error.ToError()));

  private static ValueTask WriteMessageAsync(Call call, int status, JsonRpcMessage message) =>
    WriteBytesAsync(call, status, JsonRpcWriter.ToUtf8Bytes(message));

  private static async ValueTask WriteBytesAsync(Call call, int status, byte[] bytes)
  {
    var response = call.Response;
    response.StatusCode = status;
    response.SetHeader("Content-Type", "application/json");
    call.ResponseStarted = true;
    await response.Body.WriteAsync(bytes, call.Exchange.Aborted).ConfigureAwait(false);
    await response.Body.FlushAsync(call.Exchange.Aborted).ConfigureAwait(false);
  }

  private static async ValueTask WriteAcceptedAsync(Call call)
  {
    call.Response.StatusCode = 202;
    call.ResponseStarted = true;
    await call.Response.Body.FlushAsync(call.Exchange.Aborted).ConfigureAwait(false);
  }

  // ---- Per-request state -----------------------------------------------------------------------------------------------------

  private sealed class Call(IHttpExchange exchange)
  {
    internal IHttpExchange Exchange { get; } = exchange;

    internal IHttpRequest Request => Exchange.Request;

    internal IHttpResponse Response => Exchange.Response;

    internal bool ResponseStarted { get; set; }
  }

  /// <summary>
  /// The route from a handler to the one POST response it answers. It starts as a plain JSON response and becomes an event stream the
  /// first time the handler has something to say before its result (a notification, or on legacy a request to the client).
  /// </summary>
  private sealed class PostChannel(StreamableHttpEndpoint owner, Call call, HttpSession? session, bool legacy, bool acceptsSse, bool newSession) : IServerSink
  {
    private readonly AsyncGate _gate = new();
    private SseStream? _stream;
    private SseConnection? _connection;
    private bool _released;

    /// <summary>Completes when the HTTP connection is no longer usable: the client left, or the handler closed it.</summary>
    internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask SendNotificationAsync(JsonRpcNotification notification, CancellationToken cancellationToken)
    {
      // A client that accepts only application/json cannot receive a stream; the notification is dropped and the result still arrives.
      if (await EnsureStreamAsync(cancellationToken).ConfigureAwait(false) is { } stream)
      {
        await stream.SendAsync(JsonRpcWriter.ToUtf8Bytes(notification), cancellationToken).ConfigureAwait(false);
      }
    }

    public async ValueTask<JsonElement> SendRequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
      if (!legacy || session is null)
      {
        throw new NotSupportedException("Servers do not send requests on a 2026-07-28 response stream; use an input request in the result.");
      }

      var stream = await EnsureStreamAsync(cancellationToken).ConfigureAwait(false)
        ?? throw new NotSupportedException("The client does not accept an event stream, so the server cannot send it a request.");
      return await session.SendRequestAsync(stream, method, parameters, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask CloseConnectionAsync(TimeSpan retryAfter, CancellationToken cancellationToken)
    {
      if (!legacy)
      {
        return;
      }

      if (await EnsureStreamAsync(cancellationToken).ConfigureAwait(false) is { } stream)
      {
        await stream.CloseConnectionAsync(retryAfter).ConfigureAwait(false);
      }
    }

    private async ValueTask<SseStream?> EnsureStreamAsync(CancellationToken cancellationToken)
    {
      if (!acceptsSse)
      {
        return null;
      }

      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
        if (_released)
        {
          return _stream;
        }

        if (_stream is not null)
        {
          return _stream;
        }

        var stream = legacy ? session!.NewStream(standalone: false) : new SseStream("0", record: false, owner._options, owner._time);
        var response = call.Response;
        response.StatusCode = 200;
        ApplySseHeaders(response);
        if (newSession && session is not null)
        {
          response.SetHeader(WireNames.HeaderSessionId, session.Id);
        }

        call.ResponseStarted = true;
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        var connection = await stream.AttachAsync(new SseWriter(response.Body), -1, cancellationToken).ConfigureAwait(false);
        _stream = stream;
        _connection = connection;
        _ = connection.Closed.ContinueWith(
          _ =>
          {
            if (!stream.IsComplete)
            {
              Released.TrySetResult();
            }
          },
          CancellationToken.None,
          TaskContinuationOptions.ExecuteSynchronously,
          TaskScheduler.Default);

        // 2025-11-25 Streamable HTTP, "Sending Messages to the Server", item 6: prime the client with an event id so it can reconnect.
        if (legacy && session!.State.NegotiatedVersion == ProtocolVersions.V20251125)
        {
          await stream.SendPrimingAsync(cancellationToken).ConfigureAwait(false);
        }

        return stream;
      }
      finally
      {
        _gate.Release();
      }
    }

    /// <summary>Sends the final response: on the stream when there is one, otherwise as the JSON body.</summary>
    internal async ValueTask FinishAsync(JsonRpcMessage? response, int status)
    {
      await _gate.WaitAsync().ConfigureAwait(false);
      try
      {
        if (_stream is { } stream)
        {
          if (response is not null)
          {
            await stream.SendAsync(JsonRpcWriter.ToUtf8Bytes(response), CancellationToken.None).ConfigureAwait(false);
          }

          await stream.CompleteAsync().ConfigureAwait(false);
          return;
        }

        if (_released || response is null)
        {
          return;
        }

        var http = call.Response;
        http.StatusCode = status;
        http.SetHeader("Content-Type", "application/json");
        if (newSession && session is not null && response is JsonRpcResultResponse)
        {
          http.SetHeader(WireNames.HeaderSessionId, session.Id);
        }

        call.ResponseStarted = true;
        await http.Body.WriteAsync(JsonRpcWriter.ToUtf8Bytes(response), call.Exchange.Aborted).ConfigureAwait(false);
        await http.Body.FlushAsync(call.Exchange.Aborted).ConfigureAwait(false);
      }
      finally
      {
        _gate.Release();
      }
    }

    /// <summary>Stops using the HTTP response. Nothing is written to it after this returns.</summary>
    internal async ValueTask ReleaseAsync()
    {
      await _gate.WaitAsync().ConfigureAwait(false);
      try
      {
        _released = true;
        if (_stream is not null && _connection is not null)
        {
          await _stream.DetachAsync(_connection).ConfigureAwait(false);
        }
      }
      finally
      {
        _gate.Release();
      }
    }
  }
}
