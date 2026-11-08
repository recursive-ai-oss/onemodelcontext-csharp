using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Bench;

/// <summary>One line of the result table. Ratio is our cost over the reference's: below 1.0 means we are cheaper.</summary>
internal sealed record Row(string Id, string Name, string Unit, bool Gated, double Ours, double? Reference, bool HigherIsBetter, string Detail)
{
  public double? Ratio => Reference is null || Ours <= 0 || Reference <= 0 ? null : HigherIsBetter ? Reference / Ours : Ours / Reference;
}

internal static class Program
{
  private static readonly List<Row> Rows = [];
  private static readonly List<string> Notes = [];

  private static async Task<int> Main(string[] args)
  {
    var bin = Arg(args, "--bin") ?? throw new ArgumentException("--bin DIR is required");
    var outDir = Arg(args, "--out") ?? ".";
    var rows = (Arg(args, "--rows") ?? "all").Split(',');
    var seconds = double.Parse(Arg(args, "--seconds") ?? "4", CultureInfo.InvariantCulture);
    var seq = int.Parse(Arg(args, "--seq") ?? "2000", CultureInfo.InvariantCulture);
    var referenceVersion = Arg(args, "--ref-version") ?? "unknown";
    Directory.CreateDirectory(outDir);
    bool Want(string id) => rows.Contains("all") || rows.Contains(id);

    var before = Load();
    var samples = new List<double>();
    using var stopSampling = new CancellationTokenSource();
    var sampler = Task.Run(async () =>
    {
      while (!stopSampling.IsCancellationRequested)
      {
        samples.Add(Load());
        try
        {
          await Task.Delay(5000, stopSampling.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
          return;
        }
      }
    });

    var failures = 0;
    async Task Guard(string id, Func<Task> body)
    {
      if (!Want(id))
      {
        return;
      }

      Console.Error.WriteLine("row " + id);
      try
      {
        await body().ConfigureAwait(false);
      }
      catch (Exception ex)
      {
        failures++;
        Notes.Add($"row {id} failed: {ex.Message}");
        Console.Error.WriteLine("row " + id + " failed: " + ex.Message);
      }
    }

    await Guard("r1", () => Task.Run(() => StdioRoundTrip(bin, seq, seconds))).ConfigureAwait(false);
    await Guard("r2", () => HttpThroughput(bin, seconds)).ConfigureAwait(false);
    await Guard("r3", () => Task.Run(() => ClientCalls(bin, seq, seconds))).ConfigureAwait(false);
    await Guard("r4", () => Task.Run(() => LargeResults(bin))).ConfigureAwait(false);
    await Guard("r5", () => Task.Run(() => ToolList(bin))).ConfigureAwait(false);
    await Guard("r6", () => Task.Run(() => ColdStart(bin))).ConfigureAwait(false);

    stopSampling.Cancel();
    await sampler.ConfigureAwait(false);
    var after = Load();
    Write(outDir, bin, referenceVersion, before, samples, after);
    return failures == 0 ? 0 : 1;
  }

  // Row 1: stdio tools/call round trip. Each client talks to its own stack's server, as a user of that SDK would.
  private static void StdioRoundTrip(string bin, int seq, double seconds)
  {
    var ours = Pair(bin, "ours", "ours", "call", seq, seconds);
    var theirs = Pair(bin, "ref", "ref", "call", seq, seconds);
    Rows.Add(new Row("r1a", "stdio tools/call round trip, p50", "us", true, Num(ours, "p50Us"), Num(theirs, "p50Us"), false, Alloc(ours, theirs)));
    Rows.Add(new Row("r1b", $"stdio tools/call throughput, {Int(ours, "concurrency")} in flight", "calls/s", true, Num(ours, "callsPerSec"), Num(theirs, "callsPerSec"), true, ""));
  }

  // Row 2: Streamable HTTP tools/call throughput on Kestrel, same raw requests to both servers. The built-in listener is information.
  private static async Task HttpThroughput(string bin, double seconds)
  {
    const int Concurrency = 32;
    var ours = await HttpRun(bin, "ours", "kestrel", false, Concurrency, seconds).ConfigureAwait(false);
    var theirs = await HttpRun(bin, "ref", "kestrel", false, Concurrency, seconds).ConfigureAwait(false);
    Rows.Add(new Row("r2", $"Streamable HTTP tools/call throughput on Kestrel (legacy session flow, {Concurrency} connections)", "calls/s", true, ours.CallsPerSec, theirs.CallsPerSec, true,
      $"alloc/call server: ours {ours.ServerAllocPerCall} B, errors {ours.Errors}/{theirs.Errors}"));
    var modern = await HttpRun(bin, "ours", "kestrel", true, Concurrency, seconds).ConfigureAwait(false);
    Rows.Add(new Row("r2-modern", "Streamable HTTP tools/call throughput on Kestrel, 2026-07-28 requests (ours; the reference gets the same requests, see the notes)", "calls/s", false, modern.CallsPerSec, null, true,
      modern.FirstError.Length > 0 ? modern.FirstError : $"alloc/call server {modern.ServerAllocPerCall} B"));
    var modernRef = await HttpRun(bin, "ref", "kestrel", true, Concurrency, seconds).ConfigureAwait(false);
    Notes.Add("r2-modern reference: " + (modernRef.FirstError.Length > 0 ? "does not answer 2026-07-28 requests: " + modernRef.FirstError : modernRef.CallsPerSec.ToString("F0", CultureInfo.InvariantCulture) + " calls/s"));
    var listener = await HttpRun(bin, "ours", "listener", false, Concurrency, seconds).ConfigureAwait(false);
    Rows.Add(new Row("r2-listener", "built-in listener, same load (information)", "calls/s", false, listener.CallsPerSec, null, true, $"alloc/call server {listener.ServerAllocPerCall} B"));
  }

  private static async Task<RawLoad.Result> HttpRun(string bin, string stack, string mode, bool modern, int concurrency, double seconds)
  {
    var (server, url) = Procs.StartHttp(bin, stack, mode);
    try
    {
      var result = await RawLoad.RunAsync(url, modern, concurrency, seconds).ConfigureAwait(false);
      Notes.Add($"r2 {stack} {mode} {(modern ? "modern" : "legacy")}: server peak {Procs.PeakMb(server):F0} MiB, errors {result.Errors}, alloc/call {result.ServerAllocPerCall} B");
      return result;
    }
    finally
    {
      Procs.Stop(server);
    }
  }

  // Row 3: each client against the same fixed server. The fixed server is ours (gated); the same clients against the reference server are information.
  private static void ClientCalls(string bin, int seq, double seconds)
  {
    foreach (var (fixedStack, gated) in new[] { ("ours", true), ("ref", false) })
    {
      var stdioServer = $"{Procs.Exe(bin, fixedStack)} server stdio";
      var o = Procs.RunClient(bin, "ours", stdioServer, "call", seq, 16, seconds, 500);
      var r = Procs.RunClient(bin, "ref", stdioServer, "call", seq, 16, seconds, 500);
      Rows.Add(new Row($"r3-stdio-{fixedStack}", $"client calls/s over stdio against the {fixedStack} server", "calls/s", gated, Num(o, "callsPerSec"), Num(r, "callsPerSec"), true, Alloc(o, r)));

      var ho = HttpClientCalls(bin, fixedStack, "ours", seq, seconds);
      var hr = HttpClientCalls(bin, fixedStack, "ref", seq, seconds);
      Rows.Add(new Row($"r3-http-{fixedStack}", $"client calls/s over Streamable HTTP against the {fixedStack} server on Kestrel", "calls/s", gated, Num(ho, "callsPerSec"), Num(hr, "callsPerSec"), true, Alloc(ho, hr)));
    }
  }

  private static JsonElement HttpClientCalls(string bin, string serverStack, string clientStack, int seq, double seconds)
  {
    // Each client gets its own server and the same unmeasured load pass. Reusing one server gave the second client a tiered-JIT
    // warmup advantage: reversing the client order moved the extra server CPU to whichever client ran first.
    var (server, url) = Procs.StartHttp(bin, serverStack, "kestrel");
    try
    {
      _ = Procs.RunClient(bin, clientStack, url, "call", seq, 16, Math.Max(4, seconds), 500);
      return Procs.RunClient(bin, clientStack, url, "call", seq, 16, seconds, 500);
    }
    finally
    {
      Procs.Stop(server);
    }
  }

  // Row 4: a 1 MiB text result and a 1 MiB inline image over stdio, p50 of the whole call (server encode, transport, client decode).
  private static void LargeResults(string bin)
  {
    foreach (var (kind, label) in new[] { ("text", "1 MiB text"), ("image", "1 MiB inline image") })
    {
      var ours = Pair(bin, "ours", "ours", kind, 200, 0);
      var theirs = Pair(bin, "ref", "ref", kind, 200, 0);
      Rows.Add(new Row("r4-" + kind, label + " result over stdio, p50", "us", true, Num(ours, "p50Us"), Num(theirs, "p50Us"), false, Alloc(ours, theirs)));
    }
  }

  // Row 5: tools/list with 1,000 tools over stdio, p50 (the server sends the list, the client decodes it; no caching is in play).
  private static void ToolList(string bin)
  {
    var ours = Procs.RunClient(bin, "ours", $"{Procs.Exe(bin, "ours")} server stdio --filler 1000", "list", 100, 1, 0, 20);
    var theirs = Procs.RunClient(bin, "ref", $"{Procs.Exe(bin, "ref")} server stdio --filler 1000", "list", 100, 1, 0, 20);
    Rows.Add(new Row("r5", "tools/list with 1,000 tools over stdio, p50", "us", true, Num(ours, "p50Us"), Num(theirs, "p50Us"), false, Alloc(ours, theirs)));
  }

  // Row 6: process start to the first response line. The NativeAOT apps are gated; if the reference cannot publish AOT the row passes and reports.
  private static void ColdStart(string bin)
  {
    var ours = ColdMedian(Procs.Exe(bin, "ours-aot"));
    var theirsExe = Procs.Exe(bin, "ref-aot");
    if (ours is null)
    {
      throw new InvalidOperationException("our NativeAOT app is missing or does not answer");
    }

    var theirs = ColdMedian(theirsExe);
    if (theirs is null)
    {
      Notes.Add("r6: the reference could not be published with NativeAOT (or does not run as such); the gated row passes and reports ours only.");
    }

    Rows.Add(new Row("r6", "NativeAOT stdio server, process start to first response", "ms", true, ours.Value, theirs, false, theirs is null ? "reference: no NativeAOT build" : ""));
    var jitOurs = ColdMedian(Procs.Exe(bin, "ours"));
    var jitTheirs = ColdMedian(Procs.Exe(bin, "ref"));
    if (jitOurs is not null && jitTheirs is not null)
    {
      Rows.Add(new Row("r6-jit", "JIT (framework-dependent) stdio server, process start to first response (information)", "ms", false, jitOurs.Value, jitTheirs, false, ""));
    }
  }

  private static double? ColdMedian(string exe)
  {
    if (!File.Exists(exe))
    {
      return null;
    }

    var times = new List<double>();
    for (var i = 0; i < 15; i++)
    {
      var start = Stopwatch.GetTimestamp();
      using var p = Procs.Start(exe, "server stdio", redirectInput: true);
      try
      {
        p.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"bench","version":"1"}}}""");
        p.StandardInput.Flush();
        var read = p.StandardOutput.ReadLineAsync();
        if (!read.Wait(30_000) || read.Result is not { } line || !line.Contains("result", StringComparison.Ordinal))
        {
          return null;
        }

        times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
      }
      finally
      {
        Procs.Stop(p);
      }
    }

    times.Sort();
    return times[times.Count / 2];
  }

  private static JsonElement Pair(string bin, string clientStack, string serverStack, string kind, int seq, double seconds)
  {
    var server = $"{Procs.Exe(bin, serverStack)} server stdio";
    return Procs.RunClient(bin, clientStack, server, kind, seq, 16, seconds, kind == "call" ? 500 : 20);
  }

  private static double Num(JsonElement e, string name) => e.GetProperty(name).GetDouble();

  private static int Int(JsonElement e, string name) => e.GetProperty(name).GetInt32();

  private static string Alloc(JsonElement ours, JsonElement theirs) =>
    $"alloc/call client {Num(ours, "clientAllocBytesPerCall"):F0} B, server {Num(ours, "serverAllocBytesPerCall"):F0} B; cpu/call under load client {Num(ours, "clientCpuUsPerCall"):F1} us, server {Num(ours, "serverCpuUsPerCall"):F1} us; peak MiB client {Num(ours, "clientPeakMb"):F0}, server {Num(ours, "serverPeakMb"):F0}; reference: alloc client {Num(theirs, "clientAllocBytesPerCall"):F0} B, server {Num(theirs, "serverAllocBytesPerCall"):F0} B, cpu client {Num(theirs, "clientCpuUsPerCall"):F1} us, server {Num(theirs, "serverCpuUsPerCall"):F1} us";

  private static double Load()
  {
    try
    {
      return double.Parse(File.ReadAllText("/proc/loadavg").Split(' ')[0], CultureInfo.InvariantCulture);
    }
    catch (IOException)
    {
      return -1;
    }
  }

  private static string? Arg(string[] args, string name)
  {
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
  }

  private static void Write(string outDir, string bin, string referenceVersion, double before, List<double> during, double after)
  {
    var inv = CultureInfo.InvariantCulture;
    var gated = Rows.Where(r => r.Gated && r.Ratio is not null).Select(r => r.Ratio!.Value).ToList();
    var geomean = gated.Count == 0 ? (double?)null : Math.Exp(gated.Average(Math.Log));
    var cpu = "unknown";
    var flags = "";
    try
    {
      var info = File.ReadAllLines("/proc/cpuinfo");
      cpu = info.FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':')[1].Trim() ?? cpu;
      var all = (info.FirstOrDefault(l => l.StartsWith("flags", StringComparison.Ordinal)) ?? "").Split(':').Last().Split(' ');
      flags = string.Join(' ', new[] { "sse4_2", "avx", "avx2", "bmi2", "avx512f", "avx512bw", "aes", "sha_ni" }.Where(all.Contains));
    }
    catch (IOException)
    {
      // Not Linux.
    }

    var machine = new Dictionary<string, object?>
    {
      ["host"] = Environment.MachineName,
      ["cpu"] = cpu,
      ["isaFlags"] = flags,
      ["cores"] = Environment.ProcessorCount,
      ["loadBefore"] = before,
      ["loadDuring"] = during.Count == 0 ? null : new { min = during.Min(), mean = during.Average(), max = during.Max(), samples = during.Count },
      ["loadAfter"] = after,
      ["runtime"] = RuntimeInformation.FrameworkDescription,
      ["os"] = RuntimeInformation.OSDescription,
      ["referenceSdk"] = "ModelContextProtocol " + referenceVersion,
      ["date"] = DateTime.UtcNow.ToString("O", inv),
    };

    var md = new StringBuilder();
    md.AppendLine("| Row | Metric | Unit | Ours | Reference | Ratio (ours/reference cost) | Gated | Detail |");
    md.AppendLine("|---|---|---|---:|---:|---:|---|---|");
    foreach (var r in Rows)
    {
      md.AppendLine($"| {r.Id} | {r.Name} | {r.Unit} | {r.Ours.ToString("F1", inv)} | {(r.Reference is { } x ? x.ToString("F1", inv) : "n/a")} | {(r.Ratio is { } q ? q.ToString("F3", inv) : "n/a")} | {(r.Gated ? "yes" : "info")} | {r.Detail} |");
    }

    md.AppendLine().AppendLine("Geomean of gated ratios: " + (geomean is { } g ? g.ToString("F3", inv) : "n/a") + "; worst gated ratio: " + (gated.Count == 0 ? "n/a" : gated.Max().ToString("F3", inv)));
    md.AppendLine().AppendLine("Machine: " + cpu + ", " + Environment.ProcessorCount + " cores, ISA " + flags + "; load before " + before.ToString("F2", inv) + ", during " + (during.Count == 0 ? "n/a" : $"mean {during.Average():F2} max {during.Max():F2}") + ", after " + after.ToString("F2", inv) + "; " + RuntimeInformation.FrameworkDescription + "; reference ModelContextProtocol " + referenceVersion + ".");
    foreach (var n in Notes)
    {
      md.AppendLine().Append("- ").Append(n);
    }

    md.AppendLine();
    File.WriteAllText(Path.Combine(outDir, "results.md"), md.ToString());
    var json = new Dictionary<string, object?>
    {
      ["machine"] = machine,
      ["geomean"] = geomean,
      ["worst"] = gated.Count == 0 ? null : gated.Max(),
      ["rows"] = Rows.Select(r => new { r.Id, r.Name, r.Unit, r.Gated, r.Ours, r.Reference, r.Ratio, r.Detail }).ToList(),
      ["notes"] = Notes,
    };
    File.WriteAllText(Path.Combine(outDir, "results.json"), JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(md.ToString());
  }
}
