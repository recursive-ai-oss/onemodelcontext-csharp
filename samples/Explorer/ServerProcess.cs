using System.Diagnostics;
using RecursiveAI.OneModelContext;

namespace Explorer;

/// <summary>
/// Starts a server as a child process and talks to it over its standard input and output, the way hosts launch stdio servers. Its
/// standard error is passed through to ours so the server's diagnostics stay visible.
/// </summary>
public sealed class ServerProcess : IAsyncDisposable
{
  private readonly Process _process;

  private ServerProcess(Process process) => _process = process;

  /// <summary>The transport that talks to the child.</summary>
  public ContextTransport Transport => ContextTransport.Stream(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream);

  /// <summary>Starts the command.</summary>
  /// <param name="fileName">The program to run.</param>
  /// <param name="arguments">Its arguments.</param>
  public static ServerProcess Start(string fileName, IEnumerable<string> arguments)
  {
    var info = new ProcessStartInfo(fileName) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
    foreach (var argument in arguments)
    {
      info.ArgumentList.Add(argument);
    }

    return new ServerProcess(Process.Start(info) ?? throw new InvalidOperationException($"Could not start '{fileName}'."));
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync()
  {
    // Closing the child's input is the polite way to stop a stdio server; kill it only if it ignores that.
    try
    {
      _process.StandardInput.Close();
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
    {
      // The client already closed the pipe when it was disposed, which is the same signal.
    }

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    try
    {
      await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      _process.Kill(entireProcessTree: true);
    }

    _process.Dispose();
  }
}
