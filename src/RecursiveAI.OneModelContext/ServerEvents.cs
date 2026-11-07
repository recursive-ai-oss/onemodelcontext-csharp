using System;

namespace RecursiveAI.OneModelContext;

/// <summary>What happened on the server that connected peers may want to hear about.</summary>
internal enum ServerEventKind
{
  ToolsListChanged,
  PromptsListChanged,
  ResourcesListChanged,
  ResourceUpdated,

  /// <summary>The server is ending the streams it holds open (graceful shutdown).</summary>
  Shutdown,
}

/// <summary>One thing that happened on the server.</summary>
/// <param name="Kind">What.</param>
/// <param name="Uri">The resource, for <see cref="ServerEventKind.ResourceUpdated"/>.</param>
internal readonly record struct ServerEvent(ServerEventKind Kind, string? Uri = null);

/// <summary>
/// Fans server events out to whoever holds a stream open: modern <c>subscriptions/listen</c> requests, and the legacy codec's sessions.
/// Handlers run on the thread that raised the event, so they only queue.
/// </summary>
internal sealed class ServerEvents
{
  private readonly object _gate = new();
  private Action<ServerEvent>[] _handlers = [];

  internal IDisposable Subscribe(Action<ServerEvent> handler)
  {
    lock (_gate)
    {
      _handlers = [.. _handlers, handler];
    }

    return new Registration(this, handler);
  }

  internal void Raise(ServerEvent serverEvent)
  {
    foreach (var handler in System.Threading.Volatile.Read(ref _handlers))
    {
      try
      {
        handler(serverEvent);
      }
      catch (Exception)
      {
        // One broken subscriber must not stop the others hearing about a change.
      }
    }
  }

  private sealed class Registration(ServerEvents owner, Action<ServerEvent> handler) : IDisposable
  {
    public void Dispose()
    {
      lock (owner._gate)
      {
        var list = new System.Collections.Generic.List<Action<ServerEvent>>(owner._handlers);
        list.Remove(handler);
        owner._handlers = [.. list];
      }
    }
  }
}
