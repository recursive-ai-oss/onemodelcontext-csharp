using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecursiveAI.OneModelContext.Hosting;
using Xunit;

namespace RecursiveAI.OneModelContext.AdapterTests;

public sealed class Counter
{
  private int _n;

  public int Next() => Interlocked.Increment(ref _n);
}

public sealed class PerRequest : IAsyncDisposable
{
  private static int s_disposed;

  public static int Disposed => Volatile.Read(ref s_disposed);

  public Guid Id { get; } = Guid.NewGuid();

  public ValueTask DisposeAsync()
  {
    Interlocked.Increment(ref s_disposed);
    return ValueTask.CompletedTask;
  }
}

/// <summary>A tool class with a constructor dependency, as an app would write it.</summary>
public partial class DependentTools(Counter counter)
{
  /// <summary>Counts calls.</summary>
  [Tool]
  public string Count() => "n=" + counter.Next();

  /// <summary>Returns the id of the request-scoped service.</summary>
  [Tool]
  public string Scoped(RequestContext context) => context.GetRequiredService<PerRequest>().Id.ToString();

  /// <summary>Always fails, with a message that carries a credential.</summary>
  [Tool]
  public string Boom() => throw new InvalidOperationException("upstream said: Authorization: Bearer abc123SECRET456 and eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.c2lnbmF0dXJl");
}

public sealed class HostingTests
{
  private static HostApplicationBuilder NewBuilder(CapturingLoggerProvider? logs = null)
  {
    var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
    builder.Logging.ClearProviders();
    if (logs is not null)
    {
      builder.Logging.AddProvider(logs);
      builder.Logging.SetMinimumLevel(LogLevel.Trace);
    }

    builder.Services.AddSingleton<Counter>();
    builder.Services.AddScoped<PerRequest>();
    return builder;
  }

  [Theory]
  [MemberData(nameof(Eras.Both), MemberType = typeof(Eras))]
  public async Task A_stream_hosted_server_resolves_tool_classes_and_request_scopes_from_the_container(EraPreference era)
  {
    var wire = new Wire();
    var builder = NewBuilder();
    builder.Services.AddContextServer<DependentTools>().UseTransport(_ => wire.Server());
    using var host = builder.Build();
    await host.StartAsync(TestContext.Current.CancellationToken);

    await using var client = await ModelContext.ConnectAsync(wire.Client(), era, cancellationToken: TestContext.Current.CancellationToken);
    Assert.Equal("n=1", (await client.CallToolAsync("count", default, TestContext.Current.CancellationToken)).Text);
    Assert.Equal("n=2", (await client.CallToolAsync("count", default, TestContext.Current.CancellationToken)).Text);

    var disposedBefore = PerRequest.Disposed;
    var first = (await client.CallToolAsync("scoped", default, TestContext.Current.CancellationToken)).Text;
    var second = (await client.CallToolAsync("scoped", default, TestContext.Current.CancellationToken)).Text;
    Assert.NotEqual(first, second);
    Assert.True(Guid.TryParse(first, out _));
    Assert.Equal(disposedBefore + 2, PerRequest.Disposed);

    await host.StopAsync(TestContext.Current.CancellationToken);
  }

  [Fact]
  public async Task The_host_stops_when_the_client_closes_the_stream()
  {
    var wire = new Wire();
    var builder = NewBuilder();
    builder.Services.AddContextServer<DependentTools>().UseTransport(_ => wire.Server());
    using var host = builder.Build();
    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
    await host.StartAsync(TestContext.Current.CancellationToken);

    var client = await ModelContext.ConnectAsync(wire.Client(), cancellationToken: TestContext.Current.CancellationToken);
    await client.DisposeAsync();

    await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), Task.Run(() => lifetime.ApplicationStopping.WaitHandle.WaitOne()));
    Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
    await host.StopAsync(TestContext.Current.CancellationToken);
  }

  [Theory]
  [MemberData(nameof(Eras.Both), MemberType = typeof(Eras))]
  public async Task The_listener_binds_loopback_serves_a_call_and_stops_with_the_host(EraPreference era)
  {
    var builder = NewBuilder();
    builder.Services.AddContextServer<DependentTools>().UseListener(port: 0);
    using var host = builder.Build();
    await host.StartAsync(TestContext.Current.CancellationToken);

    var address = await host.Services.GetRequiredService<ContextListener>().Started.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    Assert.True(System.Net.IPAddress.IsLoopback(System.Net.IPAddress.Parse(address.Host.Trim('[', ']'))));
    Assert.Equal("/mcp", address.AbsolutePath);

    await using (var client = await ModelContext.ConnectAsync(ContextTransport.Http(address), era, cancellationToken: TestContext.Current.CancellationToken))
    {
      Assert.Equal("n=1", (await client.CallToolAsync("count", default, TestContext.Current.CancellationToken)).Text);
    }

    await host.StopAsync(TestContext.Current.CancellationToken);

    using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
    await Assert.ThrowsAsync<System.Net.Sockets.SocketException>(async () =>
      await socket.ConnectAsync(System.Net.IPAddress.Parse(address.Host.Trim('[', ']')), address.Port, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task The_logger_bridge_reports_faults_and_never_a_credential()
  {
    var logs = new CapturingLoggerProvider();
    var wire = new Wire();
    var builder = NewBuilder(logs);
    builder.Services.AddContextServer<DependentTools>().UseTransport(_ => wire.Server());
    using var host = builder.Build();
    await host.StartAsync(TestContext.Current.CancellationToken);

    await using (var client = await ModelContext.ConnectAsync(wire.Client(), cancellationToken: TestContext.Current.CancellationToken))
    {
      var result = await client.CallToolAsync("boom", default, TestContext.Current.CancellationToken);
      Assert.True(result.IsError);
    }

    await host.StopAsync(TestContext.Current.CancellationToken);

    string[] lines;
    lock (logs.Lines)
    {
      lines = [.. logs.Lines];
    }

    Assert.Contains(lines, l => l.Contains("RecursiveAI.OneModelContext Error") && l.Contains("boom", StringComparison.Ordinal));
    foreach (var line in lines)
    {
      Assert.DoesNotContain("abc123SECRET456", line);
      Assert.DoesNotContain("eyJhbGci", line);
    }
  }

  [Theory]
  [InlineData("Authorization: Bearer abc.def-123_x==", "abc.def")]
  [InlineData("GET /cb?code=SECRETCODE&state=s", "SECRETCODE")]
  [InlineData("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.c2ln here", "eyJhbGci")]
  [InlineData("refresh_token=r-1234567890 x", "r-1234567890")]
  public void Scrub_removes_credentials_from_text(string text, string secret)
  {
    var scrubbed = ContextDiagnosticsLogger.Scrub(text);
    Assert.DoesNotContain(secret, scrubbed);
    Assert.Contains("[redacted]", scrubbed);
  }

  [Theory]
  [MemberData(nameof(Eras.Both), MemberType = typeof(Eras))]
  public async Task A_client_provider_connects_once_and_serves_calls(EraPreference era)
  {
    var wire = new Wire();
    var serverBuilder = NewBuilder();
    serverBuilder.Services.AddContextServer<DependentTools>().UseTransport(_ => wire.Server());
    using var serverHost = serverBuilder.Build();
    await serverHost.StartAsync(TestContext.Current.CancellationToken);

    var appBuilder = NewBuilder();
    appBuilder.Services.AddContextClient(_ => wire.Client(), _ => new ContextClientOptions { Era = era });
    using var app = appBuilder.Build();
    var provider = app.Services.GetRequiredService<ContextClientProvider>();

    var a = await provider.GetClientAsync(TestContext.Current.CancellationToken);
    var b = await provider.GetClientAsync(TestContext.Current.CancellationToken);
    Assert.Same(a, b);
    Assert.Equal("n=1", (await a.CallToolAsync("count", default, TestContext.Current.CancellationToken)).Text);

    await provider.DisposeAsync();
    await Assert.ThrowsAsync<ObjectDisposedException>(async () => await provider.GetClientAsync(TestContext.Current.CancellationToken));
    await serverHost.StopAsync(TestContext.Current.CancellationToken);
  }

  [Fact]
  public void AddContextServer_twice_shares_one_server()
  {
    var services = new ServiceCollection();
    services.AddSingleton<Counter>();
    services.AddContextServer("one", "1").AddTools<DependentTools>();
    services.AddContextServer().Configure((server, _) => server.Instructions = "shared");
    using var provider = services.BuildServiceProvider();
    var server = provider.GetRequiredService<ContextServer>();
    Assert.Equal("one", server.Info.Name);
    Assert.Equal("shared", server.Instructions);
    Assert.Equal(3, server.Tools.Count);
  }
}
