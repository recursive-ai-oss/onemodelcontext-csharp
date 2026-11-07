using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.Extensions;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;
using RecursiveAI.OneModelContext.Wire.Modern;

namespace RecursiveAI.OneModelContext;

/// <summary>Settings of a <see cref="ContextServer"/>. Every limit is a default you can change.</summary>
public sealed class ContextServerOptions
{
  /// <summary>The revisions the server speaks, newest first. Default <see cref="ProtocolVersions.Supported"/>; a draft revision is never in it.</summary>
  public IReadOnlyList<string> SupportedVersions { get; init; } = ProtocolVersions.Supported;

  /// <summary>
  /// Largest inline image, audio or blob in a tool result, in bytes after decoding. Larger content is replaced by a text note so one
  /// result cannot flood the connection. Default 4 MiB.
  /// </summary>
  public int MaxInlineContentBytes { get; init; } = 4 * 1024 * 1024;

  /// <summary>Most items in one page of a list result. Longer lists are paged with an opaque cursor. Default 1000.</summary>
  public int MaxPageSize { get; init; } = 1000;

  /// <summary>How long clients may cache list results (<c>ttlMs</c>, 2026-07-28). Default zero: do not reuse without asking again.</summary>
  public TimeSpan ListCacheTtl { get; init; } = TimeSpan.Zero;

  /// <summary>Who may cache list results: <c>private</c> (default) or <c>public</c>. Use <c>public</c> only when every caller sees the same lists.</summary>
  public string ListCacheScope { get; init; } = "private";

  /// <summary>Most requests handled at once on one connection; more get error -32000. Default 256.</summary>
  public int MaxConcurrentRequests { get; init; } = 256;

  /// <summary>How long a server-initiated request waits for the client's answer. Default 5 minutes.</summary>
  public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);

  /// <summary>
  /// Grace after stdio or stream input EOF for admitted requests to finish and queue their responses before cancellation.
  /// Default 5 seconds; zero cancels immediately. Explicit cancellation skips the grace; shutdown then waits at most 5 seconds for cancelled handlers.
  /// </summary>
  public TimeSpan EndOfStreamTimeout { get; init; } = TimeSpan.FromSeconds(5);

  /// <summary>Limits applied to every incoming message.</summary>
  public JsonRpcReaderOptions ReaderOptions { get; init; } = new();

  /// <summary>Most resources one legacy session may subscribe to with <c>resources/subscribe</c>; more get invalid params. Default 1000.</summary>
  public int MaxSubscriptionsPerSession { get; init; } = 1000;

  /// <summary>
  /// Called once per request after it ends, with who asked, what, and how it ended (never arguments, results or tokens). It runs inline, so it
  /// should be quick; an exception it throws is reported as a diagnostic and otherwise ignored.
  /// </summary>
  public Action<AuditRecord>? Audit { get; init; }

  /// <summary>The clock for timeouts and durations. Default <see cref="TimeProvider.System"/>.</summary>
  public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

  /// <summary>
  /// The secret that protects <c>requestState</c> (2026-07-28 multi round-trip requests), at least 32 bytes. Servers that run as several
  /// instances behind one endpoint need the same key on each, so any instance can verify what another issued. Default <c>null</c>: a random
  /// key per server object, which suits a single process; state from another process or an earlier run is then refused.
  /// </summary>
  public byte[]? RequestStateKey { get; init; }

  /// <summary>
  /// Seal <c>requestState</c> with AES-GCM so the client cannot read it, not only tamper-proof with HMAC-SHA256. Default <c>false</c>.
  /// Turn it on when <see cref="RequestContext.State"/> holds something the client mustn't see.
  /// </summary>
  public bool EncryptRequestState { get; init; }

  /// <summary>How long a <c>requestState</c> stays valid after it was issued. Default 10 minutes.</summary>
  public TimeSpan RequestStateLifetime { get; init; } = TimeSpan.FromMinutes(10);

  /// <summary>The largest <c>requestState</c> payload the server will issue, in bytes before encoding. Default 256 KiB.</summary>
  public int MaxRequestStateBytes { get; init; } = 256 * 1024;

  /// <summary>The codecs to use, in place of the built-in ones. Default: the modern codec, then the legacy codec.</summary>
  public IReadOnlyList<IServerCodec>? Codecs { get; init; }
}

/// <summary>
/// A server. Register tools, resources and prompts (also while it runs), then run it over a transport or feed it messages directly.
/// One instance serves any number of connections, in both protocol eras at once.
/// </summary>
/// <example>
/// <code>
/// var server = new ContextServer("echo", "1.0.0");
/// server.Tools.Add(new ToolDefinition("echo", "Echoes text.", schema), (args, ctx) => ValueTask.FromResult(CallToolResult.FromText(args.GetProperty("text").GetString()!)));
/// await server.RunAsync(ContextTransport.Stdio());
/// </code>
/// </example>
public sealed partial class ContextServer : IServerHost
{
  private readonly IReadOnlyList<IServerCodec> _codecs;
  private readonly ServerFeatures _features;
  private readonly Wire.Modern.RequestStateProtector _stateProtector;

  /// <summary>Creates a server.</summary>
  /// <param name="info">Its name and version, reported to clients.</param>
  /// <param name="options">Limits and hooks; <c>null</c> for the defaults.</param>
  public ContextServer(Implementation info, ContextServerOptions? options = null)
  {
    ArgumentNullException.ThrowIfNull(info);
    Info = info;
    Options = options ?? new ContextServerOptions();
    _codecs = Options.Codecs ?? DefaultCodecs.Server();
    _features = new ServerFeatures(this);
    _stateProtector = new Wire.Modern.RequestStateProtector(Options.RequestStateKey, Options.EncryptRequestState, Options.RequestStateLifetime, Options.MaxRequestStateBytes, Options.TimeProvider);
    HookListChanged();
  }

  /// <summary>Creates a server.</summary>
  /// <param name="name">Its name.</param>
  /// <param name="version">Its version.</param>
  /// <param name="options">Limits and hooks; <c>null</c> for the defaults.</param>
  public ContextServer(string name, string version, ContextServerOptions? options = null)
    : this(new Implementation { Name = name, Version = version }, options)
  {
  }

  /// <summary>The server's name and version.</summary>
  public Implementation Info { get; }

  /// <summary>Hints for the model on using the server; sent to clients.</summary>
  public string? Instructions { get; set; }

  /// <summary>The settings.</summary>
  public ContextServerOptions Options { get; }

  /// <summary>The tools. Add and remove while the server runs.</summary>
  public ToolRegistry Tools { get; } = new();

  /// <summary>The resources. Add and remove while the server runs.</summary>
  public ResourceRegistry Resources { get; } = new();

  /// <summary>The resource templates. Add and remove while the server runs.</summary>
  public ResourceTemplateRegistry ResourceTemplates { get; } = new();

  /// <summary>The prompts. Add and remove while the server runs.</summary>
  public PromptRegistry Prompts { get; } = new();

  /// <summary>
  /// The extensions the server offers. Empty by default: nothing is advertised until a registration is added (for example
  /// <c>server.Extensions.Add(...)</c> or <c>server.UseApps()</c>). See <see cref="ExtensionRegistration"/>.
  /// </summary>
  public ExtensionRegistry Extensions { get; } = new();

  /// <summary>Answers <c>completion/complete</c>. Setting it makes the server advertise the completions capability.</summary>
  public CompletionHandler? Completions { get; set; }

  /// <inheritdoc />
  public ServerCapabilities Capabilities => new()
  {
    // A capability is advertised when something is registered for it, so set up before the first client connects.
    Tools = Tools.Count > 0 ? new ToolsCapability { ListChanged = true } : null,
    Resources = Resources.Count > 0 || ResourceTemplates.Count > 0 ? new ResourcesCapability { ListChanged = true } : null,
    Prompts = Prompts.Count > 0 ? new PromptsCapability { ListChanged = true } : null,
    Completions = Completions is null ? default : EmptyObject,
    Extensions = Extensions.AdvertisedFor(null),
  };

  /// <inheritdoc />
  public Dictionary<string, JsonElement>? ExtensionsFor(string protocolVersion) => Extensions.AdvertisedFor(protocolVersion);

  /// <inheritdoc />
  public IReadOnlyList<string> SupportedVersions => Options.SupportedVersions;

  /// <inheritdoc />
  public TimeProvider TimeProvider => Options.TimeProvider;

  /// <inheritdoc />
  public TimeSpan CacheTtl => Options.ListCacheTtl;

  /// <inheritdoc />
  // A list that an extension reshapes per caller (Apps hides its metadata from callers without it) must not be shared between callers.
  public string CacheScope => Extensions.HasToolAdapters ? "private" : Options.ListCacheScope;

  internal ServerEvents Events { get; } = new();

  /// <summary>
  /// Tells clients that listen for it that the tool list changed. Adding or removing a tool does this by itself; call it when a tool
  /// definition changed in place. Modern clients hear it on a <c>subscriptions/listen</c> stream they opened with <c>toolsListChanged</c>.
  /// </summary>
  public void NotifyToolsListChanged() => Events.Raise(new ServerEvent(ServerEventKind.ToolsListChanged));

  /// <summary>Tells clients that listen for it that the prompt list changed. Registry changes do this by themselves.</summary>
  public void NotifyPromptsListChanged() => Events.Raise(new ServerEvent(ServerEventKind.PromptsListChanged));

  /// <summary>Tells clients that listen for it that the resource list changed. Registry changes do this by themselves.</summary>
  public void NotifyResourcesListChanged() => Events.Raise(new ServerEvent(ServerEventKind.ResourcesListChanged));

  /// <summary>
  /// Ends every open <c>subscriptions/listen</c> stream the graceful way: each is answered with a final result, as the revision asks for
  /// when a server shuts down on purpose.
  /// </summary>
  public void EndSubscriptions() => Events.Raise(new ServerEvent(ServerEventKind.Shutdown));

  internal static JsonElement EmptyObject { get; } = JsonDocument.Parse("{}").RootElement;

  /// <summary>
  /// Serves one connection until the peer closes it, a message exceeds the line limit, or <paramref name="cancellationToken"/> is cancelled.
  /// Requests run concurrently; <c>notifications/cancelled</c> cancels a request's <see cref="RequestContext.CancellationToken"/>.
  /// </summary>
  /// <param name="transport">The transport; opened here and closed when the connection ends.</param>
  /// <param name="cancellationToken">Stops serving.</param>
  public async Task RunAsync(ContextTransport transport, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(transport);
    var channel = await transport.OpenAsync(cancellationToken).ConfigureAwait(false);
    var connection = new ServerConnection(this, channel);
    await using (connection.ConfigureAwait(false))
    {
      var reason = await connection.RunAsync(cancellationToken).ConfigureAwait(false);
      if (reason == JsonRpcCloseReason.MessageTooLong)
      {
        ContextDiagnostics.Publish(DiagnosticKind.InvalidMessage, "A message exceeded the line limit; the connection was closed.");
      }
    }
  }

  /// <summary>
  /// The message-level entry for hosts without a stream (an in-page channel, a test): one JSON-RPC message in, the response out.
  /// </summary>
  /// <param name="message">One JSON-RPC message (or a batch where the session allows it).</param>
  /// <param name="session">The peer's state; pass the same object for every message of one peer. <c>null</c> uses a fresh state.</param>
  /// <param name="sink">Where notifications and server-initiated requests go; <c>null</c> drops notifications and refuses server requests.</param>
  /// <param name="principal">The authenticated caller, if the host knows one.</param>
  /// <param name="cancellationToken">Cancels the handler.</param>
  /// <returns>The response message, or <c>null</c> for a notification or a response. Bad input never throws: it returns a JSON-RPC error.</returns>
  public async ValueTask<string?> HandleMessageAsync(
    string message,
    ServerSessionState? session = null,
    IServerSink? sink = null,
    ClaimsPrincipal? principal = null,
    CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(message);
    session ??= new ServerSessionState();
    sink ??= NullSink.Instance;
    var read = JsonRpcReader.Read(message, Options.ReaderOptions);
    switch (read.Kind)
    {
      case JsonRpcReadKind.Invalid:
        return JsonRpcWriter.ToJson(read.Error!);
      case JsonRpcReadKind.Message:
        var single = await HandleOneAsync(read.Message!, session, sink, principal, cancellationToken).ConfigureAwait(false);
        return single is null ? null : JsonRpcWriter.ToJson(single);
      default:
        var allowed = FindCodec(session.Era ?? ProtocolEra.Legacy)?.AcceptsBatches(session) == true;
        if (!allowed)
        {
          return JsonRpcWriter.ToJson(new JsonRpcErrorResponse(default, new JsonRpcError(JsonRpcErrorCodes.InvalidRequest, "Invalid request: batches are not supported.")));
        }

        var responses = new List<JsonRpcMessage>();
        foreach (var item in read.Items!)
        {
          if (item.Kind == JsonRpcReadKind.Invalid)
          {
            responses.Add(item.Error!);
          }
          else if (await HandleOneAsync(item.Message!, session, sink, principal, cancellationToken).ConfigureAwait(false) is { } response)
          {
            responses.Add(response);
          }
        }

        if (responses.Count == 0)
        {
          return null;
        }

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        JsonRpcWriter.WriteBatch(buffer, responses);
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
  }

  internal IServerCodec? FindCodec(ProtocolEra era)
  {
    foreach (var codec in _codecs)
    {
      if (codec.Era == era)
      {
        return codec;
      }
    }

    return null;
  }

  internal async ValueTask<JsonRpcMessage?> HandleOneAsync(JsonRpcMessage message, ServerSessionState session, IServerSink sink, ClaimsPrincipal? principal, CancellationToken cancellationToken)
  {
    switch (message)
    {
      case JsonRpcRequest request:
        return await DispatchRequestAsync(request, session, sink, principal, hasLegacySessionId: session.SessionId is not null, cancellationToken).ConfigureAwait(false);
      case JsonRpcNotification notification:
        await DispatchNotificationAsync(notification, session, cancellationToken).ConfigureAwait(false);
        return null;
      default:
        // A response to a request this server sent; the stream connection correlates those, a message-level host has none.
        return null;
    }
  }

  internal async ValueTask DispatchNotificationAsync(JsonRpcNotification notification, ServerSessionState session, CancellationToken cancellationToken)
  {
    var era = CodecSelector.Select(notification.Method, notification.Params, session, session.SessionId is not null);
    if (Extensions.Count > 0 && Extensions.TryGetNotification(notification.Method, out var registration, out var extensionHandler))
    {
      await RunExtensionNotificationAsync(notification, session, era, registration, extensionHandler, cancellationToken).ConfigureAwait(false);
      return;
    }

    if (FindCodec(era) is { } codec)
    {
      await codec.HandleNotificationAsync(notification, session, this, cancellationToken).ConfigureAwait(false);
    }
  }

  // An extension's notification is delivered only for a sender that declared the extension; anything else is dropped, as the spec asks
  // of notifications nobody understands. A handler that throws is a diagnostic, never an error on the connection.
  private static async ValueTask RunExtensionNotificationAsync(JsonRpcNotification notification, ServerSessionState session, ProtocolEra era, ExtensionRegistration registration, ExtensionNotificationHandler handler, CancellationToken cancellationToken)
  {
    PeerContext? peer = null;
    try
    {
      if (era == ProtocolEra.Legacy)
      {
        if (session.NegotiatedVersion is { } negotiated)
        {
          peer = new PeerContext { Era = ProtocolEra.Legacy, ProtocolVersion = negotiated, ClientInfo = session.ClientInfo, ClientCapabilities = session.ClientCapabilities ?? new ClientCapabilities() };
        }
      }
      else if (notification.Params.ValueKind == JsonValueKind.Object
        && notification.Params.TryGetProperty("_meta", out var metaElement) && metaElement.ValueKind == JsonValueKind.Object
        && metaElement.Deserialize(WireJsonContext.Default.MetaObject) is { ProtocolVersion: { } version } meta)
      {
        // 2026-07-28 carries the sender's capabilities in the notification's own _meta, as it does on requests.
        peer = new PeerContext { Era = ProtocolEra.Modern, ProtocolVersion = version, ClientInfo = meta.ClientInfo, ClientCapabilities = meta.ClientCapabilities ?? new ClientCapabilities(), Meta = meta };
      }

      if (peer is null || !registration.SupportsRevision(peer.ProtocolVersion) || !ExtensionRegistry.Declares(peer.ClientCapabilities, registration.Id))
      {
        return;
      }

      var parameters = notification.Params.ValueKind == JsonValueKind.Object ? notification.Params : EmptyObject;
      await handler(parameters, peer, cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception ex)
    {
      ContextDiagnostics.Publish(DiagnosticKind.HandlerFault, "An extension notification handler threw " + ex.GetType().Name + ".", notification.Method, ex);
    }
  }

  /// <summary>Runs one request through codec selection, the codec's envelope rules, the shared handlers and the audit hook.</summary>
  internal async ValueTask<JsonRpcMessage?> DispatchRequestAsync(
    JsonRpcRequest request,
    ServerSessionState session,
    IServerSink sink,
    ClaimsPrincipal? principal,
    bool hasLegacySessionId,
    CancellationToken cancellationToken)
  {
    var started = TimeProvider.GetTimestamp();
    var era = CodecSelector.Select(request.Method, request.Params, session, hasLegacySessionId);
    var codec = FindCodec(era);
    string? name = ServerFeatures.NameOf(request.Method, request.Params);
    using var activity = StartActivity(request, name);
    var outcome = RequestOutcome.Success;
    int? errorCode = null;
    PeerContext? peer = null;
    try
    {
      if (codec is null)
      {
        outcome = RequestOutcome.ProtocolError;
        errorCode = JsonRpcErrorCodes.MethodNotFound;
        return new JsonRpcErrorResponse(request.Id, new JsonRpcError(JsonRpcErrorCodes.MethodNotFound, "Method not found: no " + (era == ProtocolEra.Modern ? "modern" : "legacy") + " protocol codec is available."));
      }

      var decision = await codec.BeginRequestAsync(request, session, this, cancellationToken).ConfigureAwait(false);
      if (decision.Response is { } direct)
      {
        if (direct is JsonRpcErrorResponse rejected)
        {
          outcome = RequestOutcome.ProtocolError;
          errorCode = rejected.Error.Code;
          ContextDiagnostics.Publish(DiagnosticKind.InvalidMessage, "A request was rejected with error " + rejected.Error.Code + ".", request.Method);
        }

        return direct;
      }

      peer = decision.Peer!;

      // Extensions overview, "Graceful Degradation": a mandatory extension rejects a peer that lacks it. The codec chooses the code it can say.
      if (Extensions.Count > 0 && Extensions.FirstMissingRequired(peer) is { } missing)
      {
        outcome = RequestOutcome.ProtocolError;
        errorCode = ProtocolErrorCodes.MissingRequiredClientCapability;
        var required = new ClientCapabilities { Extensions = new Dictionary<string, JsonElement> { [missing.Id] = EmptyObject } };
        return codec.EncodeError(request.Id, peer, new MissingCapabilityException("The server requires the extension '" + missing.Id + "', which the client did not declare.", required));
      }

      // 2026-07-28 mrtr: only these requests may be answered with input_required, and only they carry requestState we act on.
      var canAskForInput = request.Method is Methods.ToolsCall or Methods.PromptsGet or Methods.ResourcesRead;
      StateBinding? binding = null;
      if (canAskForInput && peer.RequestState is { } presented)
      {
        binding = BindState(request, name, principal);

        // basic/patterns/mrtr, server requirement 4: requestState is attacker-controlled; state that fails verification is rejected.
        if (!_stateProtector.TryOpen(presented, binding.Value, out var opened))
        {
          outcome = RequestOutcome.ProtocolError;
          errorCode = JsonRpcErrorCodes.InvalidParams;
          ContextDiagnostics.Publish(DiagnosticKind.InvalidMessage, "A requestState was refused (tampered, expired or presented by another caller or for another request).", request.Method);
          return codec.EncodeError(request.Id, peer, new ProtocolException(JsonRpcErrorCodes.InvalidParams, "Invalid params: the requestState is not valid."));
        }

        peer = peer.Clone();
        peer.RestoredState = opened.State;

        // Answers from earlier rounds travel inside the state, so a handler run again from the start finds all of them; this round's win.
        var answers = opened.Answers is null ? new Dictionary<string, JsonElement>(StringComparer.Ordinal) : new Dictionary<string, JsonElement>(opened.Answers, StringComparer.Ordinal);
        if (decision.Peer!.InputResponses is { } fresh)
        {
          foreach (var (key, value) in fresh)
          {
            answers[key] = value;
          }
        }

        peer.InputResponses = answers;
      }

      var channel = codec.CreateInputChannel(peer, session, sink);
      var context = new RequestContext(this, request.Method, name, peer, sink, channel, principal, cancellationToken, request.Id);
      try
      {
        var result = await _features.HandleAsync(request.Method, request.Params, context).ConfigureAwait(false);
        if (channel is Wire.Modern.IPendingInput pendingInput && pendingInput.TryTakePending(out var swallowed))
        {
          // The handler asked, was told to wait, and carried on anyway: what it returned was built without the answer.
          return EncodeInputRequired(swallowed, serverBuilt: true);
        }

        if (result is CallToolResult { IsError: true })
        {
          outcome = RequestOutcome.ToolError;
        }

        return codec.EncodeResult(request.Id, peer, result, this);
      }
      catch (InputRequiredException needsInput)
      {
        return EncodeInputRequired(needsInput.Result, needsInput.IsServerBuilt);
      }
      catch (ProtocolException error)
      {
        outcome = RequestOutcome.ProtocolError;
        errorCode = error.Code;
        return codec.EncodeError(request.Id, peer, error);
      }

      JsonRpcMessage EncodeInputRequired(InputRequiredResult asked, bool serverBuilt)
      {
        if (!canAskForInput)
        {
          outcome = RequestOutcome.ProtocolError;
          errorCode = JsonRpcErrorCodes.InternalError;
          return codec.EncodeError(request.Id, peer, new ProtocolException(JsonRpcErrorCodes.InternalError, "Internal error."));
        }

        if (serverBuilt)
        {
          try
          {
            var sealedState = _stateProtector.Seal(
              binding ?? BindState(request, name, principal),
              new Dictionary<string, string>(context.State, StringComparer.Ordinal),
              peer.InputResponses is null ? null : new Dictionary<string, JsonElement>(peer.InputResponses, StringComparer.Ordinal));
            asked = new InputRequiredResult { ResultType = WireNames.ResultTypeInputRequired, InputRequests = asked.InputRequests, RequestState = sealedState, Meta = asked.Meta, AdditionalProperties = asked.AdditionalProperties };
          }
          catch (InvalidOperationException ex)
          {
            outcome = RequestOutcome.Failed;
            errorCode = JsonRpcErrorCodes.InternalError;
            ContextDiagnostics.Publish(DiagnosticKind.HandlerFault, "The requestState exceeded MaxRequestStateBytes.", request.Method, ex);
            return codec.EncodeError(request.Id, peer, new ProtocolException(JsonRpcErrorCodes.InternalError, "Internal error."));
          }
        }

        outcome = RequestOutcome.InputRequired;
        return codec.EncodeResult(request.Id, peer, asked, this);
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      outcome = RequestOutcome.Cancelled;
      throw;
    }
    catch (Exception ex)
    {
      // The peer sees a generic error: exception text can hold internals or secrets.
      outcome = RequestOutcome.Failed;
      errorCode = JsonRpcErrorCodes.InternalError;
      ContextDiagnostics.Publish(DiagnosticKind.HandlerFault, "A request handler threw " + ex.GetType().Name + ".", request.Method, ex);
      var internalError = new ProtocolException(JsonRpcErrorCodes.InternalError, "Internal error.");
      return codec is null ? new JsonRpcErrorResponse(request.Id, internalError.ToError()) : codec.EncodeError(request.Id, peer, internalError);
    }
    finally
    {
      Finish(request.Method, name, outcome, errorCode, peer, session.ClientInfo, principal, codec?.Era, started, activity);
    }
  }

  // A request span follows the OTel MCP conventions: "{method} {target}", parented on the caller's trace context (SEP-414) when it sent one.
  private static System.Diagnostics.Activity? StartActivity(JsonRpcRequest request, string? name)
  {
    if (!ContextDiagnostics.Source.HasListeners())
    {
      return null;
    }

    var spanName = name is null ? request.Method : request.Method + " " + name;
    return TraceContext.TryRead(request.Params, out var parent)
      ? ContextDiagnostics.Source.StartActivity(spanName, System.Diagnostics.ActivityKind.Server, parent)
      : ContextDiagnostics.Source.StartActivity(spanName, System.Diagnostics.ActivityKind.Server);
  }

  // What a requestState is tied to (basic/patterns/mrtr, server requirement 5): the caller, the method, the target and the parameters.
  private static StateBinding BindState(JsonRpcRequest request, string? name, ClaimsPrincipal? principal)
  {
    var buffer = new System.Buffers.ArrayBufferWriter<byte>();
    using (var writer = new Utf8JsonWriter(buffer))
    {
      writer.WriteStartObject();
      if (request.Params.ValueKind == JsonValueKind.Object)
      {
        foreach (var property in request.Params.EnumerateObject())
        {
          if (property.NameEquals("_meta") || property.NameEquals("inputResponses") || property.NameEquals("requestState"))
          {
            continue;
          }

          property.WriteTo(writer);
        }
      }

      writer.WriteEndObject();
    }

    var digest = Wire.Modern.Base64Url.Encode(System.Security.Cryptography.SHA256.HashData(buffer.WrittenSpan));
    var who = string.Empty;
    if (principal?.Identity is { IsAuthenticated: true } identity)
    {
      var subject = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? identity.Name ?? "authenticated";
      var issuer = principal.FindFirst("iss")?.Value;
      who = issuer is null ? subject : issuer + "|" + subject;
    }

    return new StateBinding(request.Method, name, who, digest);
  }

  private void Finish(string method, string? name, RequestOutcome outcome, int? errorCode, PeerContext? peer, Implementation? sessionClientInfo, ClaimsPrincipal? principal, ProtocolEra? era, long started, System.Diagnostics.Activity? activity)
  {
    var elapsed = TimeProvider.GetElapsedTime(started);
    if (ContextDiagnostics.ServerDuration.Enabled)
    {
      ContextDiagnostics.ServerDuration.Record(
        elapsed.TotalSeconds,
        new KeyValuePair<string, object?>("mcp.method.name", method),
        new KeyValuePair<string, object?>("mcp.protocol.version", peer?.ProtocolVersion),
        new KeyValuePair<string, object?>("mcp.protocol.era", era?.ToString().ToLowerInvariant()),
        new KeyValuePair<string, object?>("error.type", outcome is RequestOutcome.Success ? null : outcome.ToString()));
    }

    if (activity is not null)
    {
      activity.SetTag("mcp.method.name", method);
      activity.SetTag("mcp.protocol.version", peer?.ProtocolVersion);
      activity.SetTag("mcp.protocol.era", era?.ToString().ToLowerInvariant());
      if (name is not null)
      {
        activity.SetTag(method switch
        {
          Methods.ToolsCall => "gen_ai.tool.name",
          Methods.PromptsGet => "gen_ai.prompt.name",
          Methods.ResourcesRead => "mcp.resource.uri",
          _ => "mcp.target",
        }, name);
      }

      if (outcome is not RequestOutcome.Success)
      {
        activity.SetTag("error.type", outcome.ToString());
      }
    }

    if (Options.Audit is { } audit)
    {
      try
      {
        audit(new AuditRecord(principal, peer?.ClientInfo ?? sessionClientInfo, method, name, outcome, errorCode, era, elapsed));
      }
      catch (Exception ex)
      {
        ContextDiagnostics.Publish(DiagnosticKind.AuditFault, "The audit callback threw " + ex.GetType().Name + ".", method, ex);
      }
    }
  }

  private sealed class NullSink : IServerSink
  {
    internal static NullSink Instance { get; } = new();

    public ValueTask SendNotificationAsync(JsonRpcNotification notification, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<JsonElement> SendRequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken) =>
      throw new NotSupportedException("This host has no way to send requests to the client.");
  }
}
