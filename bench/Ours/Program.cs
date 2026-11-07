using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.AspNetCore;
using RecursiveAI.OneModelContext.Protocol;

namespace Bench;

internal static class Program
{
  private static readonly JsonDocument EchoSchema = JsonDocument.Parse("""{"type":"object","properties":{"message":{"type":"string","description":"The text to echo."}},"required":["message"]}""");
  private static readonly JsonDocument NoArgs = JsonDocument.Parse("""{"type":"object","properties":{}}""");
  private static readonly JsonDocument FillerSchema = JsonDocument.Parse("""{"type":"object","properties":{"a":{"type":"string","description":"A text."},"b":{"type":"integer","description":"A number."}},"required":["a"]}""");

  private static async Task<int> Main(string[] args)
  {
    if (args.Length > 0 && args[0] == "client")
    {
      return await ClientMain.RunAsync(args, async server => new OursClient(await ModelContext.ConnectAsync(server).ConfigureAwait(false))).ConfigureAwait(false);
    }

    var server = BuildServer(Payloads.Filler(args));
    switch (args.Length > 1 ? args[1] : "stdio")
    {
      case "stdio":
        await ModelContext.ServeAsync(server).ConfigureAwait(false);
        break;
      case "listener":
        await ModelContext.ServeHttpAsync(server, port: 0, onListening: url => Console.WriteLine("LISTENING " + url)).ConfigureAwait(false);
        break;
      case "kestrel":
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapModelContext("/mcp", server);
        await app.StartAsync().ConfigureAwait(false);
        Console.WriteLine("LISTENING " + app.Urls.First() + "/mcp");
        await app.WaitForShutdownAsync().ConfigureAwait(false);
        break;
      default:
        return 2;
    }

    return 0;
  }

  private static ContextServer BuildServer(int filler)
  {
    var server = new ContextServer("bench", "1.0.0");
    server.Tools.Add(new ToolDefinition("echo", "Echoes the message back.", EchoSchema), static (arguments, _) =>
      ValueTask.FromResult(CallToolResult.FromText("echo: " + arguments.GetProperty("message").GetString())));
    server.Tools.Add(new ToolDefinition("text1mb", "Returns 1 MiB of text.", NoArgs), static (_, _) =>
      ValueTask.FromResult(CallToolResult.FromText(Payloads.Text)));
    server.Tools.Add(new ToolDefinition("image1mb", "Returns a 1 MiB inline image.", NoArgs), static (_, _) =>
      ValueTask.FromResult(new CallToolResult { Content = [new ImageContent { Data = Convert.ToBase64String(Payloads.Image), MimeType = Payloads.ImageMimeType }] }));
    server.Tools.Add(new ToolDefinition("bench_stats", "Process allocation and peak memory.", NoArgs), static (_, _) =>
      ValueTask.FromResult(CallToolResult.FromText(Payloads.Stats())));
    for (var i = 0; i < filler; i++)
    {
      var n = i;
      server.Tools.Add(new ToolDefinition("tool_" + n.ToString(System.Globalization.CultureInfo.InvariantCulture), "Filler tool number " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".", FillerSchema), static (_, _) =>
        ValueTask.FromResult(CallToolResult.FromText("ok")));
    }

    return server;
  }

  private sealed class OursClient(ContextClient client) : IBenchClient
  {
    private static readonly JsonElement EchoArguments = JsonDocument.Parse("""{"message":"hello"}""").RootElement;

    public async Task CallAsync(string tool) => _ = await client.CallToolAsync(tool, tool == "echo" ? EchoArguments : default).ConfigureAwait(false);

    public async Task<string> TextAsync(string tool) => (await client.CallToolAsync(tool).ConfigureAwait(false)).Text;

    public async Task<int> ListAsync() => (await client.ListToolsAsync().ConfigureAwait(false)).Count;

    public ValueTask DisposeAsync() => client.DisposeAsync();
  }
}
