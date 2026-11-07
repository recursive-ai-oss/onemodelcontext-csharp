using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;
using RecursiveAI.OneModelContext.Wire.Legacy;

namespace RecursiveAI.OneModelContext;

// Server-initiated notifications to legacy sessions: list changes and resource updates.
public sealed partial class ContextServer
{
  private readonly ConcurrentDictionary<ServerSessionState, IServerSink> _attached = new();

  /// <summary>
  /// Registers a peer so the server can notify it outside any request: list changes, and resource updates for its subscriptions.
  /// A stream connection is attached by <see cref="RunAsync"/>; a host that serves legacy sessions over HTTP attaches each session with the
  /// sink of its held stream and disposes the result when the session ends.
  /// </summary>
  /// <param name="session">The peer's state.</param>
  /// <param name="sink">Where its notifications go.</param>
  /// <returns>Disposing detaches the peer.</returns>
  public IDisposable Attach(ServerSessionState session, IServerSink sink)
  {
    ArgumentNullException.ThrowIfNull(session);
    ArgumentNullException.ThrowIfNull(sink);
    _attached[session] = sink;
    return new Detacher(_attached, session);
  }

  /// <summary>
  /// Tells every legacy session subscribed to <paramref name="uri"/> that the resource changed (<c>notifications/resources/updated</c>).
  /// Sessions that did not subscribe are not told. A peer that cannot be reached is skipped. Modern clients hear it on a
  /// <c>subscriptions/listen</c> stream that asked for this URI in <c>resourceSubscriptions</c>.
  /// </summary>
  /// <param name="uri">The resource that changed.</param>
  /// <param name="cancellationToken">Cancels the sends.</param>
  public async ValueTask NotifyResourceUpdatedAsync(string uri, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(uri);
    Events.Raise(new ServerEvent(ServerEventKind.ResourceUpdated, uri));
    var element = JsonSerializer.SerializeToElement(new ResourceUpdatedNotificationParams { Uri = uri }, WireJsonContext.Default.ResourceUpdatedNotificationParams);
    var notification = new JsonRpcNotification(Methods.NotificationsResourcesUpdated, element);
    foreach (var (session, sink) in _attached)
    {
      if (LegacySessionData.Of(session) is { } data && session.NegotiatedVersion is not null && data.Subscriptions.ContainsKey(uri))
      {
        await TrySendAsync(sink, notification, cancellationToken).ConfigureAwait(false);
      }
    }
  }

  // One path for every change: the registries and the Notify* methods raise a server event, and the event hub feeds both the modern
  // subscriptions/listen streams and, through the forwarder below, the legacy sessions.
  private void HookListChanged()
  {
    Tools.Changed += (_, _) => Events.Raise(new ServerEvent(ServerEventKind.ToolsListChanged));
    Prompts.Changed += (_, _) => Events.Raise(new ServerEvent(ServerEventKind.PromptsListChanged));
    Resources.Changed += (_, _) => Events.Raise(new ServerEvent(ServerEventKind.ResourcesListChanged));
    ResourceTemplates.Changed += (_, _) => Events.Raise(new ServerEvent(ServerEventKind.ResourcesListChanged));
    Events.Subscribe(serverEvent =>
    {
      switch (serverEvent.Kind)
      {
        case ServerEventKind.ToolsListChanged:
          _ = BroadcastListChangedAsync(Methods.NotificationsToolsListChanged, static d => d.ToolsListChanged);
          break;
        case ServerEventKind.PromptsListChanged:
          _ = BroadcastListChangedAsync(Methods.NotificationsPromptsListChanged, static d => d.PromptsListChanged);
          break;
        case ServerEventKind.ResourcesListChanged:
          _ = BroadcastListChangedAsync(Methods.NotificationsResourcesListChanged, static d => d.ResourcesListChanged);
          break;
      }
    });
  }

  // Only sessions that were told at initialize that this list can change get the notification.
  private async Task BroadcastListChangedAsync(string method, Func<LegacySessionData, bool> declared)
  {
    var notification = new JsonRpcNotification(method);
    foreach (var (session, sink) in _attached)
    {
      if (LegacySessionData.Of(session) is { } data && session.NegotiatedVersion is not null && declared(data))
      {
        await TrySendAsync(sink, notification, CancellationToken.None).ConfigureAwait(false);
      }
    }
  }

  private static async ValueTask TrySendAsync(IServerSink sink, JsonRpcNotification notification, CancellationToken cancellationToken)
  {
    try
    {
      await sink.SendNotificationAsync(notification, cancellationToken).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      // The peer went away between the snapshot and the send; its connection cleans itself up.
      ContextDiagnostics.Publish(DiagnosticKind.HandlerFault, "A notification could not be delivered (" + ex.GetType().Name + ").", notification.Method, ex);
    }
  }

  private sealed class Detacher(ConcurrentDictionary<ServerSessionState, IServerSink> map, ServerSessionState session) : IDisposable
  {
    public void Dispose() => map.TryRemove(session, out _);
  }
}
