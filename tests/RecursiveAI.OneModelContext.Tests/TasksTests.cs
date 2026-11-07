using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Extensions.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>The Tasks extension (Beta): a tool call that becomes a task, polling, input, cancellation and the stores.</summary>
public class TasksTests
{
  private static readonly JsonElement ObjectSchema = Json.Parse("""{"type":"object"}""");
  private static readonly JsonElement NameForm = Json.Parse("""{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""");

  private static TaskServerOptions Quick(ITaskStore? store = null, TimeProvider? unused = null) => new()
  {
    Store = store,
    PollInterval = TimeSpan.FromMilliseconds(20),
    StorePollInterval = TimeSpan.FromMilliseconds(20),
    InputTimeout = TimeSpan.FromSeconds(20),
  };

  private static ContextServer NewServer(TaskServerOptions? options = null, ContextServerOptions? serverOptions = null)
  {
    var server = new ContextServer("tasks", "1.0", serverOptions);
    server.UseTasks(options ?? Quick());
    return server;
  }

  private static async Task<(ContextClient Client, Func<Task> Stop)> ConnectAsync(ContextServer server, EraPreference era, string version, bool declare = true, Func<ElicitRequestParams, CancellationToken, ValueTask<ElicitResult>>? onElicit = null)
  {
    var (a, b) = PipeEnd.CreatePair();
    var stop = new CancellationTokenSource();
    var serving = server.RunAsync(ContextTransport.Stream(a), stop.Token);
    var client = await ContextClient.ConnectAsync(
      ContextTransport.Stream(b),
      new ContextClientOptions
      {
        Era = era,
        SupportedVersions = [version],
        EraCache = null,
        Extensions = declare ? [TasksExtension.Client()] : [],
        OnElicit = onElicit,
      },
      TestContext.Current.CancellationToken);
    return (client, async () =>
    {
      await client.DisposeAsync();
      await stop.CancelAsync();
      try
      {
        await serving.WaitAsync(TimeSpan.FromSeconds(10));
      }
      catch (OperationCanceledException)
      {
        // Stopping the server ends its loop by cancellation.
      }
    });
  }

  // The client answers by hand in these tests; the handler only makes it declare that it can elicit.
  private static ValueTask<ElicitResult> Unused(ElicitRequestParams request, CancellationToken cancellationToken) =>
    throw new InvalidOperationException("Not expected.");

  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  [Fact]
  public async Task Tasks_are_off_until_the_host_opts_in()
  {
    var server = new ContextServer("plain", "1.0");
    Assert.Null(server.Capabilities.Extensions);

    var (client, stop) = await ConnectAsync(server, EraPreference.Modern, ProtocolVersions.V20260728);
    Assert.False(client.ServerSupportsExtension(TasksExtension.Id));
    await stop();

    var tasks = NewServer();
    (client, stop) = await ConnectAsync(tasks, EraPreference.Modern, ProtocolVersions.V20260728);
    Assert.True(client.ServerSupportsExtension(TasksExtension.Id));
    await stop();
  }

  [Theory]
  [InlineData(ProtocolVersions.V20251125)]
  [InlineData(ProtocolVersions.V20250326)]
  public async Task Legacy_revisions_do_not_see_the_extension_and_run_the_tool_inside_the_call(string version)
  {
    var server = NewServer();
    server.AddTaskTool(new ToolDefinition("work", "Works.", ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("done")));
    var (client, stop) = await ConnectAsync(server, EraPreference.Legacy, version);

    Assert.False(client.ServerSupportsExtension(TasksExtension.Id));
    var result = await client.CallToolAsync("work", cancellationToken: Ct);
    Assert.Equal("done", result.Text);
    await stop();
  }

  [Fact]
  public async Task A_caller_without_the_extension_gets_the_result_inside_the_call_or_error_32021()
  {
    var server = NewServer();
    server.AddTaskTool(new ToolDefinition("work", "Works.", ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("done")));
    server.AddTaskTool(new ToolDefinition("only_task", "Needs a task.", ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("never")), new TaskToolOptions { RequireTasks = true });
    var (client, stop) = await ConnectAsync(server, EraPreference.Modern, ProtocolVersions.V20260728, declare: false);

    Assert.Equal("done", (await client.CallToolAsync("work", cancellationToken: Ct)).Text);
    var missing = await Assert.ThrowsAsync<MissingCapabilityException>(async () => await client.CallToolAsync("only_task", cancellationToken: Ct));
    Assert.Equal(ProtocolErrorCodes.MissingRequiredClientCapability, missing.Code);
    Assert.True(missing.Required.Extensions!.ContainsKey(TasksExtension.Id));

    // tasks/get from a client that did not declare the extension is -32021 as well (Tasks, "Error Handling").
    var parameters = Json.Parse("""{"taskId":"abc"}""");
    var refused = await Assert.ThrowsAsync<MissingCapabilityException>(async () => await client.CallExtensionAsync(TasksExtension.GetMethod, parameters, Ct));
    Assert.Equal(ProtocolErrorCodes.MissingRequiredClientCapability, refused.Code);
    await stop();
  }

  [Fact]
  public async Task A_task_is_polled_to_completion()
  {
    var release = new TaskCompletionSource();
    var server = NewServer();
    server.AddTaskTool(new ToolDefinition("work", "Works.", ObjectSchema), async (arguments, task) =>
    {
      await task.SetStatusMessageAsync("halfway");
      await release.Task.WaitAsync(task.CancellationToken);
      return CallToolResult.FromText("done:" + arguments.GetProperty("n").GetInt32());
    });
    var (client, stop) = await ConnectAsync(server, EraPreference.Modern, ProtocolVersions.V20260728);

    var outcome = await client.CallToolAsTaskAsync("work", Json.Parse("""{"n":7}"""), Ct);
    Assert.Null(outcome.Result);
    var task = Assert.IsType<RemoteTask>(outcome.Task);
    Assert.Equal(TaskStates.Working, task.Latest.Status);
    Assert.NotNull(task.Latest.PollInterval);
    Assert.True(task.TaskId.Length >= 32);

    TaskSnapshot snapshot;
    do
    {
      snapshot = await task.GetAsync(Ct);
      await Task.Delay(10, Ct);
    }
    while (snapshot.StatusMessage != "halfway");
    Assert.Equal(TaskStates.Working, snapshot.Status);

    release.SetResult();
    var final = await task.WaitAsync(Ct);
    Assert.Equal(TaskStates.Completed, final.Status);
    Assert.Equal("done:7", final.Result!.Text);
    await stop();
  }

  [Fact]
  public async Task The_wire_shape_follows_the_specification()
  {
    var server = NewServer();
    server.AddTaskTool(new ToolDefinition("work", "Works.", ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("done")));
    var session = new Wire.ServerSessionState();
    var call = await server.HandleMessageAsync(ModernRequest(1, "tools/call", """{"name":"work","arguments":{}}"""), session, null, null, Ct);
    var created = JsonDocument.Parse(call!).RootElement.GetProperty("result");

    Assert.Equal("task", created.GetProperty("resultType").GetString());
    Assert.Equal("working", created.GetProperty("status").GetString());
    Assert.False(created.TryGetProperty("content", out _));
    Assert.Equal(JsonValueKind.Number, created.GetProperty("ttlMs").ValueKind);
    var id = created.GetProperty("taskId").GetString()!;

    JsonElement got;
    do
    {
      await Task.Delay(10, Ct);
      var reply = await server.HandleMessageAsync(ModernRequest(2, "tasks/get", "{\"taskId\":\"" + id + "\"}"), session, null, null, Ct);
      got = JsonDocument.Parse(reply!).RootElement.GetProperty("result").Clone();
    }
    while (got.GetProperty("status").GetString() == "working");

    Assert.Equal("complete", got.GetProperty("resultType").GetString());
    Assert.Equal("completed", got.GetProperty("status").GetString());
    Assert.Equal("done", got.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    Assert.False(got.TryGetProperty("requestState", out _));

    var update = JsonDocument.Parse((await server.HandleMessageAsync(ModernRequest(3, "tasks/update", "{\"taskId\":\"" + id + "\",\"inputResponses\":{}}"), session, null, null, Ct))!).RootElement;
    Assert.Equal("complete", update.GetProperty("result").GetProperty("resultType").GetString());

    var unknown = JsonDocument.Parse((await server.HandleMessageAsync(ModernRequest(4, "tasks/get", """{"taskId":"nope"}"""), session, null, null, Ct))!).RootElement;
    Assert.Equal(-32602, unknown.GetProperty("error").GetProperty("code").GetInt32());
    var malformed = JsonDocument.Parse((await server.HandleMessageAsync(ModernRequest(5, "tasks/get", "{}"), session, null, null, Ct))!).RootElement;
    Assert.Equal(-32602, malformed.GetProperty("error").GetProperty("code").GetInt32());
  }

  [Fact]
  public async Task A_task_asks_for_input_and_continues_when_the_client_answers()
  {
    var server = NewServer();
    server.AddTaskTool(new ToolDefinition("hello", "Greets.", ObjectSchema), async (_, task) =>
    {
      var answer = await task.ElicitAsync("name", "Please enter your name.", NameForm);
      return CallToolResult.FromText("Hello, " + answer.Content!["name"].GetString() + "!");
    });
    var (client, stop) = await ConnectAsync(server, EraPreference.Modern, ProtocolVersions.V20260728, onElicit: Unused);
    var task = (await client.CallToolAsTaskAsync("hello", cancellationToken: Ct)).Task!;

    TaskSnapshot snapshot;
    do
    {
      await Task.Delay(10, Ct);
      snapshot = await task.GetAsync(Ct);
    }
    while (snapshot.Status == TaskStates.Working);

    Assert.Equal(TaskStates.InputRequired, snapshot.Status);
    var asked = Assert.Single(snapshot.InputRequests!);
    Assert.Equal("name", asked.Key);
    Assert.Equal(Methods.ElicitationCreate, asked.Value.Method);

    // The same question is repeated on every poll until it is answered.
    Assert.Single((await task.GetAsync(Ct)).InputRequests!);

    await task.UpdateAsync(new Dictionary<string, JsonElement> { ["name"] = Json.Parse("""{"action":"accept","content":{"name":"Luca"}}"""), ["unknown"] = Json.Parse("{}") }, Ct);
    var final = await task.WaitAsync(Ct);
    Assert.Equal("Hello, Luca!", final.Result!.Text);
    await stop();
  }

  [Fact]
  public async Task WaitAsync_answers_input_requests_with_the_clients_handlers()
  {
    var server = NewServer();
    server.AddTaskTool(new ToolDefinition("hello", "Greets.", ObjectSchema), async (_, task) =>
    {
      var first = await task.ElicitAsync("one", "First?", NameForm);
      var second = await task.ElicitAsync("two", "Second?", NameForm);
      return CallToolResult.FromText(first.Content!["name"].GetString() + "+" + second.Content!["name"].GetString());
    });
    var asked = 0;
    var (client, stop) = await ConnectAsync(server, EraPreference.Modern, ProtocolVersions.V20260728, onElicit: (_, _) =>
    {
      var n = Interlocked.Increment(ref asked);
      return ValueTask.FromResult(ElicitResult.Accept(new Dictionary<string, JsonElement> { ["name"] = Json.Parse("\"n" + n + "\"") }));
    });

    // A plain CallToolAsync on a client that declared tasks follows the task to its end.
    var result = await client.CallToolAsync("hello", cancellationToken: Ct);
    Assert.Equal("n1+n2", result.Text);
    Assert.Equal(2, asked);
    await stop();
  }

  [Fact]
  public async Task Reusing_an_input_key_fails_the_task()
  {
    var server = NewServer();
    server.AddTaskTool(new ToolDefinition("twice", "Asks twice with one key.", ObjectSchema), async (_, task) =>
    {
      await task.ElicitAsync("k", "First?", NameForm);
      await task.ElicitAsync("k", "Again?", NameForm);
      return CallToolResult.FromText("unreachable");
    });
    var (client, stop) = await ConnectAsync(server, EraPreference.Modern, ProtocolVersions.V20260728, onElicit: (_, _) =>
      ValueTask.FromResult(ElicitResult.Accept(new Dictionary<string, JsonElement> { ["name"] = Json.Parse("\"x\"") })));
    var task = (await client.CallToolAsTaskAsync("twice", cancellationToken: Ct)).Task!;

    var final = await task.WaitAsync(Ct);
    Assert.Equal(TaskStates.Failed, final.Status);
    Assert.Equal(-32603, final.Error!.Value.Code);
    await stop();
  }

  [Fact]
  public async Task Cancelling_stops_the_work_and_the_task_ends_cancelled()
  {
    var started = new TaskCompletionSource();
    var server = NewServer();
    server.AddTaskTool(new ToolDefinition("slow", "Waits.", ObjectSchema), async (_, task) =>
    {
      started.SetResult();
      await Task.Delay(Timeout.Infinite, task.CancellationToken);
      return CallToolResult.FromText("unreachable");
    });
    var (client, stop) = await ConnectAsync(server, EraPreference.Modern, ProtocolVersions.V20260728);
    var task = (await client.CallToolAsTaskAsync("slow", cancellationToken: Ct)).Task!;
    await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

    await task.CancelAsync(Ct);
    var final = await task.WaitAsync(Ct);
    Assert.Equal(TaskStates.Cancelled, final.Status);

    // Cancelling a finished task is acknowledged and changes nothing.
    await task.CancelAsync(Ct);
    Assert.Equal(TaskStates.Cancelled, (await task.GetAsync(Ct)).Status);
    await stop();
  }

  [Fact]
  public async Task A_tool_error_completes_the_task_and_a_protocol_error_fails_it()
  {
    var server = NewServer();
    server.AddTaskTool(new ToolDefinition("soft", "Fails softly.", ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromError("bad input")));
    server.AddTaskTool(new ToolDefinition("hard", "Fails hard.", ObjectSchema), (_, _) => throw new ProtocolException(-32603, "rate limited"));
    server.AddTaskTool(new ToolDefinition("crash", "Throws.", ObjectSchema), (_, _) => throw new InvalidOperationException("secret detail"));
    var (client, stop) = await ConnectAsync(server, EraPreference.Modern, ProtocolVersions.V20260728);

    var soft = await (await client.CallToolAsTaskAsync("soft", cancellationToken: Ct)).Task!.WaitAsync(Ct);
    Assert.Equal(TaskStates.Completed, soft.Status);
    Assert.True(soft.Result!.IsError);

    var hard = await (await client.CallToolAsTaskAsync("hard", cancellationToken: Ct)).Task!.WaitAsync(Ct);
    Assert.Equal(TaskStates.Failed, hard.Status);
    Assert.Equal("rate limited", hard.Error!.Value.Message);

    var crash = await (await client.CallToolAsTaskAsync("crash", cancellationToken: Ct)).Task!.WaitAsync(Ct);
    Assert.Equal(TaskStates.Failed, crash.Status);
    Assert.DoesNotContain("secret", crash.Error!.Value.Message, StringComparison.Ordinal);
    await stop();
  }

  [Fact]
  public async Task Another_caller_cannot_see_the_task()
  {
    var release = new TaskCompletionSource();
    var server = NewServer();
    server.AddTaskTool(new ToolDefinition("work", "Works.", ObjectSchema), async (_, task) =>
    {
      await release.Task.WaitAsync(task.CancellationToken);
      return CallToolResult.FromText("secret result");
    });
    var alice = Principal("https://issuer.example", "alice");
    var bob = Principal("https://issuer.example", "bob");
    var session = new Wire.ServerSessionState();

    var created = JsonDocument.Parse((await server.HandleMessageAsync(ModernRequest(1, "tools/call", """{"name":"work","arguments":{}}"""), session, null, alice, Ct))!).RootElement.GetProperty("result");
    var request = "{\"taskId\":\"" + created.GetProperty("taskId").GetString() + "\"}";

    foreach (var method in new[] { "tasks/get", "tasks/cancel" })
    {
      var denied = JsonDocument.Parse((await server.HandleMessageAsync(ModernRequest(2, method, request), session, null, bob, Ct))!).RootElement;
      Assert.Equal(-32602, denied.GetProperty("error").GetProperty("code").GetInt32());
      var anonymous = JsonDocument.Parse((await server.HandleMessageAsync(ModernRequest(3, method, request), session, null, null, Ct))!).RootElement;
      Assert.Equal(-32602, anonymous.GetProperty("error").GetProperty("code").GetInt32());
    }

    var own = JsonDocument.Parse((await server.HandleMessageAsync(ModernRequest(4, "tasks/get", request), session, null, alice, Ct))!).RootElement;
    Assert.Equal("working", own.GetProperty("result").GetProperty("status").GetString());
    release.SetResult();
  }

  [Fact]
  public async Task A_task_expires_after_its_time_to_live()
  {
    var clock = new ManualTimeProvider();
    var server = NewServer(
      new TaskServerOptions { PollInterval = TimeSpan.FromMilliseconds(20), Ttl = TimeSpan.FromMinutes(5) },
      new ContextServerOptions { TimeProvider = clock });
    server.AddTaskTool(new ToolDefinition("work", "Works.", ObjectSchema), async (_, task) =>
    {
      await Task.Delay(Timeout.Infinite, task.CancellationToken);
      return CallToolResult.FromText("unreachable");
    });
    var session = new Wire.ServerSessionState();
    var created = JsonDocument.Parse((await server.HandleMessageAsync(ModernRequest(1, "tools/call", """{"name":"work","arguments":{}}"""), session, null, null, Ct))!).RootElement.GetProperty("result");
    Assert.Equal(300_000, created.GetProperty("ttlMs").GetInt64());
    var request = "{\"taskId\":\"" + created.GetProperty("taskId").GetString() + "\"}";

    Assert.True(JsonDocument.Parse((await server.HandleMessageAsync(ModernRequest(2, "tasks/get", request), session, null, null, Ct))!).RootElement.TryGetProperty("result", out _));
    clock.Advance(TimeSpan.FromMinutes(6));
    var expired = JsonDocument.Parse((await server.HandleMessageAsync(ModernRequest(3, "tasks/get", request), session, null, null, Ct))!).RootElement;
    Assert.Equal(-32602, expired.GetProperty("error").GetProperty("code").GetInt32());
    Assert.Contains("expired", expired.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
  }

  [Fact]
  public async Task Running_work_is_bounded()
  {
    var release = new TaskCompletionSource();
    var server = NewServer(new TaskServerOptions { MaxRunningTasks = 1, PollInterval = TimeSpan.FromMilliseconds(20), StorePollInterval = TimeSpan.FromMilliseconds(20) });
    server.AddTaskTool(new ToolDefinition("work", "Works.", ObjectSchema), async (_, task) =>
    {
      await release.Task.WaitAsync(task.CancellationToken);
      return CallToolResult.FromText("done");
    });
    var (client, stop) = await ConnectAsync(server, EraPreference.Modern, ProtocolVersions.V20260728);

    var first = await client.CallToolAsTaskAsync("work", cancellationToken: Ct);
    Assert.NotNull(first.Task);
    var second = await client.CallToolAsTaskAsync("work", cancellationToken: Ct);
    Assert.True(second.Result!.IsError);
    release.SetResult();
    await first.Task.WaitAsync(Ct);
    await stop();
  }

  [Fact]
  public async Task Tasks_survive_a_restart_with_the_file_store()
  {
    var directory = Path.Combine(Path.GetTempPath(), "omc-tasks-" + Guid.NewGuid().ToString("N"));
    try
    {
      string taskId;
      using (var store = new FileTaskStore(directory))
      {
        var server = NewServer(Quick(store));
        server.AddTaskTool(new ToolDefinition("work", "Works.", ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("kept")));
        var (client, stop) = await ConnectAsync(server, EraPreference.Modern, ProtocolVersions.V20260728);
        var task = (await client.CallToolAsTaskAsync("work", cancellationToken: Ct)).Task!;
        taskId = task.TaskId;
        Assert.Equal(TaskStates.Completed, (await task.WaitAsync(Ct)).Status);
        await stop();
      }

      // A new server object (a restart, or a second instance) over the same directory answers for the task.
      using var again = new FileTaskStore(directory);
      var restarted = NewServer(Quick(again));
      var (client2, stop2) = await ConnectAsync(restarted, EraPreference.Modern, ProtocolVersions.V20260728);
      var resumed = await client2.GetTaskAsync(taskId, Ct);
      Assert.Equal(TaskStates.Completed, resumed.Latest.Status);
      Assert.Equal("kept", resumed.Latest.Result!.Text);
      await stop2();
    }
    finally
    {
      if (Directory.Exists(directory))
      {
        Directory.Delete(directory, recursive: true);
      }
    }
  }

  [Fact]
  public async Task A_second_instance_sharing_the_store_answers_and_delivers_input()
  {
    var store = new MemoryTaskStore();
    var worker = NewServer(Quick(store));
    worker.AddTaskTool(new ToolDefinition("hello", "Greets.", ObjectSchema), async (_, task) =>
    {
      var answer = await task.ElicitAsync("name", "Name?", NameForm);
      return CallToolResult.FromText("Hello, " + answer.Content!["name"].GetString());
    });
    var other = NewServer(Quick(store));
    var (client1, stop1) = await ConnectAsync(worker, EraPreference.Modern, ProtocolVersions.V20260728, onElicit: Unused);
    var (client2, stop2) = await ConnectAsync(other, EraPreference.Modern, ProtocolVersions.V20260728, onElicit: Unused);

    var task = (await client1.CallToolAsTaskAsync("hello", cancellationToken: Ct)).Task!;
    var onOther = await client2.GetTaskAsync(task.TaskId, Ct);
    TaskSnapshot snapshot;
    do
    {
      await Task.Delay(10, Ct);
      snapshot = await onOther.GetAsync(Ct);
    }
    while (snapshot.Status != TaskStates.InputRequired);

    await onOther.UpdateAsync(new Dictionary<string, JsonElement> { ["name"] = Json.Parse("""{"action":"accept","content":{"name":"Ada"}}""") }, Ct);
    Assert.Equal("Hello, Ada", (await onOther.WaitAsync(Ct)).Result!.Text);

    // Cancelling through the other instance reaches the work on the first.
    var slowServer = NewServer(Quick(store));
    slowServer.AddTaskTool(new ToolDefinition("slow", "Waits.", ObjectSchema), async (_, t) =>
    {
      await Task.Delay(Timeout.Infinite, t.CancellationToken);
      return CallToolResult.FromText("unreachable");
    });
    var (client3, stop3) = await ConnectAsync(slowServer, EraPreference.Modern, ProtocolVersions.V20260728);
    var slow = (await client3.CallToolAsTaskAsync("slow", cancellationToken: Ct)).Task!;
    await (await client2.GetTaskAsync(slow.TaskId, Ct)).CancelAsync(Ct);
    Assert.Equal(TaskStates.Cancelled, (await slow.WaitAsync(Ct)).Status);

    await stop1();
    await stop2();
    await stop3();
  }

  [Fact]
  public async Task The_stores_enforce_compare_and_swap_and_expiry()
  {
    var directory = Path.Combine(Path.GetTempPath(), "omc-store-" + Guid.NewGuid().ToString("N"));
    try
    {
      using var file = new FileTaskStore(directory, capacity: 2);
      foreach (var store in new ITaskStore[] { new MemoryTaskStore(2), file })
      {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var task = new TaskRecord { TaskId = "a", Owner = "", ToolName = "t", Status = TaskStates.Working, CreatedAt = now, LastUpdatedAt = now, TtlMs = 1000 };
        await store.CreateAsync(task, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.CreateAsync(task, Ct));

        var read = (await store.GetAsync("a", Ct))!;
        Assert.Equal(0, read.Version);
        var changed = await store.UpdateAsync(read with { StatusMessage = "one" }, Ct);
        Assert.Equal(1, changed!.Version);
        Assert.Null(await store.UpdateAsync(read with { StatusMessage = "stale" }, Ct));
        Assert.Equal("one", (await store.GetAsync("a", Ct))!.StatusMessage);
        Assert.Null(await store.UpdateAsync(new TaskRecord { TaskId = "missing", Owner = "", ToolName = "t", Status = "working", CreatedAt = now, LastUpdatedAt = now }, Ct));

        await store.CreateAsync(task with { TaskId = "b", TtlMs = null }, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.CreateAsync(task with { TaskId = "c" }, Ct));
        Assert.Equal(0, await store.DeleteExpiredAsync(now, Ct));
        Assert.Equal(1, await store.DeleteExpiredAsync(now.AddSeconds(2), Ct));
        Assert.Null(await store.GetAsync("a", Ct));
        Assert.NotNull(await store.GetAsync("b", Ct));
        Assert.True(await store.DeleteAsync("b", Ct));
        Assert.False(await store.DeleteAsync("b", Ct));
      }
    }
    finally
    {
      if (Directory.Exists(directory))
      {
        Directory.Delete(directory, recursive: true);
      }
    }
  }

  [Theory]
  [InlineData("../../etc/passwd")]
  [InlineData("a/b")]
  [InlineData("a\\b")]
  [InlineData("")]
  [InlineData("..")]
  public async Task The_file_store_refuses_ids_that_are_not_file_names(string taskId)
  {
    var directory = Path.Combine(Path.GetTempPath(), "omc-store-" + Guid.NewGuid().ToString("N"));
    try
    {
      using var store = new FileTaskStore(directory);
      Assert.Null(await store.GetAsync(taskId, Ct));
      Assert.False(await store.DeleteAsync(taskId, Ct));
      var now = DateTimeOffset.UtcNow;
      await Assert.ThrowsAsync<ArgumentException>(async () => await store.CreateAsync(new TaskRecord { TaskId = taskId, Owner = "", ToolName = "t", Status = "working", CreatedAt = now, LastUpdatedAt = now }, Ct));
    }
    finally
    {
      if (Directory.Exists(directory))
      {
        Directory.Delete(directory, recursive: true);
      }
    }
  }

  private static ClaimsPrincipal Principal(string issuer, string subject) =>
    new(new ClaimsIdentity([new Claim("iss", issuer), new Claim("sub", subject)], "test"));

  private static string ModernRequest(int id, string method, string parameters)
  {
    var meta = "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{\"extensions\":{\"io.modelcontextprotocol/tasks\":{}}}}";
    var body = parameters.Length <= 2 ? "{" + meta + "}" : parameters[..^1] + "," + meta + "}";
    return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"" + method + "\",\"params\":" + body + "}";
  }
}
