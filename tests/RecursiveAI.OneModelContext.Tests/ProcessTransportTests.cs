using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

public class ProcessTransportTests
{
  private static void RequirePosix() => Assert.SkipWhen(OperatingSystem.IsWindows(), "These tests use sh and cat.");

  [Fact]
  public async Task Messages_go_to_the_childs_input_and_come_back_from_its_output()
  {
    RequirePosix();
    var transport = ContextTransport.Process("cat");
    var channel = await transport.OpenAsync(TestContext.Current.CancellationToken);

    await channel.WriteAsync(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"x"}"""), TestContext.Current.CancellationToken);
    var read = await channel.ReadAsync(TestContext.Current.CancellationToken);

    Assert.Equal(JsonRpcChannelStatus.Message, read.Status);
    Assert.Equal("""{"jsonrpc":"2.0","method":"x"}""", Encoding.UTF8.GetString(read.Message.Span));
    await channel.DisposeAsync();
  }

  [Fact]
  public async Task Disposing_closes_the_input_and_a_well_behaved_child_exits_without_being_killed()
  {
    RequirePosix();
    var transport = ContextTransport.Process("cat", options: new ProcessTransportOptions { ShutdownGrace = TimeSpan.FromSeconds(20) });
    var channel = await transport.OpenAsync(TestContext.Current.CancellationToken);
    var stopwatch = Stopwatch.StartNew();

    await channel.DisposeAsync();

    Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), "cat exits as soon as its input closes");
  }

  [Fact]
  public async Task A_child_that_ignores_the_closed_input_is_ended_after_the_grace_period()
  {
    RequirePosix();
    var transport = ContextTransport.Process("sh", ["-c", "sleep 60"], new ProcessTransportOptions { ShutdownGrace = TimeSpan.FromMilliseconds(300) });
    var channel = await transport.OpenAsync(TestContext.Current.CancellationToken);
    var stopwatch = Stopwatch.StartNew();

    await channel.DisposeAsync();

    Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20));
  }

  [Fact]
  public async Task Standard_error_goes_to_the_handler_line_by_line_and_never_into_the_protocol()
  {
    RequirePosix();
    var lines = new List<string>();
    var transport = ContextTransport.Process(
      "sh",
      ["-c", "echo first >&2; echo second >&2; echo '{\"jsonrpc\":\"2.0\",\"method\":\"x\"}'"],
      new ProcessTransportOptions { StandardErrorHandler = line => { lock (lines) { lines.Add(line); } } });
    var channel = await transport.OpenAsync(TestContext.Current.CancellationToken);

    var read = await channel.ReadAsync(TestContext.Current.CancellationToken);
    var text = Encoding.UTF8.GetString(read.Message.Span);
    var end = await channel.ReadAsync(TestContext.Current.CancellationToken);
    await channel.DisposeAsync();

    Assert.Equal("""{"jsonrpc":"2.0","method":"x"}""", text);
    Assert.Equal(JsonRpcChannelStatus.EndOfStream, end.Status);
    lock (lines)
    {
      Assert.Equal(["first", "second"], lines);
    }
  }

  [Fact]
  public async Task A_very_long_standard_error_line_is_delivered_in_bounded_pieces()
  {
    RequirePosix();
    var pieces = new List<int>();
    var transport = ContextTransport.Process(
      "sh",
      ["-c", "head -c 20000 /dev/zero | tr '\\0' 'x' >&2"],
      new ProcessTransportOptions { StandardErrorHandler = line => { lock (pieces) { pieces.Add(line.Length); } } });
    var channel = await transport.OpenAsync(TestContext.Current.CancellationToken);

    await channel.ReadAsync(TestContext.Current.CancellationToken);
    await channel.DisposeAsync();

    lock (pieces)
    {
      Assert.All(pieces, length => Assert.True(length <= 4096));
      Assert.Equal(20000, System.Linq.Enumerable.Sum(pieces));
    }
  }

  [Fact]
  public async Task Environment_and_working_directory_reach_the_child()
  {
    RequirePosix();
    var directory = Path.GetTempPath();
    var transport = ContextTransport.Process(
      "sh",
      ["-c", "printf '%s|%s\\n' \"$ONEMODELCONTEXT_TEST\" \"$(pwd)\""],
      new ProcessTransportOptions { Environment = new Dictionary<string, string?> { ["ONEMODELCONTEXT_TEST"] = "hello" }, WorkingDirectory = directory });
    var channel = await transport.OpenAsync(TestContext.Current.CancellationToken);

    var read = await channel.ReadAsync(TestContext.Current.CancellationToken);
    var text = Encoding.UTF8.GetString(read.Message.Span);
    await channel.DisposeAsync();

    Assert.StartsWith("hello|", text, StringComparison.Ordinal);
    Assert.Equal(Path.GetFullPath(directory).TrimEnd('/'), text["hello|".Length..].TrimEnd('/'));
  }

  [Fact]
  public async Task A_command_that_does_not_exist_is_a_transport_error()
  {
    var transport = ContextTransport.Process("definitely-not-a-program-onemodelcontext");

    await Assert.ThrowsAsync<TransportException>(async () => await transport.OpenAsync(TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task A_child_that_exits_ends_the_stream()
  {
    RequirePosix();
    var transport = ContextTransport.Process("sh", ["-c", "exit 3"]);
    var channel = await transport.OpenAsync(TestContext.Current.CancellationToken);

    var read = await channel.ReadAsync(TestContext.Current.CancellationToken);
    await channel.DisposeAsync();

    Assert.Equal(JsonRpcChannelStatus.EndOfStream, read.Status);
  }

  [Fact]
  public async Task A_transport_opens_once()
  {
    RequirePosix();
    var transport = ContextTransport.Process("cat");
    var channel = await transport.OpenAsync(TestContext.Current.CancellationToken);

    await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.OpenAsync(TestContext.Current.CancellationToken));
    await channel.DisposeAsync();
  }

  [Fact]
  public void The_server_key_names_the_command_line_and_directory()
  {
    var a = ContextTransport.Process("node", ["server.js", "--x"]);
    var b = ContextTransport.Process("node", ["server.js", "--x"], new ProcessTransportOptions { WorkingDirectory = "/srv" });

    Assert.Equal("process:node server.js --x", a.ServerKey);
    Assert.NotEqual(a.ServerKey, b.ServerKey);
    Assert.Null(ContextTransport.Stream(new MemoryStream()).ServerKey);
  }
}
