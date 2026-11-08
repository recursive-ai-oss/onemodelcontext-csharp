using Xunit;
using System.Reflection;
using System.Text.Json;
using RecursiveAI.OneModelContext.Cli;

namespace RecursiveAI.OneModelContext.CliTests;

public sealed class CliTests
{
  private static readonly System.Collections.Concurrent.ConcurrentQueue<string> NegotiationNotes = new();
  // Keep earlier connections too: a cached fallback can have originated in an earlier command.
  private static readonly IDisposable NegotiationSubscription = Diagnostics.ContextDiagnostics.Subscribe(e =>
  {
    if (e.Method == "negotiation")
    {
      NegotiationNotes.Enqueue(e.Message);
    }
  });

  private static readonly string Server = "dotnet \"" + typeof(CliTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "TestServerDll").Value + "\"";

  private static async Task<(int Code, string Out, string Err)> RunAsync(string stdin, params string[] args)
  {
    // Real CLI invocations have separate process-local caches; these in-process invocations must not inherit an earlier fallback.
    EraCache.Shared.Forget(ContextTransport.ProcessFromCommandLine(Server).ServerKey!);
    NegotiationNotes.Enqueue("Command: " + (args.Length == 0 ? "help" : args[0]));
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
    Assert.True(text.Contains($"protocol: {version} ({expected} era)", StringComparison.Ordinal), text + "\n" + string.Join("\n", NegotiationNotes));
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
    Assert.True(doc.RootElement.GetProperty("era").GetString() == "modern", text + "\n" + string.Join("\n", NegotiationNotes));
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
  [InlineData("login")]
  [InlineData("logout", "some-command --flag")]
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

  private static async Task<(CliApp App, Task<int> Run, StringWriter Out, StringWriter Err, CancellationTokenSource Stop)> StartProxyAsync(params string[] args)
  {
    // Real CLI invocations have separate process-local caches; these in-process invocations must not inherit an earlier fallback.
    EraCache.Shared.Forget(ContextTransport.ProcessFromCommandLine(Server).ServerKey!);
    NegotiationNotes.Enqueue("Command: " + (args.Length == 0 ? "help" : args[0]));
    var output = new StringWriter();
    var error = new StringWriter();
    var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    var app = new CliApp(output, error, new StringReader(""), "test", colour: false);
    var run = Task.Run(() => app.RunAsync(args, stop.Token));
    for (var i = 0; i < 300 && !output.ToString().Contains("listening on", StringComparison.Ordinal) && !run.IsCompleted; i++)
    {
      await Task.Delay(50, TestContext.Current.CancellationToken);
    }

    return (app, run, output, error, stop);
  }

  private static async Task<int> PostInitializeAsync(Uri url, string? authorization)
  {
    using var http = new HttpClient();
    using var request = new HttpRequestMessage(HttpMethod.Post, url)
    {
      Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}", System.Text.Encoding.UTF8, "application/json"),
    };
    request.Headers.Accept.ParseAdd("application/json");
    request.Headers.Accept.ParseAdd("text/event-stream");
    if (authorization is not null)
    {
      request.Headers.TryAddWithoutValidation("Authorization", authorization);
    }

    using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
    return (int)response.StatusCode;
  }

  [Fact]
  public async Task ProxyServesAStdioServerOverHttpAndChecksTheToken()
  {
    var (_, run, output, _, stop) = await StartProxyAsync("proxy", Server, "--listen", "0", "--token", "test-token-123");
    try
    {
      var url = new Uri(output.ToString().Split("listening on ")[1].Trim());
      Assert.Equal(200, await PostInitializeAsync(url, "Bearer test-token-123"));
      Assert.Equal(401, await PostInitializeAsync(url, "Bearer wrong"));
      Assert.Equal(401, await PostInitializeAsync(url, null));
      Assert.Equal(401, await PostInitializeAsync(url, "Basic test-token-123"));
    }
    finally
    {
      await stop.CancelAsync();
    }

    Assert.Equal(0, await run);
    Assert.DoesNotContain("test-token-123", output.ToString());
  }

  [Fact]
  public async Task ProxyChecksOriginAndPath()
  {
    var (_, run, output, _, stop) = await StartProxyAsync("proxy", Server, "--listen", "0");
    try
    {
      var url = new Uri(output.ToString().Split("listening on ")[1].Trim());
      using var http = new HttpClient();
      using var evil = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("{}") };
      evil.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
      Assert.Equal(403, (int)(await http.SendAsync(evil, TestContext.Current.CancellationToken)).StatusCode);
      var other = await http.PostAsync(new Uri(url, "/elsewhere"), new StringContent("{}"), TestContext.Current.CancellationToken);
      Assert.Equal(404, (int)other.StatusCode);
    }
    finally
    {
      await stop.CancelAsync();
    }

    Assert.Equal(0, await run);
  }

  [Theory]
  [InlineData("--listen", "0.0.0.0:0")]
  [InlineData("--listen", "0.0.0.0:0", "--allow-non-loopback")]
  [InlineData("--listen", "not-an-address:1")]
  [InlineData("--listen", "70000")]
  [InlineData("--token", "")]
  public async Task ProxyRefusesUnsafeOrBadListenSettings(params string[] extra)
  {
    var (code, _, err) = await RunAsync("", ["proxy", Server, .. extra]);
    Assert.Equal(2, code);
    Assert.StartsWith("onemodelcontext:", err);
  }

  [Fact]
  public async Task ProxyToStdioTakesNoListenOptions()
  {
    var (code, _, err) = await RunAsync("", "proxy", "http://127.0.0.1:1/mcp", "--token", "x");
    Assert.Equal(2, code);
    Assert.Contains("stdio", err);
  }

  [Theory]
  [InlineData("modern", "2026-07-28")]
  [InlineData("legacy", "2025-11-25")]
  [InlineData("auto", "2026-07-28")]
  public async Task UrlServersWorkOnBothEras(string era, string version)
  {
    var (_, run, output, _, stop) = await StartProxyAsync("proxy", Server, "--listen", "0", "--token", "url-token-456");
    try
    {
      var url = output.ToString().Split("listening on ")[1].Trim();
      var inspect = await RunAsync("", "inspect", url, "--bearer", "url-token-456", "--era", era);
      Assert.Equal(0, inspect.Code);
      Assert.Contains($"protocol: {version}", inspect.Out);
      Assert.Contains("echo", inspect.Out);
      var call = await RunAsync("", "call", url, "add", "--arg", "a=2", "--arg", "b=40", "--bearer", "url-token-456", "--era", era);
      Assert.Equal((0, "42"), (call.Code, call.Out.Trim()));
      var read = await RunAsync("", "read", url, "test://hello", "--bearer", "url-token-456", "--era", era);
      Assert.Equal((0, "hello"), (read.Code, read.Out.Trim()));
      var prompt = await RunAsync("", "prompt", url, "greet", "--bearer", "url-token-456", "--era", era);
      Assert.Equal((0, "user: Say hello."), (prompt.Code, prompt.Out.Trim()));

      // A refused credential fails the connection; the token never appears in what the tool prints.
      var refused = await RunAsync("", "inspect", url, "--bearer", "wrong", "--era", era);
      Assert.NotEqual(0, refused.Code);
      Assert.DoesNotContain("wrong", refused.Out + refused.Err, StringComparison.Ordinal);
    }
    finally
    {
      await stop.CancelAsync();
    }

    Assert.Equal(0, await run);
  }

  [Fact]
  public async Task AnUnreachableUrlExitsThree()
  {
    var (code, _, err) = await RunAsync("", "inspect", "http://127.0.0.1:1/mcp");
    Assert.Equal(3, code);
    Assert.StartsWith("onemodelcontext:", err);
  }

  [Fact]
  public async Task AUrlWithCredentialsIsAUsageError()
  {
    var (code, _, _) = await RunAsync("", "inspect", "http://user:pw@127.0.0.1:1/mcp");
    Assert.Equal(2, code);
  }
}
