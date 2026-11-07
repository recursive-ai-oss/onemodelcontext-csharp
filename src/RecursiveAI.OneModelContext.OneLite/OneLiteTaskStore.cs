using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneLite;
using RecursiveAI.OneModelContext.Extensions.Tasks;

namespace RecursiveAI.OneModelContext.OneLite;

[OneLiteRow]
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

  public long? PollIntervalMs { get; set; }

  public string? ResultJson { get; set; }

  public string? ErrorJson { get; set; }

  public string? InputRequestsJson { get; set; }

  public string? InputResponsesJson { get; set; }

  public string? UsedInputKeysJson { get; set; }

  public long CancelRequested { get; set; }

  public long Version { get; set; }
}

/// <summary>
/// An <see cref="ITaskStore"/> in one embedded OneLite database file, so that Tasks survive a restart and several server processes on
/// one machine share them. <see cref="UpdateAsync"/> is the compare-and-swap the interface asks for, one
/// <c>UPDATE … SET Version = Version + 1 WHERE TaskId = @id AND Version = @version</c> whose changed-row count says whether the change
/// was applied. <see cref="CreateAsync"/> checks capacity and inserts in one write transaction, so the limit is exact even across
/// processes.
/// </summary>
/// <remarks>
/// Results, errors and input requests are held as the JSON text the task service wrote; tasks can carry tool output, so protect the file
/// like any other file of user data. Task ids are at most 128 characters. Dispose the store to close the file when it opened it.
/// </remarks>
[Experimental("OMCX0001")]
public sealed class OneLiteTaskStore : ITaskStore, IAsyncDisposable
{
  /// <summary>The longest task id accepted: the extension caps ids at 128 characters.</summary>
  public const int MaxTaskIdLength = StoreKeys.MaxTaskIdLength;

  private readonly StoreDatabase _store;
  private readonly int _capacity;

  private OneLiteTaskStore(StoreDatabase store, int capacity)
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
    _store = store;
    _capacity = capacity;
  }

  private Database Db => _store.Db;

  /// <summary>Opens (creating when missing) a database file and its store tables, and returns a store that owns the connection.</summary>
  /// <param name="path">The database file.</param>
  /// <param name="capacity">Most tasks held at once; <see cref="CreateAsync"/> refuses more. Default 10,000.</param>
  /// <param name="cancellationToken">Cancels the open.</param>
  /// <returns>The store. Dispose it to close the file.</returns>
  /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is below 1.</exception>
  /// <exception cref="InvalidOperationException">The file's store schema is newer than this package understands.</exception>
  public static async ValueTask<OneLiteTaskStore> OpenAsync(string path, int capacity = 10_000, CancellationToken cancellationToken = default)
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
    return new(await StoreDatabase.OpenAsync(path, cancellationToken).ConfigureAwait(false), capacity);
  }

  /// <summary>Creates the store tables in a database you opened and returns a store over it. Disposing the store leaves the database open.</summary>
  /// <param name="database">An open OneLite database.</param>
  /// <param name="capacity">Most tasks held at once. Default 10,000.</param>
  /// <param name="cancellationToken">Cancels the schema check.</param>
  /// <returns>The store.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="database"/> is null.</exception>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is below 1.</exception>
  /// <exception cref="InvalidOperationException">The database's store schema is newer than this package understands.</exception>
  public static async ValueTask<OneLiteTaskStore> AttachAsync(Database database, int capacity = 10_000, CancellationToken cancellationToken = default)
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
    return new(await StoreDatabase.AttachAsync(database, cancellationToken).ConfigureAwait(false), capacity);
  }

  /// <inheritdoc />
  public ValueTask DisposeAsync() => _store.DisposeAsync();

  /// <inheritdoc />
  public async ValueTask CreateAsync(TaskRecord task, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(task);
    if (task.TaskId.Length is 0 or > MaxTaskIdLength)
    {
      throw new ArgumentException("A task id is 1 to 128 characters.", nameof(task));
    }

    var n = ToRow(task with { Version = 0 }, task.ExpiresAt?.UtcTicks);
    await Db.WriteAsync(async tx =>
    {
      if (await tx.ScalarAsync<long>($"SELECT count(*) FROM ModelContextTasks", cancellationToken).ConfigureAwait(false) >= _capacity)
      {
        throw new InvalidOperationException("The task store is full (" + _capacity + " tasks).");
      }

      var inserted = await tx.ExecuteAsync($"""
        INSERT INTO ModelContextTasks (TaskId, Owner, ToolName, Status, StatusMessage, CreatedAtTicks, LastUpdatedAtTicks, TtlMs, ExpiresAtTicks, PollIntervalMs,
          ResultJson, ErrorJson, InputRequestsJson, InputResponsesJson, UsedInputKeysJson, CancelRequested, Version)
        VALUES ({n.Row.TaskId}, {n.Row.Owner}, {n.Row.ToolName}, {n.Row.Status}, {n.Row.StatusMessage}, {n.Row.CreatedAtTicks}, {n.Row.LastUpdatedAtTicks}, {n.Row.TtlMs}, {n.Expires}, {n.Row.PollIntervalMs},
          {n.Row.ResultJson}, {n.Row.ErrorJson}, {n.Row.InputRequestsJson}, {n.Row.InputResponsesJson}, {n.Row.UsedInputKeysJson}, {n.Row.CancelRequested}, {n.Row.Version})
        ON CONFLICT (TaskId) DO NOTHING
        """, cancellationToken).ConfigureAwait(false);
      if (inserted == 0)
      {
        throw new InvalidOperationException("A task with this id exists already.");
      }
    }, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async ValueTask<TaskRecord?> GetAsync(string taskId, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(taskId);
    if (taskId.Length > MaxTaskIdLength)
    {
      return null;
    }

    var row = (await Db.QueryAsync<TaskRow>($"SELECT * FROM ModelContextTasks WHERE TaskId = {taskId}", cancellationToken).ConfigureAwait(false)).FirstOrDefault();
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

    var n = ToRow(task, task.ExpiresAt?.UtcTicks);
    var id = task.TaskId;
    var version = task.Version;

    // The compare-and-swap: only a row still at the version the caller read is changed, and exactly one of two racing callers sees it.
    var changed = await Db.ExecuteAsync($"""
      UPDATE ModelContextTasks SET Owner = {n.Row.Owner}, ToolName = {n.Row.ToolName}, Status = {n.Row.Status}, StatusMessage = {n.Row.StatusMessage},
        CreatedAtTicks = {n.Row.CreatedAtTicks}, LastUpdatedAtTicks = {n.Row.LastUpdatedAtTicks}, TtlMs = {n.Row.TtlMs}, ExpiresAtTicks = {n.Expires},
        PollIntervalMs = {n.Row.PollIntervalMs}, ResultJson = {n.Row.ResultJson}, ErrorJson = {n.Row.ErrorJson}, InputRequestsJson = {n.Row.InputRequestsJson},
        InputResponsesJson = {n.Row.InputResponsesJson}, UsedInputKeysJson = {n.Row.UsedInputKeysJson}, CancelRequested = {n.Row.CancelRequested},
        Version = Version + 1
      WHERE TaskId = {id} AND Version = {version}
      """, cancellationToken).ConfigureAwait(false);
    return changed == 1 ? task with { Version = version + 1 } : null;
  }

  /// <inheritdoc />
  public async ValueTask<bool> DeleteAsync(string taskId, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(taskId);
    return await Db.ExecuteAsync($"DELETE FROM ModelContextTasks WHERE TaskId = {taskId}", cancellationToken).ConfigureAwait(false) > 0;
  }

  /// <inheritdoc />
  public async ValueTask<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
  {
    var ticks = now.UtcTicks;
    return (int)await Db.ExecuteAsync($"DELETE FROM ModelContextTasks WHERE ExpiresAtTicks IS NOT NULL AND ExpiresAtTicks <= {ticks}", cancellationToken).ConfigureAwait(false);
  }

  /// <summary>The expiry is a column of its own (CreatedAt plus the time to live), so expiring is one DELETE.</summary>
  private static (TaskRow Row, long? Expires) ToRow(TaskRecord t, long? expires) => (new TaskRow
  {
    TaskId = t.TaskId,
    Owner = t.Owner,
    ToolName = t.ToolName,
    Status = t.Status,
    StatusMessage = t.StatusMessage,
    CreatedAtTicks = t.CreatedAt.UtcTicks,
    LastUpdatedAtTicks = t.LastUpdatedAt.UtcTicks,
    TtlMs = t.TtlMs,
    PollIntervalMs = t.PollIntervalMs,
    ResultJson = t.ResultJson,
    ErrorJson = t.ErrorJson,
    InputRequestsJson = t.InputRequestsJson,
    InputResponsesJson = t.InputResponsesJson,
    UsedInputKeysJson = t.UsedInputKeysJson,
    CancelRequested = t.CancelRequested ? 1 : 0,
    Version = t.Version,
  }, expires);

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
    CancelRequested = r.CancelRequested != 0,
    Version = r.Version,
  };
}
