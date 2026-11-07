using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

  /// <summary>
  /// Starts a server as a child process and speaks newline-delimited JSON-RPC over its standard input and output (the MCP stdio
  /// transport, client side). The child's standard error goes to <see cref="Diagnostics.ContextDiagnostics"/>. Disposing the client closes
  /// the child's input, waits <see cref="ProcessTransportOptions.ShutdownGrace"/> for it to exit, then ends it.
  /// </summary>
  /// <param name="command">The executable, found on <c>PATH</c> if it has no directory. No shell is involved, so no quoting or expansion applies.</param>
  /// <param name="arguments">The arguments, passed as given.</param>
  /// <param name="options">Environment, working directory and limits; <c>null</c> for the defaults.</param>
  /// <exception cref="ArgumentException"><paramref name="command"/> is empty.</exception>
  public static ContextTransport Process(string command, IEnumerable<string>? arguments = null, ProcessTransportOptions? options = null)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(command);
    return new ProcessTransport(command, arguments ?? [], options ?? new ProcessTransportOptions());
  }

  /// <summary>
  /// Like <see cref="Process(string, IEnumerable{string}?, ProcessTransportOptions?)"/> for a command line such as
  /// <c>npx -y some-server --flag "a b"</c>. Words split on spaces; single or double quotes group a word; inside double quotes a backslash
  /// escapes a quote or a backslash. No shell runs, so there is no variable expansion, globbing or piping.
  /// </summary>
  /// <param name="commandLine">The command and its arguments.</param>
  /// <param name="options">Environment, working directory and limits; <c>null</c> for the defaults.</param>
  /// <exception cref="ArgumentException">The command line is empty or has an unclosed quote.</exception>
  public static ContextTransport ProcessFromCommandLine(string commandLine, ProcessTransportOptions? options = null)
  {
    var words = SplitCommandLine(commandLine);
    return Process(words[0], words.Skip(1), options);
  }

  /// <summary>
  /// Names the server this transport reaches (the command line, or the origin of a URL) so that a client can remember which era it spoke
  /// with it. <c>null</c> means the transport cannot say, and nothing is remembered.
  /// </summary>
  public virtual string? ServerKey => null;

  internal static string[] SplitCommandLine(string commandLine)
  {
    ArgumentNullException.ThrowIfNull(commandLine);
    var words = new List<string>();
    var word = new StringBuilder();
    var inWord = false;
    var quote = '\0';
    for (var i = 0; i < commandLine.Length; i++)
    {
      var c = commandLine[i];
      if (quote != '\0')
      {
        if (c == quote)
        {
          quote = '\0';
        }
        else if (quote == '"' && c == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] is '"' or '\\')
        {
          word.Append(commandLine[++i]);
        }
        else
        {
          word.Append(c);
        }
      }
      else if (c is '"' or '\'')
      {
        quote = c;
        inWord = true;
      }
      else if (char.IsWhiteSpace(c))
      {
        if (inWord)
        {
          words.Add(word.ToString());
          word.Clear();
          inWord = false;
        }
      }
      else
      {
        word.Append(c);
        inWord = true;
      }
    }

    if (quote != '\0')
    {
      throw new ArgumentException("The command line has an unclosed quote.", nameof(commandLine));
    }

    if (inWord)
    {
      words.Add(word.ToString());
    }

    return words.Count == 0 ? throw new ArgumentException("The command line is empty.", nameof(commandLine)) : [.. words];
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
