using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Protocol;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>The happy-path verbs against a real child process: the stdio sample, on each protocol era.</summary>
public class ConnectAsyncTests
{
  private static string SampleCommand()
  {
    var dll = typeof(ConnectAsyncTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "SampleServerDll").Value!;
    Assert.True(File.Exists(dll), "The sample server was not built: " + dll);
    return "dotnet " + dll;
  }

  // Set to the path of the NativeAOT build of the sample to run the same checks against the native binary.
  private static string? NativeCommand() => Environment.GetEnvironmentVariable("ONEMODELCONTEXT_SAMPLE_AOT");

  private static async Task ListAndCallAsync(string command, EraPreference era, ProtocolEra expected)
  {
    var token = TestContext.Current.CancellationToken;
    await using var client = await ModelContext.ConnectAsync(command, era, cancellationToken: token);
    Assert.Equal(expected, client.Era);

    var tools = await client.ListToolsAsync(token);
    Assert.Equal(["get_note", "save_note", "search"], tools.Select(t => t.Name).Order(StringComparer.Ordinal));

    var saved = await client.CallToolAsync("save_note", Json.Parse("""{"title":"Plan","body":"Ship it"}"""), token);
    Assert.NotEqual(true, saved.IsError);
    Assert.Equal("Saved 'Plan'.", saved.Text);

    var note = await client.CallToolAsync("get_note", Json.Parse("""{"title":"Plan"}"""), token);
    Assert.Equal("Ship it", note.Structured.GetProperty("body").GetString());

    var missing = await client.CallToolAsync("get_note", Json.Parse("""{"title":"Nope"}"""), token);
    Assert.Equal("null", missing.Text);
    Assert.Equal(JsonValueKind.Undefined, missing.Structured.ValueKind);

    var found = await client.CallToolAsync("search", Json.Parse("""{"text":"plan"}"""), token);
    Assert.Equal("""["Plan"]""", found.Text);
  }

  [Fact]
  public Task The_sample_server_works_on_the_modern_era() => ListAndCallAsync(SampleCommand(), EraPreference.Modern, ProtocolEra.Modern);

  [Fact]
  public Task The_sample_server_works_on_the_legacy_era() => ListAndCallAsync(SampleCommand(), EraPreference.Legacy, ProtocolEra.Legacy);

  [Fact]
  public Task Negotiation_picks_the_modern_era_for_the_sample_server() => ListAndCallAsync(SampleCommand(), EraPreference.Auto, ProtocolEra.Modern);

  [Theory]
  [InlineData(EraPreference.Modern, ProtocolEra.Modern)]
  [InlineData(EraPreference.Legacy, ProtocolEra.Legacy)]
  public async Task The_native_sample_works_when_it_is_published(EraPreference era, ProtocolEra expected)
  {
    if (NativeCommand() is not { Length: > 0 } native)
    {
      return;
    }

    await ListAndCallAsync(native, era, expected);
  }

  [Fact]
  public async Task A_URL_says_HTTP_arrives_with_the_HTTP_client()
  {
    var error = await Assert.ThrowsAsync<NotSupportedException>(() => ModelContext.ConnectAsync("https://example.test/mcp", cancellationToken: TestContext.Current.CancellationToken));
    Assert.Contains("HTTP client", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task An_empty_command_is_refused()
  {
    await Assert.ThrowsAnyAsync<ArgumentException>(() => ModelContext.ConnectAsync("  ", cancellationToken: TestContext.Current.CancellationToken));
  }
}
