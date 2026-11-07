using Explorer;
using RecursiveAI.OneModelContext;

// explorer [--era auto|modern|legacy] [--call TOOL [--args JSON]] [--read URI] [--prompt NAME [--arg key=value]...] -- COMMAND [ARGS...]
//
// Connects to any stdio server, prints what it is and offers, and makes the calls asked for. Questions from the server
// (elicitation) are put to you on the terminal.
var era = EraPreference.Auto;
var request = new TourRequest();
var promptArguments = new Dictionary<string, string>();
var i = 0;
for (; i < args.Length && args[i] != "--"; i++)
{
  switch (args[i])
  {
    case "--era":
      era = args[++i] switch { "modern" => EraPreference.Modern, "legacy" => EraPreference.Legacy, _ => EraPreference.Auto };
      break;
    case "--call":
      request = request with { Tool = args[++i] };
      break;
    case "--args":
      request = request with { ToolArguments = args[++i] };
      break;
    case "--read":
      request = request with { ResourceUri = args[++i] };
      break;
    case "--prompt":
      request = request with { Prompt = args[++i] };
      break;
    case "--arg":
      var pair = args[++i].Split('=', 2);
      promptArguments[pair[0]] = pair.Length > 1 ? pair[1] : "";
      break;
    default:
      Console.Error.WriteLine($"unknown option {args[i]}");
      return 2;
  }
}

if (i + 1 >= args.Length)
{
  Console.Error.WriteLine("usage: explorer [--era auto|modern|legacy] [--call TOOL [--args JSON]] [--read URI] [--prompt NAME [--arg k=v]...] -- COMMAND [ARGS...]");
  return 2;
}

await using var server = ServerProcess.Start(args[i + 1], args[(i + 2)..]);
try
{
  await using var client = await ContextClient.ConnectAsync(server.Transport, new ContextClientOptions
  {
    Era = era,
    Capabilities = ConsoleElicitation.Capabilities,
    RequestHandler = new ConsoleElicitation(Console.In, Console.Out),
  });
  await Tour.RunAsync(client, request with { PromptArguments = promptArguments }, Console.Out);
  return 0;
}
catch (ProtocolVersionException ex)
{
  // The message names the eras on both sides, for example when --era modern meets a server that only speaks older revisions.
  Console.Error.WriteLine(ex.Message);
  return 1;
}
