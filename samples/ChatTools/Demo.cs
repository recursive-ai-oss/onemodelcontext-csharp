using System.IO.Pipelines;
using Microsoft.Extensions.AI;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.Extensions.AI;

namespace ChatTools;

/// <summary>Connects to a server, hands its tools to a function-calling chat client and asks one question.</summary>
public static class Demo
{
  /// <summary>Runs the demo against an in-process server on the given era and returns the model's final answer.</summary>
  /// <param name="era">Which protocol era the client speaks.</param>
  public static async Task<string> RunAsync(EraPreference era)
  {
    // The server runs in this process over two in-memory pipes, so the sample needs nothing else; a real app would pass a command line or URL to ConnectAsync.
    var toServer = new Pipe();
    var toClient = new Pipe();

    var server = new ContextServer("forecast", "1.0.0");
    new Forecast().AddTo(server);
    using var stop = new CancellationTokenSource();
    var serving = server.RunAsync(ContextTransport.Stream(toServer.Reader.AsStream(), toClient.Writer.AsStream()), stop.Token);

    await using var client = await ModelContext.ConnectAsync(ContextTransport.Stream(toClient.Reader.AsStream(), toServer.Writer.AsStream()), era);

    // The server's tools become functions; the chat client's function-calling loop invokes them.
    IList<AIFunction> functions = await client.ToAIFunctionsAsync();
    using var chat = new FunctionInvokingChatClient(new ScriptedChatClient());
    var response = await chat.GetResponseAsync("What is the weather tomorrow?", new ChatOptions { Tools = [.. functions] });

    await stop.CancelAsync();
    try
    {
      await serving;
    }
    catch (OperationCanceledException)
    {
    }

    return response.Text;
  }
}
