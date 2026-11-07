using System.Reflection;
using RecursiveAI.OneModelContext.Cli;
using Xunit;

namespace RecursiveAI.OneModelContext.CliTests;

/// <summary>The command line tool against a server that only speaks the older protocol revisions (it refuses the 2026-07-28 request shape).</summary>
public sealed class CliLegacyServerTests
{
  private static readonly string LegacyDll = typeof(CliLegacyServerTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "LegacyServerDll").Value!;

  private static string Server(string versions) => $"dotnet \"{LegacyDll}\" serve --versions {versions}";

  private static async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
  {
    var output = new StringWriter();
    var error = new StringWriter();
    var app = new CliApp(output, error, new StringReader(""), "test", colour: false);
    var code = await app.RunAsync(args, TestContext.Current.CancellationToken);
    return (code, output.ToString(), error.ToString());
  }

  [Theory]
  [InlineData("2025-03-26")]
  [InlineData("2025-06-18")]
  [InlineData("2025-11-25,2025-06-18,2025-03-26,2024-11-05")]
  public async Task Inspect_falls_back_to_the_legacy_handshake_under_auto(string versions)
  {
    var (code, text, err) = await RunAsync("inspect", Server(versions));
    Assert.True(code == 0, err);
    Assert.Contains("(legacy era)", text);
    Assert.Contains("echo", text);
    Assert.Contains("mem://hello", text);
  }

  [Fact]
  public async Task Call_works_against_a_legacy_only_server()
  {
    var (code, text, err) = await RunAsync("call", Server("2025-03-26"), "add", "--arg", "a=2", "--arg", "b=40");
    Assert.True(code == 0, err);
    Assert.Equal("42", text.Trim());
  }

  [Fact]
  public async Task Forcing_the_modern_era_on_a_legacy_only_server_fails_with_a_non_zero_exit()
  {
    var (code, _, err) = await RunAsync("call", Server("2025-03-26"), "add", "--arg", "a=1", "--arg", "b=1", "--era", "modern");
    Assert.NotEqual(0, code);
    Assert.NotEmpty(err);
  }
}
