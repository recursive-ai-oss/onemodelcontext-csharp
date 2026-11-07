using System.ComponentModel;
using System.Diagnostics;
using Explorer;
using QuickPath;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.Protocol;
using Xunit;

namespace RecursiveAI.OneModelContext.SamplesTests;

public class ExplorerTests
{
  private static ContextClientOptions Options(EraPreference era, TextReader answers, TextWriter output) => new()
  {
    Era = era,
    Capabilities = ConsoleElicitation.Capabilities,
    RequestHandler = new ConsoleElicitation(answers, output),
  };

  private static ContextServer QuickServerWith(ContextServerOptions options)
  {
    var server = new ContextServer("quick", "1.0.0", options);
    server.Tool("add", (int a, int b) => a + b, "Adds.");
    server.Tool("confirm", async (RequestContext context) => await context.ConfirmAsync("Proceed?") ? "confirmed" : "declined", "Asks first.");
    server.Resource("mem://hello", "hello", () => "hello", "text/plain");
    server.Prompt("greet", (string name) => $"Say hello to {name}.");
    return server;
  }

  [Fact]
  public async Task ModernServerWithATtlIsListedFromTheCache()
  {
    var server = QuickServerWith(new ContextServerOptions { ListCacheTtl = TimeSpan.FromMinutes(5) });
    await using var pair = await Pair.ConnectAsync(server, Options(EraPreference.Auto, new StringReader("y\n"), TextWriter.Null));
    var output = new StringWriter();
    var report = await Tour.RunAsync(pair.Client, new TourRequest(Tool: "add", ToolArguments: """{"a":2,"b":3}""", ResourceUri: "mem://hello", Prompt: "greet", PromptArguments: new Dictionary<string, string> { ["name"] = "Ada" }), output, cancellationToken: TestContext.Current.CancellationToken);

    Assert.Equal(ProtocolEra.Modern, report.Era);
    Assert.True(report.SecondListWasCached);
    Assert.Equal("5", report.CallResult);
    Assert.Equal("hello", report.ResourceText);
    Assert.Equal("user: Say hello to Ada.", report.PromptText);
  }

  [Fact]
  public async Task LegacyOnlyServerIsNegotiatedAndNotCached()
  {
    var server = QuickServerWith(new ContextServerOptions { SupportedVersions = ProtocolVersions.SupportedLegacy });
    await using var pair = await Pair.ConnectAsync(server, Options(EraPreference.Auto, TextReader.Null, TextWriter.Null));
    var report = await Tour.RunAsync(pair.Client, new TourRequest(Tool: "add", ToolArguments: """{"a":1,"b":1}"""), TextWriter.Null, cancellationToken: TestContext.Current.CancellationToken);
    Assert.Equal(ProtocolEra.Legacy, report.Era);
    Assert.Equal("2025-11-25", report.Version);
    Assert.False(report.SecondListWasCached);
    Assert.Equal("2", report.CallResult);
  }

  [Theory]
  [InlineData(EraPreference.Modern)]
  [InlineData(EraPreference.Legacy)]
  public async Task ElicitationIsAnsweredByTheHostCallback(EraPreference era)
  {
    var server = QuickServerWith(new ContextServerOptions());
    var shown = new StringWriter();
    await using var pair = await Pair.ConnectAsync(server, Options(era, new StringReader("y\n"), shown));
    var report = await Tour.RunAsync(pair.Client, new TourRequest(Tool: "confirm"), TextWriter.Null, cancellationToken: TestContext.Current.CancellationToken);
    Assert.Equal("confirmed", report.CallResult);
    Assert.Contains("Proceed?", shown.ToString(), StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(EraPreference.Modern, "2026-07-28")]
  [InlineData(EraPreference.Legacy, "2025-11-25")]
  [InlineData(EraPreference.Auto, "2026-07-28")]
  public async Task ConnectsToAChildProcessOnEitherEra(EraPreference era, string version)
  {
    // The first sample, started the way a host would start any stdio server.
    var dll = Path.Combine(AppContext.BaseDirectory, "indexed-search.dll");
    await using var server = ServerProcess.Start("dotnet", [dll]);
    await using var client = await ContextClient.ConnectAsync(server.Transport, new ContextClientOptions { Era = era }, cancellationToken: TestContext.Current.CancellationToken);
    var report = await Tour.RunAsync(client, new TourRequest(Tool: "search", ToolArguments: """{"query":"leaves"}"""), TextWriter.Null, cancellationToken: TestContext.Current.CancellationToken);
    Assert.Equal(version, report.Version);
    Assert.Contains("Tea", report.CallResult, StringComparison.Ordinal);
  }
}
