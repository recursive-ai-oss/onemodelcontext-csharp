using System;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.Extensions.ServerCards;
using RecursiveAI.OneModelContext.Extensions.Skills;
using RecursiveAI.OneModelContext.Extensions.Tasks;
using RecursiveAI.OneModelContext.Hosting;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.Protocol;

/// <summary>
/// The experimental extensions under NativeAOT: a task from creation to its result on a file task store, a skill listed, loaded and read
/// with verification, and a server card served by the built-in listener and fetched through the guarded fetcher.
/// </summary>
internal static class ExtensionsSmoke
{
  internal static async Task<string?> RunAsync()
  {
    var directory = Path.Combine(Path.GetTempPath(), "omc-aot-ext-" + Guid.NewGuid().ToString("N"));
    try
    {
      var server = new ContextServer("ext", "1.0");
      server.UseTasks(new TaskServerOptions { Store = new FileTaskStore(Path.Combine(directory, "tasks")), PollInterval = TimeSpan.FromMilliseconds(20), StorePollInterval = TimeSpan.FromMilliseconds(20) });
      server.AddTaskTool(
        new ToolDefinition("work", "Works.", JsonDocument.Parse("""{"type":"object"}""").RootElement),
        async (_, task) =>
        {
          await Task.Delay(50, task.CancellationToken);
          return CallToolResult.FromText("done");
        });
      server.AddSkill(SkillDefinition.FromFiles(
        "demo",
        [
          new("SKILL.md", Encoding.UTF8.GetBytes("---\nname: demo\ndescription: A demo skill\nmetadata:\n  tags: [a, b]\n---\nBody\n")),
          new("notes/one.md", Encoding.UTF8.GetBytes("one\n")),
        ]));
      server.UseServerCard(server.CreateServerCard("com.example/demo", "A demo.", "https://example.com/mcp"));

      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
      var toClient = new Pipe();
      var toServer = new Pipe();
      var serving = server.RunAsync(ContextTransport.Stream(new Duplex(toServer.Reader.AsStream(), toClient.Writer.AsStream())), cts.Token);
      await using (var client = await ContextClient.ConnectAsync(
        ContextTransport.Stream(new Duplex(toClient.Reader.AsStream(), toServer.Writer.AsStream())),
        new ContextClientOptions { Extensions = [TasksExtension.Client(), SkillsExtension.Client()] },
        cts.Token))
      {
        var outcome = await client.CallToolAsTaskAsync("work", cancellationToken: cts.Token);
        var snapshot = outcome.Task is { } task ? await task.WaitAsync(cts.Token) : null;
        if (snapshot?.Status != TaskStates.Completed || snapshot.Result?.Text != "done")
        {
          return "task not completed: " + snapshot?.Status;
        }

        var skill = (await client.ListSkillsAsync(cts.Token)).SingleOrDefault();
        if (skill is null || skill.Name != "demo" || skill.Resources?.Count != 2)
        {
          return "skill not listed";
        }

        var loaded = await client.LoadSkillAsync(skill, cts.Token);
        var note = await client.ReadSkillFileAsync(skill, "notes/one.md", cts.Token);
        var children = await client.ReadSkillDirectoryAsync(skill.RootUri, cts.Token);
        if (!loaded.Text.Contains("Body", StringComparison.Ordinal) || Encoding.UTF8.GetString(note) != "one\n" || children.Count != 2)
        {
          return "skill not read";
        }
      }

      await serving.WaitAsync(TimeSpan.FromSeconds(10));

      using var endpoint = new StreamableHttpEndpoint(server);
      await using var http = new HttpServer(e => e.Request.Path == "/mcp/server-card" ? endpoint.HandleAsync(e) : ValueTask.CompletedTask);
      http.Start();
      using var fetcher = new GuardedHttpFetcher(new GuardedFetchOptions { Addresses = new AddressPolicy().Allow("127.0.0.1"), AllowHttpToAllowListedAddresses = true, MaxBytes = 64 * 1024 });
      using var cards = new ServerCardClient(fetcher);
      var fetched = await cards.FetchAsync(new Uri(http.BaseAddress!, "mcp/server-card"), null, cts.Token);
      if (fetched.Card?.Name != "com.example/demo" || fetched.ETag is null)
      {
        return "server card not fetched";
      }

      return null;
    }
    finally
    {
      if (Directory.Exists(directory))
      {
        Directory.Delete(directory, recursive: true);
      }
    }
  }
}
