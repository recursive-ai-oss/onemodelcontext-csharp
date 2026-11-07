using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Wire;

// The codec contract. A codec turns the era-neutral model (the records in Protocol, the registries and handlers of ContextServer)
// into one era's wire behaviour: what a request must carry, how a result is wrapped, which methods exist, how a handler asks the
// client for input. The shared dispatcher does everything that is the same in both eras (tools, resources, prompts, completion,
// pagination order, the audit hook, the inline-content cap) and calls the codec for everything that is not.

/// <summary>What a codec learned about the peer for one request. Handlers see it through <see cref="RequestContext"/>.</summary>
public sealed class PeerContext
{
  /// <summary>The era of the codec that read the request.</summary>
  public required ProtocolEra Era { get; init; }

  /// <summary>The protocol revision in force for this request.</summary>
  public required string ProtocolVersion { get; init; }

  /// <summary>The client's self-reported identity (not authenticated), if it sent one.</summary>
  public Implementation? ClientInfo { get; init; }

  /// <summary>What the client supports for this request. Empty when it declared nothing.</summary>
  public ClientCapabilities ClientCapabilities { get; init; } = new();

  /// <summary>The minimum log level the request asked for (2026-07-28 <c>_meta</c>), or the session level on legacy.</summary>
  public string? LogLevel { get; init; }

  /// <summary>The request's progress token, if it asked for progress.</summary>
  public JsonRpcId? ProgressToken { get; init; }

  /// <summary>The request's <c>_meta</c> as received, for trace context and extension keys.</summary>
  public MetaObject? Meta { get; init; }

  /// <summary>
  /// 2026-07-28 retries: the client's answers to earlier input requests, by key. The server adds the answers carried inside a verified
  /// <see cref="RequestState"/>, so a flow of several rounds sees every answer it was given.
  /// </summary>
  public IReadOnlyDictionary<string, JsonElement>? InputResponses { get; internal set; }

  /// <summary>2026-07-28 retries: the <c>requestState</c> exactly as the client sent it. Not yet verified; the server verifies it before any handler runs.</summary>
  public string? RequestState { get; init; }

  /// <summary>Handler state restored from a verified <c>requestState</c>; becomes <see cref="RequestContext.State"/>.</summary>
  public IReadOnlyDictionary<string, string>? RestoredState { get; internal set; }

  /// <summary>A copy the server completes with verified state; the codec's own instance is never changed.</summary>
  internal PeerContext Clone() => (PeerContext)MemberwiseClone();
}

/// <summary>
/// What the server remembers about one peer on one connection: a stdio or stream connection, or a legacy HTTP session.
/// Modern requests are stateless and leave it untouched.
/// </summary>
public sealed class ServerSessionState
{
  /// <summary>The era fixed by the first message, or null before one arrived.</summary>
  public ProtocolEra? Era { get; set; }

  /// <summary>Legacy: the revision negotiated by <c>initialize</c>.</summary>
  public string? NegotiatedVersion { get; set; }

  /// <summary>Legacy: the client's identity from <c>initialize</c>.</summary>
  public Implementation? ClientInfo { get; set; }

  /// <summary>Legacy: the client's capabilities from <c>initialize</c>.</summary>
  public ClientCapabilities? ClientCapabilities { get; set; }

  /// <summary>Legacy: true after <c>notifications/initialized</c>.</summary>
  public bool Initialized { get; set; }

  /// <summary>Legacy: the level set by <c>logging/setLevel</c>.</summary>
  public string? LogLevel { get; set; }

  /// <summary>Legacy over HTTP: the <c>Mcp-Session-Id</c> this state belongs to.</summary>
  public string? SessionId { get; set; }

  /// <summary>Scratch space for the codec that owns the session.</summary>
  public ConcurrentDictionary<string, object> Items => _items ?? Interlocked.CompareExchange(ref _items, new ConcurrentDictionary<string, object>(StringComparer.Ordinal), null) ?? _items!;

  // Most states (every stateless request makes one) never use the scratch space, so the dictionary is made on first use.
  private ConcurrentDictionary<string, object>? _items;
}

/// <summary>Where a server sends what is not the response to the request being handled.</summary>
public interface IServerSink
{
  /// <summary>Sends a notification related to the current request or connection (progress, logs, list changes).</summary>
  /// <param name="notification">The notification.</param>
  /// <param name="cancellationToken">Cancels the send.</param>
  ValueTask SendNotificationAsync(JsonRpcNotification notification, CancellationToken cancellationToken);

  /// <summary>
  /// Legacy: sends a server-initiated request (<c>sampling/createMessage</c>, <c>elicitation/create</c>, <c>roots/list</c>) and waits for
  /// the client's result.
  /// </summary>
  /// <param name="method">The method.</param>
  /// <param name="parameters">The params.</param>
  /// <param name="cancellationToken">Cancels the wait.</param>
  /// <exception cref="NotSupportedException">The transport has no way to reach the client.</exception>
  ValueTask<JsonElement> SendRequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken);
}

/// <summary>What a server offers a codec.</summary>
public interface IServerHost
{
  /// <summary>The server's name and version.</summary>
  Implementation Info { get; }

  /// <summary>Hints for the model on using the server.</summary>
  string? Instructions { get; }

  /// <summary>The capabilities the server advertises, computed from what is registered right now.</summary>
  ServerCapabilities Capabilities { get; }

  /// <summary>The revisions the server speaks, newest first.</summary>
  IReadOnlyList<string> SupportedVersions { get; }

  /// <summary>The clock for timeouts, cache lifetimes and state expiry.</summary>
  TimeProvider TimeProvider { get; }

  /// <summary>The extensions to advertise on a connection that negotiated <paramref name="protocolVersion"/>: those that work on that revision. Default: every extension in <see cref="Capabilities"/>.</summary>
  /// <param name="protocolVersion">The revision in force.</param>
  Dictionary<string, JsonElement>? ExtensionsFor(string protocolVersion) => Capabilities.Extensions;

  /// <summary>How long clients may reuse list results and discovery (2026-07-28 <c>ttlMs</c>). Default zero.</summary>
  TimeSpan CacheTtl => TimeSpan.Zero;

  /// <summary>Who may reuse them (2026-07-28 <c>cacheScope</c>): <c>private</c> or <c>public</c>. Default <c>private</c>.</summary>
  string CacheScope => "private";
}

/// <summary>A codec's verdict on an incoming request.</summary>
public sealed class ServerRequestDecision
{
  private ServerRequestDecision(JsonRpcMessage? response, PeerContext? peer)
  {
    Response = response;
    Peer = peer;
  }

  /// <summary>The response to send now: the codec answered the request itself, or rejected it.</summary>
  public JsonRpcMessage? Response { get; }

  /// <summary>The peer facts; set when the request should go to the shared handlers.</summary>
  public PeerContext? Peer { get; }

  /// <summary>The codec handled the request; send this response.</summary>
  /// <param name="response">A result or error response.</param>
  public static ServerRequestDecision Respond(JsonRpcMessage response)
  {
    ArgumentNullException.ThrowIfNull(response);
    return new ServerRequestDecision(response, null);
  }

  /// <summary>The request is acceptable; run the shared handler for its method.</summary>
  /// <param name="peer">What was learned about the peer.</param>
  public static ServerRequestDecision Dispatch(PeerContext peer)
  {
    ArgumentNullException.ThrowIfNull(peer);
    return new ServerRequestDecision(null, peer);
  }
}

/// <summary>How a handler asks the client for input in the middle of a request.</summary>
public interface IInputChannel
{
  /// <summary>
  /// Asks the client to answer a <c>sampling/createMessage</c>, <c>roots/list</c> or <c>elicitation/create</c> request.
  /// </summary>
  /// <remarks>
  /// Legacy: sends a server-initiated request and awaits the answer. Modern: returns the answer when the retry carried it in
  /// <c>inputResponses</c>; otherwise throws <see cref="InputRequiredException"/> to end this attempt, and the client calls again.
  /// A handler that asks therefore runs from the start again on a modern retry: code before the ask must be safe to repeat.
  /// </remarks>
  /// <param name="method">The input request's method.</param>
  /// <param name="parameters">Its params.</param>
  /// <param name="cancellationToken">Cancels the wait.</param>
  /// <param name="key">The name of the input request on the modern wire (its key in inputRequests and inputResponses); null numbers them input-1, input-2 and so on in order. Legacy ignores it.</param>
  /// <returns>The client's result for that request.</returns>
  ValueTask<JsonElement> RequestInputAsync(string method, JsonElement parameters, CancellationToken cancellationToken, string? key = null);
}

/// <summary>The server half of an era's wire behaviour. One instance serves every connection of its server.</summary>
public interface IServerCodec
{
  /// <summary>The era this codec speaks.</summary>
  ProtocolEra Era { get; }

  /// <summary>
  /// Reads the era's envelope of a request: modern checks <c>_meta</c> version and capabilities (-32022, -32602, -32021); legacy checks the
  /// session and answers <c>initialize</c>, <c>ping</c>, <c>logging/setLevel</c> and subscriptions itself. Never throws on bad input.
  /// </summary>
  /// <param name="request">The request.</param>
  /// <param name="session">The connection's state.</param>
  /// <param name="host">The server.</param>
  /// <param name="cancellationToken">Cancelled when the request is.</param>
  ValueTask<ServerRequestDecision> BeginRequestAsync(JsonRpcRequest request, ServerSessionState session, IServerHost host, CancellationToken cancellationToken);

  /// <summary>Handles a notification that belongs to the era (<c>notifications/initialized</c>). Others are ignored.</summary>
  /// <param name="notification">The notification.</param>
  /// <param name="session">The connection's state.</param>
  /// <param name="host">The server.</param>
  /// <param name="cancellationToken">Cancelled when the connection closes.</param>
  ValueTask HandleNotificationAsync(JsonRpcNotification notification, ServerSessionState session, IServerHost host, CancellationToken cancellationToken);

  /// <summary>Wraps a handler's result for the wire: <c>resultType</c>, server info, cache fields as the era requires.</summary>
  /// <param name="id">The request id.</param>
  /// <param name="peer">The facts <see cref="BeginRequestAsync"/> produced.</param>
  /// <param name="result">The result.</param>
  /// <param name="host">The server.</param>
  JsonRpcMessage EncodeResult(JsonRpcId id, PeerContext peer, Result result, IServerHost host);

  /// <summary>Wraps an error, mapping era-specific codes (resource not found).</summary>
  /// <param name="id">The request id.</param>
  /// <param name="peer">The peer facts, or null when the request was rejected before they existed.</param>
  /// <param name="failure">The error.</param>
  JsonRpcMessage EncodeError(JsonRpcId id, PeerContext? peer, ProtocolException failure);

  /// <summary>Creates the channel a handler uses to ask the client for input.</summary>
  /// <param name="peer">The request's peer facts.</param>
  /// <param name="session">The connection's state.</param>
  /// <param name="sink">Where server-initiated requests go.</param>
  IInputChannel CreateInputChannel(PeerContext peer, ServerSessionState session, IServerSink sink);

  /// <summary>Whether JSON-RPC batches are allowed on this connection (only after negotiating 2025-03-26).</summary>
  /// <param name="session">The connection's state.</param>
  bool AcceptsBatches(ServerSessionState session);
}

/// <summary>Picks the codec for an incoming message.</summary>
public static class CodecSelector
{
  /// <summary>
  /// Decides which era a request belongs to. <c>initialize</c> is legacy; <c>server/discover</c> and any request whose <c>_meta</c> carries
  /// the protocol version are modern; otherwise the era the connection already settled on; otherwise (a bare request with no version) modern,
  /// whose codec then rejects it with its own error. Both eras can be answered on one stream or endpoint.
  /// </summary>
  /// <param name="method">The method.</param>
  /// <param name="parameters">The params, or <c>default</c>.</param>
  /// <param name="session">The connection's state.</param>
  /// <param name="hasLegacySessionId">True when the HTTP request carried a session id.</param>
  public static ProtocolEra Select(string method, JsonElement parameters, ServerSessionState session, bool hasLegacySessionId = false)
  {
    ArgumentNullException.ThrowIfNull(method);
    ArgumentNullException.ThrowIfNull(session);
    if (method is Methods.Initialize or Methods.Initialized)
    {
      return ProtocolEra.Legacy;
    }

    if (method == Methods.ServerDiscover || CarriesVersion(parameters))
    {
      return ProtocolEra.Modern;
    }

    if (session.Era is { } settled)
    {
      return settled;
    }

    return hasLegacySessionId ? ProtocolEra.Legacy : ProtocolEra.Modern;
  }

  private static bool CarriesVersion(JsonElement parameters) =>
    parameters.ValueKind == JsonValueKind.Object
    && parameters.TryGetProperty("_meta", out var meta)
    && meta.ValueKind == JsonValueKind.Object
    && meta.TryGetProperty(WireNames.MetaProtocolVersion, out _);
}
