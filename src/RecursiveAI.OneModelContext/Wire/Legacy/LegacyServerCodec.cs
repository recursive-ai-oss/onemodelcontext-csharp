using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Wire.Legacy;

/// <summary>What the legacy codec keeps per connection or HTTP session, in <see cref="ServerSessionState.Items"/>.</summary>
internal sealed class LegacySessionData
{
  internal const string Key = "legacy";

  /// <summary>Resource URIs the client subscribed to (<c>resources/subscribe</c>).</summary>
  internal ConcurrentDictionary<string, byte> Subscriptions { get; } = new(StringComparer.Ordinal);

  // The list-changed capabilities this session was told about; a server sends list_changed only for what it declared.
  internal volatile bool ToolsListChanged;
  internal volatile bool ResourcesListChanged;
  internal volatile bool PromptsListChanged;

  internal static LegacySessionData? Of(ServerSessionState session) =>
    session.Items.TryGetValue(Key, out var data) ? data as LegacySessionData : null;
}

/// <summary>
/// The server side of revisions 2024-11-05 to 2025-11-25: the <c>initialize</c> handshake, <c>ping</c>, logging level, resource
/// subscriptions, server-initiated requests, batches (2025-03-26 only) and the per-revision result shapes. Everything else goes to
/// the shared handlers.
/// </summary>
internal sealed class LegacyServerCodec : IServerCodec
{
  public ProtocolEra Era => ProtocolEra.Legacy;

  public ValueTask<ServerRequestDecision> BeginRequestAsync(JsonRpcRequest request, ServerSessionState session, IServerHost host, CancellationToken cancellationToken)
  {
    ServerRequestDecision decision;
    try
    {
      decision = Begin(request, session, host);
    }
    catch (ProtocolException ex)
    {
      decision = ServerRequestDecision.Respond(new JsonRpcErrorResponse(request.Id, ex.ToError()));
    }
    catch (JsonException)
    {
      decision = ServerRequestDecision.Respond(Error(request.Id, JsonRpcErrorCodes.InvalidParams, "Invalid params."));
    }

    return ValueTask.FromResult(decision);
  }

  private static ServerRequestDecision Begin(JsonRpcRequest request, ServerSessionState session, IServerHost host)
  {
    if (request.Method == Methods.Initialize)
    {
      return ServerRequestDecision.Respond(Initialize(request, session, host));
    }

    // lifecycle: ping is allowed at any time, before initialization too.
    if (request.Method == Methods.Ping)
    {
      return ServerRequestDecision.Respond(new JsonRpcResultResponse(request.Id, ContextServer.EmptyObject));
    }

    if (session.NegotiatedVersion is not { } version)
    {
      return ServerRequestDecision.Respond(Error(request.Id, JsonRpcErrorCodes.InvalidRequest, "Invalid request: the server is not initialized; send initialize first."));
    }

    // Requests that arrive after the initialize response but before notifications/initialized are served: clients race the two
    // on a duplex stream, and refusing helps nobody.
    switch (request.Method)
    {
      case Methods.LoggingSetLevel:
        return ServerRequestDecision.Respond(SetLevel(request, session));
      case Methods.ResourcesSubscribe:
      case Methods.ResourcesUnsubscribe:
        return ServerRequestDecision.Respond(Subscription(request, session, host));
      case Methods.ServerDiscover:
      case Methods.SubscriptionsListen:
        // 2026-07-28 methods do not exist on a legacy session.
        return ServerRequestDecision.Respond(Error(request.Id, JsonRpcErrorCodes.MethodNotFound, "Method not found: " + request.Method + "."));
    }

    MetaObject? meta = null;
    if (request.Params.ValueKind == JsonValueKind.Object && request.Params.TryGetProperty("_meta", out var metaElement) && metaElement.ValueKind == JsonValueKind.Object)
    {
      meta = metaElement.Deserialize(WireJsonContext.Default.MetaObject);
    }

    return ServerRequestDecision.Dispatch(new PeerContext
    {
      Era = ProtocolEra.Legacy,
      ProtocolVersion = version,
      ClientInfo = session.ClientInfo,
      ClientCapabilities = session.ClientCapabilities ?? new ClientCapabilities(),
      LogLevel = session.LogLevel,
      ProgressToken = meta?.ProgressToken,
      Meta = meta,
    });
  }

  // lifecycle (every revision): the server answers the client's version if it supports it, otherwise with the newest it supports.
  private static JsonRpcMessage Initialize(JsonRpcRequest request, ServerSessionState session, IServerHost host)
  {
    if (request.Params.ValueKind != JsonValueKind.Object
      || !request.Params.TryGetProperty("protocolVersion", out var requestedElement)
      || requestedElement.ValueKind != JsonValueKind.String)
    {
      return Error(request.Id, JsonRpcErrorCodes.InvalidParams, "Invalid params: initialize needs a protocolVersion string.");
    }

    var requested = requestedElement.GetString()!;
    string? chosen = null;
    foreach (var candidate in host.SupportedVersions)
    {
      if (LegacyShape.IsLegacy(candidate) && (chosen is null || LegacyShape.Rank(candidate) > LegacyShape.Rank(chosen)))
      {
        chosen = candidate;
      }
    }

    if (chosen is null)
    {
      return VersionError(request.Id, host, requested);
    }

    foreach (var candidate in host.SupportedVersions)
    {
      if (candidate == requested && LegacyShape.IsLegacy(candidate))
      {
        chosen = candidate;
      }
    }

    // Capabilities and identity are read leniently: the version is what decides whether the handshake can proceed.
    ClientCapabilities capabilities = new();
    Implementation? clientInfo = null;
    if (request.Params.TryGetProperty("capabilities", out var capabilitiesElement) && capabilitiesElement.ValueKind == JsonValueKind.Object)
    {
      capabilities = capabilitiesElement.Deserialize(WireJsonContext.Default.ClientCapabilities) ?? capabilities;
    }

    if (request.Params.TryGetProperty("clientInfo", out var infoElement) && infoElement.ValueKind == JsonValueKind.Object)
    {
      clientInfo = infoElement.Deserialize(WireJsonContext.Default.Implementation);
    }

    var data = new LegacySessionData();
    var advertised = host.Capabilities;
    data.ToolsListChanged = advertised.Tools?.ListChanged == true;
    data.ResourcesListChanged = advertised.Resources?.ListChanged == true;
    data.PromptsListChanged = advertised.Prompts?.ListChanged == true;
    session.Items[LegacySessionData.Key] = data;
    session.Era = ProtocolEra.Legacy;
    session.NegotiatedVersion = chosen;
    session.ClientCapabilities = capabilities;
    session.ClientInfo = clientInfo;
    session.Initialized = false;
    session.LogLevel = null;

    var result = new InitializeResult
    {
      ProtocolVersion = chosen,
      Capabilities = new ServerCapabilities
      {
        Experimental = advertised.Experimental,
        Logging = ContextServer.EmptyObject,
        Completions = advertised.Completions,
        Prompts = advertised.Prompts,
        Resources = advertised.Resources is null
          ? null
          : new ResourcesCapability { Subscribe = true, ListChanged = advertised.Resources.ListChanged },
        Tools = advertised.Tools,
        Tasks = advertised.Tasks,
        Extensions = host.ExtensionsFor(chosen),
      },
      ServerInfo = host.Info,
      Instructions = host.Instructions,
    };
    var element = JsonSerializer.SerializeToElement(result, WireJsonContext.Default.InitializeResult);
    return new JsonRpcResultResponse(request.Id, LegacyShape.Result(chosen, element));
  }

  private static JsonRpcErrorResponse VersionError(JsonRpcId id, IServerHost host, string requested)
  {
    var supported = new JsonArray();
    foreach (var version in host.SupportedVersions)
    {
      supported.Add((JsonNode?)JsonValue.Create(version));
    }

    var data = new JsonObject { ["supported"] = supported, ["requested"] = requested };
    return new JsonRpcErrorResponse(id, new JsonRpcError(JsonRpcErrorCodes.InvalidParams, "Unsupported protocol version.", JsonDocument.Parse(data.ToJsonString()).RootElement.Clone()));
  }

  private static JsonRpcMessage SetLevel(JsonRpcRequest request, ServerSessionState session)
  {
    if (request.Params.ValueKind != JsonValueKind.Object
      || !request.Params.TryGetProperty("level", out var level)
      || level.ValueKind != JsonValueKind.String
      || !LegacyShape.IsLogLevel(level.GetString()))
    {
      return Error(request.Id, JsonRpcErrorCodes.InvalidParams, "Invalid params: level must be one of the RFC 5424 log levels.");
    }

    session.LogLevel = level.GetString();
    return new JsonRpcResultResponse(request.Id, ContextServer.EmptyObject);
  }

  private static JsonRpcMessage Subscription(JsonRpcRequest request, ServerSessionState session, IServerHost host)
  {
    if (host.Capabilities.Resources is null)
    {
      return Error(request.Id, JsonRpcErrorCodes.MethodNotFound, "Method not found: this server has no resources.");
    }

    if (request.Params.ValueKind != JsonValueKind.Object
      || !request.Params.TryGetProperty("uri", out var uriElement)
      || uriElement.ValueKind != JsonValueKind.String)
    {
      return Error(request.Id, JsonRpcErrorCodes.InvalidParams, "Invalid params: a resource uri is required.");
    }

    var uri = uriElement.GetString()!;
    var data = LegacySessionData.Of(session);
    if (data is null)
    {
      return Error(request.Id, JsonRpcErrorCodes.InvalidRequest, "Invalid request: the server is not initialized.");
    }

    if (request.Method == Methods.ResourcesUnsubscribe)
    {
      data.Subscriptions.TryRemove(uri, out _);
    }
    else
    {
      var limit = host is ContextServer server ? server.Options.MaxSubscriptionsPerSession : 1000;
      if (data.Subscriptions.Count >= limit && !data.Subscriptions.ContainsKey(uri))
      {
        return Error(request.Id, JsonRpcErrorCodes.InvalidParams, "Too many subscriptions on this session (limit " + limit + ").");
      }

      data.Subscriptions[uri] = 0;
    }

    return new JsonRpcResultResponse(request.Id, ContextServer.EmptyObject);
  }

  public ValueTask HandleNotificationAsync(JsonRpcNotification notification, ServerSessionState session, IServerHost host, CancellationToken cancellationToken)
  {
    if (notification.Method == Methods.Initialized && session.NegotiatedVersion is not null)
    {
      session.Initialized = true;
    }

    // notifications/roots/list_changed and anything else the server does not act on are ignored, as the spec asks of unknown notifications.
    return ValueTask.CompletedTask;
  }

  public JsonRpcMessage EncodeResult(JsonRpcId id, PeerContext peer, Result result, IServerHost host)
  {
    var info = WireJsonContext.Default.GetTypeInfo(result.GetType())
      ?? throw new InvalidOperationException("The result type is not a protocol record.");
    var element = JsonSerializer.SerializeToElement(result, info);
    return new JsonRpcResultResponse(id, LegacyShape.Result(peer.ProtocolVersion, element));
  }

  public JsonRpcMessage EncodeError(JsonRpcId id, PeerContext? peer, ProtocolException failure)
  {
    // -32021 is a 2026-07-28 code; a legacy client knows only invalid params for a request the peer cannot serve.
    if (failure.Code == ProtocolErrorCodes.MissingRequiredClientCapability)
    {
      return new JsonRpcErrorResponse(id, new JsonRpcError(JsonRpcErrorCodes.InvalidParams, failure.Message));
    }

    return new JsonRpcErrorResponse(id, failure.ToError());
  }

  public IInputChannel CreateInputChannel(PeerContext peer, ServerSessionState session, IServerSink sink) => new LegacyInputChannel(peer, sink);

  // 2025-03-26 transports: a JSON-RPC batch may be sent only on that revision; 2025-06-18 removed batching.
  public bool AcceptsBatches(ServerSessionState session) => session.NegotiatedVersion == ProtocolVersions.V20250326;

  private static JsonRpcErrorResponse Error(JsonRpcId id, int code, string message) => new(id, new JsonRpcError(code, message));

  /// <summary>Legacy input: a real server-initiated request on the same connection, gated on what the client declared.</summary>
  private sealed class LegacyInputChannel(PeerContext peer, IServerSink sink) : IInputChannel
  {
    public async ValueTask<JsonElement> RequestInputAsync(string method, JsonElement parameters, CancellationToken cancellationToken, string? key = null)
    {
      var capabilities = peer.ClientCapabilities;
      switch (method)
      {
        case Methods.ElicitationCreate:
          // Elicitation arrived with 2025-06-18; on an older revision the server must not send it whatever the client declared.
          if (LegacyShape.Rank(peer.ProtocolVersion) < 2)
          {
            throw new MissingCapabilityException("Elicitation does not exist in protocol revision " + peer.ProtocolVersion + ".", new ClientCapabilities { Elicitation = new ElicitationCapability() });
          }

          var url = parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String && mode.GetString() == "url";
          if (capabilities.Elicitation is null || (url && (capabilities.Elicitation.Url.ValueKind == JsonValueKind.Undefined || peer.ProtocolVersion != ProtocolVersions.V20251125)))
          {
            throw new MissingCapabilityException(
              url ? "The client does not support URL-mode elicitation." : "The client does not support elicitation.",
              new ClientCapabilities { Elicitation = url ? new ElicitationCapability { Url = ContextServer.EmptyObject } : new ElicitationCapability() });
          }

          break;
        case Methods.SamplingCreateMessage:
          if (capabilities.Sampling is null)
          {
            throw new MissingCapabilityException("The client does not support sampling.", new ClientCapabilities { Sampling = new SamplingCapability() });
          }

          break;
        case Methods.RootsList:
          if (capabilities.Roots is null)
          {
            throw new MissingCapabilityException("The client does not support roots.", new ClientCapabilities { Roots = new RootsCapability() });
          }

          break;
        default:
          throw new ProtocolException(JsonRpcErrorCodes.MethodNotFound, "Method not found: the server cannot ask the client for '" + method + "'.");
      }

      try
      {
        return await sink.SendRequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
      }
      catch (JsonRpcRemoteException remote)
      {
        throw ProtocolException.FromError(remote.Error);
      }
    }
  }
}
