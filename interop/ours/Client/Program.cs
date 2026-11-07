using System.Text.Json;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.Protocol;

// interop-client (--stdio "CMD ARGS" | --url URL) [--era auto|modern|legacy] [--profile fixture|generic]
// One JSON line per check on standard output: {"check":..., "ok":..., "detail":...}. Exit 1 when the connection fails.
string? Opt(string name)
{
  var i = Array.IndexOf(args, name);
  return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

void Emit(string check, bool ok, string detail = "") =>
  Console.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object> { ["check"] = check, ["ok"] = ok, ["detail"] = detail.Length > 300 ? detail[..300] : detail }));

async Task Step(string check, Func<Task<string>> body)
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

var era = Opt("--era") switch { "modern" => EraPreference.Modern, "legacy" => EraPreference.Legacy, _ => EraPreference.Auto };
var transport = Opt("--stdio") is { } command
  ? ContextTransport.ProcessFromCommandLine(command)
  : ContextTransport.Http(new Uri(Opt("--url") ?? throw new ArgumentException("--stdio or --url")));
var options = new ContextClientOptions
{
  Era = era,
  EraCache = null,
  OnConfirm = (_, _) => ValueTask.FromResult(true),
  Capabilities = new ClientCapabilities { Elicitation = new ElicitationCapability() },
  OnElicit = (_, _) => ValueTask.FromResult(new ElicitResult { Action = "accept", Content = new Dictionary<string, JsonElement> { ["confirm"] = JsonSerializer.SerializeToElement(true) } }),
};

ContextClient client;
try
{
  using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
  client = await ContextClient.ConnectAsync(transport, options, connectTimeout.Token);
}
catch (Exception ex)
{
  Emit("connect", false, ex.GetType().Name + ": " + ex.Message);
  return 1;
}

await using var closing = client;
Emit("connect", true, $"server {client.ServerInfo.Name} {client.ServerInfo.Version}; protocol {client.ProtocolVersion} ({client.Era})");
var none = JsonSerializer.SerializeToElement(new Dictionary<string, string>());

if (Opt("--profile") == "generic")
{
  // A server that does not declare a capability is not asked for its list (it would answer "method not found", which is correct).
  var capabilities = client.ServerCapabilities;
  await Step("tools/list", async () => capabilities.Tools is null ? "no tools capability" : string.Join(",", (await client.ListToolsAsync()).Select(t => t.Name)));
  await Step("resources/list", async () => capabilities.Resources is null ? "no resources capability" : (await client.ListResourcesAsync()).Count + " resources");
  await Step("prompts/list", async () => capabilities.Prompts is null ? "no prompts capability" : (await client.ListPromptsAsync()).Count + " prompts");
  return 0;
}

await Step("tools/list", async () =>
{
  var names = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
  return names.Contains("echo") ? string.Join(",", names) : throw new InvalidOperationException("no echo: " + string.Join(",", names));
});
await Step("tools/call text", async () =>
{
  var r = await client.CallToolAsync("echo", JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["message"] = "hi" }));
  return r.Text == "hi" ? r.Text : throw new InvalidOperationException(r.Text);
});
await Step("tools/call image", async () =>
{
  var r = await client.CallToolAsync("image", none);
  var image = r.Content.OfType<ImageContent>().FirstOrDefault();
  return image is { MimeType: "image/png", Data.Length: > 0 } ? $"{image.MimeType} {image.Data.Length} b64" : throw new InvalidOperationException("no png image");
});
await Step("resources/list", async () => string.Join(",", (await client.ListResourcesAsync()).Select(r => r.Uri)));
await Step("resources/read", async () => ((await client.ReadResourceAsync("mem://hello")).Contents[0] as TextResourceContents)?.Text ?? throw new InvalidOperationException("not text"));
await Step("prompts/list", async () => string.Join(",", (await client.ListPromptsAsync()).Select(p => p.Name)));
await Step("prompts/get", async () =>
{
  var r = await client.GetPromptAsync("greet", new Dictionary<string, string> { ["name"] = "Ann" });
  return (r.Messages[0].Content as TextContent)?.Text ?? throw new InvalidOperationException("not text");
});
await Step("error: unknown tool", async () =>
{
  try
  {
    var r = await client.CallToolAsync("nope", none);
    return r.IsError == true ? "isError result" : throw new InvalidOperationException("no error: " + r.Text);
  }
  catch (ProtocolException ex)
  {
    return "JSON-RPC error " + ex.Code;
  }
});
await Step("progress", async () =>
{
  var seen = 0;
  await client.CallToolAsync("slow", JsonSerializer.SerializeToElement(new Dictionary<string, int> { ["steps"] = 3 }), new Progress<ProgressNotificationParams>(_ => Interlocked.Increment(ref seen)));
  await Task.Delay(200);
  return seen > 0 ? seen + " notifications" : throw new InvalidOperationException("no progress notifications");
});
await Step("cancellation", async () =>
{
  using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
  try
  {
    await client.CallToolAsync("wait", none, cts.Token);
    throw new InvalidOperationException("call completed");
  }
  catch (OperationCanceledException)
  {
  }

  var r = await client.CallToolAsync("echo", JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["message"] = "after" }));
  return "cancelled; next call " + r.Text;
});
await Step("elicitation", async () =>
{
  var r = await client.CallToolAsync("confirm", none);
  return r.Text == "confirmed" ? r.Text : throw new InvalidOperationException(r.Text);
});
return 0;
