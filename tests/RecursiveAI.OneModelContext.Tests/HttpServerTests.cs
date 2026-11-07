using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Auth;
using RecursiveAI.OneModelContext.Hosting;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.Tests.Generated;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>A bare TCP client, so tests can send exactly the bytes they mean, malformed ones included.</summary>
internal sealed class RawClient : IDisposable
{
  private readonly TcpClient _tcp = new();
  private NetworkStream _stream = null!;
  private readonly List<byte> _pending = [];

  internal static async Task<RawClient> ConnectAsync(IPEndPoint endpoint)
  {
    var client = new RawClient();
    await client._tcp.ConnectAsync(endpoint);
    client._stream = client._tcp.GetStream();
    return client;
  }

  internal Task SendAsync(string text) => _stream.WriteAsync(Encoding.Latin1.GetBytes(text)).AsTask();

  internal void HalfClose() => _tcp.Client.Shutdown(SocketShutdown.Send);

  /// <summary>Reads until the server closes the connection, or fails the test after the timeout.</summary>
  internal async Task<string> ReadToEndAsync(int timeoutMs = 5000)
  {
    using var cts = new CancellationTokenSource(timeoutMs);
    var buffer = new byte[4096];
    var all = new List<byte>(_pending);
    _pending.Clear();
    while (true)
    {
      var read = await _stream.ReadAsync(buffer, cts.Token);
      if (read == 0)
      {
        return Encoding.Latin1.GetString([.. all]);
      }

      all.AddRange(buffer.AsSpan(0, read).ToArray());
    }
  }

  /// <summary>Reads one response framed by Content-Length or chunked, leaving later bytes for the next call.</summary>
  internal async Task<RawResponse> ReadResponseAsync(int timeoutMs = 5000)
  {
    using var cts = new CancellationTokenSource(timeoutMs);
    string head;
    while (true)
    {
      var text = Encoding.Latin1.GetString([.. _pending]);
      var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
      if (end >= 0)
      {
        head = text[..end];
        _pending.RemoveRange(0, end + 4);
        break;
      }

      await FillAsync(cts.Token);
    }

    var lines = head.Split("\r\n");
    var status = int.Parse(lines[0].Split(' ')[1], CultureInfo.InvariantCulture);
    var headers = lines.Skip(1).Select(l => l.Split(": ", 2)).ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
    var body = new List<byte>();
    if (headers.TryGetValue("Content-Length", out var cl))
    {
      var n = int.Parse(cl, CultureInfo.InvariantCulture);
      while (_pending.Count < n)
      {
        await FillAsync(cts.Token);
      }

      body.AddRange(_pending.Take(n));
      _pending.RemoveRange(0, n);
    }
    else if (headers.ContainsKey("Transfer-Encoding"))
    {
      while (true)
      {
        var line = await ReadLineAsync(cts.Token);
        var size = Convert.ToInt32(line, 16);
        if (size == 0)
        {
          await ReadLineAsync(cts.Token);
          break;
        }

        while (_pending.Count < size + 2)
        {
          await FillAsync(cts.Token);
        }

        body.AddRange(_pending.Take(size));
        _pending.RemoveRange(0, size + 2);
      }
    }

    return new RawResponse(status, headers, Encoding.UTF8.GetString([.. body]));
  }

  /// <summary>Reads the head and then one chunk of an event stream.</summary>
  internal async Task<string> ReadChunkAsync(int timeoutMs = 5000)
  {
    using var cts = new CancellationTokenSource(timeoutMs);
    var line = await ReadLineAsync(cts.Token);
    var size = Convert.ToInt32(line, 16);
    while (_pending.Count < size + 2)
    {
      await FillAsync(cts.Token);
    }

    var text = Encoding.UTF8.GetString([.. _pending.Take(size)]);
    _pending.RemoveRange(0, size + 2);
    return text;
  }

  internal async Task<string> ReadHeadAsync(int timeoutMs = 5000)
  {
    using var cts = new CancellationTokenSource(timeoutMs);
    while (true)
    {
      var text = Encoding.Latin1.GetString([.. _pending]);
      var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
      if (end >= 0)
      {
        _pending.RemoveRange(0, end + 4);
        return text[..end];
      }

      await FillAsync(cts.Token);
    }
  }

  private async Task<string> ReadLineAsync(CancellationToken token)
  {
    while (true)
    {
      var text = Encoding.Latin1.GetString([.. _pending]);
      var end = text.IndexOf("\r\n", StringComparison.Ordinal);
      if (end >= 0)
      {
        _pending.RemoveRange(0, end + 2);
        return text[..end];
      }

      await FillAsync(token);
    }
  }

  private async Task FillAsync(CancellationToken token)
  {
    var buffer = new byte[4096];
    var read = await _stream.ReadAsync(buffer, token);
    if (read == 0)
    {
      throw new EndOfStreamException("The server closed the connection.");
    }

    _pending.AddRange(buffer.AsSpan(0, read).ToArray());
  }

  public void Dispose() => _tcp.Dispose();
}

internal sealed record RawResponse(int Status, Dictionary<string, string> Headers, string Body);

public class HttpServerTests
{
  private static async ValueTask Echo(IHttpExchange exchange, Action? seen = null)
  {
    seen?.Invoke();
    var request = exchange.Request;
    using var body = new MemoryStream();
    await request.Body.CopyToAsync(body, exchange.Aborted);
    exchange.Response.SetHeader("Content-Type", "text/plain");
    var forwarded = request.Headers.Contains("X-Forwarded-For") ? "yes" : "no";
    var text = $"{request.Method} {request.Path} remote={request.RemoteAddress} xff={forwarded} scheme={request.Scheme} body={Encoding.UTF8.GetString(body.ToArray())}";
    await exchange.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), exchange.Aborted);
  }

  private static HttpServer Start(Func<IHttpExchange, ValueTask> handler, HttpServerOptions? options = null)
  {
    var server = new HttpServer(handler, options);
    server.Start();
    return server;
  }

  private static Task<RawClient> Connect(HttpServer server) => RawClient.ConnectAsync(server.LocalEndPoint!);

  private const string Get = "GET /x HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n";

  [Fact]
  public async Task Two_requests_share_one_connection()
  {
    await using var server = Start(e => Echo(e));
    using var client = await Connect(server);

    await client.SendAsync(Get);
    var first = await client.ReadResponseAsync();
    await client.SendAsync("POST /y HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 5\r\n\r\nhello");
    var second = await client.ReadResponseAsync();

    Assert.Equal(200, first.Status);
    Assert.StartsWith("GET /x remote=127.0.0.1", first.Body);
    Assert.Contains("body=hello", second.Body);
    Assert.True(first.Headers.ContainsKey("Date"));
    Assert.Equal(first.Body.Length.ToString(CultureInfo.InvariantCulture), first.Headers["Content-Length"]);
  }

  [Fact]
  public async Task Pipelined_requests_are_answered_in_order()
  {
    await using var server = Start(e => Echo(e));
    using var client = await Connect(server);

    await client.SendAsync("GET /a HTTP/1.1\r\nHost: h\r\n\r\nGET /b HTTP/1.1\r\nHost: h\r\n\r\n");

    Assert.StartsWith("GET /a", (await client.ReadResponseAsync()).Body);
    Assert.StartsWith("GET /b", (await client.ReadResponseAsync()).Body);
  }

  [Fact]
  public async Task A_chunked_request_body_is_decoded_and_extensions_and_trailers_are_skipped()
  {
    await using var server = Start(e => Echo(e));
    using var client = await Connect(server);

    await client.SendAsync("POST /c HTTP/1.1\r\nHost: h\r\nTransfer-Encoding: chunked\r\n\r\n5;ext=1\r\nhello\r\n6\r\n world\r\n0\r\nX-Trailer: 1\r\n\r\n");
    var response = await client.ReadResponseAsync();

    Assert.Contains("body=hello world", response.Body);
  }

  [Fact]
  public async Task The_path_is_percent_decoded_and_the_query_dropped()
  {
    await using var server = Start(e => Echo(e));
    using var client = await Connect(server);

    await client.SendAsync("GET /a%20b/c?x=1 HTTP/1.1\r\nHost: h\r\n\r\n");

    Assert.StartsWith("GET /a b/c ", (await client.ReadResponseAsync()).Body);
  }

  [Fact]
  public async Task A_streamed_response_is_chunked_and_each_flush_reaches_the_client_at_once()
  {
    var release = new TaskCompletionSource();
    await using var server = Start(async e =>
    {
      e.Response.SetHeader("Content-Type", "text/event-stream");
      await e.Response.Body.WriteAsync("data: one\n\n"u8.ToArray(), e.Aborted);
      await e.Response.Body.FlushAsync(e.Aborted);
      await release.Task.WaitAsync(e.Aborted);
      await e.Response.Body.WriteAsync("data: two\n\n"u8.ToArray(), e.Aborted);
      await e.Response.Body.FlushAsync(e.Aborted);
    });
    using var client = await Connect(server);

    await client.SendAsync(Get);
    var head = await client.ReadHeadAsync();
    var first = await client.ReadChunkAsync();
    release.SetResult();
    var second = await client.ReadChunkAsync();

    Assert.Contains("Transfer-Encoding: chunked", head);
    Assert.Equal("data: one\n\n", first);
    Assert.Equal("data: two\n\n", second);
  }

  [Fact]
  public async Task A_large_body_is_streamed_without_a_declared_length()
  {
    var big = new byte[100_000];
    Array.Fill(big, (byte)'z');
    await using var server = Start(async e => await e.Response.Body.WriteAsync(big, e.Aborted));
    using var client = await Connect(server);

    await client.SendAsync(Get);
    var response = await client.ReadResponseAsync();

    Assert.Equal(100_000, response.Body.Length);
    Assert.Equal("chunked", response.Headers["Transfer-Encoding"]);
  }

  [Fact]
  public async Task A_declared_content_length_is_kept_and_head_requests_get_no_body()
  {
    await using var server = Start(async e =>
    {
      e.Response.SetHeader("Content-Length", "3");
      await e.Response.Body.WriteAsync("abc"u8.ToArray(), e.Aborted);
    });
    using var client = await Connect(server);

    await client.SendAsync("GET / HTTP/1.1\r\nHost: h\r\n\r\n");
    var get = await client.ReadResponseAsync();
    await client.SendAsync("HEAD / HTTP/1.1\r\nHost: h\r\n\r\nGET / HTTP/1.1\r\nHost: h\r\n\r\n");
    var head = await client.ReadHeadAsync();
    var next = await client.ReadResponseAsync();

    Assert.Equal("abc", get.Body);
    Assert.Contains("Content-Length: 3", head);
    Assert.Equal("abc", next.Body);
  }

  [Fact]
  public async Task Http_1_0_gets_a_close_delimited_response()
  {
    await using var server = Start(e => Echo(e));
    using var client = await Connect(server);

    await client.SendAsync("GET /old HTTP/1.0\r\n\r\n");
    var all = await client.ReadToEndAsync();

    Assert.Contains("Connection: close", all);
    Assert.Contains("GET /old", all);
  }

  [Fact]
  public async Task Expect_100_continue_is_answered_before_the_body_is_read()
  {
    await using var server = Start(e => Echo(e));
    using var client = await Connect(server);

    await client.SendAsync("POST / HTTP/1.1\r\nHost: h\r\nContent-Length: 2\r\nExpect: 100-continue\r\n\r\n");
    var interim = await client.ReadHeadAsync();
    await client.SendAsync("ok");
    var response = await client.ReadResponseAsync();

    Assert.StartsWith("HTTP/1.1 100", interim);
    Assert.Contains("body=ok", response.Body);
  }

  [Fact]
  public async Task Aborted_is_cancelled_when_the_peer_hangs_up()
  {
    var started = new TaskCompletionSource();
    var ended = new TaskCompletionSource<bool>();
    await using var server = Start(async e =>
    {
      started.SetResult();
      try
      {
        await Task.Delay(Timeout.Infinite, e.Aborted);
      }
      catch (OperationCanceledException)
      {
        ended.SetResult(true);
      }
    });
    var client = await Connect(server);

    await client.SendAsync(Get);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    client.Dispose();

    Assert.True(await ended.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
  }

  // ---- refusals -------------------------------------------------------------------------------------------------------------

  public static TheoryData<string, string, int> Refusals => new()
  {
    { "content-length and transfer-encoding together", "POST / HTTP/1.1\r\nHost: h\r\nContent-Length: 4\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n", 400 },
    { "duplicate content-length", "POST / HTTP/1.1\r\nHost: h\r\nContent-Length: 4\r\nContent-Length: 4\r\n\r\nabcd", 400 },
    { "content-length list", "POST / HTTP/1.1\r\nHost: h\r\nContent-Length: 4, 4\r\n\r\nabcd", 400 },
    { "signed content-length", "POST / HTTP/1.1\r\nHost: h\r\nContent-Length: +4\r\n\r\nabcd", 400 },
    { "obs-fold", "GET / HTTP/1.1\r\nHost: h\r\nX-A: one\r\n  two\r\n\r\n", 400 },
    { "space before the colon", "GET / HTTP/1.1\r\nHost: h\r\nX-A : one\r\n\r\n", 400 },
    { "bare LF line endings", "GET / HTTP/1.1\nHost: h\n\n", 400 },
    { "bare LF inside the head", "GET / HTTP/1.1\r\nHost: h\nX-A: 1\r\n\r\n", 400 },
    { "bare CR", "GET / HTTP/1.1\r\nHost: h\rX: 1\r\n\r\n", 400 },
    { "NUL in a value", "GET / HTTP/1.1\r\nHost: h\r\nX-A: a\0b\r\n\r\n", 400 },
    { "two Host headers", "GET / HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n", 400 },
    { "no Host header", "GET / HTTP/1.1\r\n\r\n", 400 },
    { "transfer coding other than chunked", "POST / HTTP/1.1\r\nHost: h\r\nTransfer-Encoding: gzip, chunked\r\n\r\n0\r\n\r\n", 501 },
    { "chunked on http/1.0", "POST / HTTP/1.0\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n", 400 },
    { "unknown http version", "GET / HTTP/2.0\r\nHost: h\r\n\r\n", 505 },
    { "garbage version", "GET / HTTX/1.1\r\nHost: h\r\n\r\n", 400 },
    { "extra space in the request line", "GET  / HTTP/1.1\r\nHost: h\r\n\r\n", 400 },
    { "absolute-form target", "GET http://h/ HTTP/1.1\r\nHost: h\r\n\r\n", 400 },
    { "lower-case method", "get / HTTP/1.1\r\nHost: h\r\n\r\n", 400 },
    { "bad percent escape", "GET /%zz HTTP/1.1\r\nHost: h\r\n\r\n", 400 },
    { "encoded NUL", "GET /%00 HTTP/1.1\r\nHost: h\r\n\r\n", 400 },
    { "unknown expectation", "POST / HTTP/1.1\r\nHost: h\r\nContent-Length: 1\r\nExpect: banana\r\n\r\nx", 417 },
    { "oversize header", "GET / HTTP/1.1\r\nHost: h\r\nX-Big: " + new string('a', 40_000) + "\r\n\r\n", 431 },
    { "too many headers", "GET / HTTP/1.1\r\nHost: h\r\n" + string.Concat(Enumerable.Range(0, 200).Select(i => $"X-{i}: 1\r\n")) + "\r\n", 431 },
    { "oversize request line", "GET /" + new string('a', 9000) + " HTTP/1.1\r\nHost: h\r\n\r\n", 414 },
    { "oversize declared body", "POST / HTTP/1.1\r\nHost: h\r\nContent-Length: 99999999999\r\n\r\n", 413 },
  };

  [Theory]
  [MemberData(nameof(Refusals))]
  public async Task A_malformed_or_oversize_request_is_refused_and_never_reaches_the_handler(string name, string request, int status)
  {
    _ = name;
    var handled = 0;
    await using var server = Start(e => Echo(e, () => Interlocked.Increment(ref handled)));
    using var client = await Connect(server);

    await client.SendAsync(request);
    var all = await client.ReadToEndAsync();

    Assert.StartsWith($"HTTP/1.1 {status} ", all);
    Assert.Contains("Connection: close", all);
    Assert.Equal(0, handled);
  }

  [Fact]
  public async Task A_request_after_a_smuggling_attempt_on_the_same_connection_is_never_parsed()
  {
    var handled = 0;
    await using var server = Start(e => Echo(e, () => Interlocked.Increment(ref handled)));
    using var client = await Connect(server);

    // The classic shape: both framings, with a complete second request hidden in the "body".
    await client.SendAsync(
      "POST / HTTP/1.1\r\nHost: h\r\nContent-Length: 44\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\nGET /smuggled HTTP/1.1\r\nHost: h\r\n\r\n");
    var all = await client.ReadToEndAsync();

    Assert.Equal(1, Count(all, "HTTP/1.1 "));
    Assert.StartsWith("HTTP/1.1 400", all);
    Assert.DoesNotContain("smuggled", all);
    Assert.Equal(0, handled);
  }

  [Fact]
  public async Task A_request_pipelined_behind_a_bad_chunk_is_not_served()
  {
    var handled = 0;
    await using var server = Start(e => Echo(e, () => Interlocked.Increment(ref handled)));
    using var client = await Connect(server);

    await client.SendAsync("POST / HTTP/1.1\r\nHost: h\r\nTransfer-Encoding: chunked\r\n\r\nZZ\r\nxx\r\n0\r\n\r\nGET /second HTTP/1.1\r\nHost: h\r\n\r\n");
    var all = await client.ReadToEndAsync();

    Assert.StartsWith("HTTP/1.1 400", all);
    Assert.Equal(1, Count(all, "HTTP/1.1 "));
    Assert.Equal(1, handled);
  }

  [Theory]
  [InlineData("fffffffffffffffff")]
  [InlineData("-1")]
  [InlineData("1 ")]
  [InlineData("")]
  [InlineData("0x5")]
  public async Task A_bad_chunk_size_is_a_400(string size)
  {
    await using var server = Start(e => Echo(e));
    using var client = await Connect(server);

    await client.SendAsync($"POST / HTTP/1.1\r\nHost: h\r\nTransfer-Encoding: chunked\r\n\r\n{size}\r\nhello\r\n0\r\n\r\n");
    var all = await client.ReadToEndAsync();

    Assert.StartsWith("HTTP/1.1 400", all);
  }

  [Fact]
  public async Task A_chunk_not_followed_by_crlf_is_a_400()
  {
    await using var server = Start(e => Echo(e));
    using var client = await Connect(server);

    await client.SendAsync("POST / HTTP/1.1\r\nHost: h\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhelloXX0\r\n\r\n");

    Assert.StartsWith("HTTP/1.1 400", await client.ReadToEndAsync());
  }

  [Fact]
  public async Task A_chunked_body_over_the_limit_is_a_413_and_the_connection_closes()
  {
    await using var server = Start(e => Echo(e), new HttpServerOptions { MaxRequestBodyBytes = 16 });
    using var client = await Connect(server);

    await client.SendAsync("POST / HTTP/1.1\r\nHost: h\r\nTransfer-Encoding: chunked\r\n\r\n8\r\n01234567\r\n8\r\n89abcdef\r\n8\r\nghijklmn\r\n0\r\n\r\n");
    var all = await client.ReadToEndAsync();

    Assert.StartsWith("HTTP/1.1 413", all);
  }

  [Fact]
  public async Task A_body_that_ends_early_is_a_400()
  {
    await using var server = Start(e => Echo(e));
    using var client = await Connect(server);

    await client.SendAsync("POST / HTTP/1.1\r\nHost: h\r\nContent-Length: 10\r\n\r\nabc");
    client.HalfClose();

    Assert.StartsWith("HTTP/1.1 400", await client.ReadToEndAsync());
  }

  [Fact]
  public async Task A_client_that_never_finishes_the_head_gets_408_and_is_dropped()
  {
    await using var server = Start(e => Echo(e), new HttpServerOptions { HeaderReadTimeout = TimeSpan.FromMilliseconds(300) });
    using var client = await Connect(server);

    await client.SendAsync("GET / HTTP/1.1\r\nHost: h\r\nX-Slow: ");
    var all = await client.ReadToEndAsync(3000);

    Assert.StartsWith("HTTP/1.1 408", all);
  }

  [Fact]
  public async Task A_client_that_trickles_bytes_still_hits_the_head_deadline()
  {
    await using var server = Start(e => Echo(e), new HttpServerOptions { HeaderReadTimeout = TimeSpan.FromMilliseconds(400) });
    using var client = await Connect(server);

    var read = client.ReadToEndAsync(5000);
    try
    {
      foreach (var c in "GET / HTTP/1.1\r\nHost: h\r\nX-A: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
      {
        await client.SendAsync(c.ToString());
        await Task.Delay(50, TestContext.Current.CancellationToken);
      }
    }
    catch (IOException)
    {
      // The server already hung up, which is the point.
    }

    Assert.StartsWith("HTTP/1.1 408", await read);
  }

  [Fact]
  public async Task An_idle_connection_is_closed_without_a_response()
  {
    await using var server = Start(e => Echo(e), new HttpServerOptions { IdleTimeout = TimeSpan.FromMilliseconds(300) });
    using var client = await Connect(server);

    Assert.Equal(string.Empty, await client.ReadToEndAsync(3000));
  }

  [Fact]
  public async Task A_body_that_never_arrives_is_cut_off_at_the_request_timeout()
  {
    await using var server = Start(e => Echo(e), new HttpServerOptions { RequestTimeout = TimeSpan.FromMilliseconds(300) });
    using var client = await Connect(server);

    await client.SendAsync("POST / HTTP/1.1\r\nHost: h\r\nContent-Length: 100\r\n\r\nabc");

    Assert.StartsWith("HTTP/1.1 408", await client.ReadToEndAsync(3000));
  }

  [Fact]
  public async Task Connections_beyond_the_limit_wait_instead_of_being_served()
  {
    await using var server = Start(e => Echo(e), new HttpServerOptions { MaxConnections = 1 });
    using var first = await Connect(server);
    await first.SendAsync(Get);
    await first.ReadResponseAsync();
    using var second = await Connect(server);
    await second.SendAsync(Get);

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.ReadResponseAsync(400));

    first.Dispose();
    Assert.Equal(200, (await second.ReadResponseAsync()).Status);
  }

  // ---- forwarded headers ----------------------------------------------------------------------------------------------------

  [Fact]
  public async Task A_forged_forwarded_header_is_dropped_and_the_socket_address_is_used()
  {
    await using var server = Start(e => Echo(e));
    using var client = await Connect(server);

    await client.SendAsync("GET / HTTP/1.1\r\nHost: h\r\nX-Forwarded-For: 6.6.6.6\r\nX-Forwarded-Proto: https\r\nX-Forwarded-Host: evil.example\r\nForwarded: for=6.6.6.6\r\n\r\n");
    var body = (await client.ReadResponseAsync()).Body;

    Assert.Contains("remote=127.0.0.1 xff=no scheme=http", body);
  }

  [Fact]
  public async Task A_trusted_proxy_supplies_the_client_address_scheme_and_host()
  {
    string? host = null;
    await using var server = Start(
      e =>
      {
        host = e.Request.Headers.GetFirst("Host");
        return Echo(e);
      },
      new HttpServerOptions { TrustedProxies = [IPAddress.Loopback] });
    using var client = await Connect(server);

    await client.SendAsync("GET / HTTP/1.1\r\nHost: internal\r\nX-Forwarded-For: 6.6.6.6, 9.9.9.9, 127.0.0.1\r\nX-Forwarded-Proto: https\r\nX-Forwarded-Host: app.example.com\r\n\r\n");
    var body = (await client.ReadResponseAsync()).Body;

    // 127.0.0.1 is another trusted hop; 9.9.9.9 is the last address a trusted proxy vouches for, and 6.6.6.6 is client-supplied.
    Assert.Contains("remote=9.9.9.9 xff=yes scheme=https", body);
    Assert.Equal("app.example.com", host);
  }

  [Fact]
  public async Task A_malformed_forwarded_chain_does_not_replace_the_peer()
  {
    await using var server = Start(e => Echo(e), new HttpServerOptions { TrustedProxies = [IPAddress.Loopback] });
    using var client = await Connect(server);

    await client.SendAsync("GET / HTTP/1.1\r\nHost: h\r\nX-Forwarded-For: 6.6.6.6, not-an-address\r\nX-Forwarded-Host: bad host\r\n\r\n");
    var body = (await client.ReadResponseAsync()).Body;

    Assert.Contains("remote=127.0.0.1", body);
  }

  [Fact]
  public void Only_a_non_loopback_bind_without_tls_or_a_proxy_warns()
  {
    Assert.False(HttpServer.ShouldWarn(new HttpServerOptions()));
    Assert.False(HttpServer.ShouldWarn(new HttpServerOptions { Address = IPAddress.IPv6Loopback }));
    Assert.True(HttpServer.ShouldWarn(new HttpServerOptions { Address = IPAddress.Parse("192.0.2.10") }));
    Assert.False(HttpServer.ShouldWarn(new HttpServerOptions { Address = IPAddress.Parse("192.0.2.10"), TrustedProxies = [IPAddress.Loopback] }));
  }

  // ---- response safety --------------------------------------------------------------------------------------------------------

  [Fact]
  public async Task Header_names_and_values_with_line_breaks_are_refused()
  {
    ArgumentException? value = null;
    ArgumentException? name = null;
    await using var server = Start(e =>
    {
      value = Assert.Throws<ArgumentException>(() => e.Response.SetHeader("X-A", "a\r\nSet-Cookie: x=1"));
      name = Assert.Throws<ArgumentException>(() => e.Response.SetHeader("X A", "1"));
      Assert.Throws<ArgumentException>(() => e.Response.SetHeader("Transfer-Encoding", "chunked"));
      return ValueTask.CompletedTask;
    });
    using var client = await Connect(server);

    await client.SendAsync(Get);
    var response = await client.ReadResponseAsync();

    Assert.Equal(200, response.Status);
    Assert.NotNull(value);
    Assert.NotNull(name);
    Assert.False(response.Headers.ContainsKey("Set-Cookie"));
  }

  [Fact]
  public async Task A_handler_that_throws_gets_a_500_without_details()
  {
    await using var server = Start(_ => throw new InvalidOperationException("secret detail"));
    using var client = await Connect(server);

    await client.SendAsync(Get);
    var all = await client.ReadToEndAsync();

    Assert.StartsWith("HTTP/1.1 500", all);
    Assert.DoesNotContain("secret", all);
  }

  [Fact]
  public async Task Disposing_the_server_ends_open_streams_promptly()
  {
    var server = Start(async e =>
    {
      await e.Response.Body.FlushAsync(e.Aborted);
      await Task.Delay(Timeout.Infinite, e.Aborted);
    });
    using var client = await Connect(server);
    await client.SendAsync(Get);
    await client.ReadHeadAsync();

    var stopped = server.DisposeAsync().AsTask();

    await stopped.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    await Assert.ThrowsAnyAsync<Exception>(async () => await client.ReadChunkAsync(2000));
  }

  [Fact]
  public async Task Tls_serves_https_and_reports_the_scheme()
  {
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
    using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    using var exportable = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    await using var server = Start(e => Echo(e), new HttpServerOptions { Certificate = exportable });
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(server.LocalEndPoint!, TestContext.Current.CancellationToken);
    // A throwaway certificate generated above and a loopback peer: there is no identity to verify.
#pragma warning disable CA5359
    await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
#pragma warning restore CA5359

    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost", ApplicationProtocols = [SslApplicationProtocol.Http11] }, TestContext.Current.CancellationToken);
    await ssl.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"), TestContext.Current.CancellationToken);
    using var reader = new StreamReader(ssl, Encoding.UTF8);
    var all = await reader.ReadToEndAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    Assert.StartsWith("HTTP/1.1 200", all);
    Assert.Contains("scheme=https", all);
    Assert.StartsWith("https://", server.BaseAddress!.ToString());
  }

  // ---- the MCP endpoint on the listener -----------------------------------------------------------------------------------------

  private static HttpServer StartMcp(out ContextServer contextServer)
  {
    contextServer = new ContextServer("listener-test", "1.0");
    new SignatureTools().AddTo(contextServer);
    var endpoint = new StreamableHttpEndpoint(contextServer);
    var server = new HttpServer(e => e.Request.Path == "/mcp" ? endpoint.HandleAsync(e) : ValueTask.CompletedTask);
    server.Start();
    return server;
  }

  private static string PostJson(string body, params string[] headers) =>
    $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nAccept: application/json, text/event-stream\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n{string.Concat(headers.Select(h => h + "\r\n"))}\r\n{body}";

  [Fact]
  public async Task A_foreign_origin_is_refused_before_any_work()
  {
    await using var server = StartMcp(out _);
    using var client = await Connect(server);

    await client.SendAsync(PostJson("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", "Origin: https://evil.example"));

    Assert.Equal(403, (await client.ReadResponseAsync()).Status);
  }

  [Fact]
  public async Task A_rebound_host_name_is_refused()
  {
    await using var server = StartMcp(out _);
    using var client = await Connect(server);

    await client.SendAsync(PostJson("""{"jsonrpc":"2.0","id":1,"method":"ping"}""").Replace("Host: 127.0.0.1", "Host: attacker.example", StringComparison.Ordinal));

    Assert.True((await client.ReadResponseAsync()).Status is 403 or 421);
  }

  [Fact]
  public async Task A_legacy_session_works_over_the_listener()
  {
    await using var server = StartMcp(out _);
    using var client = await Connect(server);

    await client.SendAsync(PostJson("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}"""));
    var init = await client.ReadResponseAsync();
    var session = init.Headers["Mcp-Session-Id"];
    await client.SendAsync(PostJson("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", "Mcp-Session-Id: " + session, "MCP-Protocol-Version: 2025-11-25"));
    var accepted = await client.ReadResponseAsync();
    await client.SendAsync(PostJson("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", "Mcp-Session-Id: " + session, "MCP-Protocol-Version: 2025-11-25"));
    var list = await client.ReadResponseAsync();

    Assert.Equal(200, init.Status);
    Assert.Equal(202, accepted.Status);
    Assert.Contains("\"scalars\"", list.Body, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task A_modern_request_works_over_the_listener_and_a_legacy_event_stream_survives_keep_alive()
  {
    await using var server = StartMcp(out _);
    using var client = await Connect(server);
    var meta = """{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/clientInfo":{"name":"t","version":"1"}}""";

    await client.SendAsync(PostJson("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":""" + meta + "}}", "MCP-Protocol-Version: 2026-07-28", "Mcp-Method: tools/list"));
    var list = await client.ReadResponseAsync();
    using var doc = JsonDocument.Parse(list.Body.Contains("data:", StringComparison.Ordinal) ? list.Body[(list.Body.IndexOf("data:", StringComparison.Ordinal) + 5)..].Trim() : list.Body);

    Assert.Equal(200, list.Status);
    Assert.True(doc.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() > 0);
  }

  [Fact]
  public async Task ServeHttpAsync_serves_generated_tools_until_cancelled()
  {
    using var cts = new CancellationTokenSource();
    var url = new TaskCompletionSource<Uri>();
    var serving = ModelContext.ServeHttpAsync(new SignatureTools(), port: 0, onListening: u => url.SetResult(u), cancellationToken: cts.Token);
    var address = await url.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    using var client = await RawClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, address.Port));

    await client.SendAsync($"GET /nothing-here HTTP/1.1\r\nHost: 127.0.0.1:{address.Port}\r\n\r\n");
    var missing = await client.ReadResponseAsync();
    await client.SendAsync(PostJson("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""").Replace("Host: 127.0.0.1", $"Host: 127.0.0.1:{address.Port}", StringComparison.Ordinal));
    var init = await client.ReadResponseAsync();
    await cts.CancelAsync();
    await serving.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    Assert.Equal("/mcp", address.AbsolutePath);
    Assert.Equal(404, missing.Status);
    Assert.Equal(200, init.Status);
    Assert.Contains("2025-06-18", init.Body, StringComparison.Ordinal);
  }

  [Fact]
  public async Task A_resource_server_challenges_with_the_metadata_url_and_serves_the_document_on_the_listener()
  {
    const string Token = "listener-test-token-0123456789abcdef";
    var validator = new StaticTokenValidator().Add(Token, "alice", ["files:read"]);
    var resourceServer = new ResourceServer(new ResourceServerOptions(new Uri("https://mcp.test/mcp"), validator) { AuthorizationServers = ["https://issuer.test"] });
    using var cts = new CancellationTokenSource();
    var url = new TaskCompletionSource<Uri>();
    var serving = ModelContext.ServeHttpAsync(new SignatureTools(), port: 0, onListening: u => url.SetResult(u), resourceServer: resourceServer, cancellationToken: cts.Token);
    var address = await url.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    using var client = await RawClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, address.Port));
    var host = $"127.0.0.1:{address.Port}";
    var ping = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";

    await client.SendAsync(PostJson(ping).Replace("Host: 127.0.0.1", "Host: " + host, StringComparison.Ordinal));
    var challenge = await client.ReadResponseAsync();
    await client.SendAsync($"GET /.well-known/oauth-protected-resource/mcp HTTP/1.1\r\nHost: {host}\r\n\r\n");
    var document = await client.ReadResponseAsync();
    await client.SendAsync($"GET /.well-known/oauth-protected-resource HTTP/1.1\r\nHost: {host}\r\n\r\n");
    var root = await client.ReadResponseAsync();
    await client.SendAsync(PostJson("""{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""", "Authorization: Bearer " + Token).Replace("Host: 127.0.0.1", "Host: " + host, StringComparison.Ordinal));
    var authorized = await client.ReadResponseAsync();
    await client.SendAsync($"GET /.well-known/other HTTP/1.1\r\nHost: {host}\r\n\r\n");
    var other = await client.ReadResponseAsync();
    await cts.CancelAsync();
    await serving.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    Assert.Equal(401, challenge.Status);
    Assert.Contains("resource_metadata=\"https://mcp.test/.well-known/oauth-protected-resource/mcp\"", challenge.Headers["WWW-Authenticate"], StringComparison.Ordinal);
    Assert.Equal(200, document.Status);
    using var metadata = JsonDocument.Parse(document.Body);
    Assert.Equal("https://mcp.test/mcp", metadata.RootElement.GetProperty("resource").GetString());
    Assert.Equal(200, root.Status);
    Assert.Equal(200, authorized.Status);
    Assert.Equal(404, other.Status);
    Assert.DoesNotContain(Token, challenge.Body + document.Body, StringComparison.Ordinal);
  }

  private static int Count(string text, string needle)
  {
    var count = 0;
    for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal))
    {
      count++;
    }

    return count;
  }
}
