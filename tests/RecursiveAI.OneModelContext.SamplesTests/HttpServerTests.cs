using RecursiveAI.OneModelContext;
using Xunit;

namespace RecursiveAI.OneModelContext.SamplesTests;

/// <summary>The HTTP sample: the same few lines as its Program.cs, on a free loopback port, called on each era.</summary>
public class HttpServerTests
{
  [Theory]
  [MemberData(nameof(Eras.Both), MemberType = typeof(Eras))]
  public async Task Tools_are_served_over_the_built_in_listener(EraPreference era)
  {
    var ct = TestContext.Current.CancellationToken;
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
    var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
    var serving = ModelContext.ServeHttpAsync(new HttpServer.Greeter(), port: 0, onListening: listening.SetResult, cancellationToken: stop.Token);
    var url = await listening.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

    await using (var client = await ContextClient.ConnectAsync(ContextTransport.Http(url), new ContextClientOptions { Era = era }, ct))
    {
      var tools = await client.ListToolsAsync(cancellationToken: ct);
      Assert.Equal(["add", "greet"], tools.Select(t => t.Name).Order());
      var sum = await client.CallToolAsync("add", System.Text.Json.JsonDocument.Parse("""{"a":1.5,"b":2}""").RootElement, cancellationToken: ct);
      Assert.Equal("3.5", sum.Text);
    }

    await stop.CancelAsync();
    try
    {
      await serving;
    }
    catch (OperationCanceledException)
    {
      // Shutdown.
    }
  }
}
