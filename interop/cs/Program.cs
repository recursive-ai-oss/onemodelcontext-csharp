using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// interop-cs-sdk server stdio | server http PORT                       the fixture server on the official C# SDK
// interop-cs-sdk client (--stdio "CMD ARGS" | --url URL) [--sse] [--profile fixture|generic]
//                                                                      the probe client; one JSON line per check on standard output
// Written against the SDK's published documentation.
if (args.Length > 0 && args[0] == "server")
{
  return await RunServer(args);
}

return await RunClient(args);

static async Task<int> RunServer(string[] args)
{
  if (args[1] == "stdio")
  {
    var host = Host.CreateApplicationBuilder();
    host.Logging.ClearProviders();
    host.Services.AddMcpServer().WithStdioServerTransport().WithTools<FixtureTools>().WithResources<FixtureResources>().WithPrompts<FixturePrompts>();
    await host.Build().RunAsync();
    return 0;
  }

  var builder = WebApplication.CreateSlimBuilder();
  builder.Logging.ClearProviders();
  builder.WebHost.UseUrls("http://127.0.0.1:" + args[2]);
  builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = false).WithTools<FixtureTools>().WithResources<FixtureResources>().WithPrompts<FixturePrompts>();
  var app = builder.Build();
  app.MapMcp("/mcp");
  await app.StartAsync();
  Console.WriteLine("listening " + app.Urls.First());
  await app.WaitForShutdownAsync();
  return 0;
}

static void Emit(string check, bool ok, string detail = "") =>
  Console.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object> { ["check"] = check, ["ok"] = ok, ["detail"] = detail.Length > 300 ? detail[..300] : detail }));

static async Task Step(string check, Func<Task<string>> body)
{
  try
  {
    Emit(check, true, await body());
  }
  catch (Exception ex)
  {
    Emit(check, false, ex.GetType().Name + ": " + ex.Message);
  }
}

static async Task<int> RunClient(string[] args)
{
  string? Opt(string name)
  {
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
  }

  IClientTransport transport;
  if (Opt("--stdio") is { } command)
  {
    var parts = command.Split(' ');
    transport = new StdioClientTransport(new StdioClientTransportOptions { Name = "target", Command = parts[0], Arguments = parts[1..] });
  }
  else
  {
    transport = new HttpClientTransport(new HttpClientTransportOptions
    {
      Endpoint = new Uri(Opt("--url")!),
      TransportMode = args.Contains("--sse") ? HttpTransportMode.Sse : HttpTransportMode.AutoDetect,
    });
  }

  var options = new McpClientOptions
  {
    ClientInfo = new Implementation { Name = "interop-cs", Version = "1.0.0" },
    Capabilities = new ClientCapabilities { Elicitation = new ElicitationCapability() },
    Handlers = new McpClientHandlers
    {
      ElicitationHandler = (request, ct) => ValueTask.FromResult(new ElicitResult
      {
        Action = "accept",
        Content = new Dictionary<string, JsonElement> { ["confirm"] = JsonSerializer.SerializeToElement(true) },
      }),
    },
  };

  McpClient client;
  try
  {
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    client = await McpClient.CreateAsync(transport, options, cancellationToken: timeout.Token);
  }
  catch (Exception ex)
  {
    Emit("connect", false, ex.GetType().Name + ": " + ex.Message);
    return 1;
  }

  await using var closing = client;
  Emit("connect", true, $"server {client.ServerInfo?.Name} {client.ServerInfo?.Version}; protocol {client.NegotiatedProtocolVersion}");
  var none = new Dictionary<string, object?>();
  if (Opt("--profile") == "generic")
  {
    await Step("tools/list", async () => string.Join(",", (await client.ListToolsAsync()).Select(t => t.Name)));
    await Step("resources/list", async () => (await client.ListResourcesAsync()).Count + " resources");
    await Step("prompts/list", async () => (await client.ListPromptsAsync()).Count + " prompts");
    return 0;
  }

  static string Text(CallToolResult r) => string.Join("|", r.Content.OfType<TextContentBlock>().Select(t => t.Text));
  await Step("tools/list", async () =>
  {
    var names = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
    return names.Contains("echo") ? string.Join(",", names) : throw new InvalidOperationException("no echo: " + string.Join(",", names));
  });
  await Step("tools/call text", async () =>
  {
    var r = await client.CallToolAsync("echo", new Dictionary<string, object?> { ["message"] = "hi" });
    return Text(r) == "hi" ? "hi" : throw new InvalidOperationException(Text(r));
  });
  await Step("tools/call image", async () =>
  {
    var r = await client.CallToolAsync("image", none);
    var image = r.Content.OfType<ImageContentBlock>().FirstOrDefault();
    return image is { MimeType: "image/png" } ? $"{image.MimeType} {image.Data.Length} b64" : throw new InvalidOperationException("no png image");
  });
  await Step("resources/list", async () => string.Join(",", (await client.ListResourcesAsync()).Select(r => r.Uri)));
  await Step("resources/read", async () => (await client.ReadResourceAsync("mem://hello")).Contents.OfType<TextResourceContents>().First().Text);
  await Step("prompts/list", async () => string.Join(",", (await client.ListPromptsAsync()).Select(p => p.Name)));
  await Step("prompts/get", async () =>
  {
    var r = await client.GetPromptAsync("greet", new Dictionary<string, object?> { ["name"] = "Ann" });
    return (r.Messages[0].Content as TextContentBlock)?.Text ?? throw new InvalidOperationException("not text");
  });
  await Step("error: unknown tool", async () =>
  {
    try
    {
      var r = await client.CallToolAsync("nope", none);
      return r.IsError == true ? "isError result" : throw new InvalidOperationException("no error: " + Text(r));
    }
    catch (McpProtocolException ex)
    {
      return "JSON-RPC error " + (int)ex.ErrorCode;
    }
  });
  await Step("progress", async () =>
  {
    var seen = 0;
    await client.CallToolAsync("slow", new Dictionary<string, object?> { ["steps"] = 3 }, new Progress<ProgressNotificationValue>(_ => Interlocked.Increment(ref seen)));
    await Task.Delay(200);
    return seen > 0 ? seen + " notifications" : throw new InvalidOperationException("no progress notifications");
  });
  await Step("cancellation", async () =>
  {
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
    try
    {
      await client.CallToolAsync("wait", none, cancellationToken: cts.Token);
      throw new InvalidOperationException("call completed");
    }
    catch (OperationCanceledException)
    {
    }

    var r = await client.CallToolAsync("echo", new Dictionary<string, object?> { ["message"] = "after" });
    return "cancelled; next call " + Text(r);
  });
  await Step("elicitation", async () =>
  {
    var r = await client.CallToolAsync("confirm", none);
    return Text(r) == "confirmed" ? "confirmed" : throw new InvalidOperationException(Text(r));
  });
  return 0;
}

[McpServerToolType]
internal sealed class FixtureTools
{
  private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

  [McpServerTool(Name = "echo"), Description("Echoes the message.")]
  public static string Echo(string message) => message;

  [McpServerTool(Name = "image"), Description("A one-pixel PNG.")]
  public static ImageContentBlock Image() => ImageContentBlock.FromBytes(Convert.FromBase64String(Png), "image/png");

  [McpServerTool(Name = "slow"), Description("Reports progress.")]
  public static async Task<string> Slow(int steps, IProgress<ProgressNotificationValue> progress, CancellationToken cancellationToken)
  {
    for (var i = 1; i <= steps; i++)
    {
      progress.Report(new ProgressNotificationValue { Progress = i, Total = steps });
      await Task.Delay(50, cancellationToken);
    }

    return "done";
  }

  [McpServerTool(Name = "wait"), Description("Waits until cancelled.")]
  public static async Task<string> Wait(CancellationToken cancellationToken)
  {
    await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
    return "timed out";
  }

  [McpServerTool(Name = "confirm"), Description("Asks the user to confirm.")]
  public static async Task<string> Confirm(McpServer server, CancellationToken cancellationToken)
  {
    var result = await server.ElicitAsync(
      new ElicitRequestParams
      {
        Message = "Proceed?",
        RequestedSchema = new ElicitRequestParams.RequestSchema
        {
          Properties = { ["confirm"] = new ElicitRequestParams.BooleanSchema() },
        },
      },
      cancellationToken);
    return result.Action == "accept" && result.Content is { } c && c.TryGetValue("confirm", out var v) && v.ValueKind == JsonValueKind.True ? "confirmed" : "declined";
  }
}

[McpServerResourceType]
internal sealed class FixtureResources
{
  [McpServerResource(UriTemplate = "mem://hello", Name = "hello", MimeType = "text/plain"), Description("A greeting.")]
  public static string Hello() => "hello from interop";
}

[McpServerPromptType]
internal sealed class FixturePrompts
{
  [McpServerPrompt(Name = "greet"), Description("Greets someone.")]
  public static string Greet(string name) => $"Say hello to {name}.";
}
