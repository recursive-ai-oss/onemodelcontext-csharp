using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using QuickPath;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.Protocol;
using Xunit;

namespace RecursiveAI.OneModelContext.SamplesTests;

public class QuickPathTests
{
  [Theory]
  [MemberData(nameof(Eras.Both), MemberType = typeof(Eras))]
  [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The test runs the reflection path on purpose.")]
  [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The test runs the reflection path on purpose.")]
  public async Task LambdasBecomeTools(EraPreference era)
  {
    await using var pair = await Pair.ConnectAsync(QuickServer.Create(), new ContextClientOptions { Era = era });
    var client = pair.Client;

    var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
    Assert.Equal(["add", "countdown", "shout"], tools.Select(t => t.Name));
    var shout = tools.Single(t => t.Name == "shout");
    Assert.Equal(["text"], shout.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    Assert.Equal("What to shout.", shout.InputSchema.GetProperty("properties").GetProperty("text").GetProperty("description").GetString());

    Assert.Equal("7", (await client.CallToolAsync("add", Args("""{"a":3,"b":4}"""), cancellationToken: TestContext.Current.CancellationToken)).Text);
    Assert.Equal("HEY! HEY!", (await client.CallToolAsync("shout", Args("""{"text":"hey","times":2}"""), cancellationToken: TestContext.Current.CancellationToken)).Text);
    Assert.Equal("Liftoff.", (await client.CallToolAsync("countdown", Args("""{"from":3}"""), cancellationToken: TestContext.Current.CancellationToken)).Text);

    var about = await client.ReadResourceAsync("quick://about", cancellationToken: TestContext.Current.CancellationToken);
    Assert.Equal("A server made of lambdas.", Assert.IsType<TextResourceContents>(about.Contents[0]).Text);
    var prompt = await client.GetPromptAsync("explain", new Dictionary<string, string> { ["topic"] = "tides" }, cancellationToken: TestContext.Current.CancellationToken);
    Assert.Equal("Explain tides in one paragraph.", Assert.IsType<TextContent>(prompt.Messages[0].Content).Text);
  }

  private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;
}
