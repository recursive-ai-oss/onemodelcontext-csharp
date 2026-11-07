using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.Hosting;

/// <summary>
/// A mutual-exclusion lock that can be awaited. Holders may await while they hold it, which <c>lock</c> forbids. It owns no handle or
/// timer, so it needs no disposal. Waiters are served in order; a waiter whose token is cancelled leaves the queue.
/// </summary>
internal sealed class AsyncGate
{
  private readonly object _sync = new();
  private readonly LinkedList<TaskCompletionSource> _waiters = new();
  private bool _held;

  internal ValueTask WaitAsync(CancellationToken cancellationToken = default)
  {
    TaskCompletionSource waiter;
    LinkedListNode<TaskCompletionSource> node;
    lock (_sync)
    {
      if (!_held)
      {
        _held = true;
        return ValueTask.CompletedTask;
      }

      if (cancellationToken.IsCancellationRequested)
      {
        return ValueTask.FromCanceled(cancellationToken);
      }

      waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      node = _waiters.AddLast(waiter);
    }

    if (!cancellationToken.CanBeCanceled)
    {
      return new ValueTask(waiter.Task);
    }

    return new ValueTask(WaitWithCancellationAsync(waiter, node, cancellationToken));
  }

  internal void Release()
  {
    TaskCompletionSource? next = null;
    lock (_sync)
    {
      if (_waiters.First is { } first)
      {
        next = first.Value;
        _waiters.RemoveFirst();
      }
      else
      {
        _held = false;
      }
    }

    // Ownership passes straight to the next waiter; _held stays true.
    next?.TrySetResult();
  }

  private async Task WaitWithCancellationAsync(TaskCompletionSource waiter, LinkedListNode<TaskCompletionSource> node, CancellationToken cancellationToken)
  {
    await using (cancellationToken.Register(
      static state =>
      {
        var (gate, w, n) = ((AsyncGate, TaskCompletionSource, LinkedListNode<TaskCompletionSource>))state!;
        lock (gate._sync)
        {
          if (n.List is not null)
          {
            gate._waiters.Remove(n);
            w.TrySetCanceled();
          }
        }
      },
      (this, waiter, node)).ConfigureAwait(false))
    {
      await waiter.Task.ConfigureAwait(false);
    }
  }
}
