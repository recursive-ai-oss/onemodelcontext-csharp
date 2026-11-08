using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.OAuth;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

public class FileTokenStoreTests
{
  [Fact(Timeout = 20_000)]
  public async Task Replacing_tokens_while_another_instance_reads_keeps_a_complete_document()
  {
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(10));
    var token = timeout.Token;
    var directory = Path.Combine(Path.GetTempPath(), "token-replace-" + Guid.NewGuid().ToString("N"));
    try
    {
      var path = Path.Combine(directory, "tokens.json");
      using var writer = new FileTokenStore(path);
      using var reader = new FileTokenStore(path);
      var record = new TokenRecord { AccessToken = "synthetic-test-token", ClientId = "initial" };
      await writer.SetAsync("entry", record, token);
      using var stopReader = CancellationTokenSource.CreateLinkedTokenSource(token);
      var reading = Task.Run(async () =>
      {
        try
        {
          while (true)
          {
            stopReader.Token.ThrowIfCancellationRequested();
            Assert.NotNull(await reader.GetAsync("entry", stopReader.Token));
          }
        }
        catch (OperationCanceledException) when (stopReader.IsCancellationRequested)
        {
          // The writer has completed or the test reached its deadline.
        }
      }, token);
      try
      {
        for (var i = 0; i < 100; i++)
        {
          record = new TokenRecord { AccessToken = "synthetic-test-token", ClientId = "updated" };
          await writer.SetAsync("entry", record, token);
        }
      }
      finally
      {
        await stopReader.CancelAsync();
        await reading;
      }
      Assert.Equal("updated", (await reader.GetAsync("entry", token))!.ClientId);
    }
    finally
    {
      if (Directory.Exists(directory))
      {
        Directory.Delete(directory, recursive: true);
      }
    }
  }
}
