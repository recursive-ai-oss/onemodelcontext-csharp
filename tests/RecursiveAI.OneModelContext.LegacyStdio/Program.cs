using System;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text.Json;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.Protocol;

// serve                      a stdio server with echo, add, confirm and a resource
// connect [--era legacy|auto] [--version v] [--call tool --args json] -- <command> [args...]
//                            spawns the command, connects with this library's client and prints what was negotiated
if (args.Length == 0)
{
  Console.Error.WriteLine("usage: serve | connect [--era legacy|auto] [--version v] [--call tool --args json] -- command [args...]");
  return 2;
}

if (args[0] == "serve")
{
  return await ServeAsync(args);
}

return await ConnectAsync(args);

static async Task<int> ServeAsync(string[] args)
{
  var versions = args.Length > 2 && args[1] == "--versions" ? args[2].Split(',') : ProtocolVersions.Supported.ToArray();
  var server = new ContextServer("legacy-stdio", "1.0.0", new ContextServerOptions { SupportedVersions = versions });
  var schema = JsonDocument.Parse("""{"type":"object","properties":{"message":{"type":"string"},"a":{"type":"number"},"b":{"type":"number"}}}""").RootElement;
  server.Tools.Add(new ToolDefinition("echo", "Echoes the message.", schema), (arguments, _) =>
    ValueTask.FromResult(CallToolResult.FromText(arguments.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "")));
  server.Tools.Add(new ToolDefinition("add", "Adds a and b.", schema), (arguments, _) =>
    ValueTask.FromResult(CallToolResult.FromText((arguments.GetProperty("a").GetDouble() + arguments.GetProperty("b").GetDouble()).ToString(System.Globalization.CultureInfo.InvariantCulture))));
  server.Tools.Add(new ToolDefinition("confirm", "Asks the user to confirm.", schema), async (_, ctx) =>
    CallToolResult.FromText(await ctx.ConfirmAsync("Proceed?") ? "confirmed" : "declined"));
  server.Resources.Add(new ResourceDefinition("mem://hello", "hello"), (uri, _) =>
    ValueTask.FromResult(new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, Text = "hello" }] }));
  await server.RunAsync(ContextTransport.Stdio());
  return 0;
}

static async Task<int> ConnectAsync(string[] args)
{
  var era = EraPreference.Legacy;
  string[] versions = [.. ProtocolVersions.Supported];
  string? call = null;
  var callArgs = "{}";
  var i = 1;
  for (; i < args.Length && args[i] != "--"; i++)
  {
    switch (args[i])
    {
      case "--era":
        era = args[++i] == "auto" ? EraPreference.Auto : EraPreference.Legacy;
        break;
      case "--version":
        versions = [args[++i]];
        break;
      case "--call":
        call = args[++i];
        break;
      case "--args":
        callArgs = args[++i];
        break;
    }
  }

  if (i >= args.Length - 1)
  {
    Console.Error.WriteLine("missing -- command");
    return 2;
  }

  var start = new ProcessStartInfo(args[i + 1]) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
  for (var j = i + 2; j < args.Length; j++)
  {
    start.ArgumentList.Add(args[j]);
  }

  using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start the server");
  await using var client = await ContextClient.ConnectAsync(
    ContextTransport.Stream(process.StandardOutput.BaseStream, process.StandardInput.BaseStream),
    new ContextClientOptions { Era = era, SupportedVersions = versions });
  Console.WriteLine($"negotiated {client.ProtocolVersion} ({client.Era}) with {client.ServerInfo.Name} {client.ServerInfo.Version}");
  await client.PingAsync();
  foreach (var tool in await client.ListToolsAsync())
  {
    Console.WriteLine("tool " + tool.Name);
  }

  if (call is not null)
  {
    var result = await client.CallToolAsync(call, JsonDocument.Parse(callArgs).RootElement);
    Console.WriteLine($"call {call} -> {result.Text} (isError={result.IsError == true})");
  }

  process.StandardInput.Close();
  if (!process.WaitForExit(5000))
  {
    process.Kill();
  }

  return 0;
}
