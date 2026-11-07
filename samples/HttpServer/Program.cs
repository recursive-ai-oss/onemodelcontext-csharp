using HttpServer;
using RecursiveAI.OneModelContext;

// A server on the built-in HTTP listener in a few lines. It listens on 127.0.0.1 only; the first argument is the port (default 3001,
// 0 picks a free one). Point any client at the address it prints, for example: curl -s http://127.0.0.1:3001/mcp ...
var port = args.Length > 0 ? int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 3001;
await ModelContext.ServeHttpAsync(new Greeter(), port: port, onListening: url => Console.WriteLine($"Serving {url}"));
