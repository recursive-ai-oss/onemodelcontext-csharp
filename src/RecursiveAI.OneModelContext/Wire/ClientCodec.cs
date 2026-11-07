using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Wire;

/// <summary>What a client offers when it negotiates.</summary>
public sealed class ClientHello
{
  /// <summary>Who the client is.</summary>
  public required Implementation ClientInfo { get; init; }

  /// <summary>What the client supports.</summary>
  public required ClientCapabilities Capabilities { get; init; }

  /// <summary>The revisions the client speaks, newest first.</summary>
  public required IReadOnlyList<string> SupportedVersions { get; init; }

  /// <summary>The clock for timeouts.</summary>
  public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>The outcome of negotiation: what the client and server agreed on, and the codec that carries it.</summary>
public sealed class ClientSession
{
  /// <summary>Creates a session.</summary>
  /// <param name="codec">The codec that negotiated it.</param>
  /// <param name="protocolVersion">The revision in force.</param>
  /// <param name="serverInfo">Who the server is.</param>
  /// <param name="serverCapabilities">What the server supports.</param>
  public ClientSession(IClientCodec codec, string protocolVersion, Implementation serverInfo, ServerCapabilities serverCapabilities)
  {
    ArgumentNullException.ThrowIfNull(codec);
    ArgumentNullException.ThrowIfNull(protocolVersion);
    ArgumentNullException.ThrowIfNull(serverInfo);
    ArgumentNullException.ThrowIfNull(serverCapabilities);
    Codec = codec;
    ProtocolVersion = protocolVersion;
    ServerInfo = serverInfo;
    ServerCapabilities = serverCapabilities;
  }

  /// <summary>The codec that negotiated this session and encodes its requests.</summary>
  public IClientCodec Codec { get; }

  /// <summary>The era.</summary>
  public ProtocolEra Era => Codec.Era;

  /// <summary>The revision in force.</summary>
  public string ProtocolVersion { get; }

  /// <summary>Who the server is.</summary>
  public Implementation ServerInfo { get; }

  /// <summary>What the server supports.</summary>
  public ServerCapabilities ServerCapabilities { get; }

  /// <summary>Hints for the model on using the server.</summary>
  public string? Instructions { get; init; }

  /// <summary>Legacy over HTTP: the session id the server issued.</summary>
  public string? SessionId { get; init; }

  /// <summary>The revisions the server said it speaks (modern discovery), or empty.</summary>
  public IReadOnlyList<string> ServerVersions { get; init; } = [];
}

/// <summary>The answers a client can give to what a server asks of it.</summary>
public interface IClientRequestHandler
{
  /// <summary>Answers a server-initiated request or a modern input request.</summary>
  /// <param name="method"><c>sampling/createMessage</c>, <c>roots/list</c> or <c>elicitation/create</c>.</param>
  /// <param name="parameters">The request's params.</param>
  /// <param name="cancellationToken">Cancelled when the request is.</param>
  /// <returns>The result, or throws <see cref="ProtocolException"/> (method not found when the client cannot answer).</returns>
  ValueTask<Result> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken);
}

/// <summary>The client half of an era's wire behaviour.</summary>
public interface IClientCodec
{
  /// <summary>The era this codec speaks.</summary>
  ProtocolEra Era { get; }

  /// <summary>
  /// Tries to establish a session with the server. Modern probes with <c>server/discover</c>; legacy sends <c>initialize</c> and
  /// <c>notifications/initialized</c>. Returns null when the server does not speak this era, so the client can try the other codec.
  /// </summary>
  /// <param name="connection">The connection to the server.</param>
  /// <param name="hello">What the client offers.</param>
  /// <param name="cancellationToken">Cancels negotiation.</param>
  /// <exception cref="ProtocolVersionException">The server speaks this era but no revision in common.</exception>
  ValueTask<ClientSession?> NegotiateAsync(JsonRpcConnection connection, ClientHello hello, CancellationToken cancellationToken);

  /// <summary>Encodes a request's params for the wire. Modern adds the version, client info and capabilities to <c>_meta</c>.</summary>
  /// <param name="session">The session.</param>
  /// <param name="hello">What the client declared.</param>
  /// <param name="method">The method.</param>
  /// <param name="parameters">The typed params, or null for none.</param>
  JsonElement EncodeParams(ClientSession session, ClientHello hello, string method, RequestParams? parameters);

  /// <summary>Decodes a result into its record, including an <see cref="InputRequiredResult"/> when the server asks for input.</summary>
  /// <param name="session">The session.</param>
  /// <param name="method">The method the result answers.</param>
  /// <param name="result">The result value.</param>
  /// <exception cref="ProtocolException">The result is malformed.</exception>
  Result DecodeResult(ClientSession session, string method, JsonElement result);
}

/// <summary>Maps a method name to the record its result decodes into. Shared by both client codecs.</summary>
public static class ResultTypes
{
  /// <summary>Decodes a result of a method that the client calls.</summary>
  /// <param name="method">The method.</param>
  /// <param name="result">The result value.</param>
  /// <returns>The record; a result with <c>resultType</c> <c>input_required</c> decodes as <see cref="InputRequiredResult"/> whatever the method.</returns>
  /// <exception cref="ProtocolException">The method has no known result type, or the result does not match it (-32603 on this side).</exception>
  public static Result Decode(string method, JsonElement result)
  {
    ArgumentNullException.ThrowIfNull(method);
    try
    {
      if (result.ValueKind == JsonValueKind.Object
        && result.TryGetProperty("resultType", out var type) && type.ValueKind == JsonValueKind.String
        && type.GetString() == WireNames.ResultTypeInputRequired)
      {
        return result.Deserialize(WireJsonContext.Default.InputRequiredResult) ?? throw new JsonException("null result");
      }

      var context = WireJsonContext.Default;
      Result? decoded = method switch
      {
        Methods.ServerDiscover => result.Deserialize(context.DiscoverResult),
        Methods.Initialize => result.Deserialize(context.InitializeResult),
        Methods.Ping or Methods.LoggingSetLevel or Methods.ResourcesSubscribe or Methods.ResourcesUnsubscribe => result.Deserialize(context.EmptyResult),
        Methods.ToolsList => result.Deserialize(context.ListToolsResult),
        Methods.ToolsCall => result.Deserialize(context.CallToolResult),
        Methods.ResourcesList => result.Deserialize(context.ListResourcesResult),
        Methods.ResourcesTemplatesList => result.Deserialize(context.ListResourceTemplatesResult),
        Methods.ResourcesRead => result.Deserialize(context.ReadResourceResult),
        Methods.PromptsList => result.Deserialize(context.ListPromptsResult),
        Methods.PromptsGet => result.Deserialize(context.GetPromptResult),
        Methods.CompletionComplete => result.Deserialize(context.CompleteResult),
        Methods.SubscriptionsListen => result.Deserialize(context.SubscriptionsListenResult),
        _ => throw new ProtocolException(JsonRpcErrorCodes.InternalError, "No result type is known for method '" + method + "'."),
      };

      return decoded ?? throw new JsonException("null result");
    }
    catch (JsonException ex)
    {
      throw new ProtocolException(JsonRpcErrorCodes.InternalError, "The result of '" + method + "' is not valid.", default, ex);
    }
  }
}
