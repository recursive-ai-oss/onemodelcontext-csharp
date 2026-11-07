using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace Bench;

/// <summary>
/// A stack-neutral load generator for the HTTP row: the same raw requests go to either server, so the row measures the server.
/// The legacy flow does the initialize handshake once (both servers accept it) and then posts tools/call on that session;
/// the modern flow posts self-contained 2026-07-28 requests.
/// </summary>
internal static partial class RawLoad
{
  private const string LegacyVersion = "2025-06-18";
  private const string ModernVersion = "2026-07-28";

  public sealed record Result(double CallsPerSec, long Calls, long Errors, long ServerAllocPerCall, string FirstError);

  public static async Task<Result> RunAsync(string url, bool modern, int concurrency, double seconds)
  {
    using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = concurrency, AllowAutoRedirect = false, UseProxy = false };
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    string? session = null;
    if (!modern)
    {
      var init = await Post(http, url, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"bench","version":"1"}}}""", null, null).ConfigureAwait(false);
      if (!init.Response.IsSuccessStatusCode)
      {
        return new Result(0, 0, 1, 0, "initialize: " + (int)init.Response.StatusCode + " " + init.Body);
      }

      session = init.Response.Headers.TryGetValues("Mcp-Session-Id", out var ids) ? ids.First() : null;
      await Post(http, url, """{"jsonrpc":"2.0","method":"notifications/initialized"}""", session, null).ConfigureAwait(false);
    }

    var body = modern
      ? """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"echo","arguments":{"message":"hello"},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/clientInfo":{"name":"bench","version":"1"}}}}"""
      : """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"echo","arguments":{"message":"hello"}}}""";
    var first = await Post(http, url, body, session, modern ? ModernVersion : LegacyVersion, modern).ConfigureAwait(false);
    if (!first.Response.IsSuccessStatusCode || !first.Body.Contains("echo: hello", StringComparison.Ordinal))
    {
      return new Result(0, 0, 1, 0, "tools/call: " + (int)first.Response.StatusCode + " " + (first.Body.Length > 300 ? first.Body[..300] : first.Body));
    }

    for (var i = 0; i < 2000; i++)
    {
      await Post(http, url, body, session, modern ? ModernVersion : LegacyVersion, modern).ConfigureAwait(false);
    }

    var allocBefore = await StatsAsync(http, url, session, modern).ConfigureAwait(false);
    long calls = 0;
    long errors = 0;
    var stop = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
    var started = Stopwatch.GetTimestamp();
    var workers = Enumerable.Range(0, concurrency).Select(async _ =>
    {
      long n = 0;
      long bad = 0;
      while (Stopwatch.GetTimestamp() < stop)
      {
        var r = await Post(http, url, body, session, modern ? ModernVersion : LegacyVersion, modern).ConfigureAwait(false);
        if (r.Response.IsSuccessStatusCode)
        {
          n++;
        }
        else
        {
          bad++;
        }
      }

      Interlocked.Add(ref calls, n);
      Interlocked.Add(ref errors, bad);
    }).ToArray();
    await Task.WhenAll(workers).ConfigureAwait(false);
    var elapsed = (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
    var allocAfter = await StatsAsync(http, url, session, modern).ConfigureAwait(false);
    return new Result(calls / elapsed, calls, errors, calls == 0 ? 0 : (allocAfter - allocBefore) / calls, "");
  }

  private static async Task<long> StatsAsync(HttpClient http, string url, string? session, bool modern)
  {
    var body = modern
      ? """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"bench_stats","arguments":{},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/clientInfo":{"name":"bench","version":"1"}}}}"""
      : """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"bench_stats","arguments":{}}}""";
    var r = await Post(http, url, body, session, modern ? ModernVersion : LegacyVersion, modern, "bench_stats").ConfigureAwait(false);
    var m = StatsPattern().Match(r.Body);
    return m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
  }

  [GeneratedRegex("\"text\"\\s*:\\s*\"(\\d+) \\d+ \\d+\"")]
  private static partial Regex StatsPattern();

  private static async Task<(HttpResponseMessage Response, string Body)> Post(HttpClient http, string url, string body, string? session, string? version, bool modern = false, string tool = "echo")
  {
    using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
    if (session is not null)
    {
      request.Headers.Add("Mcp-Session-Id", session);
    }

    if (version is not null)
    {
      request.Headers.Add("MCP-Protocol-Version", version);
    }

    if (modern)
    {
      request.Headers.Add("Mcp-Method", "tools/call");
      request.Headers.Add("Mcp-Name", tool);
    }

    var response = await http.SendAsync(request).ConfigureAwait(false);
    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    return (response, text);
  }
}
