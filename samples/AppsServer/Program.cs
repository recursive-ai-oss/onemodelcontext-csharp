using System.IO.Pipelines;
using AppsServer;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.Extensions.Apps;

// A server with one MCP Apps tool.
//   apps-server --stdio        serve on stdin and stdout
//   apps-server --http [port]  serve on the built-in HTTP listener (127.0.0.1 only; default port 3001)
//   apps-server                connect to itself twice, once as a host that renders Apps and once as a plain client, and print both views
if (args.Contains("--stdio"))
{
  await ModelContext.ServeAsync(WeatherApp.Create());
  return;
}

if (args.Contains("--http"))
{
  var index = Array.IndexOf(args, "--http");
  var port = index + 1 < args.Length ? int.Parse(args[index + 1], System.Globalization.CultureInfo.InvariantCulture) : 3001;
  await ModelContext.ServeHttpAsync(WeatherApp.Create(), port: port, onListening: url => Console.WriteLine($"Serving {url}"));
  return;
}

var server = WeatherApp.Create();
await ShowAsync("a host that renders Apps", [AppsExtension.Client()]);
await ShowAsync("a plain client", []);

async Task ShowAsync(string who, IReadOnlyList<RecursiveAI.OneModelContext.Extensions.ClientExtension> extensions)
{
  var toServer = new Pipe();
  var toClient = new Pipe();
  var serving = server.RunAsync(ContextTransport.Stream(toServer.Reader.AsStream(), toClient.Writer.AsStream()));
  await using (var client = await ContextClient.ConnectAsync(
    ContextTransport.Stream(toClient.Reader.AsStream(), toServer.Writer.AsStream()),
    new ContextClientOptions { Extensions = extensions }))
  {
    Console.WriteLine($"-- {who} ({client.ProtocolVersion})");
    var tool = (await client.ListToolsAsync()).Single();
    var ui = tool.GetUi();
    Console.WriteLine("tool UI: " + (ui?.ResourceUri ?? "none"));
    if (ui?.ResourceUri is { } uri)
    {
      var view = await client.ReadUiResourceAsync(uri);
      Console.WriteLine($"view: {view.Html.Length} chars of {view.MimeType}, csp: {UiContentSecurityPolicy.Build(view.Meta)}");
    }

    var result = await client.CallToolAsync(tool.Name, System.Text.Json.JsonDocument.Parse("""{"city":"Lisbon"}""").RootElement);
    Console.WriteLine("text: " + result.Text);
  }

  await serving;
}
