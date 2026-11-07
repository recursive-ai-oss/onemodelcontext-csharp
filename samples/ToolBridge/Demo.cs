using System.IO.Pipelines;
using System.Text.Json;
using RecursiveAI.OneModelContext;

namespace ToolBridge;

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

    // The host application's dialog. Setting OnConfirm also tells the server this client can be asked, and the library answers the
    // protocol's form for a yes/no question, so the callback only deals with the question and the answer.
    var questions = new List<string>();
    var options = new ContextClientOptions
    {
      Era = era,
      OnConfirm = (question, _) =>
      {
        questions.Add(question);
        return ValueTask.FromResult(approve);
      },
    };
    await using var client = await ContextClient.ConnectAsync(ContextTransport.Stream(toClient.Reader.AsStream(), toServer.Writer.AsStream()), options);
    output.WriteLine($"connected on {client.ProtocolVersion} ({client.Era})");

    var result = await client.CallToolAsync("delete_file", JsonDocument.Parse("""{"path":"/tmp/old.log"}""").RootElement);
    output.WriteLine($"delete_file -> {result.Text} (isError: {result.IsError == true}); asked: {string.Join(" | ", questions)}");

    await cancel.CancelAsync();
    try
    {
      await serving;
    }
    catch (OperationCanceledException)
    {
      // Shutdown.
    }

    return (client.ProtocolVersion, result.Text, questions, deleted);
  }
}
