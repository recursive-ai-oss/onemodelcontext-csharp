using ToolBridge;
using RecursiveAI.OneModelContext;
using Xunit;

namespace RecursiveAI.OneModelContext.SamplesTests;

public class ToolBridgeTests
{
  [Theory]
  [InlineData(EraPreference.Modern, "2026-07-28")]
  [InlineData(EraPreference.Legacy, "2025-11-25")]
  public async Task ApprovedCallRunsExactlyOnce(EraPreference era, string version)
  {
    var (negotiated, text, questions, deleted) = await Demo.RunAsync(era, approve: true, TextWriter.Null);
    Assert.Equal(version, negotiated);
    Assert.Equal("Deleted.", text);

    // On the modern era the handler is replayed after the answer; the side effect must still happen once.
    Assert.Single(questions);
    Assert.Equal(["deleted /tmp/old.log"], deleted);
  }

  [Theory]
  [InlineData(EraPreference.Modern)]
  [InlineData(EraPreference.Legacy)]
  public async Task DeclinedCallNeverRuns(EraPreference era)
  {
    var (_, text, questions, deleted) = await Demo.RunAsync(era, approve: false, TextWriter.Null);
    Assert.Equal("The user declined.", text);
    Assert.Single(questions);
    Assert.Empty(deleted);
  }
}
