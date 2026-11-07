using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.JsonRpc;

namespace RecursiveAI.OneModelContext;

/// <summary>How <see cref="ContextTransport.Process(string, System.Collections.Generic.IEnumerable{string}?, ProcessTransportOptions?)"/> starts and stops a server process.</summary>
public sealed class ProcessTransportOptions
{
  /// <summary>Environment variables for the child, added to (or, with a null value, removed from) the ones this process has. Default: none.</summary>
  public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

  /// <summary>The child's working directory. Default: this process's.</summary>
  public string? WorkingDirectory { get; init; }

  /// <summary>The longest message accepted from the server, in bytes. Default 8 MiB.</summary>
  public int MaxLineBytes { get; init; } = NewlineChannel.DefaultMaxLineBytes;

  /// <summary>
  /// How long to wait for the server to exit after its input is closed before ending it forcibly. The stdio transports ask a server to
  /// exit by closing its standard input. Default 5 seconds.
  /// </summary>
  public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(5);

  /// <summary>
  /// Receives each line the server writes to standard error (at most 4096 characters per call). Default: the line goes to
  /// <see cref="ContextDiagnostics"/> as a note. Standard error is the server's own log; it is never treated as an error signal.
  /// </summary>
  public Action<string>? StandardErrorHandler { get; init; }

  /// <summary>The clock for the shutdown wait. Default <see cref="TimeProvider.System"/>.</summary>
  public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>
/// The client side of the stdio transport: starts the server as a child process and speaks newline-delimited JSON-RPC over its standard
/// input and output (2026-07-28 and 2025-11-25 "stdio"). Standard error is forwarded, not interpreted.
/// </summary>
internal sealed class ProcessTransport : ContextTransport
{
  private const int MaxStderrLine = 4096;

  private readonly string _command;
  private readonly string[] _arguments;
  private readonly ProcessTransportOptions _options;
  private int _opened;

  internal ProcessTransport(string command, IEnumerable<string> arguments, ProcessTransportOptions options)
  {
    _command = command;
    _arguments = [.. arguments];
    _options = options;
  }

  public override string? ServerKey =>
    "process:" + _command + (_arguments.Length == 0 ? string.Empty : " " + string.Join(' ', _arguments)) + (_options.WorkingDirectory is null ? string.Empty : " @" + _options.WorkingDirectory);

  public override ValueTask<IJsonRpcChannel> OpenAsync(CancellationToken cancellationToken)
  {
    if (Interlocked.Exchange(ref _opened, 1) != 0)
    {
      throw new InvalidOperationException("The transport was already opened.");
    }

    var start = new ProcessStartInfo(_command)
    {
      RedirectStandardInput = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
      WorkingDirectory = _options.WorkingDirectory ?? string.Empty,
    };
    foreach (var argument in _arguments)
    {
      start.ArgumentList.Add(argument);
    }

    foreach (var (name, value) in _options.Environment)
    {
      if (value is null)
      {
        start.Environment.Remove(name);
      }
      else
      {
        start.Environment[name] = value;
      }
    }

    System.Diagnostics.Process process;
    try
    {
      process = System.Diagnostics.Process.Start(start) ?? throw new TransportException("The server process '" + _command + "' did not start.");
    }
    catch (System.ComponentModel.Win32Exception ex)
    {
      throw new TransportException("The server process '" + _command + "' could not be started: " + ex.Message, ex);
    }

    var stderr = PumpStandardErrorAsync(process.StandardError.BaseStream, _options.StandardErrorHandler);
    var channel = new NewlineChannel(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, _options.MaxLineBytes, leaveOpen: true);
    return ValueTask.FromResult<IJsonRpcChannel>(new ProcessChannel(process, channel, stderr, _options));
  }

  // Reads standard error in bounded pieces so a server that never writes a newline cannot grow our memory.
  private static async Task PumpStandardErrorAsync(Stream stderr, Action<string>? handler)
  {
    var buffer = new byte[MaxStderrLine];
    var decoder = Encoding.UTF8.GetDecoder();
    var line = new StringBuilder();
    var chars = new char[MaxStderrLine];
    try
    {
      while (true)
      {
        var read = await stderr.ReadAsync(buffer).ConfigureAwait(false);
        if (read == 0)
        {
          break;
        }

        var count = decoder.GetChars(buffer, 0, read, chars, 0, flush: false);
        for (var i = 0; i < count; i++)
        {
          var c = chars[i];
          if (c == '\n')
          {
            Emit(line, handler);
          }
          else if (c != '\r')
          {
            line.Append(c);
            if (line.Length >= MaxStderrLine)
            {
              Emit(line, handler);
            }
          }
        }
      }

      Emit(line, handler);
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
    {
      // The process is gone.
    }
  }

  private static void Emit(StringBuilder line, Action<string>? handler)
  {
    if (line.Length == 0)
    {
      return;
    }

    var text = line.ToString();
    line.Clear();
    try
    {
      if (handler is not null)
      {
        handler(text);
      }
      else
      {
        ContextDiagnostics.Publish(DiagnosticKind.Note, "server stderr: " + text);
      }
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
      // A faulty handler must not stop the pump.
    }
  }

  /// <summary>The channel of a child process; disposing it shuts the process down.</summary>
  private sealed class ProcessChannel(System.Diagnostics.Process process, NewlineChannel inner, Task stderr, ProcessTransportOptions options) : IJsonRpcChannel
  {
    private int _disposed;

    public ValueTask<JsonRpcChannelRead> ReadAsync(CancellationToken cancellationToken) => inner.ReadAsync(cancellationToken);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken) => inner.WriteAsync(message, cancellationToken);

    // stdio "Shutdown": close the server's input, wait for it to exit, and end it forcibly if it does not. .NET has no portable way to
    // send a termination request to another process, so the forced step is Kill (SIGKILL on Unix, TerminateProcess on Windows).
    public async ValueTask DisposeAsync()
    {
      if (Interlocked.Exchange(ref _disposed, 1) != 0)
      {
        return;
      }

      await inner.DisposeAsync().ConfigureAwait(false);
      try
      {
        process.StandardInput.BaseStream.Close();
      }
      catch (Exception ex) when (ex is IOException or ObjectDisposedException)
      {
        // The server already closed its end.
      }

      // Keep draining standard output so a server that is still writing can reach its exit.
      var drain = DrainAsync(process.StandardOutput.BaseStream);
      using var grace = new CancellationTokenSource(options.ShutdownGrace, options.TimeProvider);
      try
      {
        await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        TryKill();
        using var last = new CancellationTokenSource(TimeSpan.FromSeconds(5), options.TimeProvider);
        try
        {
          await process.WaitForExitAsync(last.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
          // Nothing more can be done for a process that will not die.
        }
      }

      await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(1), options.TimeProvider)).ConfigureAwait(false);
      await Task.WhenAny(stderr, Task.Delay(TimeSpan.FromSeconds(1), options.TimeProvider)).ConfigureAwait(false);
      process.Dispose();
    }

    private void TryKill()
    {
      try
      {
        process.Kill(entireProcessTree: true);
      }
      catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
      {
        // It exited between the check and the kill.
      }
    }

    private static async Task DrainAsync(Stream stdout)
    {
      try
      {
        await stdout.CopyToAsync(System.IO.Stream.Null).ConfigureAwait(false);
      }
      catch (Exception ex) when (ex is IOException or ObjectDisposedException)
      {
        // The process is gone.
      }
    }
  }
}
