using System.Globalization;
using System.Text.Json;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.AspNetCore;
using RecursiveAI.OneModelContext.Hosting;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.Protocol;

// interop-server --stdio | --http [--port N] | --listener [--port N]
//   --http serves through MapModelContext on Kestrel, --listener on the library's built-in HTTP/1.1 listener; both on 127.0.0.1 and print
//   "listening URL". The tools, one resource and one prompt are the fixture every interop driver exercises.
const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

static JsonElement Schema(string json)
{
  using var document = JsonDocument.Parse(json);
  return document.RootElement.Clone();
}

var server = new ContextServer("interop-onemodelcontext-server", "0.1.0");
server.Tools.Add(
  new ToolDefinition("echo", "Echoes the message.", Schema("""{"type":"object","properties":{"message":{"type":"string"}},"required":["message"]}""")),
  (arguments, _) => ValueTask.FromResult(CallToolResult.FromText(arguments.GetProperty("message").GetString() ?? "")));
server.Tools.Add(
  new ToolDefinition("image", "A one-pixel PNG.", Schema("""{"type":"object","properties":{}}""")),
  (_, _) => ValueTask.FromResult(new CallToolResult { Content = [new ImageContent { Data = Png, MimeType = "image/png" }] }));
server.Tools.Add(
  new ToolDefinition("slow", "Reports progress.", Schema("""{"type":"object","properties":{"steps":{"type":"integer"}},"required":["steps"]}""")),
  async (arguments, ctx) =>
  {
    var steps = arguments.GetProperty("steps").GetInt32();
    for (var i = 1; i <= steps; i++)
    {
      await ctx.ReportProgressAsync(i, steps, "step " + i.ToString(CultureInfo.InvariantCulture));
      await Task.Delay(50, ctx.CancellationToken);
    }

    return CallToolResult.FromText("done");
  });
server.Tools.Add(
  new ToolDefinition("wait", "Waits until cancelled.", Schema("""{"type":"object","properties":{}}""")),
  async (_, ctx) =>
  {
    try
    {
      await Task.Delay(TimeSpan.FromSeconds(15), ctx.CancellationToken);
      return CallToolResult.FromText("timed out");
    }
    catch (OperationCanceledException)
    {
      Console.Error.WriteLine("wait: cancelled");
      throw;
    }
  });
server.Tools.Add(
  new ToolDefinition("confirm", "Asks the user to confirm.", Schema("""{"type":"object","properties":{}}""")),
  async (_, ctx) => CallToolResult.FromText(await ctx.ConfirmAsync("Proceed?") ? "confirmed" : "declined"));
server.Resources.Add(
  new ResourceDefinition("mem://hello", "hello") { MimeType = "text/plain", Description = "A greeting." },
  (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, MimeType = "text/plain", Text = "hello from interop" }] }));
server.Prompts.Add(
  new PromptDefinition("greet", "Greets someone.") { Arguments = [new PromptArgument { Name = "name", Required = true }] },
  (arguments, _) => ValueTask.FromResult(new GetPromptResult { Messages = [new PromptMessage { Role = "user", Content = TextContent.Of("Say hello to " + arguments["name"] + ".") }] }));

if (args.Contains("--stdio"))
{
  await server.RunAsync(ContextTransport.Stdio());
  return 0;
}

var port = 0;
var portIndex = Array.IndexOf(args, "--port");
if (portIndex >= 0 && portIndex + 1 < args.Length)
{
  port = int.Parse(args[portIndex + 1], CultureInfo.InvariantCulture);
}

if (args.Contains("--listener"))
{
  using var endpoint = new StreamableHttpEndpoint(server);
  await using var listener = new HttpServer(
    exchange => exchange.Request.Path == "/mcp" ? endpoint.HandleAsync(exchange) : ValueTask.CompletedTask,
    new HttpServerOptions { Port = port });
  listener.Start();
  Console.WriteLine("listening " + listener.BaseAddress + "mcp");
  await Task.Delay(Timeout.Infinite);
  return 0;
}

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
var app = builder.Build();
app.MapModelContext("/mcp", server);
await app.StartAsync();
Console.WriteLine("listening " + app.Urls.First() + "/mcp");
await app.WaitForShutdownAsync();
return 0;
