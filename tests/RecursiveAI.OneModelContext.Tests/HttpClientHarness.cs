using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Hosting;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>One request the client sent.</summary>
internal sealed record SentRequest(string Method, Uri Uri, string Body, IReadOnlyDictionary<string, string> Headers)
{
  internal string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

  internal string? RpcMethod
  {
    get
    {
      try
      {
        using var document = JsonDocument.Parse(Body);
        return document.RootElement.TryGetProperty("method", out var m) ? m.GetString() : null;
      }
      catch (JsonException)
      {
        return null;
      }
    }
  }
}

/// <summary>
/// An <see cref="HttpMessageHandler"/> that serves requests with a <see cref="StreamableHttpEndpoint"/> in memory, streams included,
/// records what was sent and lets a test replace answers.
/// </summary>
internal sealed class EndpointHandler(StreamableHttpEndpoint endpoint) : HttpMessageHandler
{
  public ConcurrentQueue<SentRequest> Sent { get; } = new();

  /// <summary>Called before the endpoint sees a request; a non-null response replaces the endpoint's answer.</summary>
  public Func<SentRequest, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Intercept { get; set; }

  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
  {
    var (sent, body) = await ReadAsync(request, cancellationToken);
    Sent.Enqueue(sent);
    if (Intercept is not null && await Intercept(sent, request, cancellationToken) is { } replaced)
    {
      return replaced;
    }

    return await ServeAsync(request.Method.Method, sent, body, cancellationToken);
  }

  /// <summary>Serves a request with the endpoint, skipping <see cref="Intercept"/> and the record.</summary>
  internal async Task<HttpResponseMessage> PassthroughAsync(HttpRequestMessage request, CancellationToken cancellationToken)
  {
    var (sent, body) = await ReadAsync(request, cancellationToken);
    return await ServeAsync(request.Method.Method, sent, body, cancellationToken);
  }

  private static async Task<(SentRequest, byte[])> ReadAsync(HttpRequestMessage request, CancellationToken cancellationToken)
  {
    var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = request.RequestUri!.Authority };
    foreach (var header in request.Headers)
    {
      headers[header.Key] = string.Join(", ", header.Value);
    }

    if (request.Content is not null)
    {
      foreach (var header in request.Content.Headers)
      {
        headers[header.Key] = string.Join(", ", header.Value);
      }
    }

    return (new SentRequest(request.Method.Method, request.RequestUri, Encoding.UTF8.GetString(body), headers), body);
  }

  private async Task<HttpResponseMessage> ServeAsync(string method, SentRequest sent, byte[] body, CancellationToken cancellationToken)
  {
    var exchange = new TestExchange(method, new MemoryStream(body), body.Length, [.. ToPairs(new Dictionary<string, string>(sent.Headers))]);
    cancellationToken.Register(exchange.Disconnect);
    exchange.Run(endpoint);
    await exchange.HeadersSent.WaitAsync(cancellationToken);
    var response = new HttpResponseMessage((HttpStatusCode)exchange.Status) { Content = new StreamContent(new DisconnectOnDispose(exchange.ResponseStream, exchange.Disconnect)) };
    foreach (var name in exchange.ResponseHeaders.Names)
    {
      foreach (var value in exchange.ResponseHeaders.GetAll(name))
      {
        if (!response.Headers.TryAddWithoutValidation(name, value))
        {
          response.Content.Headers.TryAddWithoutValidation(name, value);
        }
      }
    }

    return response;
  }

  private static IEnumerable<(string, string)> ToPairs(Dictionary<string, string> headers)
  {
    foreach (var (name, value) in headers)
    {
      yield return (name, value);
    }
  }

  internal static HttpResponseMessage Sse(string text, HttpStatusCode status = HttpStatusCode.OK) =>
    new(status) { Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(text))) { Headers = { ContentType = new("text/event-stream") } } };

  internal static HttpResponseMessage Sse(Stream stream) =>
    new(HttpStatusCode.OK) { Content = new StreamContent(stream) { Headers = { ContentType = new("text/event-stream") } } };

  internal static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
    new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

  internal static HttpResponseMessage Plain(HttpStatusCode status, string text = "") =>
    new(status) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

  private sealed class DisconnectOnDispose(Stream inner, Action disconnect) : Stream
  {
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

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
      if (disposing)
      {
        disconnect();
        inner.Dispose();
      }

      base.Dispose(disposing);
    }
  }
}

/// <summary>A response body that gives bytes when the test says so and never ends by itself.</summary>
internal sealed class OpenStream : Stream
{
  private readonly Pipe _pipe = new();

  public override bool CanRead => true;

  public override bool CanSeek => false;

  public override bool CanWrite => false;

  public override long Length => throw new NotSupportedException();

  public override long Position
  {
    get => throw new NotSupportedException();
    set => throw new NotSupportedException();
  }

  internal async Task WriteAsync(string text) => await _pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(text));

  internal Task EndAsync() => _pipe.Writer.CompleteAsync().AsTask();

  public override void Flush()
  {
  }

  public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

  public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
  {
    var result = await _pipe.Reader.ReadAsync(cancellationToken);
    var source = result.Buffer;
    var length = (int)Math.Min(source.Length, buffer.Length);
    source.Slice(0, length).CopyTo(buffer.Span);
    _pipe.Reader.AdvanceTo(source.GetPosition(length));
    return length;
  }

  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

  public override void SetLength(long value) => throw new NotSupportedException();

  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
