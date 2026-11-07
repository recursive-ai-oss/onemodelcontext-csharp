using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;

namespace RecursiveAI.OneModelContext;

/// <summary>
/// A way to exchange whole JSON-RPC messages with a peer. <see cref="Stdio"/> and <see cref="Stream(System.IO.Stream, int, bool)"/> are
/// newline-delimited; other transports implement <see cref="OpenAsync"/> over their own framing.
/// </summary>
public abstract class ContextTransport : IAsyncDisposable
{
  /// <summary>
  /// Newline-delimited JSON-RPC over this process's standard input and output, the MCP stdio transport. Standard output carries protocol
  /// messages only, so opening the transport points <see cref="Console.Out"/> at standard error: a stray <c>Console.WriteLine</c> then cannot
  /// corrupt the stream.
  /// </summary>
  /// <param name="maxLineBytes">The longest message accepted. Default 8 MiB.</param>
  public static ContextTransport Stdio(int maxLineBytes = NewlineChannel.DefaultMaxLineBytes) => new StdioTransport(maxLineBytes);

  /// <summary>Newline-delimited JSON-RPC over a duplex stream: a socket, a pipe, an in-memory pair.</summary>
  /// <param name="duplex">A stream that reads and writes.</param>
  /// <param name="maxLineBytes">The longest message accepted. Default 8 MiB. A longer message closes the connection.</param>
  /// <param name="leaveOpen">Leave the stream open when the connection ends.</param>
  public static ContextTransport Stream(Stream duplex, int maxLineBytes = NewlineChannel.DefaultMaxLineBytes, bool leaveOpen = false)
  {
    ArgumentNullException.ThrowIfNull(duplex);
    return new StreamTransport(duplex, duplex, maxLineBytes, leaveOpen);
  }

  /// <summary>Newline-delimited JSON-RPC over separate input and output streams (the pipes of a child process).</summary>
  /// <param name="input">Where messages are read from.</param>
  /// <param name="output">Where messages are written.</param>
  /// <param name="maxLineBytes">The longest message accepted. Default 8 MiB.</param>
  /// <param name="leaveOpen">Leave the streams open when the connection ends.</param>
  public static ContextTransport Stream(Stream input, Stream output, int maxLineBytes = NewlineChannel.DefaultMaxLineBytes, bool leaveOpen = false)
  {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    return new StreamTransport(input, output, maxLineBytes, leaveOpen);
  }

  /// <summary>Opens the channel. A transport can be opened once.</summary>
  /// <param name="cancellationToken">Cancels the open.</param>
  /// <exception cref="InvalidOperationException">The transport was already opened.</exception>
  public abstract ValueTask<IJsonRpcChannel> OpenAsync(CancellationToken cancellationToken);

  /// <summary>Releases what the transport holds when it was never opened. An opened channel is owned by its connection.</summary>
  public virtual ValueTask DisposeAsync()
  {
    GC.SuppressFinalize(this);
    return ValueTask.CompletedTask;
  }

  private sealed class StreamTransport(Stream input, Stream output, int maxLineBytes, bool leaveOpen) : ContextTransport
  {
    private int _opened;

    public override ValueTask<IJsonRpcChannel> OpenAsync(CancellationToken cancellationToken)
    {
      if (Interlocked.Exchange(ref _opened, 1) != 0)
      {
        throw new InvalidOperationException("The transport was already opened.");
      }

      return ValueTask.FromResult<IJsonRpcChannel>(new NewlineChannel(input, output, maxLineBytes, leaveOpen));
    }
  }

  private sealed class StdioTransport(int maxLineBytes) : ContextTransport
  {
    private int _opened;

    public override ValueTask<IJsonRpcChannel> OpenAsync(CancellationToken cancellationToken)
    {
      if (Interlocked.Exchange(ref _opened, 1) != 0)
      {
        throw new InvalidOperationException("The transport was already opened.");
      }

      var input = Console.OpenStandardInput();
      var output = Console.OpenStandardOutput();
      Console.SetOut(Console.Error);
      return ValueTask.FromResult<IJsonRpcChannel>(new NewlineChannel(input, output, maxLineBytes, leaveOpen: false));
    }
  }
}
