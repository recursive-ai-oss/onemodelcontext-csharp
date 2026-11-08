using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.Storage;

// Shared by file-backed stores; no protocol state belongs here.
internal static class AtomicFile
{
  private static readonly object WriteGate = new();
  private static readonly Dictionary<string, int> ActiveWrites = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

  // Entries exist only while writes are in flight, never for every path the process has seen.
  internal static IDisposable BeginWrite(string path)
  {
    lock (WriteGate)
    {
      ActiveWrites[path] = ActiveWrites.GetValueOrDefault(path) + 1;
    }
    return new WriteLease(path);
  }

  private sealed class WriteLease(string path) : IDisposable
  {
    private bool _disposed;
    public void Dispose()
    {
      lock (WriteGate)
      {
        if (_disposed)
        {
          return;
        }
        _disposed = true;
        if (ActiveWrites[path] == 1)
        {
          ActiveWrites.Remove(path);
        }
        else
        {
          ActiveWrites[path]--;
        }
      }
    }
  }

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

  internal static ValueTask<FileStream?> OpenReadAsync(string path, int retries, TimeProvider time, CancellationToken cancellationToken, int bufferSize = 4096, bool asynchronous = true)
  {
    var windows = OperatingSystem.IsWindows();
    return OpenReadCoreAsync(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, asynchronous),
      () => File.Exists(path) || HasPendingReplacement(path, retries, time), windows && File.Exists(path), retries, time, windows, cancellationToken);
  }

  // Windows can temporarily remove the destination name or deny new opens during replacement, even with deletion-sharing readers.
  // Remember evidence only for this operation; keeping a set of previously read paths would grow with every task ever observed.
  internal static async ValueTask<T?> OpenReadCoreAsync<T>(Func<T> open, Func<bool> replacementPending, bool observedFile, int retries, TimeProvider time, bool windows, CancellationToken cancellationToken)
    where T : class
  {
    for (var attempt = 0; ; attempt++)
    {
      cancellationToken.ThrowIfCancellationRequested();
      try
      {
        return open();
      }
      catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
      {
        if (!windows)
        {
          return null;
        }
        var pending = replacementPending();
        observedFile |= pending;
        // Confirm a completely unknown miss once as well: directory metadata can lag the rename briefly.
        if (!observedFile && attempt >= Math.Min(retries, 1))
        {
          return null;
        }
        if (attempt >= retries)
        {
          // No writer and still missing after the budget: it was deleted. An active writer means unavailable, not absent.
          return pending ? throw new IOException("The file remained unavailable during replacement.", ex) : null;
        }
      }
      catch (Exception ex) when (windows && attempt < retries && IsContention(ex))
      {
        observedFile = true;
      }
      await Task.Delay(TimeSpan.FromMilliseconds(10), time, cancellationToken).ConfigureAwait(false);
    }
  }

  internal static bool HasPendingReplacement(string path, int retries, TimeProvider time)
  {
    lock (WriteGate)
    {
      if (ActiveWrites.ContainsKey(path))
      {
        return true;
      }
    }
    // A closed temp is evidence only briefly, not proof of a live writer. Half the delay budget (default 1 s) lets unchanged
    // crash leftovers stop blocking within a read's budget. A local in-flight write is tracked independently of timestamps.
    var recent = TimeSpan.FromMilliseconds(Math.Max(10, retries * 5d));
    var now = time.GetUtcNow();
    try
    {
      foreach (var temporary in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"))
      {
        var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(temporary), TimeSpan.Zero);
        if ((now - modified).Duration() <= recent)
        {
          return true;
        }
      }
      return false;
    }
    catch (DirectoryNotFoundException)
    {
      return false;
    }
    catch (Exception ex) when (IsContention(ex))
    {
      // An inaccessible directory is not evidence of absence; the caller's bounded budget still applies.
      return true;
    }
  }

  private static bool IsContention(Exception ex) =>
    ex is UnauthorizedAccessException || ex is IOException && (ex.HResult & 0xffff) is 32 or 33;

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
      catch (Exception ex) when (attempt < retries && IsContention(ex))
      {
        await Task.Delay(TimeSpan.FromMilliseconds(10), time, cancellationToken).ConfigureAwait(false);
      }
    }
  }
}
