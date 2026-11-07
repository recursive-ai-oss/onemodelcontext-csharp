using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.Hosting;
using RecursiveAI.OneModelContext.Http;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>Regression tests for the limits that keep one peer from exhausting the built-in listener or the Streamable HTTP endpoint.</summary>
public class ListenerHardeningTests
{
  private static readonly JsonElement ObjectSchema = Json.Parse("""{"type":"object"}""");

  private static ValueTask Hello(IHttpExchange exchange) =>
    exchange.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("hello"), exchange.Aborted);

  private static HttpServer Start(HttpServerOptions? options = null, TimeProvider? time = null, Action<Socket>? afterAccept = null)
  {
    var server = new HttpServer(Hello, options, time) { AfterAccept = afterAccept };
    server.Start();
    return server;
  }

  private const string Get = "GET /x HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n";

  private static async Task WaitUntilAsync(Func<bool> condition)
  {
    for (var i = 0; i < 200 && !condition(); i++)
    {
      await Task.Delay(25, TestContext.Current.CancellationToken);
    }

    Assert.True(condition());
  }

  // ---- F1: body memory ---------------------------------------------------------------------------------------------------------

  /// <summary>Delivers a few bytes, then nothing: a client that declares a big body and sends it slowly.</summary>
  private sealed class StallAfterStream(int bytes) : Stream
  {
    private int _left = bytes;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
      get => throw new NotSupportedException();
      set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
      if (_left > 0)
      {
        var n = Math.Min(_left, buffer.Length);
        buffer.Span[..n].Fill((byte)' ');
        _left -= n;
        return n;
      }

      await Task.Delay(Timeout.Infinite, cancellationToken);
      return 0;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }

  [Fact]
  public async Task Bodies_in_flight_are_bounded_by_the_budget_and_the_budget_is_returned()
  {
    var clock = new ManualTimeProvider();
    var server = new ContextServer("budget", "1", new ContextServerOptions { TimeProvider = clock });
    using var endpoint = new StreamableHttpEndpoint(server, new StreamableHttpOptions { MaxRequestBodyBytes = 800, MaxInFlightBodyBytes = 1000, BodyReadTimeout = TimeSpan.FromSeconds(30) });
    var headers = new[] { ("Content-Type", "application/json"), ("Accept", "application/json") };

    var slow = new TestExchange("POST", new StallAfterStream(10), 600, headers).Run(endpoint);
    await Task.Delay(200, TestContext.Current.CancellationToken);
    var refused = new TestExchange("POST", new MemoryStream(new byte[600]), 600, headers).Run(endpoint);
    await refused.BodyAsync();

    Assert.Equal(503, refused.Status);
    Assert.Equal("1", refused.ResponseHeaders.GetFirst("Retry-After"));

    // The slow sender times out; its 600 bytes go back, so the same request now gets past the budget (and fails as invalid JSON, 400).
    clock.Advance(TimeSpan.FromSeconds(31));
    await slow.BodyAsync();
    Assert.Equal(408, slow.Status);
    var again = new TestExchange("POST", new MemoryStream(new byte[600]), 600, headers).Run(endpoint);
    await again.BodyAsync();

    Assert.Equal(400, again.Status);
  }

  [Fact]
  public async Task A_chunked_body_is_charged_as_it_grows()
  {
    var server = new ContextServer("budget", "1");
    using var endpoint = new StreamableHttpEndpoint(server, new StreamableHttpOptions { MaxRequestBodyBytes = 100_000, MaxInFlightBodyBytes = 10_000 });

    var exchange = new TestExchange("POST", new MemoryStream(Enumerable.Repeat((byte)' ', 50_000).ToArray()), null, ("Content-Type", "application/json"), ("Accept", "application/json")).Run(endpoint);
    await exchange.BodyAsync();

    Assert.Equal(503, exchange.Status);
  }

  [Fact]
  public async Task A_body_exactly_at_the_limit_is_accepted_and_one_byte_more_is_not()
  {
    var server = new ContextServer("limit", "1");
    using var endpoint = new StreamableHttpEndpoint(server, new StreamableHttpOptions { MaxRequestBodyBytes = 64 });
    var headers = new[] { ("Content-Type", "application/json"), ("Accept", "application/json") };

    var fits = new TestExchange("POST", new MemoryStream(new byte[64]), null, headers).Run(endpoint);
    await fits.BodyAsync();
    var over = new TestExchange("POST", new MemoryStream(new byte[65]), null, headers).Run(endpoint);
    await over.BodyAsync();

    Assert.Equal(400, fits.Status);
    Assert.Equal(413, over.Status);
  }

  [Fact]
  public void Serving_over_http_uses_a_one_megabyte_body_cap()
  {
    // ModelContext.ServeHttpAsync passes this limit to both the listener and the endpoint; the constant is the single source.
    Assert.Equal(1024 * 1024, ModelContext.LocalBodyLimit);
  }

  // ---- F2: connection exhaustion -----------------------------------------------------------------------------------------------

  [Fact]
  public async Task One_address_cannot_hold_more_than_its_share_of_connections()
  {
    await using var server = Start(new HttpServerOptions { MaxConnectionsPerAddress = 3 });
    var idle = new System.Collections.Generic.List<RawClient>();
    for (var i = 0; i < 3; i++)
    {
      idle.Add(await RawClient.ConnectAsync(server.LocalEndPoint!));
    }

    await WaitUntilAsync(() => server.ConnectionCount == 3);
    using var surplus = await RawClient.ConnectAsync(server.LocalEndPoint!);
    Assert.Equal(string.Empty, await surplus.ReadToEndAsync());

    idle[0].Dispose();
    await WaitUntilAsync(() => server.ConnectionCount == 2);
    using var next = await RawClient.ConnectAsync(server.LocalEndPoint!);
    await next.SendAsync(Get);
    Assert.Equal(200, (await next.ReadResponseAsync()).Status);
    foreach (var client in idle)
    {
      client.Dispose();
    }
  }

  [Fact]
  public async Task A_new_connection_that_sends_nothing_is_closed_after_the_first_byte_timeout()
  {
    var clock = new ManualTimeProvider();
    await using var server = Start(new HttpServerOptions { FirstByteTimeout = TimeSpan.FromSeconds(5), IdleTimeout = TimeSpan.FromSeconds(60) }, clock);
    using var silent = await RawClient.ConnectAsync(server.LocalEndPoint!);
    await WaitUntilAsync(() => server.ConnectionCount == 1);
    await Task.Delay(200, TestContext.Current.CancellationToken);

    clock.Advance(TimeSpan.FromSeconds(6));

    Assert.Equal(string.Empty, await silent.ReadToEndAsync());
  }

  [Fact]
  public async Task A_keep_alive_connection_keeps_the_longer_idle_timeout()
  {
    var clock = new ManualTimeProvider();
    await using var server = Start(new HttpServerOptions { FirstByteTimeout = TimeSpan.FromSeconds(5), IdleTimeout = TimeSpan.FromSeconds(60) }, clock);
    using var client = await RawClient.ConnectAsync(server.LocalEndPoint!);
    await client.SendAsync(Get);
    Assert.Equal(200, (await client.ReadResponseAsync()).Status);
    await Task.Delay(200, TestContext.Current.CancellationToken);

    clock.Advance(TimeSpan.FromSeconds(10));
    await client.SendAsync(Get);

    Assert.Equal(200, (await client.ReadResponseAsync()).Status);
  }

  [Fact]
  public async Task A_legacy_get_stream_is_ended_with_a_retry_after_its_lifetime()
  {
    var clock = new ManualTimeProvider();
    var server = new ContextServer("real", "1", new ContextServerOptions { TimeProvider = clock });
    using var endpoint = new StreamableHttpEndpoint(server, new StreamableHttpOptions { KeepAliveInterval = TimeSpan.FromSeconds(15), MaxStreamLifetime = TimeSpan.FromMinutes(1) });
    var init = TestExchange.Post("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""").Run(endpoint);
    await init.BodyAsync();
    var session = init.ResponseHeaders.GetFirst("Mcp-Session-Id")!;
    var get = new TestExchange("GET", headers: [("Accept", "text/event-stream"), ("Mcp-Session-Id", session)]).Run(endpoint);
    await get.HeadersSent;
    var events = get.Events();

    SseEvent? last = null;
    for (var i = 0; i < 3; i++)
    {
      await Task.Delay(200, TestContext.Current.CancellationToken);
      clock.Advance(TimeSpan.FromSeconds(16));
      last = await events.NextAsync();
      Assert.True(last!.IsComment);
    }

    await Task.Delay(200, TestContext.Current.CancellationToken);
    clock.Advance(TimeSpan.FromSeconds(16));
    last = await events.NextAsync();

    Assert.NotNull(last!.Retry);
    await get.Completed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
  }

  // ---- F3: the non-loopback warning --------------------------------------------------------------------------------------------

  [Fact]
  public void The_wide_bind_warning_goes_to_standard_error_when_nobody_listens()
  {
    var writer = new StringWriter();

    Assert.True(HttpServer.ShouldWarn(new HttpServerOptions { Address = IPAddress.Any }));
    Assert.False(HttpServer.ShouldWarn(new HttpServerOptions()));
    HttpServer.Warn("Anyone can reach it.", hasSubscribers: false, () => writer);

    Assert.Contains("warning: Anyone can reach it.", writer.ToString(), StringComparison.Ordinal);
  }

  [Fact]
  public void The_warning_goes_to_diagnostics_instead_when_someone_subscribed()
  {
    var writer = new StringWriter();
    var seen = new System.Collections.Concurrent.ConcurrentQueue<DiagnosticEvent>();
    using (ContextDiagnostics.Subscribe(seen.Enqueue))
    {
      HttpServer.Warn("Heard.", hasSubscribers: true, () => writer);
    }

    Assert.Equal(string.Empty, writer.ToString());
    Assert.Contains(seen, e => e.Kind == DiagnosticKind.Warning && e.Message == "Heard.");
  }

  [Fact]
  public void Trusted_proxies_on_a_wide_bind_deserve_a_warning_and_on_loopback_do_not()
  {
    var proxy = new[] { IPAddress.Parse("10.0.0.1") };

    Assert.True(HttpServer.ShouldWarnProxyOnWideBind(new HttpServerOptions { Address = IPAddress.Any, TrustedProxies = proxy }));
    Assert.True(HttpServer.ShouldWarnProxyOnWideBind(new HttpServerOptions { Address = IPAddress.IPv6Any, TrustedProxies = proxy }));
    Assert.False(HttpServer.ShouldWarnProxyOnWideBind(new HttpServerOptions { TrustedProxies = proxy }));
    Assert.False(HttpServer.ShouldWarnProxyOnWideBind(new HttpServerOptions { Address = IPAddress.Any }));
  }

  // ---- F4: content type --------------------------------------------------------------------------------------------------------

  [Fact]
  public async Task A_post_without_a_content_type_gets_415_and_a_json_charset_is_accepted()
  {
    var server = new ContextServer("ct", "1");
    using var endpoint = new StreamableHttpEndpoint(server);
    var body = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"ping"}""");

    var none = new TestExchange("POST", new MemoryStream(body), body.Length, ("Accept", "application/json")).Run(endpoint);
    await none.BodyAsync();
    var charset = new TestExchange("POST", new MemoryStream(body), body.Length, ("Content-Type", "Application/JSON; charset=utf-8"), ("Accept", "application/json")).Run(endpoint);
    await charset.BodyAsync();
    var lookalike = new TestExchange("POST", new MemoryStream(body), body.Length, ("Content-Type", "application/jsonp"), ("Accept", "application/json")).Run(endpoint);
    await lookalike.BodyAsync();

    Assert.Equal(415, none.Status);
    Assert.NotEqual(415, charset.Status);
    Assert.Equal(415, lookalike.Status);
  }

  // ---- F5, F6: the accept loop -------------------------------------------------------------------------------------------------

  [Fact]
  public async Task Many_connect_and_close_cycles_leave_no_connection_entries()
  {
    await using var server = Start();
    for (var i = 0; i < 150; i++)
    {
      using var client = await RawClient.ConnectAsync(server.LocalEndPoint!);
      if (i % 2 == 0)
      {
        await client.SendAsync(Get);
        await client.ReadResponseAsync();
      }
    }

    await WaitUntilAsync(() => server.ConnectionCount == 0);
  }

  [Fact]
  public async Task A_failure_while_setting_up_one_connection_does_not_end_the_accept_loop_or_leak_its_slot()
  {
    var first = true;
    await using var server = Start(
      new HttpServerOptions { MaxConnections = 1 },
      afterAccept: _ =>
      {
        if (first)
        {
          first = false;
          throw new InvalidOperationException("injected");
        }
      });
    using var doomed = await RawClient.ConnectAsync(server.LocalEndPoint!);
    Assert.Equal(string.Empty, await doomed.ReadToEndAsync());

    using var next = await RawClient.ConnectAsync(server.LocalEndPoint!);
    await next.SendAsync(Get);

    Assert.Equal(200, (await next.ReadResponseAsync()).Status);
  }

  // ---- F7: TLS shutdown --------------------------------------------------------------------------------------------------------

  [Fact]
  public async Task A_close_notify_that_never_completes_is_given_up_on()
  {
    var clock = new ManualTimeProvider();
    var never = new TaskCompletionSource().Task;
    var bounded = HttpConnection.BoundedAsync(never, TimeSpan.FromSeconds(2), clock, CancellationToken.None);
    await Task.Delay(100, TestContext.Current.CancellationToken);

    clock.Advance(TimeSpan.FromSeconds(3));

    await Assert.ThrowsAsync<OperationCanceledException>(() => bounded);
  }

  // ---- F9 ----------------------------------------------------------------------------------------------------------------------

  [Fact]
  public async Task Listener_error_responses_forbid_content_sniffing()
  {
    await using var server = Start();
    using var client = await RawClient.ConnectAsync(server.LocalEndPoint!);

    await client.SendAsync("GET /x HTTP/1.1\r\nHost: 127.0.0.1\r\nTransfer-Encoding: gzip\r\n\r\n");
    var response = await client.ReadResponseAsync();

    Assert.Equal(501, response.Status);
    Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
  }
}
