using Xunit;
using System.Reflection;
using System.Text.Json;
using RecursiveAI.OneModelContext.Cli;

namespace RecursiveAI.OneModelContext.CliTests;

public sealed class CliTests
{
  private static readonly string Server = "dotnet \"" + typeof(CliTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "TestServerDll").Value + "\"";

  private static async Task<(int Code, string Out, string Err)> RunAsync(string stdin, params string[] args)
  {
    var output = new StringWriter();
    var error = new StringWriter();
    var app = new CliApp(output, error, new StringReader(stdin), "test", colour: false);
    var code = await app.RunAsync(args, TestContext.Current.CancellationToken);
    return (code, output.ToString(), error.ToString());
  }

  [Theory]
  [InlineData("modern", "2026-07-28", "modern")]
  [InlineData("legacy", "2025-11-25", "legacy")]
  [InlineData("auto", "2026-07-28", "modern")]
  public async Task InspectReportsTheEra(string era, string version, string expected)
  {
    var (code, text, _) = await RunAsync("", "inspect", Server, "--era", era);
    Assert.Equal(0, code);
    Assert.Contains($"protocol: {version} ({expected} era)", text);
    Assert.Contains("echo", text);
    Assert.Contains("test://hello", text);
    Assert.Contains("greet", text);
    Assert.Contains(era == "auto" ? "server/discover" : "pinned by --era " + era, text);
  }

  [Fact]
  public async Task InspectJsonIsOneDocument()
  {
    var (code, text, _) = await RunAsync("", "inspect", Server, "--json");
    Assert.Equal(0, code);
    using var doc = JsonDocument.Parse(text);
    Assert.Equal("modern", doc.RootElement.GetProperty("era").GetString());
    Assert.Equal("onemodelcontext-testserver", doc.RootElement.GetProperty("serverInfo").GetProperty("name").GetString());
    Assert.Equal(4, doc.RootElement.GetProperty("tools").GetArrayLength());
  }

  [Theory]
  [InlineData("modern")]
  [InlineData("legacy")]
  public async Task CallTypesArgumentsFromTheSchema(string era)
  {
    var (code, text, _) = await RunAsync("", "call", Server, "add", "--arg", "a=2", "--arg", "b=40", "--era", era);
    Assert.Equal(0, code);
    Assert.Equal("42", text.Trim());
  }

  [Fact]
  public async Task CallWithJsonArgumentsAndJsonOutput()
  {
    var (code, text, _) = await RunAsync("", "call", Server, "echo", "--json", "{\"text\":\"hi\"}", "--json");
    Assert.Equal(0, code);
    using var doc = JsonDocument.Parse(text);
    Assert.Equal("hi", doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
  }

  [Fact]
  public async Task CallWithArgumentsFromAFile()
  {
    var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
    await File.WriteAllTextAsync(file, "{\"text\":\"from file\"}", TestContext.Current.CancellationToken);
    try
    {
      var (code, text, _) = await RunAsync("", "call", Server, "echo", "--json", "@" + file);
      Assert.Equal(0, code);
      Assert.Equal("from file", text.Trim());
    }
    finally
    {
      File.Delete(file);
    }
  }

  [Fact]
  public async Task ToolErrorsExitOne()
  {
    // add with a missing argument: the server's tool throws, which a server reports as an isError result.
    var (code, _, _) = await RunAsync("", "call", Server, "add", "--arg", "a=1");
    Assert.Equal(1, code);
  }

  [Fact]
  public async Task UnknownToolExitsOne()
  {
    var (code, _, err) = await RunAsync("", "call", Server, "nope");
    Assert.Equal(1, code);
    Assert.StartsWith("onemodelcontext:", err);
  }

  [Fact]
  public async Task ConfirmationQuestionsAreAnsweredFromInputOrYes()
  {
    Assert.Equal("confirmed", (await RunAsync("y\n", "call", Server, "confirm")).Out.Trim());
    Assert.Equal("declined", (await RunAsync("", "call", Server, "confirm")).Out.Trim());
    Assert.Equal("confirmed", (await RunAsync("", "call", Server, "confirm", "--yes")).Out.Trim());
  }

  [Theory]
  [InlineData("modern")]
  [InlineData("legacy")]
  public async Task ReadAndPrompt(string era)
  {
    var read = await RunAsync("", "read", Server, "test://hello", "--era", era);
    Assert.Equal((0, "hello"), (read.Code, read.Out.Trim()));
    var prompt = await RunAsync("", "prompt", Server, "greet", "--era", era);
    Assert.Equal((0, "user: Say hello."), (prompt.Code, prompt.Out.Trim()));
    var json = await RunAsync("", "read", Server, "test://hello", "--json", "--era", era);
    using var doc = JsonDocument.Parse(json.Out);
    Assert.Equal("hello", doc.RootElement.GetProperty("contents")[0].GetProperty("text").GetString());
  }

  [Fact]
  public async Task AServerThatCannotStartExitsThree()
  {
    var (code, _, err) = await RunAsync("", "inspect", "/nonexistent/server-binary");
    Assert.Equal(3, code);
    Assert.Contains("could not be started", err);
  }

  [Theory]
  [InlineData("bogus")]
  [InlineData("call", "only-server")]
  [InlineData("inspect", "x", "--era", "ancient")]
  [InlineData("inspect", "x", "--nope")]
  [InlineData("call", "x", "t", "--arg", "novalue")]
  [InlineData("login", "http://x")]
  public async Task UsageErrorsExitTwo(params string[] args)
  {
    var (code, _, err) = await RunAsync("", args);
    Assert.Equal(2, code);
    Assert.StartsWith("onemodelcontext:", err);
  }

  [Fact]
  public async Task BothArgumentFormsAtOnceIsAUsageError()
  {
    var (code, _, _) = await RunAsync("", "call", Server, "echo", "--arg", "text=x", "--args", "{}");
    Assert.Equal(2, code);
  }

  [Fact]
  public async Task WrongArgumentTypeIsAUsageError()
  {
    var (code, _, err) = await RunAsync("", "call", Server, "add", "--arg", "a=two", "--arg", "b=1");
    Assert.Equal(2, code);
    Assert.Contains("should be integer", err);
  }

  [Fact]
  public async Task ServersCanBeNamedInAConfigFile()
  {
    var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
    var dll = Server.Split('"')[1];
    await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { servers = new { test = new { command = "dotnet", args = new[] { dll } } } }), TestContext.Current.CancellationToken);
    try
    {
      var (code, text, _) = await RunAsync("", "call", "test", "echo", "--arg", "text=named", "--config", file);
      Assert.Equal((0, "named"), (code, text.Trim()));
    }
    finally
    {
      File.Delete(file);
    }
  }

  [Fact]
  public async Task HelpAndVersion()
  {
    var help = await RunAsync("", "--help");
    Assert.Equal(0, help.Code);
    Assert.Contains("usage:", help.Out);
    Assert.Equal((0, "onemodelcontext test"), ((await RunAsync("", "--version")).Code, (await RunAsync("", "--version")).Out.Trim()));
    Assert.Equal(2, (await RunAsync("")).Code);
  }

  [Theory]
  [InlineData("3001", "127.0.0.1", 3001)]
  [InlineData("0.0.0.0:80", "0.0.0.0", 80)]
  [InlineData("[::1]:9", "::1", 9)]
  public void ListenParses(string text, string host, int port)
  {
    Assert.True(CliOptions.TryParseListen(text, out var h, out var p));
    Assert.Equal((host, port), (h, p));
  }

  [Theory]
  [InlineData("x:y")]
  [InlineData("70000")]
  public void BadListenIsRejected(string text) => Assert.False(CliOptions.TryParseListen(text, out _, out _));
}
