using System.Text.Json;
using RecursiveAI.OneModelContext.Extensions.Tasks;
using RecursiveAI.OneModelContext.Hosting;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.Protocol;
using Xunit;

namespace RecursiveAI.OneModelContext.EntityFrameworkCore.Tests;

public sealed class TaskStoreTests : IDisposable
{
  private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  private readonly Database _db = new();

  public void Dispose() => _db.Dispose();

  private static TaskRecord Task(string id, long? ttl = 1000) => new()
  {
    TaskId = id, Owner = "", ToolName = "t", Status = TaskStates.Working, CreatedAt = Now, LastUpdatedAt = Now, TtlMs = ttl,
  };

  [Fact]
  public async Task The_store_enforces_compare_and_swap_capacity_and_expiry_like_the_memory_store()
  {
    var store = new EfTaskStore<AppDbContext>(_db.NewFactory(), capacity: 2);
    var task = Task("a") with { PollIntervalMs = 50, InputRequestsJson = "{\"k\":1}", UsedInputKeysJson = "[\"k\"]" };
    await store.CreateAsync(task, Ct);
    await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.CreateAsync(task, Ct));

    var read = (await store.GetAsync("a", Ct))!;
    Assert.Equal(0, read.Version);
    Assert.Equal(50, read.PollIntervalMs);
    Assert.Equal("{\"k\":1}", read.InputRequestsJson);
    Assert.Equal(Now.AddSeconds(1), read.ExpiresAt);

    var changed = await store.UpdateAsync(read with { StatusMessage = "one", Status = TaskStates.Completed, ResultJson = "{}", CancelRequested = true }, Ct);
    Assert.Equal(1, changed!.Version);
    Assert.Null(await store.UpdateAsync(read with { StatusMessage = "stale" }, Ct));
    var stored = (await store.GetAsync("a", Ct))!;
    Assert.Equal("one", stored.StatusMessage);
    Assert.Equal(TaskStates.Completed, stored.Status);
    Assert.True(stored.CancelRequested);
    Assert.Equal(1, stored.Version);
    Assert.Null(await store.UpdateAsync(Task("missing"), Ct));

    await store.CreateAsync(Task("b", ttl: null), Ct);
    await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.CreateAsync(Task("c"), Ct));
    Assert.Equal(0, await store.DeleteExpiredAsync(Now, Ct));
    Assert.Equal(1, await store.DeleteExpiredAsync(Now.AddSeconds(2), Ct));
    Assert.Null(await store.GetAsync("a", Ct));
    Assert.NotNull(await store.GetAsync("b", Ct));
    Assert.True(await store.DeleteAsync("b", Ct));
    Assert.False(await store.DeleteAsync("b", Ct));
    Assert.Null(await store.GetAsync(new string('x', 500), Ct));
    await Assert.ThrowsAsync<ArgumentException>(async () => await store.CreateAsync(Task(new string('x', 129)), Ct));
  }

  [Fact]
  public async Task Of_many_instances_updating_one_version_exactly_one_wins()
  {
    var instances = Enumerable.Range(0, 8).Select(_ => new EfTaskStore<AppDbContext>(_db.NewFactory())).ToArray();
    await instances[0].CreateAsync(Task("a"), Ct);
    var read = (await instances[1].GetAsync("a", Ct))!;

    var results = await Task_WhenAll(instances.Select((s, i) => System.Threading.Tasks.Task.Run(async () => await s.UpdateAsync(read with { StatusMessage = "by " + i }, Ct), Ct)));

    Assert.Equal(1, results.Count(r => r is not null));
    var stored = (await instances[2].GetAsync("a", Ct))!;
    Assert.Equal(1, stored.Version);
    Assert.Equal(results.Single(r => r is not null)!.StatusMessage, stored.StatusMessage);
  }

  [Fact]
  public async Task Of_many_instances_creating_one_id_exactly_one_wins()
  {
    var instances = Enumerable.Range(0, 8).Select(_ => new EfTaskStore<AppDbContext>(_db.NewFactory())).ToArray();
    var outcomes = await Task_WhenAll(instances.Select(s => System.Threading.Tasks.Task.Run(async () =>
    {
      try
      {
        await s.CreateAsync(Task("same"), Ct);
        return true;
      }
      catch (InvalidOperationException)
      {
        return false;
      }
    }, Ct)));
    Assert.Equal(1, outcomes.Count(o => o));
  }

  private static Task<T[]> Task_WhenAll<T>(IEnumerable<Task<T>> tasks) => System.Threading.Tasks.Task.WhenAll(tasks);
}

/// <summary>
/// Tasks across two server instances that share nothing but one database: a task is created through the first, polled, answered and
/// cancelled through the second, and the work on the first sees the answer and the cancellation.
/// </summary>
public sealed class TwoInstanceTasksTests : IAsyncDisposable
{
  private static readonly JsonElement ObjectSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
  private static readonly JsonElement NameForm = JsonDocument.Parse("""{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""").RootElement.Clone();
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  private readonly Database _database = new();
  private readonly List<HttpServer> _http = [];
  private readonly List<StreamableHttpEndpoint> _endpoints = [];

  public async ValueTask DisposeAsync()
  {
    foreach (var h in _http)
    {
      await h.DisposeAsync();
    }

    foreach (var e in _endpoints)
    {
      e.Dispose();
    }

    _database.Dispose();
  }

  private Uri StartInstance(bool withTools)
  {
    var server = new ContextServer("tasks-ef", "1");
    server.UseTasks(new TaskServerOptions
    {
      Store = new EfTaskStore<AppDbContext>(_database.NewFactory()),
      PollInterval = TimeSpan.FromMilliseconds(20),
      StorePollInterval = TimeSpan.FromMilliseconds(20),
      InputTimeout = TimeSpan.FromSeconds(20),
    });
    if (withTools)
    {
      server.AddTaskTool(new ToolDefinition("hello", "Greets.", ObjectSchema), async (_, task) =>
      {
        var answer = await task.ElicitAsync("name", "Name?", NameForm);
        return CallToolResult.FromText("Hello, " + answer.Content!["name"].GetString());
      });
      server.AddTaskTool(new ToolDefinition("slow", "Waits.", ObjectSchema), async (_, task) =>
      {
        await System.Threading.Tasks.Task.Delay(Timeout.Infinite, task.CancellationToken);
        return CallToolResult.FromText("unreachable");
      });
    }

    var endpoint = new StreamableHttpEndpoint(server, new StreamableHttpOptions());
    var http = new HttpServer(exchange => endpoint.HandleAsync(exchange));
    http.Start();
    _endpoints.Add(endpoint);
    _http.Add(http);
    return new Uri(http.BaseAddress!, "/");
  }

  private static async Task<ContextClient> ConnectAsync(Uri url) => await ContextClient.ConnectAsync(
    ContextTransport.Http(url, new HttpClient()),
    new ContextClientOptions
    {
      Era = EraPreference.Modern,
      SupportedVersions = [ProtocolVersions.V20260728],
      EraCache = null,
      Extensions = [TasksExtension.Client()],
      OnElicit = (_, _) => throw new InvalidOperationException("Answered by hand."),
    },
    Ct);

  [Fact]
  public async Task A_task_created_on_one_instance_is_polled_updated_and_cancelled_through_the_other()
  {
    var a = StartInstance(withTools: true);
    var b = StartInstance(withTools: false);
    await using var clientA = await ConnectAsync(a);
    await using var clientB = await ConnectAsync(b);

    var task = (await clientA.CallToolAsTaskAsync("hello", cancellationToken: Ct)).Task!;
    var onB = await clientB.GetTaskAsync(task.TaskId, Ct);
    TaskSnapshot snapshot;
    do
    {
      await System.Threading.Tasks.Task.Delay(10, Ct);
      snapshot = await onB.GetAsync(Ct);
    }
    while (snapshot.Status != TaskStates.InputRequired);

    await onB.UpdateAsync(new Dictionary<string, JsonElement> { ["name"] = JsonDocument.Parse("""{"action":"accept","content":{"name":"Ada"}}""").RootElement.Clone() }, Ct);
    Assert.Equal("Hello, Ada", (await onB.WaitAsync(Ct)).Result!.Text);

    var slow = (await clientA.CallToolAsTaskAsync("slow", cancellationToken: Ct)).Task!;
    await (await clientB.GetTaskAsync(slow.TaskId, Ct)).CancelAsync(Ct);
    Assert.Equal(TaskStates.Cancelled, (await slow.WaitAsync(Ct)).Status);
    Assert.Equal(TaskStates.Cancelled, (await (await clientB.GetTaskAsync(slow.TaskId, Ct)).GetAsync(Ct)).Status);
  }
}
