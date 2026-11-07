using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;

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
  };

  /// <inheritdoc />
  public IReadOnlyList<string> SupportedVersions => Options.SupportedVersions;

  /// <inheritdoc />
  public TimeProvider TimeProvider => Options.TimeProvider;

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
    if (FindCodec(era) is { } codec)
    {
      await codec.HandleNotificationAsync(notification, session, this, cancellationToken).ConfigureAwait(false);
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
    using var activity = ContextDiagnostics.Source.StartActivity(request.Method, System.Diagnostics.ActivityKind.Server);
    var era = CodecSelector.Select(request.Method, request.Params, session, hasLegacySessionId);
    var codec = FindCodec(era);
    string? name = ServerFeatures.NameOf(request.Method, request.Params);
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
        }

        return direct;
      }

      peer = decision.Peer!;
      var context = new RequestContext(this, request.Method, name, peer, sink, codec.CreateInputChannel(peer, session, sink), principal, cancellationToken);
      try
      {
        var result = await _features.HandleAsync(request.Method, request.Params, context).ConfigureAwait(false);
        if (result is CallToolResult { IsError: true })
        {
          outcome = RequestOutcome.ToolError;
        }

        return codec.EncodeResult(request.Id, peer, result, this);
      }
      catch (InputRequiredException needsInput)
      {
        outcome = RequestOutcome.InputRequired;
        return codec.EncodeResult(request.Id, peer, needsInput.Result, this);
      }
      catch (ProtocolException error)
      {
        outcome = RequestOutcome.ProtocolError;
        errorCode = error.Code;
        return codec.EncodeError(request.Id, peer, error);
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
      Finish(request.Method, name, outcome, errorCode, peer?.ClientInfo ?? session.ClientInfo, principal, codec?.Era, started, activity);
    }
  }

  private void Finish(string method, string? name, RequestOutcome outcome, int? errorCode, Implementation? clientInfo, ClaimsPrincipal? principal, ProtocolEra? era, long started, System.Diagnostics.Activity? activity)
  {
    var elapsed = TimeProvider.GetElapsedTime(started);
    if (ContextDiagnostics.ServerDuration.Enabled)
    {
      ContextDiagnostics.ServerDuration.Record(
        elapsed.TotalSeconds,
        new KeyValuePair<string, object?>("mcp.method.name", method),
        new KeyValuePair<string, object?>("mcp.protocol.era", era?.ToString().ToLowerInvariant()),
        new KeyValuePair<string, object?>("error.type", outcome is RequestOutcome.Success ? null : outcome.ToString()));
    }

    if (activity is not null)
    {
      activity.SetTag("mcp.method.name", method);
      activity.SetTag("mcp.protocol.era", era?.ToString().ToLowerInvariant());
      if (name is not null)
      {
        activity.SetTag("gen_ai.tool.name", method == Methods.ToolsCall ? name : null);
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
        audit(new AuditRecord(principal, clientInfo, method, name, outcome, errorCode, era, elapsed));
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
