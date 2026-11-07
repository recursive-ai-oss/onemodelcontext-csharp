using HostedServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecursiveAI.OneModelContext.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Standard output carries the protocol, so the console logger writes to standard error.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

// The tool class takes its dependency from the container; the host stops when the client closes the stream.
builder.Services.AddSingleton<GreetingBook>();
builder.Services.AddContextServer<Greeter>().UseStdio();

await builder.Build().RunAsync();
