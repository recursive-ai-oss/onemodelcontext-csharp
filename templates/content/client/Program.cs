using System.Text.Json;
using RecursiveAI.OneModelContext;

// Usage: dotnet run -- "<server command line or URL>" [tool-name [json-arguments]]
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: <server command line or URL> [tool-name [json-arguments]]");
    return 2;
}

// The client negotiates the protocol era itself: the newest revision first, an older one when the server needs it.
await using var client = await ModelContext.ConnectAsync(args[0]);
Console.WriteLine($"{client.ServerInfo.Name} {client.ServerInfo.Version}, protocol {client.ProtocolVersion}");

foreach (var tool in await client.ListToolsAsync())
{
    Console.WriteLine($"tool {tool.Name}: {tool.Description}");
}

if (args.Length > 1)
{
    using var arguments = JsonDocument.Parse(args.Length > 2 ? args[2] : "{}");
    var result = await client.CallToolAsync(args[1], arguments.RootElement);
    Console.WriteLine(result.Text);
    return result.IsError == true ? 1 : 0;
}

return 0;
