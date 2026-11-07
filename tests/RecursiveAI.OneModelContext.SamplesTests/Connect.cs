using System.IO.Pipelines;
using RecursiveAI.OneModelContext;

namespace RecursiveAI.OneModelContext.SamplesTests;

/// <summary>A client and a server joined by two in-memory pipes.</summary>
internal sealed class Pair : IAsyncDisposable
{
  private readonly CancellationTokenSource _stop = new();
  private readonly Task _serving;

  private Pair(ContextClient client, Task serving)
  {
    Client = client;
    _serving = serving;
  }

  public ContextClient Client { get; }

  public static async Task<Pair> ConnectAsync(ContextServer server, ContextClientOptions? options = null)
  {
    var toServer = new Pipe();
    var toClient = new Pipe();
    var pair = new CancellationTokenSource();
    var serving = server.RunAsync(ContextTransport.Stream(toServer.Reader.AsStream(), toClient.Writer.AsStream()), pair.Token);
    var client = await ContextClient.ConnectAsync(ContextTransport.Stream(toClient.Reader.AsStream(), toServer.Writer.AsStream()), options);
    var result = new Pair(client, serving);
    result._stop.Token.Register(pair.Cancel);
    return result;
  }

  public async ValueTask DisposeAsync()
  {
    await Client.DisposeAsync();
    await _stop.CancelAsync();
    try
    {
      await _serving;
    }
    catch (OperationCanceledException)
    {
      // Shutdown.
    }
  }
}

internal static class Eras
{
  public static IEnumerable<object[]> Both() => [[EraPreference.Modern], [EraPreference.Legacy]];
}
