using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.Hosting;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.Protocol;
using SharpFuzz;

// Fuzz target for the Streamable HTTP endpoint: arbitrary request headers and bodies, and GET and DELETE with arbitrary session and
// Last-Event-ID headers. Only a typed answer may come out; an exception escaping HandleAsync, a hang or a memory blow-up is a finding.
//
//   Program                    run under libFuzzer (see fuzz/run.sh)
//   Program replay FILE...     run saved inputs once and print the status each one gets
//
// FUZZ_TARGET=sse selects the other target: the event-stream reader the HTTP client uses on responses. Byte 0 picks how many bytes the
// stream hands over per read, the rest is the stream. It must end, keep every event within the bound, and never throw.
//
// Input layout: byte 0 selects the method (POST, GET, DELETE or another); the rest is header lines ("Name: value"), a blank line,
// then the body. The text "$SESSION" in a header value is replaced by the id of a legacy session created at start-up, so that GET
// and DELETE reach real state.
internal static class Program
{
  private static readonly string[] Methods = ["POST", "POST", "POST", "GET", "DELETE", "PUT"];
  private static readonly ContextServer Server = CreateServer();
  private static readonly StreamableHttpEndpoint Endpoint = new(Server, new StreamableHttpOptions { MaxRequestBodyBytes = 64 * 1024, BodyReadTimeout = TimeSpan.FromSeconds(1), SseWriteTimeout = TimeSpan.FromSeconds(1) });
  private static readonly string Session = StartSession();

  private static int Main(string[] args)
  {
    if (AuthorizationServerTarget.Selected)
    {
      return AuthorizationServerTarget.Start(args);
    }

    if (args.Length > 0 && args[0] == "replay")
    {
      foreach (var file in args.Skip(1))
      {
        Console.WriteLine(file + ": " + Dispatch(File.ReadAllBytes(file)));
      }

      return 0;
    }

    Fuzzer.LibFuzzer.Run(span => Dispatch(span));
    return 0;
  }

  // FUZZ_TARGET=listener selects the HTTP/1.1 parsers of the built-in listener, FUZZ_TARGET=sse the event-stream reader of the HTTP client;
  // otherwise the Streamable HTTP endpoint.
  private static int Dispatch(ReadOnlySpan<byte> data) => Environment.GetEnvironmentVariable("FUZZ_TARGET") switch
  {
    "listener" => ListenerTarget.Run(data),
    "sse" => RunSse(data),
    "oauth" => RunOAuth(data),
    "skills" => RunSkills(data),
    _ => Run(data),
  };

  // FUZZ_TARGET=oauth: what an OAuth client reads from a server it does not trust. Byte 0 picks the parser: a WWW-Authenticate value, a
  // protected resource metadata document, an authorization server metadata document, or a redirect query. Only OAuthException may come out.
  private static int RunOAuth(ReadOnlySpan<byte> data)
  {
    if (data.Length == 0)
    {
      return 0;
    }

    var body = data[1..];
    try
    {
      switch (data[0] % 4)
      {
        case 0:
          return RecursiveAI.OneModelContext.OAuth.BearerChallenge.Parse([Encoding.UTF8.GetString(body)]).Count;
        case 1:
          return RecursiveAI.OneModelContext.OAuth.ResourceMetadata.Parse(body)?.AuthorizationServers.Count ?? 0;
        case 2:
          return RecursiveAI.OneModelContext.OAuth.AuthorizationServerMetadata.Parse(body)?.Issuer?.Length ?? 0;
        default:
          var query = Encoding.UTF8.GetString(body);
          return RecursiveAI.OneModelContext.OAuth.AuthorizationResponse.Validate(new Uri("http://127.0.0.1:9/callback?" + query.Replace("#", "%23", StringComparison.Ordinal)), "s", "https://i.example", issParameterSupported: false).Code.Length;
      }
    }
    catch (RecursiveAI.OneModelContext.OAuth.OAuthException)
    {
      return 0;
    }
    catch (UriFormatException)
    {
      return 0;
    }
  }

  // FUZZ_TARGET=skills: the YAML frontmatter reader for SKILL.md files, which a client runs on content a server sent. It must return a JSON
  // object or refuse with FormatException; any other exception, a hang or runaway memory is a finding.
  private static int RunSkills(ReadOnlySpan<byte> data)
  {
    try
    {
      var frontmatter = RecursiveAI.OneModelContext.Extensions.Skills.SkillYaml.ParseFrontmatter(data);
      if (frontmatter.ValueKind != JsonValueKind.Object)
      {
        throw new InvalidOperationException("The frontmatter reader returned something other than an object.");
      }

      return frontmatter.GetRawText().Length;
    }
    catch (FormatException)
    {
      return -1;
    }
  }

  private static ContextServer CreateServer()
  {
    var server = new ContextServer("fuzz", "1.0");
    using var schema = JsonDocument.Parse("""{"type":"object","properties":{"text":{"type":"string","x-mcp-header":"Text"}}}""");
    server.Tools.Add(new ToolDefinition("echo", "Echoes.", schema.RootElement.Clone()), (_, _) => ValueTask.FromResult(CallToolResult.FromText("ok")));
    server.Tools.Add(
      new ToolDefinition("progress", "Reports progress.", schema.RootElement.Clone()),
      async (_, ctx) =>
      {
        await ctx.ReportProgressAsync(1, 2, "half");
        return CallToolResult.FromText("done");
      });
    return server;
  }

  private static string StartSession()
  {
    var body = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"f","version":"1"}}}""");
    using var exchange = new FuzzExchange("POST", body, [("Content-Type", "application/json"), ("Accept", "application/json, text/event-stream"), ("Host", "127.0.0.1")]);
    Endpoint.HandleAsync(exchange).AsTask().GetAwaiter().GetResult();
    return exchange.ResponseHeaders.TryGetValue("Mcp-Session-Id", out var id) ? id : "none";
  }

  internal static int RunSse(ReadOnlySpan<byte> data)
  {
    const int Bound = 2048;
    if (data.Length == 0)
    {
      return -1;
    }

    using var reader = new RecursiveAI.OneModelContext.Sse.SseEventReader(new ChunkedStream(data[1..].ToArray(), (data[0] % 64) + 1), Bound);
    var events = 0;
    while (true)
    {
      var read = reader.ReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
      if (read.Status != RecursiveAI.OneModelContext.Sse.SseReadStatus.Event)
      {
        return events;
      }

      events++;
      if (read.Event.Data.Length > Bound)
      {
        throw new InvalidOperationException("An event is larger than the bound.");
      }

      // What the client does with an event: read it as a message. It must classify, never throw.
      RecursiveAI.OneModelContext.JsonRpc.JsonRpcReader.Read(read.Event.Data);
    }
  }

  internal static int Run(ReadOnlySpan<byte> data)
  {
    if (data.Length == 0)
    {
      return -1;
    }

    var method = Methods[data[0] % Methods.Length];
    var text = Encoding.UTF8.GetString(data[1..]);
    var split = text.IndexOf("\n\n", StringComparison.Ordinal);
    var head = split < 0 ? text : text[..split];
    var body = Encoding.UTF8.GetBytes(split < 0 ? string.Empty : text[(split + 2)..]);

    var headers = new List<(string, string)> { ("Host", "127.0.0.1") };
    foreach (var line in head.Split('\n'))
    {
      var colon = line.IndexOf(':', StringComparison.Ordinal);
      if (colon > 0)
      {
        headers.Add((line[..colon].Trim(), line[(colon + 1)..].Trim().Replace("$SESSION", Session, StringComparison.Ordinal)));
      }
    }

    using var exchange = new FuzzExchange(method, body, [.. headers]);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    using var registration = timeout.Token.Register(exchange.Abort);
    try
    {
      Endpoint.HandleAsync(exchange).AsTask().GetAwaiter().GetResult();
    }
    catch (OperationCanceledException)
    {
      // The harness hung up on a stream that was still open.
    }

    return exchange.StatusCode;
  }
}

internal sealed class FuzzExchange : IHttpExchange, IHttpRequest, IHttpResponse, IDisposable
{
  private readonly CancellationTokenSource _abort = new();
  private readonly MemoryStream _requestBody;

  internal FuzzExchange(string method, byte[] body, (string Name, string Value)[] headers)
  {
    Method = method;
    _requestBody = new MemoryStream(body);
    ContentLength = body.Length;
    foreach (var (name, value) in headers)
    {
      try
      {
        Headers.Add(name, value);
      }
      catch (ArgumentException)
      {
        // A header the host would never have delivered.
      }
    }
  }

  public IHttpRequest Request => this;

  public IHttpResponse Response => this;

  public ClaimsPrincipal? User => null;

  public CancellationToken Aborted => _abort.Token;

  public string Method { get; }

  public string Path => "/mcp";

  public HttpHeaders Headers { get; } = new();

  public long? ContentLength { get; }

  Stream IHttpRequest.Body => _requestBody;

  public IPAddress? RemoteAddress => IPAddress.Loopback;

  public int StatusCode { get; set; } = 200;

  internal Dictionary<string, string> ResponseHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

  Stream IHttpResponse.Body { get; } = Stream.Null;

  public void SetHeader(string name, string value) => ResponseHeaders[name] = value;

  internal void Abort() => _abort.Cancel();

  public void Dispose()
  {
    _abort.Dispose();
    _requestBody.Dispose();
  }
}

/// <summary>A stream that hands out its bytes a few at a time, as a network does.</summary>
internal sealed class ChunkedStream(byte[] bytes, int chunk) : Stream
{
  private int _position;

  public override bool CanRead => true;

  public override bool CanSeek => false;

  public override bool CanWrite => false;

  public override long Length => throw new NotSupportedException();

  public override long Position
  {
    get => throw new NotSupportedException();
    set => throw new NotSupportedException();
  }

  public override void Flush()
  {
  }

  public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

  public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
  {
    var n = Math.Min(Math.Min(chunk, buffer.Length), bytes.Length - _position);
    bytes.AsSpan(_position, n).CopyTo(buffer.Span);
    _position += n;
    return ValueTask.FromResult(n);
  }

  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

  public override void SetLength(long value) => throw new NotSupportedException();

  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
