using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.Extensions.Tasks;

/// <summary>
/// Keeps tasks in memory. Fast and simple, and the default: tasks are lost when the process ends, and a second server instance cannot see
/// them, so it suits one process only. Use <see cref="FileTaskStore"/> to survive a restart, or a database store to share tasks between instances.
/// </summary>
[Experimental(ExperimentalIds.Tasks)]
public sealed class MemoryTaskStore : ITaskStore
{
  private readonly object _gate = new();
  private readonly Dictionary<string, TaskRecord> _tasks = new(StringComparer.Ordinal);
  private readonly int _capacity;

  /// <summary>Creates a store.</summary>
  /// <param name="capacity">Most tasks held at once; <see cref="CreateAsync"/> refuses more. Default 10,000.</param>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is below 1.</exception>
  public MemoryTaskStore(int capacity = 10_000)
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
    _capacity = capacity;
  }

  /// <summary>How many tasks are held.</summary>
  public int Count
  {
    get
    {
      lock (_gate)
      {
        return _tasks.Count;
      }
    }
  }

  /// <inheritdoc />
  public ValueTask CreateAsync(TaskRecord task, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(task);
    lock (_gate)
    {
      if (_tasks.Count >= _capacity)
      {
        throw new InvalidOperationException("The task store is full (" + _capacity + " tasks).");
      }

      if (!_tasks.TryAdd(task.TaskId, task with { Version = 0 }))
      {
        throw new InvalidOperationException("A task with this id exists already.");
      }
    }

    return ValueTask.CompletedTask;
  }

  /// <inheritdoc />
  public ValueTask<TaskRecord?> GetAsync(string taskId, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(taskId);
    lock (_gate)
    {
      return ValueTask.FromResult(_tasks.GetValueOrDefault(taskId));
    }
  }

  /// <inheritdoc />
  public ValueTask<TaskRecord?> UpdateAsync(TaskRecord task, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(task);
    lock (_gate)
    {
      if (!_tasks.TryGetValue(task.TaskId, out var current) || current.Version != task.Version)
      {
        return ValueTask.FromResult<TaskRecord?>(null);
      }

      var next = task with { Version = current.Version + 1 };
      _tasks[task.TaskId] = next;
      return ValueTask.FromResult<TaskRecord?>(next);
    }
  }

  /// <inheritdoc />
  public ValueTask<bool> DeleteAsync(string taskId, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(taskId);
    lock (_gate)
    {
      return ValueTask.FromResult(_tasks.Remove(taskId));
    }
  }

  /// <inheritdoc />
  public ValueTask<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
  {
    lock (_gate)
    {
      List<string>? expired = null;
      foreach (var (id, task) in _tasks)
      {
        if (task.ExpiresAt is { } at && at <= now)
        {
          (expired ??= []).Add(id);
        }
      }

      if (expired is null)
      {
        return ValueTask.FromResult(0);
      }

      foreach (var id in expired)
      {
        _tasks.Remove(id);
      }

      return ValueTask.FromResult(expired.Count);
    }
  }
}
