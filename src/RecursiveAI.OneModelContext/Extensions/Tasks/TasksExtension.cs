using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Extensions.Tasks;

/// <summary>The names the Tasks extension defines (extension <c>io.modelcontextprotocol/tasks</c>, Beta).</summary>
[Experimental(ExperimentalIds.Tasks)]
public static class TasksExtension
{
  /// <summary>The extension identifier.</summary>
  public const string Id = "io.modelcontextprotocol/tasks";

  /// <summary><c>tasks/get</c>: the state of a task, with its result, error or outstanding input requests.</summary>
  public const string GetMethod = "tasks/get";

  /// <summary><c>tasks/update</c>: answers to the input requests of a task.</summary>
  public const string UpdateMethod = "tasks/update";

  /// <summary><c>tasks/cancel</c>: asks the server to stop a task.</summary>
  public const string CancelMethod = "tasks/cancel";

  /// <summary>The <c>resultType</c> of a result that is a task handle instead of the request's own result.</summary>
  public const string TaskResultType = "task";

  /// <summary>The notification method that carries a task's state to a <c>subscriptions/listen</c> stream.</summary>
  public const string StatusNotification = "notifications/tasks";

  /// <summary>True for the three requests the extension adds; their <c>Mcp-Name</c> header carries <c>params.taskId</c>.</summary>
  /// <param name="method">A method name.</param>
  public static bool IsTaskMethod(string method) => method is GetMethod or UpdateMethod or CancelMethod;

  /// <summary>
  /// What a client adds to <see cref="ContextClientOptions.Extensions"/> to say it understands tasks. A server only ever answers with a task
  /// to a client that did.
  /// </summary>
  /// <returns>The declaration; the extension has no settings.</returns>
  public static ClientExtension Client() => new(Id, default, official: true);
}

/// <summary>Settings of the Tasks extension on a server. Every limit is a default you can change.</summary>
[Experimental(ExperimentalIds.Tasks)]
public sealed class TaskServerOptions
{
  /// <summary>Where tasks are kept. Default a new <see cref="MemoryTaskStore"/>, which suits one process; share a store between instances to run several.</summary>
  public ITaskStore? Store { get; init; }

  /// <summary>Maximum duration to persist a background task's final outcome. Default 15 seconds; must be positive. Stores must honor cancellation. A persistence failure makes this instance refuse task requests with an internal error until the server is recreated, rather than return stale status.</summary>
  public TimeSpan PersistenceTimeout { get; init; } = TimeSpan.FromSeconds(15);

  /// <summary>How long a task lives from creation; after that it is discarded and polls get an error. <c>null</c> means no limit. Default one hour.</summary>
  public TimeSpan? Ttl { get; init; } = TimeSpan.FromHours(1);

  /// <summary>The polling interval suggested to clients. Default one second.</summary>
  public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

  /// <summary>How often running work looks in the store for answers, cancellation and expiry. Default 200 milliseconds.</summary>
  public TimeSpan StorePollInterval { get; init; } = TimeSpan.FromMilliseconds(200);

  /// <summary>How long a task waits for the client's answer to an input request before it fails. Default 15 minutes.</summary>
  public TimeSpan InputTimeout { get; init; } = TimeSpan.FromMinutes(15);

  /// <summary>Most tasks this server instance runs at once; a call beyond that gets a tool error. Default 256.</summary>
  public int MaxRunningTasks { get; init; } = 256;

  /// <summary>Most input requests one task may have outstanding. Default 16.</summary>
  public int MaxOutstandingInputRequests { get; init; } = 16;

  /// <summary>
  /// A <c>revision</c> string for the extension's settings, so peers can tell the version of this Beta extension apart. Default <c>null</c>:
  /// no revision is sent (the current specification defines none).
  /// </summary>
  public string? Revision { get; init; }

  /// <summary>Cancelled when the host shuts down; it cancels the running work of every task. Default none.</summary>
  public CancellationToken StopToken { get; init; }
}

/// <summary>Runs the work of a task. It receives the tool's arguments and a <see cref="TaskContext"/>.</summary>
/// <param name="arguments">The tool's arguments (an empty object when there were none). Valid after the call returned.</param>
/// <param name="task">The task, for cancellation, status messages and asking the client for input.</param>
/// <returns>The tool's result. A result with <c>IsError</c> still completes the task; throw <see cref="ProtocolException"/> to fail it.</returns>
[Experimental(ExperimentalIds.Tasks)]
public delegate ValueTask<CallToolResult> TaskToolHandler(JsonElement arguments, TaskContext task);

/// <summary>Per-tool settings for <see cref="TasksServerExtensions.AddTaskTool"/>.</summary>
[Experimental(ExperimentalIds.Tasks)]
public sealed class TaskToolOptions
{
  /// <summary>
  /// When true, a caller that did not declare the extension is refused with -32021 (the tool cannot be served without a task). Default
  /// <c>false</c>: such a caller gets the tool run to completion inside the call.
  /// </summary>
  public bool RequireTasks { get; init; }

  /// <summary>
  /// Runs inside the call before the task is created, with the request's context: ask the user for what the work needs
  /// (<see cref="RequestContext.ElicitAsync(string, System.Text.Json.JsonElement, string?)"/> works as in any tool, and the call is retried with the answers
  /// on 2026-07-28), check the arguments, or refuse. Whatever it returns becomes the arguments the work receives. Default none: the work
  /// receives the arguments of the call. The task exists only after this returns, as the extension asks for input needed before a task.
  /// </summary>
  public Func<JsonElement, RequestContext, ValueTask<JsonElement>>? Prepare { get; init; }
}

/// <summary>
/// What the work of a task sees: cancellation, a status message and the way to ask the client for input. When the caller does not understand
/// tasks the same work runs inside the call and this object behaves the same, except that only <see cref="ElicitAsync"/> can ask for input.
/// </summary>
[Experimental(ExperimentalIds.Tasks)]
public sealed class TaskContext
{
  private readonly TaskService? _service;
  private readonly Running? _run;
  private readonly RequestContext? _inline;

  internal TaskContext(TaskService service, Running run, string taskId, ClaimsPrincipal? principal, Implementation? clientInfo, ClientCapabilities capabilities)
  {
    _service = service;
    _run = run;
    TaskId = taskId;
    Principal = principal;
    ClientInfo = clientInfo;
    Capabilities = capabilities;
    CancellationToken = run.Token;
  }

  internal TaskContext(RequestContext inline)
  {
    _inline = inline;
    Principal = inline.Principal;
    ClientInfo = inline.ClientInfo;
    CancellationToken = inline.CancellationToken;
  }

  internal ClientCapabilities Capabilities { get; } = new();

  /// <summary>The task's id, or <c>null</c> when the work runs inside the call because the caller does not understand tasks.</summary>
  public string? TaskId { get; }

  /// <summary>The caller that started the work (not authenticated beyond what the host did), or null.</summary>
  public ClaimsPrincipal? Principal { get; }

  /// <summary>The client's self-reported name and version; not authenticated.</summary>
  public Implementation? ClientInfo { get; }

  /// <summary>Cancelled when the client cancels the task, the task expires or the server stops. Pass it to everything that waits.</summary>
  public CancellationToken CancellationToken { get; }

  /// <summary>Sets the status message clients see with the task (progress text, what it is waiting for). Does nothing when the work runs inside the call.</summary>
  /// <param name="message">A short text, shown to users or models.</param>
  public ValueTask SetStatusMessageAsync(string message)
  {
    ArgumentNullException.ThrowIfNull(message);
    return _service is null ? ValueTask.CompletedTask : _service.SetStatusMessageAsync(_run!.TaskId, message);
  }

  /// <summary>Asks the user for values described by a flat form schema and waits for the answer.</summary>
  /// <param name="inputKey">The name of this question in the task's <c>inputRequests</c>. It must be new for the task; reusing a key throws.</param>
  /// <param name="message">What to ask.</param>
  /// <param name="requestedSchema">An object schema of primitive properties.</param>
  /// <returns>The user's answer.</returns>
  /// <exception cref="InvalidOperationException">The key was used before in this task.</exception>
  /// <exception cref="TimeoutException">The client did not answer within <see cref="TaskServerOptions.InputTimeout"/>.</exception>
  /// <exception cref="OperationCanceledException">The task was cancelled.</exception>
  public async ValueTask<ElicitResult> ElicitAsync(string inputKey, string message, JsonElement requestedSchema)
  {
    ArgumentException.ThrowIfNullOrEmpty(inputKey);
    ArgumentNullException.ThrowIfNull(message);
    if (_inline is not null)
    {
      return await _inline.ElicitAsync(message, requestedSchema, inputKey).ConfigureAwait(false);
    }

    var parameters = new ElicitRequestParams { Mode = "form", Message = message, RequestedSchema = requestedSchema };
    var element = JsonSerializer.SerializeToElement(parameters, WireJsonContext.Default.ElicitRequestParams);
    var answer = await RequestInputAsync(inputKey, Methods.ElicitationCreate, element).ConfigureAwait(false);
    try
    {
      return answer.Deserialize(WireJsonContext.Default.ElicitResult)
        ?? throw new ProtocolException(JsonRpc.JsonRpcErrorCodes.InvalidParams, "The elicitation answer is empty.");
    }
    catch (JsonException ex)
    {
      throw new ProtocolException(JsonRpc.JsonRpcErrorCodes.InvalidParams, "The elicitation answer is not valid.", default, ex);
    }
  }

  /// <summary>
  /// Asks the client for input of any kind a task may request (<c>elicitation/create</c>, <c>sampling/createMessage</c>, <c>roots/list</c>)
  /// and waits for the answer. The task shows <c>input_required</c> with the request until the client answers with <c>tasks/update</c>.
  /// </summary>
  /// <param name="inputKey">The name of this request in <c>inputRequests</c>. It must be new for the task; reusing a key throws.</param>
  /// <param name="method">The request's method.</param>
  /// <param name="parameters">The request's params.</param>
  /// <returns>The client's answer, as the result object it sent.</returns>
  /// <exception cref="NotSupportedException">The work runs inside the call; only <see cref="ElicitAsync"/> is available then.</exception>
  /// <exception cref="InvalidOperationException">The key was used before in this task, or too many requests are outstanding.</exception>
  /// <exception cref="TimeoutException">The client did not answer within <see cref="TaskServerOptions.InputTimeout"/>.</exception>
  /// <exception cref="OperationCanceledException">The task was cancelled.</exception>
  public ValueTask<JsonElement> RequestInputAsync(string inputKey, string method, JsonElement parameters)
  {
    ArgumentException.ThrowIfNullOrEmpty(inputKey);
    ArgumentException.ThrowIfNullOrEmpty(method);
    if (_service is null)
    {
      throw new NotSupportedException("This call runs without a task; only ElicitAsync can ask the client for input.");
    }

    return _service.RequestInputAsync(_run!, Capabilities, inputKey, method, parameters);
  }
}

/// <summary>Registering the Tasks extension on a <see cref="ContextServer"/>.</summary>
[Experimental(ExperimentalIds.Tasks)]
public static class TasksServerExtensions
{
  private static readonly ConditionalWeakTable<ContextServer, TaskService> Services = new();

  /// <summary>
  /// Opts the server in to the Tasks extension: it advertises <see cref="TasksExtension.Id"/> on 2026-07-28 and answers <c>tasks/get</c>,
  /// <c>tasks/update</c> and <c>tasks/cancel</c>. Tools become tasks only when added with <see cref="AddTaskTool"/>, and only for callers
  /// that declared the extension. Call it once, before the server starts serving.
  /// </summary>
  /// <param name="server">The server.</param>
  /// <param name="options">The store and limits; <c>null</c> for the defaults.</param>
  /// <returns>The server.</returns>
  /// <exception cref="InvalidOperationException">The server already uses the extension.</exception>
  public static ContextServer UseTasks(this ContextServer server, TaskServerOptions? options = null)
  {
    ArgumentNullException.ThrowIfNull(server);
    options ??= new TaskServerOptions();
    var service = new TaskService(server, options);
    lock (Services)
    {
      if (Services.TryGetValue(server, out _))
      {
        throw new InvalidOperationException("The server already uses the Tasks extension.");
      }

      Services.Add(server, service);
    }

    server.Extensions.Add(service.CreateRegistration());
    return server;
  }

  /// <summary>
  /// Registers a tool whose work can run as a task. A caller that declared the extension gets a task handle at once and the work continues in
  /// the background; any other caller gets the work done inside the call (or error -32021 with <see cref="TaskToolOptions.RequireTasks"/>).
  /// The server opts in to the extension first if it has not (<see cref="UseTasks"/>).
  /// </summary>
  /// <param name="server">The server.</param>
  /// <param name="definition">The tool.</param>
  /// <param name="handler">Does the work.</param>
  /// <param name="options">Per-tool settings; <c>null</c> for the defaults.</param>
  /// <returns>The server.</returns>
  public static ContextServer AddTaskTool(this ContextServer server, ToolDefinition definition, TaskToolHandler handler, TaskToolOptions? options = null)
  {
    ArgumentNullException.ThrowIfNull(server);
    ArgumentNullException.ThrowIfNull(definition);
    ArgumentNullException.ThrowIfNull(handler);
    if (!Services.TryGetValue(server, out var service))
    {
      server.UseTasks();
      Services.TryGetValue(server, out service);
    }

    service!.AddTool(definition.Name, handler, options ?? new TaskToolOptions());
    var prepare = options?.Prepare;
    server.Tools.Add(definition, async (arguments, context) =>
    {
      if (prepare is not null)
      {
        arguments = await prepare(arguments, context).ConfigureAwait(false);
      }

      return await TaskService.RunInlineAsync(handler, arguments, context).ConfigureAwait(false);
    });
    return server;
  }
}
