using System;
using System.Text.Json;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.Protocol;

// A small stdio server for checking the library against other clients: run it as `dotnet RecursiveAI.OneModelContext.TestServer.dll`.
// It speaks both eras on the same stream, so a client's choice of era decides what it sees.
var server = new ContextServer("onemodelcontext-testserver", "0.1.0");
server.Instructions = "A test server: echo, add, confirm and count.";

var objectSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement;
server.Tools.Add(
  new ToolDefinition("echo", "Returns its text argument.", JsonDocument.Parse("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""").RootElement)
  {
    Annotations = new ToolAnnotations { ReadOnlyHint = true },
  },
  (args, _) => ValueTask.FromResult(CallToolResult.FromText(args.GetProperty("text").GetString() ?? string.Empty)));

server.Tools.Add(
  new ToolDefinition("add", "Adds two integers.", JsonDocument.Parse("""{"type":"object","properties":{"a":{"type":"integer"},"b":{"type":"integer"}},"required":["a","b"]}""").RootElement),
  (args, _) => ValueTask.FromResult(CallToolResult.FromText((args.GetProperty("a").GetInt64() + args.GetProperty("b").GetInt64()).ToString(System.Globalization.CultureInfo.InvariantCulture))));

server.Tools.Add(
  new ToolDefinition("confirm", "Asks the user to confirm, then answers.", objectSchema),
  async (_, ctx) => CallToolResult.FromText(await ctx.ConfirmAsync("Proceed?") ? "confirmed" : "declined"));

server.Tools.Add(
  new ToolDefinition("count", "Counts to three, reporting progress.", objectSchema),
  async (_, ctx) =>
  {
    for (var i = 1; i <= 3; i++)
    {
      await ctx.ReportProgressAsync(i, 3, "step " + i);
    }

    return CallToolResult.FromText("3");
  });

server.Resources.Add(
  new ResourceDefinition("test://hello", "hello") { MimeType = "text/plain" },
  (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, Text = "hello" }] }));

server.ResourceTemplates.Add(
  new ResourceTemplateDefinition("test://{name}", "named"),
  (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, Text = "hello" }] }));

server.Prompts.Add(
  new PromptDefinition("greet", "Greets."),
  (_, _) => ValueTask.FromResult(new GetPromptResult { Messages = [new PromptMessage { Role = "user", Content = TextContent.Of("Say hello.") }] }));

await server.RunAsync(ContextTransport.Stdio());
