using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>
/// A minimal codec, only for testing the shared dispatcher and the contracts. It does none of an era's real work (no discovery,
/// no handshake, no signed state): it reads the progress token and forwards input requests to the client.
/// </summary>
internal sealed class TestServerCodec(ProtocolEra era, string version, bool elicitation = true) : IServerCodec
{
  public ProtocolEra Era => era;

  public ValueTask<ServerRequestDecision> BeginRequestAsync(JsonRpcRequest request, ServerSessionState session, IServerHost host, CancellationToken cancellationToken)
  {
    if (request.Method == "test/reject")
    {
      return ValueTask.FromResult(ServerRequestDecision.Respond(new JsonRpcErrorResponse(request.Id, new JsonRpcError(ProtocolErrorCodes.UnsupportedProtocolVersion, "rejected"))));
    }

    if (request.Method == "initialize")
    {
      session.Era = era;
      session.NegotiatedVersion = version;
      return ValueTask.FromResult(ServerRequestDecision.Respond(new JsonRpcResultResponse(request.Id, Json.Parse("{}"))));
    }

    MetaObject? meta = null;
    if (request.Params.ValueKind == JsonValueKind.Object && request.Params.TryGetProperty("_meta", out var metaElement))
    {
      meta = metaElement.Deserialize(WireJsonContext.Default.MetaObject);
    }

    return ValueTask.FromResult(ServerRequestDecision.Dispatch(new PeerContext
    {
      Era = era,
      ProtocolVersion = version,
      ClientInfo = meta?.ClientInfo,
      ClientCapabilities = elicitation ? new ClientCapabilities { Elicitation = new ElicitationCapability() } : new ClientCapabilities(),
      ProgressToken = meta?.ProgressToken,
      LogLevel = meta?.LogLevel,
      Meta = meta,
    }));
  }

  public ValueTask HandleNotificationAsync(JsonRpcNotification notification, ServerSessionState session, IServerHost host, CancellationToken cancellationToken)
  {
    if (notification.Method == "notifications/initialized")
    {
      session.Initialized = true;
    }

    return ValueTask.CompletedTask;
  }

  public JsonRpcMessage EncodeResult(JsonRpcId id, PeerContext peer, Result result, IServerHost host)
  {
    var info = WireJsonContext.Default.GetTypeInfo(result.GetType())!;
    return new JsonRpcResultResponse(id, JsonSerializer.SerializeToElement(result, info));
  }

  public JsonRpcMessage EncodeError(JsonRpcId id, PeerContext? peer, ProtocolException failure) => new JsonRpcErrorResponse(id, failure.ToError());

  public IInputChannel CreateInputChannel(PeerContext peer, ServerSessionState session, IServerSink sink) => new ForwardingChannel(sink);

  public bool AcceptsBatches(ServerSessionState session) => era == ProtocolEra.Legacy && session.NegotiatedVersion == ProtocolVersions.V20250326;

  private sealed class ForwardingChannel(IServerSink sink) : IInputChannel
  {
    public ValueTask<JsonElement> RequestInputAsync(string method, JsonElement parameters, CancellationToken cancellationToken, string? key = null) =>
      sink.SendRequestAsync(method, parameters, cancellationToken);
  }
}

internal sealed class TestClientCodec(ProtocolEra era, string version) : IClientCodec
{
  public ProtocolEra Era => era;

  public ValueTask<ClientSession?> NegotiateAsync(JsonRpcConnection connection, ClientHello hello, CancellationToken cancellationToken) =>
    ValueTask.FromResult<ClientSession?>(new ClientSession(this, version, new Implementation { Name = "test-server", Version = "1" }, new ServerCapabilities()));

  public JsonElement EncodeParams(ClientSession session, ClientHello hello, string method, RequestParams? parameters)
  {
    if (parameters is null)
    {
      return default;
    }

    var info = WireJsonContext.Default.GetTypeInfo(parameters.GetType())!;
    return JsonSerializer.SerializeToElement(parameters, info);
  }

  public Result DecodeResult(ClientSession session, string method, JsonElement result) => ResultTypes.Decode(method, result);
}

/// <summary>A client codec that does not speak its era: negotiation finds nothing.</summary>
internal sealed class RefusedClientCodec(ProtocolEra era) : IClientCodec
{
  public ProtocolEra Era => era;

  public ValueTask<ClientSession?> NegotiateAsync(JsonRpcConnection connection, ClientHello hello, CancellationToken cancellationToken) => ValueTask.FromResult<ClientSession?>(null);

  public JsonElement EncodeParams(ClientSession session, ClientHello hello, string method, RequestParams? parameters) => throw new NotSupportedException();

  public Result DecodeResult(ClientSession session, string method, JsonElement result) => throw new NotSupportedException();
}

internal sealed class RecordingSink : IServerSink
{
  public List<JsonRpcNotification> Notifications { get; } = [];

  public Func<string, JsonElement, JsonElement>? Answer { get; set; }

  public ValueTask SendNotificationAsync(JsonRpcNotification notification, CancellationToken cancellationToken)
  {
    lock (Notifications)
    {
      Notifications.Add(notification);
    }

    return ValueTask.CompletedTask;
  }

  public ValueTask<JsonElement> SendRequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken) =>
    Answer is { } answer ? ValueTask.FromResult(answer(method, parameters)) : throw new NotSupportedException();
}
