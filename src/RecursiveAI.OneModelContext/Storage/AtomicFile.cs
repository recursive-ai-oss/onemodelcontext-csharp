using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.Storage;

// Shared by file-backed stores; no protocol state belongs here.
internal static class AtomicFile
{
  internal static ValueTask CommitAsync(string temporary, string path, int retries, TimeProvider time, CancellationToken cancellationToken) =>
    RetryAsync(() =>
    {
      if (File.Exists(path))
      {
        File.Replace(temporary, path, destinationBackupFileName: null);
      }
      else
      {
        File.Move(temporary, path);
      }
    }, retries, time, cancellationToken);

  // Even deletion-sharing readers can briefly conflict with replacement on Windows. Never retry forever or hide the final error.
  internal static async ValueTask RetryAsync(Action commit, int retries, TimeProvider time, CancellationToken cancellationToken)
  {
    for (var attempt = 0; ; attempt++)
    {
      cancellationToken.ThrowIfCancellationRequested();
      try
      {
        commit();
        return;
      }
      catch (Exception ex) when (attempt < retries && (ex is UnauthorizedAccessException || ex is IOException && (ex.HResult & 0xffff) is 32 or 33))
      {
        await Task.Delay(TimeSpan.FromMilliseconds(10), time, cancellationToken).ConfigureAwait(false);
      }
    }
  }
}
