using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RecursiveAI.OneModelContext.Extensions.Tasks;

namespace RecursiveAI.OneModelContext.EntityFrameworkCore;

internal sealed class TaskRow
{
  public string TaskId { get; set; } = string.Empty;

  public string Owner { get; set; } = string.Empty;

  public string ToolName { get; set; } = string.Empty;

  public string Status { get; set; } = string.Empty;

  public string? StatusMessage { get; set; }

  public long CreatedAtTicks { get; set; }

  public long LastUpdatedAtTicks { get; set; }

  public long? TtlMs { get; set; }

  /// <summary>CreatedAt plus the time to live, kept as a column so expiry is one DELETE.</summary>
  public long? ExpiresAtTicks { get; set; }

  public long? PollIntervalMs { get; set; }

  public string? ResultJson { get; set; }

  public string? ErrorJson { get; set; }

  public string? InputRequestsJson { get; set; }

  public string? InputResponsesJson { get; set; }

  public string? UsedInputKeysJson { get; set; }

  public bool CancelRequested { get; set; }

  public long Version { get; set; }
}

/// <summary>
/// An <see cref="ITaskStore"/> in a relational database, so that several server instances share their tasks: a task created through one
/// instance is polled, answered and cancelled through another. <see cref="UpdateAsync"/> is the compare-and-swap the interface asks for,
/// one <c>UPDATE … SET Version = Version + 1 WHERE TaskId = @id AND Version = @version</c> whose affected-row count says whether the change
/// was applied.
/// </summary>
/// <typeparam name="TContext">Your context. Its model must include <see cref="ContextStoresModelBuilderExtensions.ApplyTaskStore"/>.</typeparam>
/// <remarks>
/// Results, errors and input requests are held as the JSON text the task service wrote; tasks can carry tool output, so protect the table
/// like any other table of user data. The capacity check counts rows before the insert, so with several instances the limit can be
/// overshot by about the number of concurrent creators. Entity Framework Core is not trim- or AOT-safe, and neither is this adapter.
/// </remarks>
[Experimental("OMCX0001")]
public sealed class EfTaskStore<TContext> : ITaskStore
  where TContext : DbContext
{
  /// <summary>The longest task id accepted: the extension caps ids at 128 characters.</summary>
  public const int MaxTaskIdLength = StoreKeys.MaxTaskIdLength;

  private readonly IDbContextFactory<TContext> _factory;
  private readonly int _capacity;

  /// <summary>Creates a store over a context factory (<c>AddDbContextFactory</c>).</summary>
  /// <param name="factory">Creates one short-lived context per call.</param>
  /// <param name="capacity">Most tasks held at once; <see cref="CreateAsync"/> refuses more. Default 10,000.</param>
  /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is below 1.</exception>
  public EfTaskStore(IDbContextFactory<TContext> factory, int capacity = 10_000)
  {
    ArgumentNullException.ThrowIfNull(factory);
    ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
    _factory = factory;
    _capacity = capacity;
  }

  private TContext Open() => StoreContext.Open(_factory, typeof(TaskRow), "ApplyTaskStore");

  /// <inheritdoc />
  public async ValueTask CreateAsync(TaskRecord task, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(task);
    if (task.TaskId.Length is 0 or > MaxTaskIdLength)
    {
      throw new ArgumentException("A task id is 1 to 128 characters.", nameof(task));
    }

    using var db = Open();
    if (await db.Set<TaskRow>().CountAsync(cancellationToken).ConfigureAwait(false) >= _capacity)
    {
      throw new InvalidOperationException("The task store is full (" + _capacity + " tasks).");
    }

    db.Set<TaskRow>().Add(ToRow(task with { Version = 0 }));
    try
    {
      await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
    catch (DbUpdateException ex)
    {
      using var check = Open();
      if (await check.Set<TaskRow>().AnyAsync(r => r.TaskId == task.TaskId, cancellationToken).ConfigureAwait(false))
      {
        throw new InvalidOperationException("A task with this id exists already.", ex);
      }

      throw;
    }
  }

  /// <inheritdoc />
  public async ValueTask<TaskRecord?> GetAsync(string taskId, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(taskId);
    if (taskId.Length > MaxTaskIdLength)
    {
      return null;
    }

    using var db = Open();
    var row = await db.Set<TaskRow>().AsNoTracking().FirstOrDefaultAsync(r => r.TaskId == taskId, cancellationToken).ConfigureAwait(false);
    return row is null ? null : FromRow(row);
  }

  /// <inheritdoc />
  public async ValueTask<TaskRecord?> UpdateAsync(TaskRecord task, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(task);
    if (task.TaskId.Length > MaxTaskIdLength)
    {
      return null;
    }

    var n = ToRow(task);
    var id = task.TaskId;
    var version = task.Version;
    using var db = Open();

    // The compare-and-swap: only a row still at the version the caller read is changed, and exactly one of two racing callers sees it.
    var changed = await db.Set<TaskRow>().Where(r => r.TaskId == id && r.Version == version)
      .ExecuteUpdateAsync(s => s
        .SetProperty(r => r.Owner, n.Owner)
        .SetProperty(r => r.ToolName, n.ToolName)
        .SetProperty(r => r.Status, n.Status)
        .SetProperty(r => r.StatusMessage, n.StatusMessage)
        .SetProperty(r => r.CreatedAtTicks, n.CreatedAtTicks)
        .SetProperty(r => r.LastUpdatedAtTicks, n.LastUpdatedAtTicks)
        .SetProperty(r => r.TtlMs, n.TtlMs)
        .SetProperty(r => r.ExpiresAtTicks, n.ExpiresAtTicks)
        .SetProperty(r => r.PollIntervalMs, n.PollIntervalMs)
        .SetProperty(r => r.ResultJson, n.ResultJson)
        .SetProperty(r => r.ErrorJson, n.ErrorJson)
        .SetProperty(r => r.InputRequestsJson, n.InputRequestsJson)
        .SetProperty(r => r.InputResponsesJson, n.InputResponsesJson)
        .SetProperty(r => r.UsedInputKeysJson, n.UsedInputKeysJson)
        .SetProperty(r => r.CancelRequested, n.CancelRequested)
        .SetProperty(r => r.Version, r => r.Version + 1), cancellationToken).ConfigureAwait(false);
    return changed == 1 ? task with { Version = version + 1 } : null;
  }

  /// <inheritdoc />
  public async ValueTask<bool> DeleteAsync(string taskId, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(taskId);
    using var db = Open();
    return await db.Set<TaskRow>().Where(r => r.TaskId == taskId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;
  }

  /// <inheritdoc />
  public async ValueTask<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
  {
    var ticks = now.UtcTicks;
    using var db = Open();
    return await db.Set<TaskRow>().Where(r => r.ExpiresAtTicks != null && r.ExpiresAtTicks <= ticks).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
  }

  private static TaskRow ToRow(TaskRecord t) => new()
  {
    TaskId = t.TaskId,
    Owner = t.Owner,
    ToolName = t.ToolName,
    Status = t.Status,
    StatusMessage = t.StatusMessage,
    CreatedAtTicks = t.CreatedAt.UtcTicks,
    LastUpdatedAtTicks = t.LastUpdatedAt.UtcTicks,
    TtlMs = t.TtlMs,
    ExpiresAtTicks = t.ExpiresAt?.UtcTicks,
    PollIntervalMs = t.PollIntervalMs,
    ResultJson = t.ResultJson,
    ErrorJson = t.ErrorJson,
    InputRequestsJson = t.InputRequestsJson,
    InputResponsesJson = t.InputResponsesJson,
    UsedInputKeysJson = t.UsedInputKeysJson,
    CancelRequested = t.CancelRequested,
    Version = t.Version,
  };

  private static TaskRecord FromRow(TaskRow r) => new()
  {
    TaskId = r.TaskId,
    Owner = r.Owner,
    ToolName = r.ToolName,
    Status = r.Status,
    StatusMessage = r.StatusMessage,
    CreatedAt = StoreKeys.At(r.CreatedAtTicks),
    LastUpdatedAt = StoreKeys.At(r.LastUpdatedAtTicks),
    TtlMs = r.TtlMs,
    PollIntervalMs = r.PollIntervalMs,
    ResultJson = r.ResultJson,
    ErrorJson = r.ErrorJson,
    InputRequestsJson = r.InputRequestsJson,
    InputResponsesJson = r.InputResponsesJson,
    UsedInputKeysJson = r.UsedInputKeysJson,
    CancelRequested = r.CancelRequested,
    Version = r.Version,
  };
}
