using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.AspNetCore;
using RecursiveAI.OneModelContext.ConformanceServer;

// conformance-server --http [--port N]   serve Streamable HTTP on 127.0.0.1 (port 0 picks a free one and prints it)
// conformance-server --stdio             serve newline-delimited JSON-RPC on standard input and output
var stdio = args.Contains("--stdio");
var server = ConformanceContent.Create();
if (stdio)
{
  await server.RunAsync(ContextTransport.Stdio());
  return 0;
}

var port = 0;
var portIndex = Array.IndexOf(args, "--port");
if (portIndex >= 0 && portIndex + 1 < args.Length)
{
  port = int.Parse(args[portIndex + 1], System.Globalization.CultureInfo.InvariantCulture);
}

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
// Loopback only: the suite and the interop drivers run on the same machine.
builder.WebHost.UseUrls("http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture));
var app = builder.Build();
app.MapModelContext("/mcp", server);
await app.StartAsync();
var address = app.Urls.First();
Console.WriteLine("listening " + address + "/mcp");
await app.WaitForShutdownAsync();
return 0;
