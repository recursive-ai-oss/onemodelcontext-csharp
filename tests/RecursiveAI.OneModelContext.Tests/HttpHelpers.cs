using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Hosting;
using RecursiveAI.OneModelContext.Http;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>An HTTP exchange in memory: the test plays the client, the endpoint plays the server.</summary>
internal sealed class TestExchange : IHttpExchange, IDisposable
{
  private readonly CancellationTokenSource _abort = new();
  private readonly Pipe _responsePipe = new();
  private readonly TestRequest _request;
  private readonly TestResponse _response;
  private Task? _running;

  internal TestExchange(string method, Stream? body = null, long? contentLength = null, params (string Name, string Value)[] headers)
  {
    _request = new TestRequest(method, body ?? new MemoryStream(), contentLength);
    foreach (var (name, value) in headers)
    {
      _request.Headers.Add(name, value);
    }

    _response = new TestResponse(_responsePipe.Writer);
  }

  public IHttpRequest Request => _request;

  public IHttpResponse Response => _response;

  public ClaimsPrincipal? User { get; init; }

  public CancellationToken Aborted => _abort.Token;

  internal int Status => _response.StatusCode;

  internal HttpHeaders ResponseHeaders => _response.Headers;

  /// <summary>Completes when the endpoint has sent the response headers (the first write or flush).</summary>
  internal Task HeadersSent => _response.Started.Task;

  internal Task Completed => _running ?? Task.CompletedTask;

  /// <summary>The response body as the client reads it; ends when the endpoint finishes the response.</summary>
  internal Stream ResponseStream => _responsePipe.Reader.AsStream();

  public void Dispose() => _abort.Dispose();

  /// <summary>Plays the client hanging up.</summary>
  internal void Disconnect() => _abort.Cancel();

  internal static TestExchange Post(string json, params (string Name, string Value)[] headers)
  {
    var bytes = Encoding.UTF8.GetBytes(json);
    return new TestExchange("POST", new MemoryStream(bytes), bytes.Length, [("Content-Type", "application/json"), ("Accept", "application/json, text/event-stream"), .. headers]);
  }

  internal TestExchange Run(StreamableHttpEndpoint endpoint)
  {
    _running = Task.Run(async () =>
    {
      try
      {
        await endpoint.HandleAsync(this);
      }
      finally
      {
        await _responsePipe.Writer.CompleteAsync();
        _response.Started.TrySetResult();
      }
    });
    return this;
  }

  internal async Task<string> BodyAsync()
  {
    await Completed.WaitAsync(TimeSpan.FromSeconds(10));
    using var reader = new StreamReader(_responsePipe.Reader.AsStream(), Encoding.UTF8);
    return await reader.ReadToEndAsync();
  }

  internal async Task<JsonElement> JsonAsync() => JsonDocument.Parse(await BodyAsync()).RootElement.Clone();

  /// <summary>Reads the response as an event stream, one event at a time.</summary>
  internal SseReader Events() => new(_responsePipe.Reader.AsStream());

  internal sealed class TestRequest(string method, Stream body, long? contentLength) : IHttpRequest
  {
    public string Method { get; } = method;

    public string Path => "/mcp";

    public HttpHeaders Headers { get; } = new();

    public long? ContentLength { get; } = contentLength;

    public Stream Body { get; } = body;

    public IPAddress? RemoteAddress => IPAddress.Loopback;
  }

  internal sealed class TestResponse : IHttpResponse, IDisposable
  {
    private readonly NotifyingStream _body;

    internal TestResponse(PipeWriter writer)
    {
      _body = new NotifyingStream(writer.AsStream(), Started);
    }

    public int StatusCode { get; set; } = 200;

    public HttpHeaders Headers { get; } = new();

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void SetHeader(string name, string value)
    {
      if (Started.Task.IsCompleted)
      {
        throw new InvalidOperationException("Headers were already sent.");
      }

      Headers.Set(name, value);
    }

    public Stream Body => _body;

    public void Dispose() => _body.Dispose();
  }

  private sealed class NotifyingStream(Stream inner, TaskCompletionSource started) : Stream
  {
    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
      get => throw new NotSupportedException();
      set => throw new NotSupportedException();
    }

    public override void Flush() => started.TrySetResult();

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
      started.TrySetResult();
      return inner.FlushAsync(cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
      started.TrySetResult();
      await inner.WriteAsync(buffer, cancellationToken);
    }
  }
}

/// <summary>One parsed server-sent event.</summary>
internal sealed record SseEvent(string? Id, string Data, int? Retry, bool IsComment);

/// <summary>Reads events from a stream the way a client does: lines, blank line ends an event, comments are reported.</summary>
internal sealed class SseReader(Stream stream) : IDisposable
{
  private readonly StreamReader _reader = new(stream, Encoding.UTF8);

  public void Dispose() => _reader.Dispose();

  internal async Task<SseEvent?> NextAsync(TimeSpan? timeout = null)
  {
    using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
    string? id = null;
    int? retry = null;
    var data = new StringBuilder();
    var any = false;
    var comment = false;
    while (true)
    {
      var line = await _reader.ReadLineAsync(cts.Token);
      if (line is null)
      {
        return null;
      }

      if (line.Length == 0)
      {
        if (any || comment)
        {
          return new SseEvent(id, data.ToString(), retry, comment && !any);
        }

        continue;
      }

      if (line[0] == ':')
      {
        comment = true;
        continue;
      }

      var colon = line.IndexOf(':', StringComparison.Ordinal);
      var field = colon < 0 ? line : line[..colon];
      var value = colon < 0 ? string.Empty : line[(colon + 1)..].TrimStart(' ');
      any = true;
      switch (field)
      {
        case "id":
          id = value;
          break;
        case "data":
          if (data.Length > 0)
          {
            data.Append('\n');
          }

          data.Append(value);
          break;
        case "retry":
          retry = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
          break;
      }
    }
  }
}
