using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

public class EndOfStreamTests
{
  [Theory]
  [InlineData(1)]
  [InlineData(8)]
  public async Task Input_EOF_after_requests_writes_every_response(int count)
  {
    var messages = string.Join('\n', Enumerable.Range(1, count).Select(i => "{\"jsonrpc\":\"2.0\",\"id\":" + i + ",\"method\":\"echo\"}")) +
      "\n{\"jsonrpc\":\"2.0\",\"method\":\"notice\"}\n";
    var output = new MemoryStream();
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var connection = new JsonRpcConnection(new NewlineChannel(new MemoryStream(Encoding.UTF8.GetBytes(messages)), output, leaveOpen: true), new()
    {
      RequestHandler = async (request, ct) =>
      {
        await release.Task.WaitAsync(ct);
        return new JsonRpcResultResponse(request.Id, Json.Parse("{}"));
      },
    });
    var run = connection.RunAsync(TestContext.Current.CancellationToken);
    release.SetResult();
    Assert.Equal(JsonRpcCloseReason.EndOfStream, await run);
    await connection.DisposeAsync();
    var lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Assert.Equal(count, lines.Length);
    Assert.Equal(Enumerable.Range(1, count), lines.Select(line => Json.Parse(line).GetProperty("id").GetInt32()).Order());
  }

  [Theory]
  [InlineData("2026-07-28")]
  [InlineData("2025-11-25")]
  [InlineData("2025-06-18")]
  [InlineData("2025-03-26")]
  [InlineData("2024-11-05")]
  public async Task Server_answers_discovery_or_initialize_before_exiting_on_EOF(string version)
  {
    var message = version == "2026-07-28"
      ? """{"jsonrpc":"2.0","id":1,"method":"server/discover","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}"""
      : "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"" + version + "\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"}}}";
    var output = new MemoryStream();
    var server = new ContextServer("test", "1");
    await server.RunAsync(ContextTransport.Stream(new MemoryStream(Encoding.UTF8.GetBytes(message + "\n")), output, leaveOpen: true), TestContext.Current.CancellationToken);
    var result = Json.Parse(Encoding.UTF8.GetString(output.ToArray())).GetProperty("result");
    if (version == "2026-07-28")
    {
      Assert.Equal("complete", result.GetProperty("resultType").GetString());
    }
    else
    {
      Assert.Equal(version, result.GetProperty("protocolVersion").GetString());
    }
  }

  [Fact]
  public async Task EOF_grace_cancels_a_waiting_handler_when_the_injected_clock_expires()
  {
    var clock = new ManualTimeProvider();
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var connection = new JsonRpcConnection(new NewlineChannel(new MemoryStream("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"wait\"}\n"u8.ToArray()), new MemoryStream()), new()
    {
      TimeProvider = clock,
      RequestHandler = async (_, ct) =>
      {
        started.SetResult();
        try
        {
          await Task.Delay(Timeout.Infinite, ct);
        }
        finally
        {
          cancelled.SetResult();
        }

        return null;
      },
    });
    var run = connection.RunAsync(TestContext.Current.CancellationToken);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    Assert.False(cancelled.Task.IsCompleted);
    clock.Advance(TimeSpan.FromSeconds(5));
    Assert.Equal(JsonRpcCloseReason.EndOfStream, await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    Assert.True(cancelled.Task.IsCompleted);
  }

  [Fact]
  public async Task EOF_shutdown_abandons_a_handler_that_ignores_cancellation()
  {
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var connection = new JsonRpcConnection(new NewlineChannel(new MemoryStream("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"wait\"}\n"u8.ToArray()), new MemoryStream()), new()
    {
      EndOfStreamTimeout = TimeSpan.Zero,
      ShutdownTimeout = TimeSpan.Zero,
      RequestHandler = async (_, _) => { await release.Task; return null; },
    });
    try
    {
      Assert.Equal(JsonRpcCloseReason.EndOfStream, await connection.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }
    finally
    {
      release.SetResult();
    }
  }
}
