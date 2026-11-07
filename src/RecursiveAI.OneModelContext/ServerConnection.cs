using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;

namespace RecursiveAI.OneModelContext;

/// <summary>One stream or stdio connection to a peer: a JSON-RPC connection that feeds the server and carries its notifications.</summary>
internal sealed class ServerConnection : IServerSink, IAsyncDisposable
{
  private readonly ContextServer _server;
  private readonly JsonRpcConnection _rpc;
  private readonly ServerSessionState _session = new();

  internal ServerConnection(ContextServer server, IJsonRpcChannel channel)
  {
    _server = server;
    _rpc = new JsonRpcConnection(channel, new JsonRpcConnectionOptions
    {
      RequestHandler = HandleRequestAsync,
      NotificationHandler = HandleNotificationAsync,
      HandlerFaulted = ex => ContextDiagnostics.Publish(DiagnosticKind.HandlerFault, "A request handler threw " + ex.GetType().Name + ".", null, ex),
      MaxConcurrentRequests = server.Options.MaxConcurrentRequests,
      RequestTimeout = server.Options.RequestTimeout,
      ReaderOptions = server.Options.ReaderOptions,
      TimeProvider = server.Options.TimeProvider,
    });
  }

  internal Task<JsonRpcCloseReason> RunAsync(CancellationToken cancellationToken) => _rpc.RunAsync(cancellationToken);

  public ValueTask SendNotificationAsync(JsonRpcNotification notification, CancellationToken cancellationToken) =>
    _rpc.SendAsync(notification, cancellationToken);

  public ValueTask<JsonElement> SendRequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken) =>
    _rpc.SendRequestAsync(method, parameters, cancellationToken);

  public ValueTask DisposeAsync() => _rpc.DisposeAsync();

  private async ValueTask<JsonRpcMessage?> HandleRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken)
  {
    var response = await _server.DispatchRequestAsync(request, _session, this, principal: null, hasLegacySessionId: false, cancellationToken).ConfigureAwait(false);

    // Batches are allowed only once 2025-03-26 has been negotiated.
    _rpc.AcceptBatches = _session.Era is { } era && _server.FindCodec(era)?.AcceptsBatches(_session) == true;
    return response;
  }

  private async ValueTask HandleNotificationAsync(JsonRpcNotification notification, CancellationToken cancellationToken)
  {
    if (notification.Method == Methods.NotificationsCancelled)
    {
      // Cancellation has the same wire form in every revision: stop the handler of the named request.
      if (notification.Params.ValueKind == JsonValueKind.Object
        && notification.Params.TryGetProperty("requestId", out var idElement)
        && JsonRpcId.TryFromElement(idElement, out var id))
      {
        _rpc.CancelIncomingRequest(id);
      }

      return;
    }

    await _server.DispatchNotificationAsync(notification, _session, cancellationToken).ConfigureAwait(false);
    _rpc.AcceptBatches = _session.Era is { } era && _server.FindCodec(era)?.AcceptsBatches(_session) == true;
  }
}
