using System.Net.Sockets;
using System.Text.Json;
using LiveControl;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.Protocol;
using Xunit;

namespace RecursiveAI.OneModelContext.SamplesTests;

public class LiveControlTests
{
  private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

  private static LiveControlHost NewHost()
  {
    var host = new LiveControlHost("live", "1.0.0", "Poke the app.");
    host.Add(new Operation("snap", "Takes a screenshot.", """{"type":"object","properties":{}}""", false, _ => Task.FromResult(new OperationResult("{\"ok\":true}", Png))));
    host.Add(new Operation("fail", "Always fails.", """{"type":"object","properties":{}}""", true, _ => throw new OperationException("no_such_entity", "There is no entity 7.")));
    return host;
  }

  private static async Task<ContextClient> ConnectSocketAsync(string path, EraPreference era)
  {
    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken: TestContext.Current.CancellationToken);
    return await ModelContext.ConnectAsync(ContextTransport.Stream(new NetworkStream(socket, ownsSocket: true)), era, cancellationToken: TestContext.Current.CancellationToken);
  }

  private static string SocketPath() => Path.Combine(Path.GetTempPath(), $"lc-{Guid.NewGuid():N}.sock");

  [Theory]
  [MemberData(nameof(Eras.Both), MemberType = typeof(Eras))]
  public async Task OperationsAreToolsOverASocket(EraPreference era)
  {
    using var host = NewHost();
    var path = SocketPath();
    using var listener = LiveControlHost.Listen(path);
    using var stop = new CancellationTokenSource();
    var serving = host.ServeAsync(listener, stop.Token);

    await using (var client = await ConnectSocketAsync(path, era))
    {
      Assert.Equal("Poke the app.", client.Instructions);
      var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
      Assert.Equal(["fail", "snap"], tools.Select(t => t.Name));
      Assert.True(tools.Single(t => t.Name == "snap").Annotations?.ReadOnlyHint);

      var snap = await client.CallToolAsync("snap", cancellationToken: TestContext.Current.CancellationToken);
      Assert.Equal("{\"ok\":true}", snap.Text);
      var image = Assert.IsType<ImageContent>(snap.Content[1]);
      Assert.Equal("image/png", image.MimeType);
      Assert.Equal(Png, Convert.FromBase64String(image.Data));

      var fail = await client.CallToolAsync("fail", cancellationToken: TestContext.Current.CancellationToken);
      Assert.True(fail.IsError);
      Assert.Contains("no_such_entity", fail.Text, StringComparison.Ordinal);
    }

    stop.Cancel();
    await serving.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
    File.Delete(path);
  }

  [Fact]
  public async Task AddingAndRemovingNotifiesAConnectedClient()
  {
    using var host = NewHost();
    var path = SocketPath();
    using var listener = LiveControlHost.Listen(path);
    using var stop = new CancellationTokenSource();
    var serving = host.ServeAsync(listener, stop.Token);
    await using var client = await ConnectSocketAsync(path, EraPreference.Legacy);
    var changes = 0;
    var changed = new SemaphoreSlim(0);
    client.NotificationReceived += n =>
    {
      if (n.Method == "notifications/tools/list_changed")
      {
        Interlocked.Increment(ref changes);
        changed.Release();
      }
    };

    host.Add(new Operation("reset", "Resets.", """{"type":"object","properties":{}}""", true, _ => Task.FromResult(new OperationResult("{}"))));
    Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken));
    Assert.Contains("reset", (await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).Select(t => t.Name));

    Assert.True(host.Remove("reset"));
    Assert.False(host.Remove("reset"));
    Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken));
    Assert.DoesNotContain("reset", (await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).Select(t => t.Name));

    stop.Cancel();
    await serving.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
    File.Delete(path);
  }

  [Fact]
  public async Task SeveralAgentsCanBeConnectedAtOnce()
  {
    using var host = NewHost();
    var path = SocketPath();
    using var listener = LiveControlHost.Listen(path);
    using var stop = new CancellationTokenSource();
    var serving = host.ServeAsync(listener, stop.Token);
    await using var a = await ConnectSocketAsync(path, EraPreference.Modern);
    await using var b = await ConnectSocketAsync(path, EraPreference.Legacy);
    Assert.Equal("{\"ok\":true}", (await a.CallToolAsync("snap", cancellationToken: TestContext.Current.CancellationToken)).Text);
    Assert.Equal("{\"ok\":true}", (await b.CallToolAsync("snap", cancellationToken: TestContext.Current.CancellationToken)).Text);
    stop.Cancel();
    await serving.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
    File.Delete(path);
  }

  [Fact]
  public async Task AnInPageChannelHandlesWholeMessagesOnEitherEra()
  {
    using var host = NewHost();

    // Modern: no handshake, every request carries its own version and identity.
    var modern = host.OpenPage();
    const string meta = "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientInfo\":{\"name\":\"page\",\"version\":\"1\"},\"io.modelcontextprotocol/clientCapabilities\":{}}";
    var list = await modern.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{" + meta + "}}", cancellationToken: TestContext.Current.CancellationToken);
    Assert.Contains("\"snap\"", list, StringComparison.Ordinal);

    // Legacy: initialize first; the page remembers the negotiated revision.
    var legacy = host.OpenPage();
    var init = await legacy.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"page","version":"1"}}}""", cancellationToken: TestContext.Current.CancellationToken);
    Assert.Contains("\"protocolVersion\":\"2025-06-18\"", init, StringComparison.Ordinal);
    Assert.Null(await legacy.SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", cancellationToken: TestContext.Current.CancellationToken));
    var call = await legacy.SendAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"snap","arguments":{}}}""", cancellationToken: TestContext.Current.CancellationToken);
    Assert.Contains("image/png", call, StringComparison.Ordinal);

    // Bad input never throws; it is answered with a JSON-RPC error.
    var bad = await legacy.SendAsync("{ not json", cancellationToken: TestContext.Current.CancellationToken);
    using var error = JsonDocument.Parse(bad!);
    Assert.Equal(-32700, error.RootElement.GetProperty("error").GetProperty("code").GetInt32());
  }
}
