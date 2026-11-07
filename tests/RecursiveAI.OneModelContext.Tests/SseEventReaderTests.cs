using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Sse;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

public class SseEventReaderTests
{
  private static SseEventReader ReaderOf(string text, int max = SseEventReader.DefaultMaxEventBytes) =>
    new(new MemoryStream(Encoding.UTF8.GetBytes(text)), max);

  private static async Task<(string Name, string Data)[]> AllAsync(SseEventReader reader)
  {
    var events = new System.Collections.Generic.List<(string, string)>();
    while (true)
    {
      var read = await reader.ReadAsync(TestContext.Current.CancellationToken);
      if (read.Status != SseReadStatus.Event)
      {
        return [.. events];
      }

      events.Add((read.Event.EventName, Encoding.UTF8.GetString(read.Event.Data.Span)));
    }
  }

  [Theory]
  [InlineData("data: a\n\ndata: b\n\n")]
  [InlineData("data: a\r\n\r\ndata: b\r\n\r\n")]
  [InlineData("data: a\r\rdata: b\r\r")]
  [InlineData("data:a\n\ndata:b\n\n")]
  public async Task Lines_end_in_lf_crlf_or_cr_and_one_space_after_the_colon_is_dropped(string text)
  {
    using var reader = ReaderOf(text);

    Assert.Equal([("message", "a"), ("message", "b")], await AllAsync(reader));
  }

  [Fact]
  public async Task Several_data_lines_join_with_a_line_feed_and_the_event_name_is_kept()
  {
    using var reader = ReaderOf("event: endpoint\ndata: one\ndata: two\n\n");

    Assert.Equal([("endpoint", "one\ntwo")], await AllAsync(reader));
  }

  [Fact]
  public async Task Comments_unknown_fields_and_a_byte_order_mark_are_ignored()
  {
    using var reader = ReaderOf("\uFEFF: keep-alive\nfoo: bar\nnotafield\ndata: x\n\n");

    Assert.Equal([("message", "x")], await AllAsync(reader));
  }

  [Fact]
  public async Task An_event_without_data_still_sets_the_last_event_id_and_the_retry_wait()
  {
    using var reader = ReaderOf("id: 41\nretry: 250\ndata:\n\nid: 42\n\n");

    var read = await reader.ReadAsync(TestContext.Current.CancellationToken);
    Assert.Equal(SseReadStatus.Event, read.Status);
    Assert.True(read.Event.Data.IsEmpty);
    Assert.Equal("41", reader.LastEventId);
    Assert.Equal(250, reader.RetryMilliseconds);

    Assert.Equal(SseReadStatus.EndOfStream, (await reader.ReadAsync(TestContext.Current.CancellationToken)).Status);
    Assert.Equal("42", reader.LastEventId);
  }

  [Fact]
  public async Task An_unfinished_event_at_the_end_of_the_stream_is_discarded()
  {
    using var reader = ReaderOf("data: complete\n\ndata: cut off");

    Assert.Equal([("message", "complete")], await AllAsync(reader));
  }

  [Fact]
  public async Task Bytes_arriving_one_at_a_time_give_the_same_events()
  {
    var text = "\uFEFFid: 1\r\nevent: message\r\ndata: héllo\r\ndata: wörld\r\n\r\n: c\r\ndata: z\r\n\r\n";
    using var reader = new SseEventReader(new OneByteStream(Encoding.UTF8.GetBytes(text)));

    Assert.Equal([("message", "héllo\nwörld"), ("message", "z")], await AllAsync(reader));
  }

  [Fact]
  public async Task An_event_larger_than_the_bound_ends_the_reader()
  {
    using var reader = ReaderOf("data: " + new string('x', 300) + "\n\ndata: ok\n\n", max: 100);

    Assert.Equal(SseReadStatus.TooLarge, (await reader.ReadAsync(TestContext.Current.CancellationToken)).Status);
    Assert.Equal(SseReadStatus.EndOfStream, (await reader.ReadAsync(TestContext.Current.CancellationToken)).Status);
  }

  [Fact]
  public async Task Many_small_data_lines_add_up_to_the_bound()
  {
    using var reader = ReaderOf(string.Concat(System.Linq.Enumerable.Repeat("data: 0123456789\n", 50)) + "\n", max: 100);

    Assert.Equal(SseReadStatus.TooLarge, (await reader.ReadAsync(TestContext.Current.CancellationToken)).Status);
  }

  [Fact]
  public async Task A_line_without_a_newline_cannot_grow_past_the_bound()
  {
    using var reader = new SseEventReader(new RepeatingStream((byte)'a'), 1000);

    Assert.Equal(SseReadStatus.TooLarge, (await reader.ReadAsync(TestContext.Current.CancellationToken)).Status);
  }

  [Fact]
  public async Task Junk_lines_do_not_stop_later_events()
  {
    using var reader = ReaderOf(":\n:\nretry: abc\nretry: -4\nid: a\0b\n\x01\x02: x\ndata: fine\n\n");

    Assert.Equal([("message", "fine")], await AllAsync(reader));
    Assert.Null(reader.RetryMilliseconds);
    Assert.Null(reader.LastEventId);
  }

  private sealed class OneByteStream(byte[] bytes) : Stream
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

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default)
    {
      if (_position >= bytes.Length)
      {
        return ValueTask.FromResult(0);
      }

      buffer.Span[0] = bytes[_position++];
      return ValueTask.FromResult(1);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }

  private sealed class RepeatingStream(byte value) : Stream
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

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default)
    {
      buffer.Span.Fill(value);
      return ValueTask.FromResult(buffer.Length);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
