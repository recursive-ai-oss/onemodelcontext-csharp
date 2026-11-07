using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Extensions.Tasks;

/// <summary>What a server said about a task at one moment (a <c>tasks/get</c> answer or the handle a tool call returned).</summary>
[Experimental(ExperimentalIds.Tasks)]
public sealed record TaskSnapshot
{
  /// <summary>The task's id.</summary>
  public required string TaskId { get; init; }

  /// <summary>One of <see cref="TaskStates"/>; a state this library does not know is kept as sent.</summary>
  public required string Status { get; init; }

  /// <summary>A message about the state, meant for users or models.</summary>
  public string? StatusMessage { get; init; }

  /// <summary>When the server created the task.</summary>
  public DateTimeOffset CreatedAt { get; init; }

  /// <summary>When the task last changed.</summary>
  public DateTimeOffset LastUpdatedAt { get; init; }

  /// <summary>The task's time to live in milliseconds from creation, or <c>null</c> for none.</summary>
  public long? TtlMs { get; init; }

  /// <summary>How long the server suggests waiting between polls.</summary>
  public TimeSpan? PollInterval { get; init; }

  /// <summary>The requests waiting for an answer, by key, when <see cref="Status"/> is <c>input_required</c>.</summary>
  public IReadOnlyDictionary<string, InputRequest>? InputRequests { get; init; }

  /// <summary>The tool's result when <see cref="Status"/> is <c>completed</c>. A result with <c>IsError</c> is still a completed task.</summary>
  public CallToolResult? Result { get; init; }

  /// <summary>The JSON-RPC error when <see cref="Status"/> is <c>failed</c>.</summary>
  public JsonRpcError? Error { get; init; }

  /// <summary>True when the task will not change again.</summary>
  public bool IsTerminal => TaskStates.IsTerminal(Status);
}

/// <summary>The answer to a tool call on a client that understands tasks: the tool's own result, or a task to follow.</summary>
[Experimental(ExperimentalIds.Tasks)]
public sealed class ToolCallOutcome
{
  internal ToolCallOutcome(CallToolResult? result, RemoteTask? task)
  {
    Result = result;
    Task = task;
  }

  /// <summary>The tool's result when the server answered at once; otherwise <c>null</c>.</summary>
  public CallToolResult? Result { get; }

  /// <summary>The task when the server turned the call into one; otherwise <c>null</c>.</summary>
  public RemoteTask? Task { get; }
}

/// <summary>A task on a server, as a client sees it: poll it, answer its questions, cancel it.</summary>
[Experimental(ExperimentalIds.Tasks)]
public sealed class RemoteTask
{
  private readonly ContextClient _client;
  private TaskSnapshot _latest;

  internal RemoteTask(ContextClient client, TaskSnapshot snapshot)
  {
    _client = client;
    _latest = snapshot;
  }

  /// <summary>The task's id. Keep it somewhere durable to resume polling after a restart (<see cref="TasksClientExtensions.GetTaskAsync"/>).</summary>
  public string TaskId => _latest.TaskId;

  /// <summary>The newest state this client has seen.</summary>
  public TaskSnapshot Latest => Volatile.Read(ref _latest);

  /// <summary>Asks the server for the task's state now (<c>tasks/get</c>).</summary>
  /// <param name="cancellationToken">Cancels the call.</param>
  /// <exception cref="ProtocolException">The task is unknown or expired (-32602), or the client did not declare the extension (-32021).</exception>
  public async ValueTask<TaskSnapshot> GetAsync(CancellationToken cancellationToken = default)
  {
    var parameters = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["taskId"] = TaskId }, WireJsonContext.Default.DictionaryStringString);
    var answer = await _client.CallExtensionAsync(TasksExtension.GetMethod, parameters, cancellationToken).ConfigureAwait(false);
    var snapshot = TasksClientExtensions.ParseSnapshot(answer);
    Volatile.Write(ref _latest, snapshot);
    return snapshot;
  }

  /// <summary>Answers the task's input requests (<c>tasks/update</c>).</summary>
  /// <param name="inputResponses">The answers by request key: the result object of each request.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  public async ValueTask UpdateAsync(IReadOnlyDictionary<string, JsonElement> inputResponses, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(inputResponses);
    var parameters = JsonSerializer.SerializeToElement(
      new TaskUpdateParams { TaskId = TaskId, InputResponses = new Dictionary<string, JsonElement>(inputResponses) },
      TasksJsonContext.Default.TaskUpdateParams);
    await _client.CallExtensionAsync(TasksExtension.UpdateMethod, parameters, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Asks the server to stop the task (<c>tasks/cancel</c>). The server acknowledges and stops when it can; the task may still finish
  /// first. There is no need to poll afterwards.
  /// </summary>
  /// <param name="cancellationToken">Cancels the call.</param>
  public async ValueTask CancelAsync(CancellationToken cancellationToken = default)
  {
    var parameters = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["taskId"] = TaskId }, WireJsonContext.Default.DictionaryStringString);
    await _client.CallExtensionAsync(TasksExtension.CancelMethod, parameters, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Polls until the task is completed, failed or cancelled, waiting the interval the server suggests (one second when it suggests none).
  /// Questions the task asks are answered by the handlers the client was created with (the same ones that answer input requests of a
  /// tool call), each key once.
  /// </summary>
  /// <param name="cancellationToken">Stops waiting. The task keeps running on the server; call <see cref="CancelAsync"/> to stop it.</param>
  /// <returns>The final state.</returns>
  /// <exception cref="ContextException">The task asked for input and the client has no handler for it.</exception>
  public async ValueTask<TaskSnapshot> WaitAsync(CancellationToken cancellationToken = default)
  {
    var answered = new HashSet<string>(StringComparer.Ordinal);
    var snapshot = Latest;
    if (snapshot.IsTerminal)
    {
      return snapshot;
    }

    // What this object last saw may be stale (the caller may have answered a question by hand), so start from the server's view.
    snapshot = await GetAsync(cancellationToken).ConfigureAwait(false);
    while (true)
    {
      if (snapshot.IsTerminal)
      {
        return snapshot;
      }

      if (snapshot.InputRequests is { Count: > 0 } requests)
      {
        // Tasks extension, "Task Update Requests": keys are deduplicated across polls, and each question is shown once.
        var responses = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, request) in requests)
        {
          if (answered.Add(key))
          {
            responses[key] = await _client.AnswerInputRequestAsync(request, cancellationToken).ConfigureAwait(false);
          }
        }

        if (responses.Count > 0)
        {
          await UpdateAsync(responses, cancellationToken).ConfigureAwait(false);
        }
      }

      await Task.Delay(snapshot.PollInterval ?? TimeSpan.FromSeconds(1), _client.Clock, cancellationToken).ConfigureAwait(false);
      snapshot = await GetAsync(cancellationToken).ConfigureAwait(false);
    }
  }
}

/// <summary>Calling tools as tasks and following tasks, on a <see cref="ContextClient"/> that declared <see cref="TasksExtension.Client"/>.</summary>
[Experimental(ExperimentalIds.Tasks)]
public static class TasksClientExtensions
{
  /// <summary>
  /// Calls a tool and returns what the server chose to answer with: its result, or a task to follow. The client must have declared the
  /// extension in <see cref="ContextClientOptions.Extensions"/>, or the server answers with the result.
  /// </summary>
  /// <param name="client">The client.</param>
  /// <param name="name">The tool name.</param>
  /// <param name="arguments">The arguments object, or <c>default</c> for none.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  public static async ValueTask<ToolCallOutcome> CallToolAsTaskAsync(this ContextClient client, string name, JsonElement arguments = default, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(client);
    ArgumentNullException.ThrowIfNull(name);
    var result = await client.CallToolRawAsync(name, arguments, null, cancellationToken).ConfigureAwait(false);
    return result is CallToolResult tool ? new ToolCallOutcome(tool, null) : new ToolCallOutcome(null, ToRemote(client, result));
  }

  /// <summary>Follows a task by id, for example one saved before a restart.</summary>
  /// <param name="client">The client.</param>
  /// <param name="taskId">The task id.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  /// <returns>The task, with its current state.</returns>
  /// <exception cref="ProtocolException">The task is unknown or expired (-32602).</exception>
  public static async ValueTask<RemoteTask> GetTaskAsync(this ContextClient client, string taskId, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(client);
    ArgumentException.ThrowIfNullOrEmpty(taskId);
    var placeholder = new RemoteTask(client, new TaskSnapshot { TaskId = taskId, Status = TaskStates.Working });
    await placeholder.GetAsync(cancellationToken).ConfigureAwait(false);
    return placeholder;
  }

  internal static RemoteTask ToRemote(ContextClient client, Result handle)
  {
    var element = JsonSerializer.SerializeToElement(handle, WireJsonContext.Default.ExtensionResult);
    return new RemoteTask(client, ParseSnapshot(element));
  }

  // Follows a task handle that a plain CallToolAsync received, and returns the tool's result as if the call had been answered at once.
  internal static async ValueTask<CallToolResult> CompleteAsync(ContextClient client, Result handle, CancellationToken cancellationToken)
  {
    var task = ToRemote(client, handle);
    var final = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    return final.Status switch
    {
      TaskStates.Completed when final.Result is { } result => result,
      TaskStates.Failed => throw new ProtocolException(final.Error?.Code ?? JsonRpcErrorCodes.InternalError, final.Error?.Message ?? "The task failed.", final.Error?.Data ?? default),
      TaskStates.Cancelled => throw new OperationCanceledException("The server cancelled the task."),
      _ => throw new ProtocolException(JsonRpcErrorCodes.InternalError, "The task ended in state '" + final.Status + "' without a result."),
    };
  }

  // The answer of a server is input: a malformed one is a protocol error, never an unhandled exception.
  internal static TaskSnapshot ParseSnapshot(JsonElement element)
  {
    try
    {
      if (element.ValueKind != JsonValueKind.Object
        || !element.TryGetProperty("taskId", out var id) || id.ValueKind != JsonValueKind.String
        || !element.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
      {
        throw new ProtocolException(JsonRpcErrorCodes.InternalError, "The server's task has no taskId or status.");
      }

      Dictionary<string, InputRequest>? requests = null;
      if (element.TryGetProperty("inputRequests", out var asked) && asked.ValueKind == JsonValueKind.Object)
      {
        requests = asked.Deserialize(TasksJsonContext.Default.DictionaryStringInputRequest);
      }

      CallToolResult? result = null;
      if (element.TryGetProperty("result", out var resultElement) && resultElement.ValueKind == JsonValueKind.Object)
      {
        result = resultElement.Deserialize(WireJsonContext.Default.CallToolResult);
      }

      JsonRpcError? error = null;
      if (element.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object)
      {
        error = new JsonRpcError(
          errorElement.TryGetProperty("code", out var code) && code.TryGetInt32(out var number) ? number : JsonRpcErrorCodes.InternalError,
          errorElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString()! : "The task failed.",
          errorElement.TryGetProperty("data", out var data) ? data.Clone() : default);
      }

      return new TaskSnapshot
      {
        TaskId = id.GetString()!,
        Status = status.GetString()!,
        StatusMessage = element.TryGetProperty("statusMessage", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null,
        CreatedAt = ReadDate(element, "createdAt"),
        LastUpdatedAt = ReadDate(element, "lastUpdatedAt"),
        TtlMs = element.TryGetProperty("ttlMs", out var ttl) && ttl.ValueKind == JsonValueKind.Number && ttl.TryGetInt64(out var ms) ? ms : null,
        PollInterval = element.TryGetProperty("pollIntervalMs", out var poll) && poll.ValueKind == JsonValueKind.Number && poll.TryGetInt64(out var interval) && interval >= 0
          ? TimeSpan.FromMilliseconds(Math.Min(interval, 3_600_000))
          : null,
        InputRequests = requests,
        Result = result,
        Error = error,
      };
    }
    catch (JsonException ex)
    {
      throw new ProtocolException(JsonRpcErrorCodes.InternalError, "The server's task is malformed.", default, ex);
    }
  }

  private static DateTimeOffset ReadDate(JsonElement element, string name) =>
    element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
    && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
      ? parsed
      : default;
}
