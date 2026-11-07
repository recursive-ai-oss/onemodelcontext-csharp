using System.Diagnostics;
using System.Text.Json;

namespace Bench;

/// <summary>Starting and stopping the bench apps.</summary>
internal static class Procs
{
  public static string Exe(string bin, string stack) => Path.Combine(bin, stack, stack == "ours" || stack == "ours-aot" ? "omc-bench" : "ref-bench");

  public static Process Start(string exe, string args, bool redirectInput = false)
  {
    var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardInput = redirectInput, RedirectStandardError = true, UseShellExecute = false };
    psi.Environment["DOTNET_TieredPGO"] = "1";
    return Process.Start(psi) ?? throw new InvalidOperationException("cannot start " + exe);
  }

  /// <summary>Runs a client process to completion and returns its JSON line.</summary>
  public static JsonElement RunClient(string bin, string stack, string server, string kind, int seq, int conc, double seconds, int warmup)
  {
    using var p = Start(Exe(bin, stack), $"client --server \"{server}\" --kind {kind} --seq {seq} --conc {conc} --seconds {seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} --warmup {warmup}");
    var err = p.StandardError.ReadToEndAsync();
    var output = p.StandardOutput.ReadToEnd();
    if (!p.WaitForExit(600_000))
    {
      p.Kill(true);
      throw new TimeoutException("client did not finish: " + server);
    }

    var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
    if (p.ExitCode != 0 || line is null || !line.StartsWith('{'))
    {
      throw new InvalidOperationException($"client {stack} against {server} failed (exit {p.ExitCode}): {Tail(err.Result)} {Tail(output)}");
    }

    return JsonDocument.Parse(line).RootElement.Clone();
  }

  /// <summary>Starts an HTTP server app and waits for its LISTENING line.</summary>
  public static (Process Process, string Url) StartHttp(string bin, string stack, string mode, int filler = 0)
  {
    var p = Start(Exe(bin, stack), $"server {mode} --filler {filler}");
    var task = p.StandardOutput.ReadLineAsync();
    if (!task.Wait(60_000) || task.Result is not { } line || !line.StartsWith("LISTENING ", StringComparison.Ordinal))
    {
      p.Kill(true);
      throw new InvalidOperationException($"{stack} {mode} server did not start: {Tail(p.StandardError.ReadToEnd())}");
    }

    var url = line["LISTENING ".Length..].Trim();
    _ = p.StandardOutput.ReadToEndAsync();
    _ = p.StandardError.ReadToEndAsync();
    return (p, url);
  }

  public static void Stop(Process p)
  {
    try
    {
      if (!p.HasExited)
      {
        p.Kill(true);
      }
    }
    catch (InvalidOperationException)
    {
      // Already gone.
    }

    p.Dispose();
  }

  /// <summary>The server's peak resident memory in MiB, from /proc (Linux).</summary>
  public static double PeakMb(Process p)
  {
    try
    {
      foreach (var line in File.ReadLines($"/proc/{p.Id}/status"))
      {
        if (line.StartsWith("VmHWM:", StringComparison.Ordinal))
        {
          return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], System.Globalization.CultureInfo.InvariantCulture) / 1024.0;
        }
      }
    }
    catch (IOException)
    {
      // No /proc: report nothing.
    }

    return 0;
  }

  private static string Tail(string text) => text.Length <= 400 ? text : text[^400..];
}
