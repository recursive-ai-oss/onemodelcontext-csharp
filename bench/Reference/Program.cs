using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Bench;

// The speed reference, driven only through its published API (documentation at csharp.sdk.modelcontextprotocol.io).
internal static class Program
{
  private static async Task<int> Main(string[] args)
  {
    if (args.Length > 0 && args[0] == "client")
    {
      return await ClientMain.RunAsync(args, async server => new ReferenceClient(await Connect(server).ConfigureAwait(false))).ConfigureAwait(false);
    }

    var tools = BuildTools(Payloads.Filler(args));
    switch (args.Length > 1 ? args[1] : "stdio")
    {
      case "stdio":
        var host = Host.CreateApplicationBuilder();
        host.Logging.ClearProviders();
        host.Services.AddMcpServer().WithStdioServerTransport().WithTools(tools);
        await host.Build().RunAsync().ConfigureAwait(false);
        break;
      case "kestrel":
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools(tools);
        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync().ConfigureAwait(false);
        Console.WriteLine("LISTENING " + app.Urls.First() + "/mcp");
        await app.WaitForShutdownAsync().ConfigureAwait(false);
        break;
      default:
        return 2;
    }

    return 0;
  }

  private static List<McpServerTool> BuildTools(int filler)
  {
    List<McpServerTool> tools =
    [
      McpServerTool.Create((string message) => "echo: " + message, new McpServerToolCreateOptions { Name = "echo", Description = "Echoes the message back." }),
      McpServerTool.Create(() => Payloads.Text, new McpServerToolCreateOptions { Name = "text1mb", Description = "Returns 1 MiB of text." }),
      McpServerTool.Create(() => ImageContentBlock.FromBytes(Payloads.Image, Payloads.ImageMimeType), new McpServerToolCreateOptions { Name = "image1mb", Description = "Returns a 1 MiB inline image." }),
      McpServerTool.Create(() => Payloads.Stats(), new McpServerToolCreateOptions { Name = "bench_stats", Description = "Process allocation and peak memory." }),
    ];
    for (var i = 0; i < filler; i++)
    {
      var n = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
      tools.Add(McpServerTool.Create((string a, int b) => "ok", new McpServerToolCreateOptions { Name = "tool_" + n, Description = "Filler tool number " + n + "." }));
    }

    return tools;
  }

  private static Task<McpClient> Connect(string server)
  {
    IClientTransport transport;
    if (server.StartsWith("http://", StringComparison.Ordinal))
    {
      transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(server) });
    }
    else
    {
      var parts = server.Split(' ', StringSplitOptions.RemoveEmptyEntries);
      transport = new StdioClientTransport(new StdioClientTransportOptions { Name = "bench", Command = parts[0], Arguments = parts[1..] });
    }

    return McpClient.CreateAsync(transport);
  }

  private sealed class ReferenceClient(McpClient client) : IBenchClient
  {
    private static readonly Dictionary<string, object?> EchoArguments = new() { ["message"] = "hello" };

    public async Task CallAsync(string tool) => _ = await client.CallToolAsync(tool, tool == "echo" ? EchoArguments : null).ConfigureAwait(false);

    public async Task<string> TextAsync(string tool) => ((TextContentBlock)(await client.CallToolAsync(tool).ConfigureAwait(false)).Content[0]).Text;

    public async Task<int> ListAsync() => (await client.ListToolsAsync().ConfigureAwait(false)).Count;

    public ValueTask DisposeAsync() => client.DisposeAsync();
  }
}
