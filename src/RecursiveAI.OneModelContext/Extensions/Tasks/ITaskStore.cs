using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.Extensions.Tasks;

/// <summary>The ids of the experimental extensions, for <c>[Experimental]</c>.</summary>
internal static class ExperimentalIds
{
  internal const string Tasks = "OMCX0001";
  internal const string Skills = "OMCX0002";
  internal const string ServerCards = "OMCX0003";
}

/// <summary>The states of a task (Tasks extension, "Task Status").</summary>
[Experimental(ExperimentalIds.Tasks)]
public static class TaskStates
{
  /// <summary>The request is being processed.</summary>
  public const string Working = "working";

  /// <summary>The server needs input from the client; the outstanding requests are in <see cref="TaskRecord.InputRequestsJson"/>.</summary>
  public const string InputRequired = "input_required";

  /// <summary>The request finished; the result is in <see cref="TaskRecord.ResultJson"/>. A tool result with <c>isError</c> is still completed.</summary>
  public const string Completed = "completed";

  /// <summary>The request failed with a JSON-RPC error, in <see cref="TaskRecord.ErrorJson"/>.</summary>
  public const string Failed = "failed";

  /// <summary>The request was cancelled before it finished.</summary>
  public const string Cancelled = "cancelled";

  /// <summary>True for states a task never leaves.</summary>
  /// <param name="status">A state.</param>
  public static bool IsTerminal(string status) => status is Completed or Failed or Cancelled;
}

/// <summary>
/// Everything the server remembers about one task. A plain record of strings, numbers and dates, so a relational store can hold it in one
/// row. JSON members are kept as text the store never needs to read.
/// </summary>
[Experimental(ExperimentalIds.Tasks)]
public sealed record TaskRecord
{
  /// <summary>The unguessable identifier the client polls with. Server generated; at most 128 characters of <c>A-Z a-z 0-9 - _</c>.</summary>
  public required string TaskId { get; init; }

  /// <summary>
  /// Who may use the task: the issuer and subject of the caller that created it, or an empty string for an unauthenticated caller. Every
  /// request on the task is checked against it.
  /// </summary>
  public required string Owner { get; init; }

  /// <summary>The tool whose call became this task.</summary>
  public required string ToolName { get; init; }

  /// <summary>One of <see cref="TaskStates"/>.</summary>
  public required string Status { get; init; }

  /// <summary>A short message for the status, shown to the user or model.</summary>
  public string? StatusMessage { get; init; }

  /// <summary>When the task was created (UTC).</summary>
  public required DateTimeOffset CreatedAt { get; init; }

  /// <summary>When it last changed (UTC).</summary>
  public required DateTimeOffset LastUpdatedAt { get; init; }

  /// <summary>Time to live from <see cref="CreatedAt"/> in milliseconds; <c>null</c> for no limit. After it the server may discard the task.</summary>
  public long? TtlMs { get; init; }

  /// <summary>The polling interval suggested to clients, in milliseconds.</summary>
  public long? PollIntervalMs { get; init; }

  /// <summary>The final <c>CallToolResult</c> as JSON text, when <see cref="Status"/> is completed.</summary>
  public string? ResultJson { get; init; }

  /// <summary>The JSON-RPC error object as JSON text, when <see cref="Status"/> is failed.</summary>
  public string? ErrorJson { get; init; }

  /// <summary>Outstanding input requests as a JSON object keyed by request key; <c>null</c> when none.</summary>
  public string? InputRequestsJson { get; init; }

  /// <summary>Answers the client sent that the running work has not picked up yet, as a JSON object keyed by request key; <c>null</c> when none.</summary>
  public string? InputResponsesJson { get; init; }

  /// <summary>Every input request key used so far, as a JSON array. A key is never used twice in a task's life.</summary>
  public string? UsedInputKeysJson { get; init; }

  /// <summary>True once the client asked to cancel. The running work looks at it and stops when it can.</summary>
  public bool CancelRequested { get; init; }

  /// <summary>
  /// A counter the store owns. <see cref="ITaskStore.UpdateAsync"/> applies a change only when this still matches the stored row, and the row
  /// then carries the next value. A new task starts at 0.
  /// </summary>
  public long Version { get; init; }

  /// <summary>When the task may be discarded, or <c>null</c> when it has no limit.</summary>
  public DateTimeOffset? ExpiresAt => TtlMs is { } ttl ? CreatedAt.AddMilliseconds(ttl) : null;
}

/// <summary>
/// Keeps tasks for the Tasks extension. The server calls it from many threads, and when several server instances share one store from
/// several processes, so a change is a compare-and-swap on <see cref="TaskRecord.Version"/> and nothing else is assumed to be atomic.
/// </summary>
/// <remarks>
/// Implement it over a relational table by mapping <see cref="TaskRecord"/> to one row; <see cref="UpdateAsync"/> is
/// <c>UPDATE ... SET Version = Version + 1 WHERE TaskId = @id AND Version = @version</c>. Implementations must not throw for an unknown id.
/// </remarks>
[Experimental(ExperimentalIds.Tasks)]
public interface ITaskStore
{
  /// <summary>Stores a new task. It is durable when this returns: a <c>tasks/get</c> for the id finds it.</summary>
  /// <param name="task">The task, with <see cref="TaskRecord.Version"/> 0.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  /// <exception cref="InvalidOperationException">The id exists already, or the store is full.</exception>
  ValueTask CreateAsync(TaskRecord task, CancellationToken cancellationToken = default);

  /// <summary>Reads a task.</summary>
  /// <param name="taskId">The id.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  /// <returns>The task, or <c>null</c> when there is none (never created, or discarded).</returns>
  ValueTask<TaskRecord?> GetAsync(string taskId, CancellationToken cancellationToken = default);

  /// <summary>Replaces a task when nobody changed it since it was read.</summary>
  /// <param name="task">The new content, carrying the <see cref="TaskRecord.Version"/> that was read.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  /// <returns>The stored row with its new version, or <c>null</c> when the task is gone or its version moved on. The caller reads again and retries.</returns>
  ValueTask<TaskRecord?> UpdateAsync(TaskRecord task, CancellationToken cancellationToken = default);

  /// <summary>Removes a task.</summary>
  /// <param name="taskId">The id.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  /// <returns>True when it existed.</returns>
  ValueTask<bool> DeleteAsync(string taskId, CancellationToken cancellationToken = default);

  /// <summary>Removes every task whose <see cref="TaskRecord.ExpiresAt"/> has passed.</summary>
  /// <param name="now">The current time.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  /// <returns>How many were removed.</returns>
  ValueTask<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}
