using AppsServer;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.Extensions.Apps;
using Xunit;

namespace RecursiveAI.OneModelContext.SamplesTests;

public class AppsServerTests
{
  [Theory]
  [MemberData(nameof(Eras.Both), MemberType = typeof(Eras))]
  public async Task AHostThatRendersAppsGetsTheViewAndTheShortText(EraPreference era)
  {
    await using var pair = await Pair.ConnectAsync(WeatherApp.Create(), new ContextClientOptions { Era = era, Extensions = [AppsExtension.Client()] });

    var tool = Assert.Single(await pair.Client.ListToolsAsync(TestContext.Current.CancellationToken));
    var ui = tool.GetUi();
    var view = await pair.Client.ReadUiResourceAsync(ui!.ResourceUri!, TestContext.Current.CancellationToken);
    var result = await pair.Client.CallToolAsync("get_forecast", System.Text.Json.JsonDocument.Parse("""{"city":"Lisbon"}""").RootElement, TestContext.Current.CancellationToken);

    Assert.Equal(WeatherApp.ViewUri, ui.ResourceUri);
    Assert.Contains("ui/initialize", view.Html, StringComparison.Ordinal);
    Assert.True(view.Meta!.PrefersBorder);
    Assert.Equal("Forecast for Lisbon is shown.", result.Text);
    Assert.Equal("Lisbon", result.StructuredContent.GetProperty("city").GetString());
  }

  [Theory]
  [MemberData(nameof(Eras.Both), MemberType = typeof(Eras))]
  public async Task AnyOtherClientGetsAnOrdinaryToolWithTheFullText(EraPreference era)
  {
    await using var pair = await Pair.ConnectAsync(WeatherApp.Create(), new ContextClientOptions { Era = era });

    var tool = Assert.Single(await pair.Client.ListToolsAsync(TestContext.Current.CancellationToken));
    var result = await pair.Client.CallToolAsync("get_forecast", System.Text.Json.JsonDocument.Parse("""{"city":"Lisbon"}""").RootElement, TestContext.Current.CancellationToken);

    Assert.Null(tool.GetUi());
    Assert.Equal("Lisbon: 24 \u00b0C, partly cloudy.", result.Text);
  }

  [Theory]
  [MemberData(nameof(Eras.Both), MemberType = typeof(Eras))]
  public async Task TheExtensionWorksOverTheBuiltInListener(EraPreference era)
  {
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    var listening = new TaskCompletionSource<Uri>();
    var serving = ModelContext.ServeHttpAsync(WeatherApp.Create(), port: 0, onListening: url => listening.TrySetResult(url), cancellationToken: stop.Token);
    var url = await listening.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    await using (var client = await ContextClient.ConnectAsync(
      ContextTransport.Http(url),
      new ContextClientOptions { Era = era, EraCache = null, Extensions = [AppsExtension.Client()] },
      TestContext.Current.CancellationToken))
    {
      var tool = Assert.Single(await client.ListToolsAsync(TestContext.Current.CancellationToken));
      var view = await client.ReadUiResourceAsync(tool.GetUi()!.ResourceUri!, TestContext.Current.CancellationToken);

      Assert.Equal(WeatherApp.ViewUri, tool.GetUi()!.ResourceUri);
      Assert.Equal(AppsExtension.MimeType, view.MimeType);
      Assert.True(client.ServerSupportsExtension(AppsExtension.Id));
    }

    await stop.CancelAsync();
    await serving;
  }
}
