using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests.JsonRpc;

public class NewlineChannelTests
{
  private static async Task<List<string>> ReadAllAsync(NewlineChannel channel, CancellationToken ct)
  {
    var lines = new List<string>();
    while (true)
    {
      var read = await channel.ReadAsync(ct);
      if (read.Status != JsonRpcChannelStatus.Message)
      {
        lines.Add("<" + read.Status + ">");
        return lines;
      }

      lines.Add(Encoding.UTF8.GetString(read.Message.Span));
    }
  }

  [Fact]
  public async Task Several_lines_in_one_chunk_are_split()
  {
    var ct = TestContext.Current.CancellationToken;
    await using var channel = new NewlineChannel(new MemoryStream(Encoding.UTF8.GetBytes("{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n")));

    Assert.Equal(["{\"a\":1}", "{\"b\":2}", "{\"c\":3}", "<EndOfStream>"], await ReadAllAsync(channel, ct));
  }

  [Fact]
  public async Task A_line_arriving_one_byte_at_a_time_is_reassembled()
  {
    var ct = TestContext.Current.CancellationToken;
    var (a, b) = PipeEnd.CreatePair();
    await using var channel = new NewlineChannel(b);
    var reading = ReadAllAsync(channel, ct);
    foreach (var c in "{\"x\":\"y\"}\n{\"z\":1}\n")
    {
      a.Write([(byte)c], 0, 1);
      await Task.Yield();
    }

    await a.DisposeAsync();

    Assert.Equal(["{\"x\":\"y\"}", "{\"z\":1}", "<EndOfStream>"], await reading);
  }

  [Fact]
  public async Task Carriage_returns_and_empty_lines_are_ignored()
  {
    var ct = TestContext.Current.CancellationToken;
    await using var channel = new NewlineChannel(new MemoryStream(Encoding.UTF8.GetBytes("\n\r\n{\"a\":1}\r\n\n{\"b\":2}\n")));

    Assert.Equal(["{\"a\":1}", "{\"b\":2}", "<EndOfStream>"], await ReadAllAsync(channel, ct));
  }

  [Fact]
  public async Task A_last_line_without_a_newline_is_still_a_message()
  {
    var ct = TestContext.Current.CancellationToken;
    await using var channel = new NewlineChannel(new MemoryStream(Encoding.UTF8.GetBytes("{\"a\":1}\n{\"b\":2}")));

    Assert.Equal(["{\"a\":1}", "{\"b\":2}", "<EndOfStream>"], await ReadAllAsync(channel, ct));
  }

  [Fact]
  public async Task A_line_exactly_at_the_bound_is_accepted()
  {
    var ct = TestContext.Current.CancellationToken;
    var line = new string('x', 1000);
    await using var channel = new NewlineChannel(new MemoryStream(Encoding.UTF8.GetBytes(line + "\n" + line + "\r\n")), maxLineBytes: 1000);

    Assert.Equal([line, line, "<EndOfStream>"], await ReadAllAsync(channel, ct));
  }

  [Theory]
  [InlineData(1001, true)]
  [InlineData(1001, false)]
  [InlineData(50_000, true)]
  public async Task A_line_over_the_bound_ends_the_channel(int length, bool withNewline)
  {
    var ct = TestContext.Current.CancellationToken;
    var data = new string('x', length) + (withNewline ? "\n{\"after\":1}\n" : string.Empty);
    await using var channel = new NewlineChannel(new MemoryStream(Encoding.UTF8.GetBytes(data)), maxLineBytes: 1000);

    var first = await channel.ReadAsync(ct);
    var second = await channel.ReadAsync(ct);

    Assert.Equal(JsonRpcChannelStatus.MessageTooLong, first.Status);
    // The channel is finished: nothing after the overrun is delivered, because the stream cannot be resynchronised safely.
    Assert.Equal(JsonRpcChannelStatus.EndOfStream, second.Status);
  }

  [Fact]
  public async Task Memory_use_is_bounded_while_an_overlong_line_streams_in()
  {
    var ct = TestContext.Current.CancellationToken;
    var (a, b) = PipeEnd.CreatePair();
    await using var channel = new NewlineChannel(b, maxLineBytes: 4096);
    var read = channel.ReadAsync(ct).AsTask();
    var chunk = new byte[1024];
    Array.Fill(chunk, (byte)'x');
    for (var i = 0; i < 20 && !read.IsCompleted; i++)
    {
      await a.WriteAsync(chunk, ct);
      await Task.Delay(1, ct);
    }

    Assert.Equal(JsonRpcChannelStatus.MessageTooLong, (await read).Status);
  }

  [Fact]
  public async Task A_message_larger_than_the_initial_buffer_grows_it()
  {
    var ct = TestContext.Current.CancellationToken;
    var line = "{\"big\":\"" + new string('y', 3_000_000) + "\"}";
    await using var channel = new NewlineChannel(new MemoryStream(Encoding.UTF8.GetBytes(line + "\n")));

    var read = await channel.ReadAsync(ct);

    Assert.Equal(JsonRpcChannelStatus.Message, read.Status);
    Assert.Equal(line.Length, read.Message.Length);
  }

  [Fact]
  public async Task Write_appends_one_newline_and_flushes()
  {
    var ct = TestContext.Current.CancellationToken;
    var (a, b) = PipeEnd.CreatePair();
    await using var channel = new NewlineChannel(b);

    await channel.WriteAsync(Encoding.UTF8.GetBytes("{\"a\":1}"), ct);
    await channel.WriteAsync(Encoding.UTF8.GetBytes("{\"b\":2}"), ct);

    var reader = new LineReader(a);
    Assert.Equal("{\"a\":1}", await reader.ReadLineAsync());
    Assert.Equal("{\"b\":2}", await reader.ReadLineAsync());
  }

  [Fact]
  public void A_zero_bound_is_refused()
  {
    Assert.Throws<ArgumentOutOfRangeException>(() => new NewlineChannel(new MemoryStream(), maxLineBytes: 0));
  }
}

public class JsonRpcConnectionTests
{
  private static (JsonRpcConnection Connection, PipeEnd Peer) Create(JsonRpcConnectionOptions? options = null)
  {
    var (a, b) = PipeEnd.CreatePair();
    return (new JsonRpcConnection(new NewlineChannel(a), options), b);
  }

  private static async Task SendLineAsync(Stream peer, string line) => await peer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), TestContext.Current.CancellationToken);

  [Fact]
  public async Task A_response_is_matched_to_its_request_by_id()
  {
    var (connection, peer) = Create();
    await using var _ = connection;
    var run = connection.RunAsync(TestContext.Current.CancellationToken);
    var reader = new LineReader(peer);

    var first = connection.SendRequestAsync("one", default, TestContext.Current.CancellationToken).AsTask();
    var second = connection.SendRequestAsync("two", default, TestContext.Current.CancellationToken).AsTask();
    var r1 = await reader.ReadJsonAsync();
    var r2 = await reader.ReadJsonAsync();

    // Answer out of order.
    await SendLineAsync(peer, "{\"jsonrpc\":\"2.0\",\"id\":" + r2.GetProperty("id").GetRawText() + ",\"result\":{\"which\":\"" + r2.GetProperty("method").GetString() + "\"}}");
    await SendLineAsync(peer, "{\"jsonrpc\":\"2.0\",\"id\":" + r1.GetProperty("id").GetRawText() + ",\"result\":{\"which\":\"" + r1.GetProperty("method").GetString() + "\"}}");

    Assert.Equal("one", (await first).GetProperty("which").GetString());
    Assert.Equal("two", (await second).GetProperty("which").GetString());
    await peer.DisposeAsync();
    Assert.Equal(JsonRpcCloseReason.EndOfStream, await run);
  }

  [Fact]
  public async Task An_error_response_throws_a_remote_exception_with_its_code_and_data()
  {
    var (connection, peer) = Create();
    await using var _ = connection;
    var run = connection.RunAsync(TestContext.Current.CancellationToken);
    var reader = new LineReader(peer);

    var call = connection.SendRequestAsync("m", default, TestContext.Current.CancellationToken).AsTask();
    var request = await reader.ReadJsonAsync();
    await SendLineAsync(peer, "{\"jsonrpc\":\"2.0\",\"id\":" + request.GetProperty("id").GetRawText() + ",\"error\":{\"code\":-32022,\"message\":\"nope\",\"data\":{\"supported\":[\"x\"]}}}");

    var ex = await Assert.ThrowsAsync<JsonRpcRemoteException>(() => call);
    Assert.Equal(-32022, ex.Code);
    Assert.Equal("x", ex.ErrorData.GetProperty("supported")[0].GetString());
    await peer.DisposeAsync();
    await run;
  }

  [Fact]
  public async Task Pending_requests_fail_when_the_peer_closes()
  {
    var (connection, peer) = Create();
    await using var _ = connection;
    var run = connection.RunAsync(TestContext.Current.CancellationToken);
    var reader = new LineReader(peer);
    var call = connection.SendRequestAsync("m", default, TestContext.Current.CancellationToken).AsTask();
    await reader.ReadLineAsync();

    await peer.DisposeAsync();

    await Assert.ThrowsAsync<JsonRpcClosedException>(() => call);
    await run;
    await Assert.ThrowsAsync<JsonRpcClosedException>(() => connection.SendRequestAsync("again", default, TestContext.Current.CancellationToken).AsTask());
  }

  [Fact]
  public async Task Incoming_requests_run_concurrently()
  {
    var started = new TaskCompletionSource();
    var release = new TaskCompletionSource();
    var running = 0;
    var (connection, peer) = Create(new JsonRpcConnectionOptions
    {
      RequestHandler = async (request, ct) =>
      {
        if (Interlocked.Increment(ref running) == 2)
        {
          started.SetResult();
        }

        await release.Task.WaitAsync(ct);
        return new JsonRpcResultResponse(request.Id, Json.Parse("{\"m\":\"" + request.Method + "\"}"));
      },
    });
    await using var _ = connection;
    var run = connection.RunAsync(TestContext.Current.CancellationToken);

    await SendLineAsync(peer, """{"jsonrpc":"2.0","id":1,"method":"slow1"}""");
    await SendLineAsync(peer, """{"jsonrpc":"2.0","id":2,"method":"slow2"}""");
    await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    // Both handlers are inside at once; neither blocked the read loop.
    release.SetResult();
    var reader = new LineReader(peer);
    var ids = new[] { (await reader.ReadJsonAsync()).GetProperty("id").GetInt32(), (await reader.ReadJsonAsync()).GetProperty("id").GetInt32() }.Order().ToArray();
    Assert.Equal([1, 2], ids);
    await peer.DisposeAsync();
    await run;
  }

  [Fact]
  public async Task Notifications_are_delivered_in_order()
  {
    var seen = new List<string>();
    var done = new TaskCompletionSource();
    var (connection, peer) = Create(new JsonRpcConnectionOptions
    {
      NotificationHandler = (n, _) =>
      {
        lock (seen)
        {
          seen.Add(n.Method);
          if (seen.Count == 50)
          {
            done.SetResult();
          }
        }

        return ValueTask.CompletedTask;
      },
    });
    await using var _ = connection;
    var run = connection.RunAsync(TestContext.Current.CancellationToken);

    for (var i = 0; i < 50; i++)
    {
      await SendLineAsync(peer, "{\"jsonrpc\":\"2.0\",\"method\":\"n" + i + "\"}");
    }

    await done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    Assert.Equal(Enumerable.Range(0, 50).Select(i => "n" + i), seen);
    await peer.DisposeAsync();
    await run;
  }

  [Fact]
  public async Task Cancelling_an_incoming_request_cancels_its_token_and_sends_no_response()
  {
    var entered = new TaskCompletionSource();
    var sawCancel = new TaskCompletionSource<bool>();
    var (connection, peer) = Create(new JsonRpcConnectionOptions
    {
      RequestHandler = async (request, ct) =>
      {
        entered.SetResult();
        try
        {
          await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
          sawCancel.SetResult(true);
          throw;
        }

        return null;
      },
    });
    await using var _ = connection;
    var run = connection.RunAsync(TestContext.Current.CancellationToken);

    await SendLineAsync(peer, """{"jsonrpc":"2.0","id":"r1","method":"wait"}""");
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    Assert.True(connection.CancelIncomingRequest(JsonRpcId.FromString("r1")));

    Assert.True(await sawCancel.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    // The next thing the peer hears is the answer to a later request, not a response to the cancelled one.
    await SendLineAsync(peer, """{"jsonrpc":"2.0","id":9,"method":"other"}""");
    var reader = new LineReader(peer);
    var next = await reader.ReadJsonAsync();
    Assert.Equal(9, next.GetProperty("id").GetInt32());
    Assert.False(connection.CancelIncomingRequest(JsonRpcId.FromString("r1")));
    await peer.DisposeAsync();
    await run;
  }

  [Fact]
  public async Task Cancelling_an_outgoing_request_tells_the_cancel_hook_and_throws()
  {
    var cancelled = new TaskCompletionSource<JsonRpcId>();
    var (connection, peer) = Create(new JsonRpcConnectionOptions
    {
      OutgoingRequestCancelled = id =>
      {
        cancelled.SetResult(id);
        return ValueTask.CompletedTask;
      },
    });
    await using var _ = connection;
    var run = connection.RunAsync(TestContext.Current.CancellationToken);
    var reader = new LineReader(peer);
    using var cts = new CancellationTokenSource();

    var call = connection.SendRequestAsync("m", default, cts.Token).AsTask();
    var request = await reader.ReadJsonAsync();
    await cts.CancelAsync();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    Assert.Equal(request.GetProperty("id").GetRawText(), (await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Text);
    await peer.DisposeAsync();
    await run;
  }

  [Fact]
  public async Task A_request_times_out_on_the_injected_clock()
  {
    var clock = new ManualTimeProvider();
    var (connection, peer) = Create(new JsonRpcConnectionOptions { RequestTimeout = TimeSpan.FromSeconds(30), TimeProvider = clock });
    await using var _ = connection;
    var run = connection.RunAsync(TestContext.Current.CancellationToken);
    var reader = new LineReader(peer);

    var call = connection.SendRequestAsync("m", default, TestContext.Current.CancellationToken).AsTask();
    await reader.ReadLineAsync();
    clock.Advance(TimeSpan.FromSeconds(31));

    await Assert.ThrowsAsync<TimeoutException>(() => call);
    await peer.DisposeAsync();
    await run;
  }

  [Fact]
  public async Task A_handler_that_throws_gives_a_generic_internal_error_that_hides_the_message()
  {
    Exception? reported = null;
    var (connection, peer) = Create(new JsonRpcConnectionOptions
    {
      RequestHandler = (_, _) => throw new InvalidOperationException("secret-token-abc"),
      HandlerFaulted = ex => reported = ex,
    });
    await using var _ = connection;
    var run = connection.RunAsync(TestContext.Current.CancellationToken);

    await SendLineAsync(peer, """{"jsonrpc":"2.0","id":1,"method":"boom"}""");
    var line = await new LineReader(peer).ReadLineAsync();

    Assert.NotNull(line);
    Assert.Contains("-32603", line, StringComparison.Ordinal);
    Assert.DoesNotContain("secret-token", line, StringComparison.Ordinal);
    Assert.IsType<InvalidOperationException>(reported);
    await peer.DisposeAsync();
    await run;
  }

  [Fact]
  public async Task Without_a_handler_every_request_is_method_not_found()
  {
    var (connection, peer) = Create();
    await using var _ = connection;
    var run = connection.RunAsync(TestContext.Current.CancellationToken);

    await SendLineAsync(peer, """{"jsonrpc":"2.0","id":5,"method":"nope"}""");
    var response = await new LineReader(peer).ReadJsonAsync();

    Assert.Equal(-32601, response.GetProperty("error").GetProperty("code").GetInt32());
    Assert.Equal(5, response.GetProperty("id").GetInt32());
    await peer.DisposeAsync();
    await run;
  }

  [Fact]
  public async Task Bad_input_gets_an_error_and_the_connection_keeps_going()
  {
    var (connection, peer) = Create(new JsonRpcConnectionOptions
    {
      RequestHandler = (r, _) => ValueTask.FromResult<JsonRpcMessage?>(new JsonRpcResultResponse(r.Id, Json.Parse("{}"))),
    });
    await using var _ = connection;
    var run = connection.RunAsync(TestContext.Current.CancellationToken);
    var reader = new LineReader(peer);

    await SendLineAsync(peer, "this is not json");
    var parse = await reader.ReadJsonAsync();
    await SendLineAsync(peer, "42");
    var invalid = await reader.ReadJsonAsync();
    await SendLineAsync(peer, """{"jsonrpc":"2.0","id":3,"method":"ok"}""");
    var ok = await reader.ReadJsonAsync();

    Assert.Equal(-32700, parse.GetProperty("error").GetProperty("code").GetInt32());
    Assert.Equal(System.Text.Json.JsonValueKind.Null, parse.GetProperty("id").ValueKind);
    Assert.Equal(-32600, invalid.GetProperty("error").GetProperty("code").GetInt32());
    Assert.Equal(3, ok.GetProperty("id").GetInt32());
    await peer.DisposeAsync();
    await run;
  }

  [Fact]
  public async Task A_batch_is_rejected_unless_batches_are_accepted()
  {
    var (connection, peer) = Create(new JsonRpcConnectionOptions
    {
      RequestHandler = (r, _) => ValueTask.FromResult<JsonRpcMessage?>(new JsonRpcResultResponse(r.Id, Json.Parse("{}"))),
    });
    await using var _ = connection;
    var run = connection.RunAsync(TestContext.Current.CancellationToken);
    var reader = new LineReader(peer);
    var batch = """[{"jsonrpc":"2.0","id":1,"method":"a"},{"jsonrpc":"2.0","id":2,"method":"b"},{"jsonrpc":"2.0","method":"n"}]""";

    await SendLineAsync(peer, batch);
    var rejected = await reader.ReadJsonAsync();
    connection.AcceptBatches = true;
    await SendLineAsync(peer, batch);
    var accepted = await reader.ReadJsonAsync();

    Assert.Equal(-32600, rejected.GetProperty("error").GetProperty("code").GetInt32());
    Assert.Equal(System.Text.Json.JsonValueKind.Array, accepted.ValueKind);
    Assert.Equal(2, accepted.GetArrayLength());
    await peer.DisposeAsync();
    await run;
  }

  [Fact]
  public async Task Too_many_requests_in_flight_get_a_busy_error()
  {
    var release = new TaskCompletionSource();
    var (connection, peer) = Create(new JsonRpcConnectionOptions
    {
      MaxConcurrentRequests = 2,
      RequestHandler = async (r, ct) =>
      {
        await release.Task.WaitAsync(ct);
        return new JsonRpcResultResponse(r.Id, Json.Parse("{}"));
      },
    });
    await using var _ = connection;
    var run = connection.RunAsync(TestContext.Current.CancellationToken);
    var reader = new LineReader(peer);

    await SendLineAsync(peer, """{"jsonrpc":"2.0","id":1,"method":"a"}""");
    await SendLineAsync(peer, """{"jsonrpc":"2.0","id":2,"method":"a"}""");
    await SendLineAsync(peer, """{"jsonrpc":"2.0","id":3,"method":"a"}""");
    var busy = await reader.ReadJsonAsync();
    release.SetResult();

    Assert.Equal(3, busy.GetProperty("id").GetInt32());
    Assert.Equal(-32000, busy.GetProperty("error").GetProperty("code").GetInt32());
    await peer.DisposeAsync();
    await run;
  }

  [Fact]
  public async Task A_duplicate_id_in_flight_is_refused()
  {
    var release = new TaskCompletionSource();
    var (connection, peer) = Create(new JsonRpcConnectionOptions
    {
      RequestHandler = async (r, ct) =>
      {
        await release.Task.WaitAsync(ct);
        return new JsonRpcResultResponse(r.Id, Json.Parse("{}"));
      },
    });
    await using var _ = connection;
    var run = connection.RunAsync(TestContext.Current.CancellationToken);
    var reader = new LineReader(peer);

    await SendLineAsync(peer, """{"jsonrpc":"2.0","id":1,"method":"a"}""");
    await SendLineAsync(peer, """{"jsonrpc":"2.0","id":1,"method":"a"}""");
    var refused = await reader.ReadJsonAsync();
    release.SetResult();

    Assert.Equal(-32600, refused.GetProperty("error").GetProperty("code").GetInt32());
    await reader.ReadJsonAsync();
    await peer.DisposeAsync();
    await run;
  }

  [Fact]
  public async Task A_message_over_the_line_limit_closes_the_connection_with_an_error_first()
  {
    var (a, b) = PipeEnd.CreatePair();
    await using var connection = new JsonRpcConnection(new NewlineChannel(a, maxLineBytes: 200));
    var run = connection.RunAsync(TestContext.Current.CancellationToken);

    await SendLineAsync(b, "{\"jsonrpc\":\"2.0\",\"method\":\"" + new string('m', 500) + "\"}");
    var response = await new LineReader(b).ReadJsonAsync();

    Assert.Equal(JsonRpcCloseReason.MessageTooLong, await run);
    Assert.Equal(-32600, response.GetProperty("error").GetProperty("code").GetInt32());
  }

  [Fact]
  public async Task Concurrent_sends_never_interleave()
  {
    var (connection, peer) = Create();
    await using var _ = connection;
    var run = connection.RunAsync(TestContext.Current.CancellationToken);
    var padding = new string('p', 20_000);

    var sends = Enumerable.Range(0, 100).Select(i => Task.Run(async () =>
      await connection.SendNotificationAsync("n", Json.Parse("{\"i\":" + i + ",\"pad\":\"" + padding + "\"}"), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)).ToArray();
    var reader = new LineReader(peer);
    var seen = new HashSet<int>();
    for (var i = 0; i < 100; i++)
    {
      var message = await reader.ReadJsonAsync();
      Assert.Equal(padding, message.GetProperty("params").GetProperty("pad").GetString());
      seen.Add(message.GetProperty("params").GetProperty("i").GetInt32());
    }

    await Task.WhenAll(sends);
    Assert.Equal(100, seen.Count);
    await peer.DisposeAsync();
    await run;
  }

  [Fact]
  public async Task Disposing_stops_a_running_connection_and_cancels_handlers()
  {
    var entered = new TaskCompletionSource();
    var cancelled = new TaskCompletionSource();
    var (connection, peer) = Create(new JsonRpcConnectionOptions
    {
      RequestHandler = async (_, ct) =>
      {
        entered.SetResult();
        try
        {
          await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
          cancelled.SetResult();
          throw;
        }

        return null;
      },
    });
    var run = connection.RunAsync(TestContext.Current.CancellationToken);
    await SendLineAsync(peer, """{"jsonrpc":"2.0","id":1,"method":"wait"}""");
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    await connection.DisposeAsync();

    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    Assert.Equal(JsonRpcCloseReason.Cancelled, await run);
  }
}
