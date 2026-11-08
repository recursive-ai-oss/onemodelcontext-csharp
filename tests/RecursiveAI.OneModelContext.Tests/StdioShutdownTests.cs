using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

public class StdioShutdownTests
{
  [Fact]
  public async Task A_large_EOF_reply_survives_a_reader_starting_three_seconds_late()
  {
    var message = Encoding.UTF8.GetBytes("{\"text\":\"" + new string('x', 20_000_000) + "\"}");
    var output = new SlowReaderStream(64 * 1024);
    var channel = new NewlineChannel(new MemoryStream(), output, leaveOpen: true);
    await channel.WriteAsync(message, TestContext.Current.CancellationToken);
    await output.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    var disposing = channel.DisposeAsync().AsTask();
    await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    Assert.False(disposing.IsCompleted);
    output.Open();
    await disposing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    var bytes = output.ToArray();
    Assert.Equal(message.Length + 1, bytes.Length);
    Assert.Equal((byte)'\n', bytes[^1]);
    Assert.Equal(20_000_000, Json.Parse(Encoding.UTF8.GetString(bytes)).GetProperty("text").GetString()!.Length);
  }

  [Theory]
  [InlineData("2024-11-05")]
  [InlineData("2025-03-26")]
  [InlineData("2025-06-18")]
  [InlineData("2025-11-25")]
  public async Task Pipelined_legacy_initialize_is_a_barrier_for_later_requests(string version)
  {
    for (var attempt = 0; attempt < 10; attempt++)
    {
      var input = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"" + version + "\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"}}}\n" +
        "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}\n{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"ping\"}\n";
      var output = new MemoryStream();
      await new ContextServer("test", "1").RunAsync(ContextTransport.Stream(new MemoryStream(Encoding.UTF8.GetBytes(input)), output, leaveOpen: true), TestContext.Current.CancellationToken);
      var replies = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Json.Parse).ToArray();
      Assert.Equal(3, replies.Length);
      Assert.All(replies, reply => Assert.True(reply.TryGetProperty("result", out _), reply.GetRawText()));
    }
  }

  [Fact]
  public async Task A_peer_that_does_not_read_backpressures_the_input_loop()
  {
    var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, 100_000).Select(i => "{\"jsonrpc\":\"2.0\",\"id\":" + i + ",\"method\":\"unknown\"}\n"))));
    var output = new SlowReaderStream(0);
    await using var connection = new JsonRpcConnection(new NewlineChannel(input, output, leaveOpen: true));
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    var run = connection.RunAsync(stop.Token);
    await output.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5), stop.Token);
    // Give the producer time to fill its queue, then verify a bounded prefix was read and consumption stops completely.
    await Task.Delay(500, stop.Token);
    var first = input.Position;
    await Task.Delay(200, stop.Token);
    Assert.Equal(first, input.Position);
    Assert.True(first < input.Length / 2, "The read loop consumed the flood despite a blocked output.");
    stop.Cancel();
    output.Open();
    await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
  }

  [Fact]
  public async Task Concurrent_random_sized_writes_and_disposal_never_corrupt_or_duplicate_messages()
  {
    var output = new SlowReaderStream(0);
    var channel = new NewlineChannel(new MemoryStream(), output, leaveOpen: true);
    var accepted = new ConcurrentDictionary<int, byte>();
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var writers = Enumerable.Range(0, 16).Select(writer => Task.Run(async () =>
    {
      var random = new Random(writer);
      for (var i = 0; i < 200; i++)
      {
        var id = writer * 200 + i;
        var message = Encoding.UTF8.GetBytes("{\"id\":" + id + ",\"text\":\"" + new string('x', random.Next(1, 32768)) + "\"}");
        try
        {
          await channel.WriteAsync(message, TestContext.Current.CancellationToken);
          accepted.TryAdd(id, 0);
          if (accepted.Count >= 50)
          {
            ready.TrySetResult();
          }
        }
        catch (ObjectDisposedException)
        {
          break;
        }
      }
    }, TestContext.Current.CancellationToken)).ToArray();
    await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    await output.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    var disposing = channel.DisposeAsync().AsTask();
    output.Open();
    await disposing;
    await Task.WhenAll(writers);
    var ids = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => Json.Parse(line).GetProperty("id").GetInt32()).ToArray();
    Assert.Equal(ids.Length, ids.Distinct().Count());
    Assert.All(accepted.Keys, id => Assert.Contains(id, ids));
  }

  [Theory]
  [InlineData("2026-07-28")]
  [InlineData("2025-11-25")]
  [InlineData("2025-06-18")]
  [InlineData("2025-03-26")]
  [InlineData("2024-11-05")]
  public async Task Invalid_tool_name_encoding_is_invalid_params_not_a_handler_fault(string version)
  {
    var faults = new ConcurrentQueue<DiagnosticEvent>();
    var ownRequest = new AsyncLocal<bool> { Value = true };
    using var listener = ContextDiagnostics.Subscribe(e => { if (ownRequest.Value && e.Kind == DiagnosticKind.HandlerFault) faults.Enqueue(e); });
    var server = new ContextServer("test", "1");
    var session = new RecursiveAI.OneModelContext.Wire.ServerSessionState();
    if (version != "2026-07-28")
    {
      await server.HandleMessageAsync("{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"" + version + "\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"}}}", session, cancellationToken: TestContext.Current.CancellationToken);
    }

    foreach (var invalid in new[] { "bad\\ud800", "bad\\udc00" })
    {
      var meta = version == "2026-07-28" ? ",\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{}}" : "";
      var request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + invalid + "\"" + meta + "}}";
      var result = await server.HandleMessageAsync(request, session, cancellationToken: TestContext.Current.CancellationToken);
      Assert.Equal(-32602, Json.Parse(result!).GetProperty("error").GetProperty("code").GetInt32());
    }

    var metadata = version == "2026-07-28" ? ",\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{}}" : "";
    var requestBytes = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"bad?\"" + metadata + "}}");
    requestBytes[Array.IndexOf(requestBytes, (byte)'?')] = 0xFF;
    var decoded = Assert.IsType<JsonRpcRequest>(JsonRpcReader.Read(requestBytes).Message);
    var response = await server.DispatchRequestAsync(decoded, session, new RecordingSink(), null, false, TestContext.Current.CancellationToken);
    Assert.Equal(-32602, Assert.IsType<JsonRpcErrorResponse>(response).Error.Code);
    Assert.Empty(faults);
  }

  private sealed class RecordingSink : RecursiveAI.OneModelContext.Wire.IServerSink
  {
    public ValueTask SendNotificationAsync(JsonRpcNotification notification, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask<System.Text.Json.JsonElement> SendRequestAsync(string method, System.Text.Json.JsonElement parameters, CancellationToken cancellationToken) => throw new NotSupportedException();
  }

  private sealed class SlowReaderStream(int prefix) : MemoryStream
  {
    private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal void Open() => _open.TrySetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
      var first = (int)Math.Min(buffer.Length, Math.Max(0, prefix - Length));
      if (first > 0)
      {
        await base.WriteAsync(buffer[..first], cancellationToken);
      }

      if (first < buffer.Length)
      {
        Blocked.TrySetResult();
        await _open.Task.WaitAsync(cancellationToken);
        await base.WriteAsync(buffer[first..], cancellationToken);
      }
    }
  }
}
