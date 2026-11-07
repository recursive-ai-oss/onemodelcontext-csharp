using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext;

// Change notifications: subscriptions/listen on 2026-07-28, resources/subscribe plus the list-changed notifications before it.
public sealed partial class ContextClient
{
  private readonly ConcurrentDictionary<JsonRpcId, ContextSubscription> _listens = new();
  private readonly ConcurrentDictionary<ContextSubscription, SubscriptionFilter> _legacyListens = new();

  /// <summary>
  /// Opens a stream of change notifications and waits until the server accepts it. On 2026-07-28 this is a <c>subscriptions/listen</c>
  /// request; on earlier revisions it subscribes to each resource and filters the list-changed notifications the server sends anyway, so
  /// the code is the same. Over HTTP the stream is the response of that request on 2026-07-28 and the standalone GET stream before it.
  /// </summary>
  /// <param name="filter">What to hear about. A type left out is not sent.</param>
  /// <param name="cancellationToken">Cancels opening the subscription.</param>
  /// <returns>The subscription; dispose it to end it. <see cref="ContextSubscription.Acknowledged"/> says which parts the server honours.</returns>
  /// <exception cref="ProtocolException">The server rejected the request.</exception>
  /// <exception cref="TransportException">The connection ended before the server accepted the subscription.</exception>
  public async ValueTask<ContextSubscription> ListenAsync(SubscriptionFilter filter, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(filter);
    return Era == ProtocolEra.Modern
      ? await ListenModernAsync(filter, cancellationToken).ConfigureAwait(false)
      : await ListenLegacyAsync(filter, cancellationToken).ConfigureAwait(false);
  }

  private async ValueTask<ContextSubscription> ListenModernAsync(SubscriptionFilter filter, CancellationToken cancellationToken)
  {
    var subscription = new ContextSubscription(filter, DisposeModernAsync);
    var parameters = _session.Codec.EncodeParams(_session, _hello, Methods.SubscriptionsListen, new SubscriptionsListenRequestParams { Notifications = filter });
    var requestOptions = new JsonRpcRequestOptions
    {
      // The request lasts as long as the subscription: no response timeout.
      Timeout = Timeout.InfiniteTimeSpan,
      IdAssigned = id =>
      {
        subscription.RequestId = id;
        _listens[id] = subscription;
      },
    };

    _ = Task.Run(async () =>
    {
      var how = SubscriptionEnd.ConnectionLost;
      try
      {
        // 2026-07-28 subscriptions, "Graceful Closure": a result ends it on purpose; an error or a dropped stream does not.
        await _rpc.SendRequestAsync(Methods.SubscriptionsListen, parameters, requestOptions, subscription.Cancellation.Token).ConfigureAwait(false);
        how = SubscriptionEnd.ServerEnded;
      }
      catch (OperationCanceledException)
      {
        how = SubscriptionEnd.Closed;
      }
      catch (JsonRpcRemoteException remote)
      {
        subscription.Fail(ProtocolException.FromError(remote.Error));
      }
      catch (Exception ex) when (ex is JsonRpcClosedException or TimeoutException)
      {
        subscription.Fail(new TransportException("The connection closed before the server accepted the subscription.", ex));
      }
      finally
      {
        if (subscription.RequestId.HasValue)
        {
          _listens.TryRemove(subscription.RequestId, out _);
        }

        subscription.Finish(how);
      }
    }, CancellationToken.None);

    try
    {
      await subscription.AcknowledgedTask.WaitAsync(_options.RequestTimeout, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
      return subscription;
    }
    catch
    {
      await subscription.DisposeAsync().ConfigureAwait(false);
      throw;
    }
  }

  // Closing the request ends the stream: the connection tells the transport the request was cancelled (HTTP closes the response stream,
  // stdio sends notifications/cancelled).
  private static ValueTask DisposeModernAsync(ContextSubscription subscription)
  {
    try
    {
      subscription.Cancellation.Cancel();
    }
    catch (ObjectDisposedException)
    {
      // Already ended.
    }

    return ValueTask.CompletedTask;
  }

  private async ValueTask<ContextSubscription> ListenLegacyAsync(SubscriptionFilter filter, CancellationToken cancellationToken)
  {
    // Before 2026-07-28 the server sends list changes to every client whose capabilities say it will, and resource updates only after
    // resources/subscribe, which only some servers offer. The acknowledged filter says what that adds up to.
    var capabilities = ServerCapabilities;
    var resources = capabilities.Resources?.Subscribe == true && filter.ResourceSubscriptions is { Count: > 0 } wanted ? new List<string>(wanted) : null;
    var honoured = new SubscriptionFilter
    {
      ToolsListChanged = filter.ToolsListChanged == true && capabilities.Tools?.ListChanged == true ? true : null,
      PromptsListChanged = filter.PromptsListChanged == true && capabilities.Prompts?.ListChanged == true ? true : null,
      ResourcesListChanged = filter.ResourcesListChanged == true && capabilities.Resources?.ListChanged == true ? true : null,
      ResourceSubscriptions = resources,
    };

    var subscription = new ContextSubscription(filter, DisposeLegacyAsync);
    var subscribed = new List<string>();
    try
    {
      foreach (var uri in resources ?? [])
      {
        await SubscribeResourceAsync(uri, cancellationToken).ConfigureAwait(false);
        subscribed.Add(uri);
      }
    }
    catch
    {
      foreach (var uri in subscribed)
      {
        try
        {
          await UnsubscribeResourceAsync(uri, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ContextException or JsonRpcClosedException)
        {
          // Best effort.
        }
      }

      subscription.Finish(SubscriptionEnd.Closed);
      throw;
    }

    _legacyListens[subscription] = honoured;
    subscription.Acknowledge(honoured);
    return subscription;
  }

  private async ValueTask DisposeLegacyAsync(ContextSubscription subscription)
  {
    if (!_legacyListens.TryRemove(subscription, out var honoured))
    {
      return;
    }

    foreach (var uri in honoured.ResourceSubscriptions ?? [])
    {
      // Another subscription may still want the resource.
      if (IsStillWanted(uri))
      {
        continue;
      }

      try
      {
        await UnsubscribeResourceAsync(uri, CancellationToken.None).ConfigureAwait(false);
      }
      catch (Exception ex) when (ex is ContextException or JsonRpcClosedException or TimeoutException)
      {
        // The connection is going away or the server forgot; nothing to repair.
      }
    }
  }

  private bool IsStillWanted(string uri)
  {
    foreach (var other in _legacyListens.Values)
    {
      if (other.ResourceSubscriptions?.Contains(uri) == true)
      {
        return true;
      }
    }

    return false;
  }

  private void EndSubscriptions()
  {
    foreach (var subscription in _legacyListens.Keys)
    {
      subscription.Finish(SubscriptionEnd.ConnectionLost);
    }

    _legacyListens.Clear();
  }

  // Routes a notification to the subscriptions it belongs to. Called on the read loop, so it only queues.
  private void RouteToSubscriptions(JsonRpcNotification notification)
  {
    SubscriptionEventKind kind;
    switch (notification.Method)
    {
      case Methods.NotificationsToolsListChanged:
        kind = SubscriptionEventKind.ToolsListChanged;
        break;
      case Methods.NotificationsPromptsListChanged:
        kind = SubscriptionEventKind.PromptsListChanged;
        break;
      case Methods.NotificationsResourcesListChanged:
        kind = SubscriptionEventKind.ResourcesListChanged;
        break;
      case Methods.NotificationsResourcesUpdated:
        kind = SubscriptionEventKind.ResourceUpdated;
        break;
      case Methods.NotificationsSubscriptionsAcknowledged:
        if (TryGetSubscription(notification, out var target))
        {
          try
          {
            var ack = notification.Params.Deserialize(WireJsonContext.Default.SubscriptionsAcknowledgedNotificationParams);
            target.Acknowledge(ack?.Notifications ?? new SubscriptionFilter());
          }
          catch (JsonException)
          {
            target.Acknowledge(new SubscriptionFilter());
          }
        }

        return;
      default:
        return;
    }

    string? uri = null;
    if (kind == SubscriptionEventKind.ResourceUpdated
      && notification.Params.ValueKind == JsonValueKind.Object && notification.Params.TryGetProperty("uri", out var uriElement) && uriElement.ValueKind == JsonValueKind.String)
    {
      uri = uriElement.GetString();
    }

    var item = new SubscriptionEvent(kind, uri, notification);
    if (Era == ProtocolEra.Modern)
    {
      // 2026-07-28 subscriptions: every notification of a stream carries the id of the request that opened it.
      if (TryGetSubscription(notification, out var subscription))
      {
        subscription.Offer(item);
      }

      return;
    }

    foreach (var (legacy, honoured) in _legacyListens)
    {
      var wanted = kind switch
      {
        SubscriptionEventKind.ToolsListChanged => honoured.ToolsListChanged == true,
        SubscriptionEventKind.PromptsListChanged => honoured.PromptsListChanged == true,
        SubscriptionEventKind.ResourcesListChanged => honoured.ResourcesListChanged == true,
        _ => uri is not null && honoured.ResourceSubscriptions?.Contains(uri) == true,
      };

      if (wanted)
      {
        legacy.Offer(item);
      }
    }
  }

  private bool TryGetSubscription(JsonRpcNotification notification, out ContextSubscription subscription)
  {
    subscription = null!;
    return notification.Params.ValueKind == JsonValueKind.Object
      && notification.Params.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object
      && meta.TryGetProperty(WireNames.MetaSubscriptionId, out var idElement)
      && JsonRpcId.TryFromElement(idElement, out var id)
      && _listens.TryGetValue(id, out subscription!);
  }
}
