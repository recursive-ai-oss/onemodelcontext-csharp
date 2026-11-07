using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Wire.Modern;

/// <summary>
/// The server half of revision 2026-07-28: stateless requests whose <c>_meta</c> carries the version, the client's capabilities and
/// identity (basic, versioning), <c>server/discover</c>, <c>resultType</c> on every result, cache hints and multi round-trip input.
/// </summary>
internal sealed class ModernServerCodec : IServerCodec
{
  public ProtocolEra Era => ProtocolEra.Modern;

  public ValueTask<ServerRequestDecision> BeginRequestAsync(JsonRpcRequest request, ServerSessionState session, IServerHost host, CancellationToken cancellationToken) =>
    ValueTask.FromResult(Begin(request, host));

  public ValueTask HandleNotificationAsync(JsonRpcNotification notification, ServerSessionState session, IServerHost host, CancellationToken cancellationToken) =>
    // Modern has no connection state to update; cancellation is handled by the connection before it gets here.
    ValueTask.CompletedTask;

  public JsonRpcMessage EncodeResult(JsonRpcId id, PeerContext peer, Result result, IServerHost host)
  {
    var info = WireJsonContext.Default.GetTypeInfo(result.GetType())
      ?? throw new InvalidOperationException("The result type " + result.GetType().Name + " is not a protocol record.");
    var serialized = JsonSerializer.SerializeToElement(result, info);
    var wrapped = ModernEnvelope.WrapResult(serialized, host.Info, result is CacheableResult, (long)host.CacheTtl.TotalMilliseconds, host.CacheScope);
    return new JsonRpcResultResponse(id, wrapped);
  }

  public JsonRpcMessage EncodeError(JsonRpcId id, PeerContext? peer, ProtocolException failure) => new JsonRpcErrorResponse(id, failure.ToError());

  public IInputChannel CreateInputChannel(PeerContext peer, ServerSessionState session, IServerSink sink) => new ModernInputChannel(peer);

  public bool AcceptsBatches(ServerSessionState session) => false;

  private ServerRequestDecision Begin(JsonRpcRequest request, IServerHost host)
  {
    try
    {
      return Read(request, host);
    }
    catch (JsonException)
    {
      return Reject(request, JsonRpcErrorCodes.InvalidParams, "Invalid params: the request metadata is malformed.");
    }
    catch (InvalidOperationException)
    {
      // A JsonElement of an unexpected kind: the same verdict as any other malformed metadata.
      return Reject(request, JsonRpcErrorCodes.InvalidParams, "Invalid params: the request metadata is malformed.");
    }
  }

  private ServerRequestDecision Read(JsonRpcRequest request, IServerHost host)
  {
    // 2026-07-28 basic, per-request protocol fields: a request missing a required field is malformed (-32602).
    if (request.Params.ValueKind != JsonValueKind.Object
      || !request.Params.TryGetProperty("_meta", out var metaElement)
      || metaElement.ValueKind != JsonValueKind.Object
      || !metaElement.TryGetProperty(WireNames.MetaProtocolVersion, out var versionElement)
      || versionElement.ValueKind != JsonValueKind.String)
    {
      return Reject(request, JsonRpcErrorCodes.InvalidParams, "Invalid params: _meta must carry " + WireNames.MetaProtocolVersion + " and " + WireNames.MetaClientCapabilities + ".");
    }

    // Version first: an unsupported client needs the supported list more than it needs a lecture about missing fields.
    var version = versionElement.GetString()!;
    if (version != ProtocolVersions.V20260728 || !Contains(host.SupportedVersions, version))
    {
      // 2026-07-28 basic/versioning: -32022 lists what the server supports so the client can pick and retry.
      var data = JsonSerializer.SerializeToElement(new UnsupportedProtocolVersionData { Supported = [.. host.SupportedVersions], Requested = version }, WireJsonContext.Default.UnsupportedProtocolVersionData);
      return Respond(request, new JsonRpcError(ProtocolErrorCodes.UnsupportedProtocolVersion, "Unsupported protocol version", data));
    }

    if (!metaElement.TryGetProperty(WireNames.MetaClientCapabilities, out var capabilitiesElement) || capabilitiesElement.ValueKind != JsonValueKind.Object)
    {
      return Reject(request, JsonRpcErrorCodes.InvalidParams, "Invalid params: _meta must carry " + WireNames.MetaClientCapabilities + ".");
    }

    var meta = metaElement.Deserialize(WireJsonContext.Default.MetaObject)!;
    var capabilities = capabilitiesElement.Deserialize(WireJsonContext.Default.ClientCapabilities)!;

    Dictionary<string, JsonElement>? inputResponses = null;
    if (request.Params.TryGetProperty("inputResponses", out var responsesElement))
    {
      if (responsesElement.ValueKind != JsonValueKind.Object)
      {
        return Reject(request, JsonRpcErrorCodes.InvalidParams, "Invalid params: inputResponses must be an object.");
      }

      inputResponses = responsesElement.Deserialize(WireJsonContext.Default.DictionaryStringJsonElement);
    }

    string? requestState = null;
    if (request.Params.TryGetProperty("requestState", out var stateElement))
    {
      if (stateElement.ValueKind != JsonValueKind.String)
      {
        return Reject(request, JsonRpcErrorCodes.InvalidParams, "Invalid params: requestState must be a string.");
      }

      requestState = stateElement.GetString();
    }

    var peer = new PeerContext
    {
      Era = ProtocolEra.Modern,
      ProtocolVersion = version,
      ClientInfo = meta.ClientInfo,
      ClientCapabilities = capabilities,
      LogLevel = meta.LogLevel,
      ProgressToken = meta.ProgressToken,
      Meta = meta,
      InputResponses = inputResponses,
      RequestState = requestState,
    };

    return request.Method == Methods.ServerDiscover
      ? ServerRequestDecision.Respond(Discover(request.Id, peer, host))
      : ServerRequestDecision.Dispatch(peer);
  }

  private JsonRpcMessage Discover(JsonRpcId id, PeerContext peer, IServerHost host)
  {
    var capabilities = host.Capabilities;

    // 2026-07-28 replaced resources/subscribe with subscriptions/listen, which this codec serves for any registered resource, and
    // log messages are available to any request that sets a level.
    var withModernFeatures = new ServerCapabilities
    {
      Experimental = capabilities.Experimental,
      Logging = capabilities.Logging.ValueKind == JsonValueKind.Undefined ? EmptyObject : capabilities.Logging,
      Completions = capabilities.Completions,
      Prompts = capabilities.Prompts,
      Resources = capabilities.Resources is { } resources ? new ResourcesCapability { Subscribe = resources.Subscribe ?? true, ListChanged = resources.ListChanged, AdditionalProperties = resources.AdditionalProperties } : null,
      Tools = capabilities.Tools,
      Tasks = capabilities.Tasks,
      Extensions = host.ExtensionsFor(peer.ProtocolVersion),
      AdditionalProperties = capabilities.AdditionalProperties,
    };

    var result = new DiscoverResult
    {
      SupportedVersions = [.. host.SupportedVersions],
      Capabilities = withModernFeatures,
      Instructions = host.Instructions,
    };

    return EncodeResult(id, peer, result, host);
  }

  private static JsonElement EmptyObject { get; } = JsonDocument.Parse("{}").RootElement;

  private static bool Contains(IReadOnlyList<string> versions, string version)
  {
    foreach (var candidate in versions)
    {
      if (candidate == version)
      {
        return true;
      }
    }

    return false;
  }

  private static ServerRequestDecision Reject(JsonRpcRequest request, int code, string message) =>
    Respond(request, new JsonRpcError(code, message));

  private static ServerRequestDecision Respond(JsonRpcRequest request, JsonRpcError error) =>
    ServerRequestDecision.Respond(new JsonRpcErrorResponse(request.Id, error));
}
