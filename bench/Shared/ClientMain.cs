using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Bench;

/// <summary>One connected client, as the measurement loop sees it.</summary>
internal interface IBenchClient : IAsyncDisposable
{
  /// <summary>Calls a tool with the standard small argument and discards the result.</summary>
  Task CallAsync(string tool);

  /// <summary>Calls a tool and returns the first text block.</summary>
  Task<string> TextAsync(string tool);

  /// <summary>Lists every tool.</summary>
  Task<int> ListAsync();
}

/// <summary>The client process: connect, warm up, time sequential calls, then run concurrent calls for a fixed time, and print one JSON line.</summary>
internal static class ClientMain
{
  public static async Task<int> RunAsync(string[] args, Func<string, Task<IBenchClient>> connect)
  {
    var server = Arg(args, "--server") ?? throw new ArgumentException("--server is required");
    var kind = Arg(args, "--kind") ?? "call";
    var seq = int.Parse(Arg(args, "--seq") ?? "2000", CultureInfo.InvariantCulture);
    var warmup = int.Parse(Arg(args, "--warmup") ?? "500", CultureInfo.InvariantCulture);
    var conc = int.Parse(Arg(args, "--conc") ?? "16", CultureInfo.InvariantCulture);
    var seconds = double.Parse(Arg(args, "--seconds") ?? "0", CultureInfo.InvariantCulture);

    await using var client = await connect(server).ConfigureAwait(false);
    Func<Task> call = kind switch
    {
      "call" => () => client.CallAsync("echo"),
      "text" => () => client.CallAsync("text1mb"),
      "image" => () => client.CallAsync("image1mb"),
      "list" => () => client.ListAsync(),
      _ => throw new ArgumentException("unknown --kind " + kind),
    };

    for (var i = 0; i < warmup; i++)
    {
      await call().ConfigureAwait(false);
    }

    var (allocBefore, _, _) = ParseStats(await client.TextAsync("bench_stats").ConfigureAwait(false));
    var clientBefore = GC.GetTotalAllocatedBytes(false);
    var ticks = new long[seq];
    for (var i = 0; i < seq; i++)
    {
      var start = Stopwatch.GetTimestamp();
      await call().ConfigureAwait(false);
      ticks[i] = Stopwatch.GetTimestamp() - start;
    }

    var clientAlloc = GC.GetTotalAllocatedBytes(false) - clientBefore;
    var (allocAfter, _, _) = ParseStats(await client.TextAsync("bench_stats").ConfigureAwait(false));
    Array.Sort(ticks);
    double ToMicros(long t) => t * 1_000_000.0 / Stopwatch.Frequency;
    var p50 = ToMicros(ticks[seq / 2]);
    var p90 = ToMicros(ticks[(int)(seq * 0.9)]);

    double perSecond = 0;
    long loadedCalls = 0;
    long serverCpuBefore = 0;
    var clientCpuBefore = 0.0;
    using var self = Process.GetCurrentProcess();
    if (seconds > 0)
    {
      serverCpuBefore = ParseStats(await client.TextAsync("bench_stats").ConfigureAwait(false)).CpuUs;
      self.Refresh();
      clientCpuBefore = self.TotalProcessorTime.TotalMilliseconds * 1000;
      var stop = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
      var started = Stopwatch.GetTimestamp();
      long total = 0;
      var workers = Enumerable.Range(0, conc).Select(async _ =>
      {
        long n = 0;
        while (Stopwatch.GetTimestamp() < stop)
        {
          await call().ConfigureAwait(false);
          n++;
        }

        Interlocked.Add(ref total, n);
      }).ToArray();
      await Task.WhenAll(workers).ConfigureAwait(false);
      loadedCalls = total;
      perSecond = total / ((Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency);
    }

    var (_, serverPeak, serverCpuAfter) = ParseStats(await client.TextAsync("bench_stats").ConfigureAwait(false));
    self.Refresh();
    var clientCpuPerCall = loadedCalls == 0 ? 0 : (self.TotalProcessorTime.TotalMilliseconds * 1000 - clientCpuBefore) / loadedCalls;
    var serverCpuPerCall = loadedCalls == 0 ? 0 : (double)(serverCpuAfter - serverCpuBefore) / loadedCalls;
    using var me = Process.GetCurrentProcess();
    var inv = CultureInfo.InvariantCulture;
    var json = new StringBuilder()
      .Append("{\"kind\":\"").Append(kind).Append('"')
      .Append(",\"seqCalls\":").Append(seq.ToString(inv))
      .Append(",\"p50Us\":").Append(p50.ToString("F1", inv))
      .Append(",\"p90Us\":").Append(p90.ToString("F1", inv))
      .Append(",\"concurrency\":").Append(conc.ToString(inv))
      .Append(",\"callsPerSec\":").Append(perSecond.ToString("F1", inv))
      .Append(",\"clientAllocBytesPerCall\":").Append((clientAlloc / seq).ToString(inv))
      .Append(",\"serverAllocBytesPerCall\":").Append(((allocAfter - allocBefore) / seq).ToString(inv))
      .Append(",\"clientCpuUsPerCall\":").Append(clientCpuPerCall.ToString("F1", inv))
      .Append(",\"serverCpuUsPerCall\":").Append(serverCpuPerCall.ToString("F1", inv))
      .Append(",\"clientPeakMb\":").Append((me.PeakWorkingSet64 / 1048576.0).ToString("F1", inv))
      .Append(",\"serverPeakMb\":").Append((serverPeak / 1048576.0).ToString("F1", inv))
      .Append('}');
    Console.WriteLine(json.ToString());
    return 0;
  }

  private static (long Alloc, long PeakWs, long CpuUs) ParseStats(string text)
  {
    var parts = text.Split(' ');
    return (long.Parse(parts[0], CultureInfo.InvariantCulture), long.Parse(parts[1], CultureInfo.InvariantCulture), long.Parse(parts[2], CultureInfo.InvariantCulture));
  }

  public static string? Arg(string[] args, string name)
  {
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
  }
}
