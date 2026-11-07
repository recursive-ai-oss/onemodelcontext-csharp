using System.IO.Pipelines;
using System.Text.Json;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;

namespace ToolBridge;

/// <summary>Answers the server's confirmation questions the way a host application's dialog would.</summary>
/// <param name="approve">Whether the user clicks yes.</param>
public sealed class ApprovalHandler(bool approve) : IClientRequestHandler
{
  /// <summary>The questions asked so far.</summary>
  public List<string> Questions { get; } = [];

  /// <inheritdoc />
  public ValueTask<Result> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
  {
    if (method != "elicitation/create")
    {
      throw new ProtocolException(-32601, "This client only answers elicitation.");
    }

    Questions.Add(parameters.GetProperty("message").GetString() ?? "");
    Result answer = approve
      ? new ElicitResult { Action = "accept", Content = new() { ["confirm"] = JsonDocument.Parse("true").RootElement } }
      : new ElicitResult { Action = "decline" };
    return ValueTask.FromResult(answer);
  }
}

/// <summary>Runs the bridge end to end in one process: a server on one end of a pair of pipes, a client on the other.</summary>
public static class Demo
{
  /// <summary>The tools a framework might already have.</summary>
  public static IEnumerable<BridgedTool> SampleTools(List<string> log) =>
  [
    new("word_count", "Counts the words in some text.", """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""", false,
      (args, _) => Task.FromResult(args.GetProperty("text").GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length.ToString(System.Globalization.CultureInfo.InvariantCulture))),
    new("delete_file", "Deletes a file.", """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""", true,
      (args, _) =>
      {
        log.Add("deleted " + args.GetProperty("path").GetString());
        return Task.FromResult("Deleted.");
      }),
  ];

  /// <summary>Calls <c>delete_file</c> through the bridge on one era and reports what happened.</summary>
  /// <param name="era">Which era the client may use.</param>
  /// <param name="approve">Whether the user approves.</param>
  /// <param name="output">Where to print progress.</param>
  public static async Task<(string Version, string Result, IReadOnlyList<string> Questions, IReadOnlyList<string> Deleted)> RunAsync(EraPreference era, bool approve, TextWriter output)
  {
    var deleted = new List<string>();
    var server = new ContextServer("bridge", "1.0.0");
    Bridge.Register(server, SampleTools(deleted));

    // Two pipes make a duplex connection with no sockets.
    var toServer = new Pipe();
    var toClient = new Pipe();
    using var cancel = new CancellationTokenSource();
    var serving = server.RunAsync(ContextTransport.Stream(toServer.Reader.AsStream(), toClient.Writer.AsStream()), cancel.Token);

    var host = new ApprovalHandler(approve);
    var options = new ContextClientOptions
    {
      Era = era,
      // The client says it can elicit; without this the server would not ask.
      Capabilities = new ClientCapabilities { Elicitation = new ElicitationCapability() },
      RequestHandler = host,
    };
    await using var client = await ContextClient.ConnectAsync(ContextTransport.Stream(toClient.Reader.AsStream(), toServer.Writer.AsStream()), options);
    output.WriteLine($"connected on {client.ProtocolVersion} ({client.Era})");

    var result = await client.CallToolAsync("delete_file", JsonDocument.Parse("""{"path":"/tmp/old.log"}""").RootElement);
    output.WriteLine($"delete_file -> {result.Text} (isError: {result.IsError == true}); asked: {string.Join(" | ", host.Questions)}");

    await cancel.CancelAsync();
    try
    {
      await serving;
    }
    catch (OperationCanceledException)
    {
      // Shutdown.
    }

    return (client.ProtocolVersion, result.Text, host.Questions, deleted);
  }
}
