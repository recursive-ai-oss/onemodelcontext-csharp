using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext;

/// <summary>What a <see cref="SubscriptionEvent"/> reports.</summary>
public enum SubscriptionEventKind
{
  /// <summary>The server's tool list changed (<c>notifications/tools/list_changed</c>).</summary>
  ToolsListChanged,

  /// <summary>The server's prompt list changed (<c>notifications/prompts/list_changed</c>).</summary>
  PromptsListChanged,

  /// <summary>The server's resource list changed (<c>notifications/resources/list_changed</c>).</summary>
  ResourcesListChanged,

  /// <summary>A subscribed resource changed (<c>notifications/resources/updated</c>); see <see cref="SubscriptionEvent.Uri"/>.</summary>
  ResourceUpdated,
}

/// <summary>One notification delivered to a <see cref="ContextSubscription"/>.</summary>
/// <param name="Kind">What changed.</param>
/// <param name="Uri">The resource, for <see cref="SubscriptionEventKind.ResourceUpdated"/>; otherwise null.</param>
/// <param name="Notification">The notification as the server sent it, with its <c>_meta</c>.</param>
public readonly record struct SubscriptionEvent(SubscriptionEventKind Kind, string? Uri, JsonRpcNotification Notification);

/// <summary>How a <see cref="ContextSubscription"/> ended.</summary>
public enum SubscriptionEnd
{
  /// <summary>The application disposed it.</summary>
  Closed,

  /// <summary>The server ended it on purpose (2026-07-28: a final <c>subscriptions/listen</c> result).</summary>
  ServerEnded,

  /// <summary>The stream or connection broke. The server holds no state for it; open a new subscription to continue.</summary>
  ConnectionLost,
}

/// <summary>
/// A stream of change notifications the server sends the client: list changes and resource updates. On 2026-07-28 it is one
/// <c>subscriptions/listen</c> request; on earlier revisions it is <c>resources/subscribe</c> for each resource plus the list-changed
/// notifications the server sends on its own, so the same code works on both. Read events with <see cref="ReadAllAsync"/>; dispose to end it.
/// </summary>
public sealed class ContextSubscription : IAsyncDisposable
{
  private readonly Channel<SubscriptionEvent> _events = Channel.CreateBounded<SubscriptionEvent>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
  private readonly TaskCompletionSource<SubscriptionFilter> _acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
  private readonly TaskCompletionSource<SubscriptionEnd> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
  private readonly Func<ContextSubscription, ValueTask> _close;
  private int _closing;

  internal ContextSubscription(SubscriptionFilter requested, Func<ContextSubscription, ValueTask> close)
  {
    Requested = requested;
    _close = close;
  }

  /// <summary>What the application asked for.</summary>
  public SubscriptionFilter Requested { get; }

  /// <summary>The part of the request the server agreed to honour (2026-07-28 <c>notifications/subscriptions/acknowledged</c>). Types it left out will not arrive.</summary>
  public SubscriptionFilter Acknowledged { get; private set; } = new();

  /// <summary>Completes when the subscription ends, with how it ended.</summary>
  public Task<SubscriptionEnd> Completion => _completion.Task;

  internal Task<SubscriptionFilter> AcknowledgedTask => _acknowledged.Task;

  internal CancellationTokenSource Cancellation { get; } = new();

  /// <summary>The id of the <c>subscriptions/listen</c> request (modern), which tags every notification of this subscription.</summary>
  internal JsonRpcId RequestId { get; set; }

  /// <summary>Events in the order they arrived, until the subscription ends. Only one reader should enumerate.</summary>
  /// <param name="cancellationToken">Stops the enumeration; it does not end the subscription.</param>
  /// <remarks>If the application reads slower than events arrive, the oldest of more than 4096 waiting events are dropped; list changes carry no payload, so the reader still learns that something changed.</remarks>
  public async IAsyncEnumerable<SubscriptionEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
  {
    await foreach (var item in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
    {
      yield return item;
    }
  }

  /// <summary>Ends the subscription: on 2026-07-28 the stream is closed (stdio: cancelled), on earlier revisions the resources are unsubscribed.</summary>
  public async ValueTask DisposeAsync()
  {
    if (Interlocked.Exchange(ref _closing, 1) != 0)
    {
      return;
    }

    try
    {
      await _close(this).ConfigureAwait(false);
    }
    finally
    {
      Finish(SubscriptionEnd.Closed);
      Cancellation.Dispose();
    }
  }

  internal void Acknowledge(SubscriptionFilter honoured)
  {
    Acknowledged = honoured;
    _acknowledged.TrySetResult(honoured);
  }

  internal void Fail(Exception error) => _acknowledged.TrySetException(error);

  internal bool Offer(SubscriptionEvent item) => _events.Writer.TryWrite(item);

  internal void Finish(SubscriptionEnd how)
  {
    _acknowledged.TrySetException(new TransportException("The subscription ended before the server acknowledged it."));
    _events.Writer.TryComplete();
    _completion.TrySetResult(how);
  }
}
