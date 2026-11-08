using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire.Modern;

namespace RecursiveAI.OneModelContext.Extensions.Tasks;

/// <summary>The work of one task on this server instance.</summary>
internal sealed class Running(string taskId, string owner, CancellationToken stop) : IDisposable
{
  private readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
  private TaskCompletionSource _tick = new(TaskCreationOptions.RunContinuationsAsynchronously);

  internal string TaskId { get; } = taskId;

  internal string Owner { get; } = owner;

  internal CancellationToken Token => _cts.Token;

  internal Task Tick => Volatile.Read(ref _tick).Task;

  /// <summary>Wakes whoever waits for news about the task (an answer arrived, cancellation was requested).</summary>
  internal void Pulse() => Interlocked.Exchange(ref _tick, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

  internal void Cancel()
  {
    try
    {
      _cts.Cancel();
    }
    catch (ObjectDisposedException)
    {
      // The work has finished and released its token already.
    }
  }

  public void Dispose() => _cts.Dispose();
}

/// <summary>
/// The server half of the Tasks extension: creates tasks from tool calls, runs their work in the background, and answers
/// <c>tasks/get</c>, <c>tasks/update</c> and <c>tasks/cancel</c> from the store, so any instance that shares the store can answer.
/// </summary>
internal sealed class TaskService
{
  private const int MaxMutationAttempts = 50;

  private readonly ContextServer _server;
  private readonly TaskServerOptions _options;
  private readonly ITaskStore _store;
  private readonly TimeProvider _time;
  private readonly ConcurrentDictionary<string, (TaskToolHandler Handler, TaskToolOptions Options)> _tools = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, Running> _running = new(StringComparer.Ordinal);
  private long _nextSweep;
  private int _runningCount;
  private int _storageFaulted;

  internal TaskService(ContextServer server, TaskServerOptions options)
  {
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.PersistenceTimeout, TimeSpan.Zero);
    _server = server;
    _options = options;
    _store = options.Store ?? new MemoryTaskStore();
    _time = server.TimeProvider;
  }

  internal ITaskStore Store => _store;

  internal ExtensionRegistration CreateRegistration()
  {
    JsonElement settings = default;
    if (_options.Revision is { } revision)
    {
      settings = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["revision"] = revision }, WireJsonContext.Default.DictionaryStringString);
    }

    // The extension is specified against 2026-07-28 only; earlier revisions run the tool inside the call.
    return new ExtensionRegistration(TasksExtension.Id, settings, [ProtocolVersions.V20260728], official: true)
    {
      UndeclaredRequestsFail = true,
      ToolCallInterceptor = InterceptAsync,
    }
    .Request(TasksExtension.GetMethod, GetAsync)
    .Request(TasksExtension.UpdateMethod, UpdateAsync)
    .Request(TasksExtension.CancelMethod, CancelAsync);
  }

  internal void AddTool(string name, TaskToolHandler handler, TaskToolOptions options) => _tools[name] = (handler, options);

  internal static ValueTask<CallToolResult> RunInlineAsync(TaskToolHandler handler, JsonElement arguments, RequestContext context) =>
    handler(arguments, new TaskContext(context));

  // Who owns a task: the issuer and subject of the caller, like the binding of a requestState.
  internal static string OwnerOf(ClaimsPrincipal? principal)
  {
    if (principal?.Identity is not { IsAuthenticated: true } identity)
    {
      return string.Empty;
    }

    var subject = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? identity.Name ?? "authenticated";
    var issuer = principal.FindFirst("iss")?.Value;
    return issuer is null ? subject : issuer + "|" + subject;
  }

  // ---- creating tasks ----

  private async ValueTask<Result?> InterceptAsync(string toolName, JsonElement arguments, ToolHandler inline, RequestContext context)
  {
    if (!_tools.TryGetValue(toolName, out var entry))
    {
      return null;
    }

    if (!context.SupportsExtension(TasksExtension.Id))
    {
      if (entry.Options.RequireTasks)
      {
        throw new MissingCapabilityException(
          "The tool '" + toolName + "' needs the extension '" + TasksExtension.Id + "', which the client did not declare.",
          new ClientCapabilities { Extensions = new Dictionary<string, JsonElement> { [TasksExtension.Id] = ContextServer.EmptyObject } });
      }

      return null;
    }

    ThrowIfStorageFaulted();
    if (Volatile.Read(ref _runningCount) >= _options.MaxRunningTasks)
    {
      return CallToolResult.FromError("The server is running as many tasks as it allows. Try again later.");
    }

    if (entry.Options.Prepare is { } prepare)
    {
      arguments = await prepare(arguments, context).ConfigureAwait(false);
    }

    await SweepAsync(context.CancellationToken).ConfigureAwait(false);
    var now = _time.GetUtcNow();
    var record = new TaskRecord
    {
      TaskId = Base64Url.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)),
      Owner = OwnerOf(context.Principal),
      ToolName = toolName,
      Status = TaskStates.Working,
      CreatedAt = now,
      LastUpdatedAt = now,
      TtlMs = _options.Ttl is { } ttl ? (long)ttl.TotalMilliseconds : null,
      PollIntervalMs = (long)_options.PollInterval.TotalMilliseconds,
    };

    try
    {
      // Tasks extension, "Task Creation": the handle is returned only once a tasks/get for it would find the task.
      await _store.CreateAsync(record, context.CancellationToken).ConfigureAwait(false);
    }
    catch (InvalidOperationException)
    {
      return CallToolResult.FromError("The server cannot keep another task right now. Try again later.");
    }

    var run = new Running(record.TaskId, record.Owner, _options.StopToken);
    _running[record.TaskId] = run;
    Interlocked.Increment(ref _runningCount);

    // The work outlives the request: it gets copies of what it needs and never touches the request's context again.
    var task = new TaskContext(this, run, record.TaskId, context.Principal, context.ClientInfo, context.ClientCapabilities);
    var cloned = arguments.Clone();
    _ = Task.Run(() => RunAsync(entry.Handler, cloned, task, run), CancellationToken.None);
    return ToResult(record, TasksExtension.TaskResultType);
  }

  private async Task RunAsync(TaskToolHandler handler, JsonElement arguments, TaskContext task, Running run)
  {
    using var watchStop = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
    var watcher = WatchAsync(run, watchStop.Token);
    try
    {
      CallToolResult result;
      try
      {
        result = await handler(arguments, task).ConfigureAwait(false);
      }
      catch (ToolException failure)
      {
        result = CallToolResult.FromError(failure.Message);
      }
      catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
      {
        await FinishAsync(run.TaskId, TaskStates.Cancelled, "The task was cancelled.", null, null).ConfigureAwait(false);
        return;
      }
      catch (ProtocolException failure)
      {
        await FinishAsync(run.TaskId, TaskStates.Failed, failure.Message, null, WriteError(failure.ToError())).ConfigureAwait(false);
        return;
      }
      catch (TimeoutException)
      {
        await FinishAsync(run.TaskId, TaskStates.Failed, "The client did not answer in time.", null, WriteError(new JsonRpcError(JsonRpcErrorCodes.InternalError, "The client did not answer an input request in time."))).ConfigureAwait(false);
        return;
      }
      catch (Exception ex)
      {
        // The client sees a generic error: exception text can hold internals or secrets.
        ContextDiagnostics.Publish(DiagnosticKind.HandlerFault, "A task's work threw " + ex.GetType().Name + ".", TasksExtension.GetMethod, ex);
        await FinishAsync(run.TaskId, TaskStates.Failed, "The task failed.", null, WriteError(new JsonRpcError(JsonRpcErrorCodes.InternalError, "Internal error."))).ConfigureAwait(false);
        return;
      }

      result = InlineContentLimit.Apply(result, _server.Options.MaxInlineContentBytes);
      await FinishAsync(run.TaskId, TaskStates.Completed, null, JsonSerializer.Serialize(result, WireJsonContext.Default.CallToolResult), null).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
      // A durable outcome could not be recorded. Fail closed so polling callers see an error instead of stale work until expiry.
      Volatile.Write(ref _storageFaulted, 1);
      ContextDiagnostics.Publish(DiagnosticKind.HandlerFault, "A task's outcome could not be stored (" + ex.GetType().Name + ").", TasksExtension.GetMethod, ex);
    }
    finally
    {
      await watchStop.CancelAsync().ConfigureAwait(false);
      try
      {
        await watcher.ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        // The watcher ends by cancellation.
      }

      _running.TryRemove(run.TaskId, out _);
      Interlocked.Decrement(ref _runningCount);
      run.Dispose();
    }
  }

  // Looks in the store for what other instances or the clock did to the task: a cancel request, a deletion, the end of its life.
  private async Task WatchAsync(Running run, CancellationToken stop)
  {
    try
    {
      while (!stop.IsCancellationRequested)
      {
        await Task.Delay(_options.StorePollInterval, _time, stop).ConfigureAwait(false);
        var record = await _store.GetAsync(run.TaskId, stop).ConfigureAwait(false);
        if (record is null)
        {
          run.Cancel();
          return;
        }

        if (record.CancelRequested || (record.ExpiresAt is { } at && at <= _time.GetUtcNow()))
        {
          run.Cancel();
          return;
        }
      }
    }
    catch (OperationCanceledException)
    {
      // Work finished.
    }
  }

  private async ValueTask FinishAsync(string taskId, string status, string? message, string? resultJson, string? errorJson)
  {
    using var timeout = new CancellationTokenSource(_options.PersistenceTimeout, _time);
    await MutateAsync(taskId, record => TaskStates.IsTerminal(record.Status)
      ? null
      : record with
      {
        Status = status,
        StatusMessage = message ?? record.StatusMessage,
        ResultJson = resultJson,
        ErrorJson = errorJson,
        InputRequestsJson = null,
        InputResponsesJson = null,
      }, timeout.Token).ConfigureAwait(false);
  }

  private void ThrowIfStorageFaulted()
  {
    if (Volatile.Read(ref _storageFaulted) != 0)
    {
      throw new ProtocolException(JsonRpcErrorCodes.InternalError, "Task storage is unavailable. Recreate the server after repairing the store.");
    }
  }

  private async ValueTask SweepAsync(CancellationToken cancellationToken)
  {
    // Expired tasks are removed as new ones arrive, at most once a minute.
    var now = _time.GetTimestamp();
    var due = Interlocked.Read(ref _nextSweep);
    if (now < due || Interlocked.CompareExchange(ref _nextSweep, now + _time.TimestampFrequency * 60, due) != due)
    {
      return;
    }

    await _store.DeleteExpiredAsync(_time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
  }

  // Applies a change with compare-and-swap; the change returns the new record, or null for "nothing to do".
  private async ValueTask<TaskRecord?> MutateAsync(string taskId, Func<TaskRecord, TaskRecord?> change, CancellationToken cancellationToken)
  {
    for (var attempt = 0; attempt < MaxMutationAttempts; attempt++)
    {
      var current = await _store.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
      if (current is null)
      {
        return null;
      }

      var next = change(current);
      if (next is null)
      {
        return current;
      }

      if (await _store.UpdateAsync(next with { LastUpdatedAt = _time.GetUtcNow() }, cancellationToken).ConfigureAwait(false) is { } stored)
      {
        return stored;
      }

      if (attempt > 3)
      {
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(attempt, 20)), _time, cancellationToken).ConfigureAwait(false);
      }
    }

    throw new InvalidOperationException("The task kept changing; the update was abandoned.");
  }

  internal async ValueTask SetStatusMessageAsync(string taskId, string message) =>
    await MutateAsync(taskId, record => TaskStates.IsTerminal(record.Status) ? null : record with { StatusMessage = message }, CancellationToken.None).ConfigureAwait(false);

  // ---- input requests ----

  internal async ValueTask<JsonElement> RequestInputAsync(Running run, ClientCapabilities capabilities, string key, string method, JsonElement parameters)
  {
    // Only ask for what the client said it can answer, as for any server-to-client request.
    var needed = method switch
    {
      Methods.ElicitationCreate when capabilities.Elicitation is null => new ClientCapabilities { Elicitation = new ElicitationCapability() },
      Methods.SamplingCreateMessage when capabilities.Sampling is null => new ClientCapabilities { Sampling = new SamplingCapability() },
      Methods.RootsList when capabilities.Roots is null => new ClientCapabilities { Roots = new RootsCapability() },
      _ => null,
    };
    if (needed is not null)
    {
      throw new MissingCapabilityException("The client did not declare the capability for " + method + ".", needed);
    }

    var request = JsonSerializer.Serialize(
      new InputRequest { Method = method, Params = parameters.ValueKind == JsonValueKind.Undefined ? default : parameters },
      WireJsonContext.Default.InputRequest);
    string? refusal = null;

    var stored = await MutateAsync(run.TaskId, record =>
    {
      if (TaskStates.IsTerminal(record.Status) || record.CancelRequested)
      {
        refusal = "cancelled";
        return null;
      }

      var used = ReadKeys(record.UsedInputKeysJson);
      if (used.Contains(key))
      {
        refusal = "The input key '" + key + "' was used before in this task; every key is used once.";
        return null;
      }

      var outstanding = ReadMap(record.InputRequestsJson);
      if (outstanding.Count >= _options.MaxOutstandingInputRequests)
      {
        refusal = "The task already has " + outstanding.Count + " outstanding input requests.";
        return null;
      }

      used.Add(key);
      outstanding[key] = request;
      return record with
      {
        Status = TaskStates.InputRequired,
        InputRequestsJson = WriteMap(outstanding),
        UsedInputKeysJson = WriteKeys(used),
      };
    }, run.Token).ConfigureAwait(false);

    if (stored is null || refusal == "cancelled")
    {
      run.Cancel();
      throw new OperationCanceledException(run.Token);
    }

    if (refusal is not null)
    {
      throw new InvalidOperationException(refusal);
    }

    using var timeout = new CancellationTokenSource(_options.InputTimeout, _time);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.Token, timeout.Token);
    try
    {
      while (true)
      {
        var record = await _store.GetAsync(run.TaskId, linked.Token).ConfigureAwait(false);
        if (record is null || record.CancelRequested || TaskStates.IsTerminal(record.Status))
        {
          run.Cancel();
          throw new OperationCanceledException(run.Token);
        }

        var answers = ReadMap(record.InputResponsesJson);
        if (answers.TryGetValue(key, out var raw))
        {
          // Consuming the answer is a change like any other, so two instances cannot both take it.
          var taken = await MutateAsync(run.TaskId, current =>
          {
            var map = ReadMap(current.InputResponsesJson);
            if (!map.Remove(key))
            {
              return null;
            }

            return current with { InputResponsesJson = map.Count == 0 ? null : WriteMap(map) };
          }, linked.Token).ConfigureAwait(false);
          if (taken is not null)
          {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
          }
        }

        using var wake = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        await Task.WhenAny(run.Tick, Task.Delay(_options.StorePollInterval, _time, wake.Token)).ConfigureAwait(false);
        await wake.CancelAsync().ConfigureAwait(false);
      }
    }
    catch (OperationCanceledException) when (timeout.IsCancellationRequested && !run.Token.IsCancellationRequested)
    {
      throw new TimeoutException("The client did not answer the input request '" + key + "' in time.");
    }
  }

  // ---- the three requests ----

  private async ValueTask<TaskRecord> LoadAsync(JsonElement parameters, RequestContext context)
  {
    if (parameters.ValueKind != JsonValueKind.Object
      || !parameters.TryGetProperty("taskId", out var idElement)
      || idElement.ValueKind != JsonValueKind.String
      || idElement.GetString() is not { Length: > 0 } taskId)
    {
      throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "Invalid params: taskId is required.");
    }

    var record = await _store.GetAsync(taskId, context.CancellationToken).ConfigureAwait(false);

    // Tasks extension, "Security Considerations": every task request is checked against the caller; another caller's task does not exist.
    if (record is null || !string.Equals(record.Owner, OwnerOf(context.Principal), StringComparison.Ordinal))
    {
      throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "Failed to retrieve task: Task not found");
    }

    ThrowIfStorageFaulted();
    if (record.ExpiresAt is { } at && at <= _time.GetUtcNow())
    {
      if (_running.TryGetValue(taskId, out var run))
      {
        run.Cancel();
      }

      await _store.DeleteAsync(taskId, context.CancellationToken).ConfigureAwait(false);
      throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "Failed to retrieve task: Task has expired");
    }

    return record;
  }

  private async ValueTask<JsonElement> GetAsync(JsonElement parameters, RequestContext context)
  {
    var record = await LoadAsync(parameters, context).ConfigureAwait(false);
    return ToElement(record, WireNames.ResultTypeComplete);
  }

  private async ValueTask<JsonElement> UpdateAsync(JsonElement parameters, RequestContext context)
  {
    var record = await LoadAsync(parameters, context).ConfigureAwait(false);
    if (!parameters.TryGetProperty("inputResponses", out var responses) || responses.ValueKind != JsonValueKind.Object)
    {
      throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "Invalid params: inputResponses is required.");
    }

    var given = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var property in responses.EnumerateObject())
    {
      if (property.Value.ValueKind != JsonValueKind.Object)
      {
        throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "Invalid params: every input response is an object.");
      }

      given[property.Name] = property.Value.GetRawText();
    }

    // "A server SHOULD ignore any inputResponses mapped to a key that is not currently outstanding": those are dropped, not an error.
    await MutateAsync(record.TaskId, current =>
    {
      if (TaskStates.IsTerminal(current.Status))
      {
        return null;
      }

      var outstanding = ReadMap(current.InputRequestsJson);
      var answered = ReadMap(current.InputResponsesJson);
      var changed = false;
      foreach (var (key, raw) in given)
      {
        if (outstanding.Remove(key))
        {
          answered[key] = raw;
          changed = true;
        }
      }

      if (!changed)
      {
        return null;
      }

      return current with
      {
        Status = outstanding.Count == 0 ? TaskStates.Working : current.Status,
        InputRequestsJson = outstanding.Count == 0 ? null : WriteMap(outstanding),
        InputResponsesJson = WriteMap(answered),
      };
    }, context.CancellationToken).ConfigureAwait(false);

    if (_running.TryGetValue(record.TaskId, out var run))
    {
      run.Pulse();
    }

    return Acknowledgement();
  }

  private async ValueTask<JsonElement> CancelAsync(JsonElement parameters, RequestContext context)
  {
    var record = await LoadAsync(parameters, context).ConfigureAwait(false);

    // Cancellation is cooperative and eventually consistent: the request is recorded and the work is told, and the task reaches
    // "cancelled" when the work stops (it may finish first).
    if (!TaskStates.IsTerminal(record.Status))
    {
      await MutateAsync(record.TaskId, current => TaskStates.IsTerminal(current.Status) || current.CancelRequested ? null : current with { CancelRequested = true }, context.CancellationToken).ConfigureAwait(false);
      if (_running.TryGetValue(record.TaskId, out var run))
      {
        run.Cancel();
      }
    }

    return Acknowledgement();
  }

  private static JsonElement Acknowledgement() =>
    JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["resultType"] = WireNames.ResultTypeComplete }, WireJsonContext.Default.DictionaryStringString);

  // ---- JSON ----

  private static ExtensionResult ToResult(TaskRecord record, string resultType) =>
    ToElement(record, resultType).Deserialize(WireJsonContext.Default.ExtensionResult)!;

  internal static JsonElement ToElement(TaskRecord record, string resultType)
  {
    var buffer = new ArrayBufferWriter<byte>();
    using (var writer = new Utf8JsonWriter(buffer))
    {
      WriteTask(writer, record, resultType);
    }

    using var document = JsonDocument.Parse(buffer.WrittenMemory);
    return document.RootElement.Clone();
  }

  // The Task shape of the Tasks extension, with the status-specific members inlined (DetailedTask).
  internal static void WriteTask(Utf8JsonWriter writer, TaskRecord record, string? resultType)
  {
    writer.WriteStartObject();
    if (resultType is not null)
    {
      writer.WriteString("resultType", resultType);
    }

    writer.WriteString("taskId", record.TaskId);
    writer.WriteString("status", record.Status);
    if (record.StatusMessage is { } message)
    {
      writer.WriteString("statusMessage", message);
    }

    writer.WriteString("createdAt", Iso(record.CreatedAt));
    writer.WriteString("lastUpdatedAt", Iso(record.LastUpdatedAt));
    if (record.TtlMs is { } ttl)
    {
      writer.WriteNumber("ttlMs", ttl);
    }
    else
    {
      writer.WriteNull("ttlMs");
    }

    if (record.PollIntervalMs is { } poll)
    {
      writer.WriteNumber("pollIntervalMs", poll);
    }

    switch (record.Status)
    {
      case TaskStates.InputRequired:
        writer.WritePropertyName("inputRequests");
        WriteRaw(writer, record.InputRequestsJson ?? "{}");
        break;
      case TaskStates.Completed when record.ResultJson is { } result:
        writer.WritePropertyName("result");
        WriteRaw(writer, result);
        break;
      case TaskStates.Failed when record.ErrorJson is { } error:
        writer.WritePropertyName("error");
        WriteRaw(writer, error);
        break;
    }

    writer.WriteEndObject();
  }

  private static void WriteRaw(Utf8JsonWriter writer, string json)
  {
    using var document = JsonDocument.Parse(json);
    document.RootElement.WriteTo(writer);
  }

  private static string Iso(DateTimeOffset value) =>
    value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

  private static string WriteError(JsonRpcError error)
  {
    var buffer = new ArrayBufferWriter<byte>();
    using (var writer = new Utf8JsonWriter(buffer))
    {
      writer.WriteStartObject();
      writer.WriteNumber("code", error.Code);
      writer.WriteString("message", error.Message);
      if (error.Data.ValueKind != JsonValueKind.Undefined)
      {
        writer.WritePropertyName("data");
        error.Data.WriteTo(writer);
      }

      writer.WriteEndObject();
    }

    return Encoding.UTF8.GetString(buffer.WrittenSpan);
  }

  private static Dictionary<string, string> ReadMap(string? json)
  {
    var map = new Dictionary<string, string>(StringComparer.Ordinal);
    if (json is null)
    {
      return map;
    }

    using var document = JsonDocument.Parse(json);
    foreach (var property in document.RootElement.EnumerateObject())
    {
      map[property.Name] = property.Value.GetRawText();
    }

    return map;
  }

  private static string WriteMap(Dictionary<string, string> map)
  {
    var buffer = new ArrayBufferWriter<byte>();
    using (var writer = new Utf8JsonWriter(buffer))
    {
      writer.WriteStartObject();
      foreach (var (key, raw) in map)
      {
        writer.WritePropertyName(key);
        WriteRaw(writer, raw);
      }

      writer.WriteEndObject();
    }

    return Encoding.UTF8.GetString(buffer.WrittenSpan);
  }

  private static HashSet<string> ReadKeys(string? json)
  {
    var keys = new HashSet<string>(StringComparer.Ordinal);
    if (json is null)
    {
      return keys;
    }

    using var document = JsonDocument.Parse(json);
    foreach (var item in document.RootElement.EnumerateArray())
    {
      if (item.GetString() is { } key)
      {
        keys.Add(key);
      }
    }

    return keys;
  }

  private static string WriteKeys(HashSet<string> keys)
  {
    var buffer = new ArrayBufferWriter<byte>();
    using (var writer = new Utf8JsonWriter(buffer))
    {
      writer.WriteStartArray();
      foreach (var key in keys)
      {
        writer.WriteStringValue(key);
      }

      writer.WriteEndArray();
    }

    return Encoding.UTF8.GetString(buffer.WrittenSpan);
  }
}
