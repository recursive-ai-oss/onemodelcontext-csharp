using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.AspNetCore;
using RecursiveAI.OneModelContext.Hosting;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.ConformanceServer;

// conformance-server --http [--port N]   serve Streamable HTTP on 127.0.0.1 through MapModelContext (port 0 picks a free one and prints it)
// conformance-server --listener [--port N]   the same endpoint on the library's built-in HTTP/1.1 listener instead
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

if (args.Contains("--listener"))
{
  using var listenerEndpoint = new StreamableHttpEndpoint(server);
  await using var http = new HttpServer(
    exchange => exchange.Request.Path is "/mcp" or "/mcp/server-card" ? listenerEndpoint.HandleAsync(exchange) : ValueTask.CompletedTask,
    new HttpServerOptions { Port = port });
  http.Start();
  Console.WriteLine("listening " + http.BaseAddress + "mcp");
  await Task.Delay(Timeout.Infinite);
  return 0;
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
