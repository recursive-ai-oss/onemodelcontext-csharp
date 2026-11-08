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
}
