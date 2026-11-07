using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.Sse;

/// <summary>
/// Writes a Server-Sent Events stream (W3C HTML Living Standard, "Server-sent events") to a stream. Every event is formatted into one
/// pooled buffer, written in a single call and flushed, so a reader sees whole events. Writes are serialized: it is safe to call from
/// several tasks.
/// </summary>
public sealed class SseWriter : IDisposable
{
  private readonly Stream _output;
  private readonly SemaphoreSlim _gate = new(1, 1);

  /// <summary>Creates a writer over a response stream.</summary>
  /// <param name="output">Where the event stream goes. It is not disposed by the writer.</param>
  public SseWriter(Stream output)
  {
    ArgumentNullException.ThrowIfNull(output);
    _output = output;
  }

  /// <summary>Writes one event.</summary>
  /// <param name="data">The event data as UTF-8. A newline inside it becomes a further <c>data:</c> line, so the reader joins it back. Empty data writes an empty <c>data</c> field.</param>
  /// <param name="id">The event id the client sends back as <c>Last-Event-ID</c>, or null for none.</param>
  /// <param name="eventName">The event type, or null for the default <c>message</c>.</param>
  /// <param name="cancellationToken">Cancels the write.</param>
  /// <exception cref="ArgumentException"><paramref name="id"/> or <paramref name="eventName"/> contains a line break.</exception>
  public async ValueTask WriteEventAsync(ReadOnlyMemory<byte> data, string? id = null, string? eventName = null, CancellationToken cancellationToken = default)
  {
    RejectLineBreak(id, nameof(id));
    RejectLineBreak(eventName, nameof(eventName));
    var idBytes = id is null ? 0 : Encoding.UTF8.GetByteCount(id);
    var nameBytes = eventName is null ? 0 : Encoding.UTF8.GetByteCount(eventName);
    // Upper bound: field names and separators, one "data: " prefix per line, and the closing blank line.
    var lines = 1 + CountLineBreaks(data.Span);
    var capacity = idBytes + nameBytes + data.Length + (lines * 7) + 32;
    var rented = ArrayPool<byte>.Shared.Rent(capacity);
    try
    {
      var length = 0;
      if (id is not null)
      {
        length += Append(rented, length, "id: "u8);
        length += Encoding.UTF8.GetBytes(id, rented.AsSpan(length));
        rented[length++] = (byte)'\n';
      }

      if (eventName is not null)
      {
        length += Append(rented, length, "event: "u8);
        length += Encoding.UTF8.GetBytes(eventName, rented.AsSpan(length));
        rented[length++] = (byte)'\n';
      }

      if (data.Length == 0)
      {
        length += Append(rented, length, "data:\n"u8);
      }
      else
      {
        var span = data.Span;
        while (true)
        {
          var end = span.IndexOfAny((byte)'\n', (byte)'\r');
          var line = end < 0 ? span : span[..end];
          length += Append(rented, length, "data: "u8);
          length += Append(rented, length, line);
          rented[length++] = (byte)'\n';
          if (end < 0)
          {
            break;
          }

          // A CRLF pair is one line break.
          var skip = span[end] == (byte)'\r' && end + 1 < span.Length && span[end + 1] == (byte)'\n' ? end + 2 : end + 1;
          span = span[skip..];
        }
      }

      rented[length++] = (byte)'\n';
      await WriteAsync(rented.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
    }
    finally
    {
      ArrayPool<byte>.Shared.Return(rented, clearArray: true);
    }
  }

  /// <summary>Writes a <c>retry</c> field telling the client how long to wait before reconnecting, in milliseconds.</summary>
  /// <param name="milliseconds">The reconnection delay.</param>
  /// <param name="cancellationToken">Cancels the write.</param>
  public ValueTask WriteRetryAsync(int milliseconds, CancellationToken cancellationToken = default)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
    return WriteAsync(Encoding.UTF8.GetBytes("retry: " + milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n\n"), cancellationToken);
  }

  /// <summary>Writes a comment line, which readers ignore: a keep-alive that stops intermediaries from closing an idle stream.</summary>
  /// <param name="cancellationToken">Cancels the write.</param>
  public ValueTask WriteCommentAsync(CancellationToken cancellationToken = default) =>
    WriteAsync(":\n\n"u8.ToArray(), cancellationToken);

  /// <inheritdoc />
  public void Dispose() => _gate.Dispose();

  private async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
  {
    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      await _output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
      await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    finally
    {
      _gate.Release();
    }
  }

  private static int Append(byte[] buffer, int offset, ReadOnlySpan<byte> bytes)
  {
    bytes.CopyTo(buffer.AsSpan(offset));
    return bytes.Length;
  }

  private static int CountLineBreaks(ReadOnlySpan<byte> data)
  {
    var count = 0;
    foreach (var b in data)
    {
      if (b is (byte)'\n' or (byte)'\r')
      {
        count++;
      }
    }

    return count;
  }

  private static void RejectLineBreak(string? value, string name)
  {
    if (value is not null && value.AsSpan().IndexOfAny('\r', '\n') >= 0)
    {
      throw new ArgumentException("An SSE field cannot contain a line break.", name);
    }
  }
}
