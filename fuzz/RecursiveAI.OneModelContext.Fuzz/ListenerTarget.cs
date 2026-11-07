using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Http;

// Fuzz target for the built-in listener's parsers (select it with FUZZ_TARGET=listener): the request head, a chunked body and a
// length-delimited body, each fed arbitrary bytes. The only outcomes allowed are an accepted message that satisfies the invariants below
// or a typed refusal; any other exception, a hang, or a body longer than its limit is a finding.
//
// Input layout: byte 0 selects the parser (0 head, 1 chunked body, 2 body with a declared length taken from byte 1); the rest is the
// bytes the parser sees. Chunked and length bodies are fed through the same reader the connection uses, in a small buffer.
internal static class ListenerTarget
{
  private const int BodyLimit = 64 * 1024;
  private static readonly HttpServerOptions Options = new() { MaxRequestBodyBytes = BodyLimit, MaxHeaderBytes = 4096, MaxRequestLineBytes = 1024, MaxHeaderCount = 32 };

  internal static int Run(ReadOnlySpan<byte> data)
  {
    if (data.Length == 0)
    {
      return -1;
    }

    var rest = data[1..];
    return (data[0] % 3) switch
    {
      0 => ParseHead(rest),
      1 => ReadBody(rest.ToArray(), chunked: true, 0),
      _ => rest.Length == 0 ? -1 : ReadBody(rest[1..].ToArray(), chunked: false, rest[0]),
    };
  }

  private static int ParseHead(ReadOnlySpan<byte> block)
  {
    var error = RequestHeadParser.TryParse(block, Options, out var head);
    if (error is { } failure)
    {
      return failure.Status;
    }

    if (head is null || head.Path.Length == 0 || head.Path[0] != '/')
    {
      throw new InvalidOperationException("accepted a head without a path");
    }

    // Request smuggling needs two readings of one message: an accepted head must have exactly one framing.
    if (head.Chunked && head.ContentLength is not null)
    {
      throw new InvalidOperationException("chunked with a length");
    }

    if (head.Chunked && head.Headers.GetAll("Content-Length").Count != 0)
    {
      throw new InvalidOperationException("chunked beside Content-Length");
    }

    if (head.Headers.GetAll("Content-Length").Count > 1 || head.Headers.GetAll("Host").Count > 1 || head.Headers.GetAll("Transfer-Encoding").Count > 1)
    {
      throw new InvalidOperationException("accepted a repeated framing or host header");
    }

    if (head.ContentLength is < 0)
    {
      throw new InvalidOperationException("negative length");
    }

    return 0;
  }

  private static int ReadBody(byte[] bytes, bool chunked, byte declared)
  {
    using var reader = new ConnectionReader(new MemoryStream(bytes), 4096);
    var owner = new Owner();
    using var body = chunked
      ? (RequestBodyStream)new ChunkedBodyStream(owner, reader, BodyLimit, CancellationToken.None)
      : new LengthBodyStream(owner, reader, declared, CancellationToken.None);
    var buffer = new byte[512];
    long total = 0;
    try
    {
      while (true)
      {
        var read = body.ReadAsync(buffer.AsMemory()).AsTask().GetAwaiter().GetResult();
        if (read == 0)
        {
          return 0;
        }

        total += read;
        if (total > BodyLimit)
        {
          throw new InvalidOperationException("read past the body limit");
        }
      }
    }
    catch (RequestAbortedException)
    {
      return owner.Status;
    }
  }

  private sealed class Owner : IRequestBodyOwner
  {
    internal int Status { get; private set; }

    public void RecordError(int status, string reason) => Status = status;

    public ValueTask SendContinueAsync() => ValueTask.CompletedTask;

    public void BodyCompleted()
    {
    }
  }
}
