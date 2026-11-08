using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Storage;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

public class AtomicFileTests
{
  [Fact]
  public async Task A_transient_replacement_failure_is_retried_on_the_injected_clock()
  {
    var clock = new ManualTimeProvider();
    var calls = 0;
    var committing = AtomicFile.RetryAsync(() =>
    {
      if (++calls == 1)
      {
        throw new UnauthorizedAccessException("Synthetic replacement contention.");
      }
    }, 1, clock, TestContext.Current.CancellationToken).AsTask();
    Assert.False(committing.IsCompleted);
    clock.Advance(TimeSpan.FromMilliseconds(10));
    await committing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    Assert.Equal(2, calls);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task The_final_access_or_sharing_failure_is_not_hidden(bool accessDenied)
  {
    var clock = new ManualTimeProvider();
    Exception failure = accessDenied
      ? new UnauthorizedAccessException("Synthetic access failure.")
      : new IOException("Synthetic sharing failure.", unchecked((int)0x80070020));
    var calls = 0;
    var committing = AtomicFile.RetryAsync(() => { calls++; throw failure; }, 1, clock, TestContext.Current.CancellationToken).AsTask();
    clock.Advance(TimeSpan.FromMilliseconds(10));
    var thrown = await Record.ExceptionAsync(() => committing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    Assert.Same(failure, thrown);
    Assert.Equal(2, calls);
  }

  [Fact]
  public async Task Cancellation_interrupts_the_retry_delay()
  {
    var clock = new ManualTimeProvider();
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    var calls = 0;
    var committing = AtomicFile.RetryAsync(() => { calls++; throw new UnauthorizedAccessException("Synthetic contention."); }, 200, clock, stop.Token).AsTask();
    await stop.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => committing);
    Assert.Equal(1, calls);
  }

  [Theory]
  [InlineData(0, true, false)]
  [InlineData(0, false, true)]
  [InlineData(1, true, false)]
  [InlineData(2, false, false)]
  [InlineData(3, false, false)]
  public async Task Windows_read_open_retries_replacement_misses_or_contention(int kind, bool observed, bool pending)
  {
    var clock = new ManualTimeProvider();
    var value = new object();
    var calls = 0;
    var opening = AtomicFile.OpenReadCoreAsync(() =>
    {
      if (++calls == 1)
      {
        throw kind switch
        {
          0 => new FileNotFoundException("Synthetic replacement gap."),
          1 => new DirectoryNotFoundException("Synthetic replacement gap."),
          2 => new IOException("Synthetic sharing violation.", unchecked((int)0x80070020)),
          _ => (Exception)new UnauthorizedAccessException("Synthetic access contention."),
        };
      }
      return value;
    }, () => pending, observed, 1, clock, windows: true, TestContext.Current.CancellationToken).AsTask();
    Assert.False(opening.IsCompleted);
    clock.Advance(TimeSpan.FromMilliseconds(10));
    Assert.Same(value, await opening.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    Assert.Equal(2, calls);
  }

  [Fact]
  public async Task A_genuinely_unknown_missing_file_gets_only_one_confirmation_not_the_whole_budget()
  {
    var clock = new ManualTimeProvider();
    var calls = 0;
    var opening = AtomicFile.OpenReadCoreAsync<object>(() => { calls++; throw new FileNotFoundException(); },
      () => false, observedFile: false, 200, clock, windows: true, TestContext.Current.CancellationToken).AsTask();
    Assert.False(opening.IsCompleted);
    clock.Advance(TimeSpan.FromMilliseconds(10));
    Assert.Null(await opening.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    Assert.Equal(2, calls);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task Missing_reads_exhaust_the_budget_without_reporting_an_active_replacement_as_absent(bool pending)
  {
    var clock = new ManualTimeProvider();
    var calls = 0;
    var opening = AtomicFile.OpenReadCoreAsync<object>(() => { calls++; throw new FileNotFoundException(); },
      () => pending, observedFile: true, 1, clock, windows: true, TestContext.Current.CancellationToken).AsTask();
    Assert.False(opening.IsCompleted);
    clock.Advance(TimeSpan.FromMilliseconds(10));
    if (pending)
    {
      await Assert.ThrowsAsync<IOException>(() => opening);
    }
    else
    {
      Assert.Null(await opening.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }
    Assert.Equal(2, calls);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task Linux_reads_keep_immediate_missing_or_sharing_failure_behavior(bool sharing)
  {
    var calls = 0;
    var opening = AtomicFile.OpenReadCoreAsync<object>(() =>
    {
      calls++;
      throw sharing ? (Exception)new IOException("Synthetic sharing violation.", unchecked((int)0x80070020)) : new FileNotFoundException();
    }, () => true, observedFile: true, 200, new ManualTimeProvider(), windows: false, TestContext.Current.CancellationToken).AsTask();
    if (sharing)
    {
      await Assert.ThrowsAsync<IOException>(() => opening);
    }
    else
    {
      Assert.Null(await opening);
    }
    Assert.Equal(1, calls);
  }

  [Fact]
  public async Task Read_retry_delays_are_cancelled_without_using_the_rest_of_the_budget()
  {
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    var calls = 0;
    var opening = AtomicFile.OpenReadCoreAsync<object>(() => { calls++; throw new FileNotFoundException(); },
      () => true, observedFile: true, 200, new ManualTimeProvider(), windows: true, stop.Token).AsTask();
    await stop.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
    Assert.Equal(1, calls);
  }

}
