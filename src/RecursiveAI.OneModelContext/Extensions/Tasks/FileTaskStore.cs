using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.Extensions.Tasks;

[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TaskRecord))]
internal sealed partial class TaskStoreJsonContext : JsonSerializerContext;

/// <summary>
/// Keeps each task in a file of its own in a directory, so tasks survive a restart and several server processes on one machine can share
/// them. A change takes a lock file for the directory, then writes a temporary file and renames it over the old one, so a reader never sees
/// half a task.
/// </summary>
/// <remarks>
/// The files hold task results, which can be private, so the directory and files are readable by the owner only on Unix. Task ids are
/// checked before they become file names; a client cannot name a path. Expired tasks are removed by <see cref="DeleteExpiredAsync"/>, which
/// the server calls as tasks are created. The store does not span machines: use a database store for that.
/// </remarks>
[Experimental(ExperimentalIds.Tasks)]
public sealed class FileTaskStore : ITaskStore, IDisposable
{
  private const string Extension = ".task.json";
  private const string LockName = ".lock";
  private readonly string _directory;
  private readonly int _capacity;
  private readonly int _replacementRetries;
  private readonly TimeProvider _time;
  private readonly SemaphoreSlim _local = new(1, 1);

  /// <summary>Creates a store.</summary>
  /// <param name="directory">The directory; created when missing.</param>
  /// <param name="capacity">Most tasks held at once; <see cref="CreateAsync"/> refuses more. Default 10,000.</param>
  /// <param name="replacementRetries">Retries on replacement sharing/access failures, 10 ms apart; default 200 (at most 2 seconds of delay). Zero disables retries. The final error is thrown.</param>
  /// <param name="timeProvider">Clock for replacement retries; null uses <see cref="TimeProvider.System"/>.</param>
  public FileTaskStore(string directory, int capacity = 10_000, int replacementRetries = 200, TimeProvider? timeProvider = null)
  {
    ArgumentException.ThrowIfNullOrEmpty(directory);
    ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
    _directory = Path.GetFullPath(directory);
    ArgumentOutOfRangeException.ThrowIfNegative(replacementRetries);
    _capacity = capacity;
    _replacementRetries = replacementRetries;
    _time = timeProvider ?? TimeProvider.System;
    if (OperatingSystem.IsWindows())
    {
      Directory.CreateDirectory(_directory);
    }
    else
    {
      Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
  }

  /// <summary>Releases the store. Tasks stay in the directory.</summary>
  public void Dispose() => _local.Dispose();

  /// <inheritdoc />
  public async ValueTask CreateAsync(TaskRecord task, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(task);
    var path = PathFor(task.TaskId) ?? throw new ArgumentException("The task id is not a valid file name.", nameof(task));
    await using var gate = await EnterAsync(cancellationToken).ConfigureAwait(false);
    if (File.Exists(path))
    {
      throw new InvalidOperationException("A task with this id exists already.");
    }

    var count = 0;
    foreach (var _ in Directory.EnumerateFiles(_directory, "*" + Extension))
    {
      count++;
    }

    if (count >= _capacity)
    {
      throw new InvalidOperationException("The task store is full (" + _capacity + " tasks).");
    }

    await WriteAsync(path, task with { Version = 0 }, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async ValueTask<TaskRecord?> GetAsync(string taskId, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(taskId);
    var path = PathFor(taskId);
    return path is null ? null : await ReadAsync(path, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async ValueTask<TaskRecord?> UpdateAsync(TaskRecord task, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(task);
    var path = PathFor(task.TaskId);
    if (path is null)
    {
      return null;
    }

    await using var gate = await EnterAsync(cancellationToken).ConfigureAwait(false);
    var current = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
    if (current is null || current.Version != task.Version)
    {
      return null;
    }

    var next = task with { Version = current.Version + 1 };
    await WriteAsync(path, next, cancellationToken).ConfigureAwait(false);
    return next;
  }

  /// <inheritdoc />
  public async ValueTask<bool> DeleteAsync(string taskId, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(taskId);
    var path = PathFor(taskId);
    if (path is null)
    {
      return false;
    }

    await using var gate = await EnterAsync(cancellationToken).ConfigureAwait(false);
    if (!File.Exists(path))
    {
      return false;
    }

    File.Delete(path);
    return true;
  }

  /// <inheritdoc />
  public async ValueTask<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
  {
    await using var gate = await EnterAsync(cancellationToken).ConfigureAwait(false);
    var removed = 0;
    foreach (var path in Directory.EnumerateFiles(_directory, "*" + Extension))
    {
      var task = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
      if (task?.ExpiresAt is { } at && at <= now)
      {
        File.Delete(path);
        removed++;
      }
    }

    return removed;
  }

  // The id becomes part of a file name, so only the characters the server itself generates are allowed.
  private string? PathFor(string taskId)
  {
    if (taskId.Length is 0 or > 128)
    {
      return null;
    }

    foreach (var c in taskId)
    {
      if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
      {
        return null;
      }
    }

    return Path.Combine(_directory, taskId + Extension);
  }

  private static async ValueTask<TaskRecord?> ReadAsync(string path, CancellationToken cancellationToken)
  {
    try
    {
      await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
      return await JsonSerializer.DeserializeAsync(stream, TaskStoreJsonContext.Default.TaskRecord, cancellationToken).ConfigureAwait(false);
    }
    catch (FileNotFoundException)
    {
      return null;
    }
    catch (DirectoryNotFoundException)
    {
      return null;
    }
    catch (JsonException)
    {
      // A file that is not a task (damaged by something else) is treated as no task rather than failing every poll.
      return null;
    }
  }

  // The temporary file is created with its final permissions, then renamed: readers see the old task or the new one.
  private async ValueTask WriteAsync(string path, TaskRecord task, CancellationToken cancellationToken)
  {
    var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
    if (!OperatingSystem.IsWindows())
    {
      options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    }

    try
    {
      await using (var stream = new FileStream(temporary, options))
      {
        await JsonSerializer.SerializeAsync(stream, task, TaskStoreJsonContext.Default.TaskRecord, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
      }

      await Storage.AtomicFile.CommitAsync(temporary, path, _replacementRetries, _time, cancellationToken).ConfigureAwait(false);
    }
    catch
    {
      try
      {
        File.Delete(temporary);
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
      {
        // Preserve the original write failure if cleanup also fails.
      }

      throw;
    }
  }

  // One writer at a time: a semaphore inside this process and an exclusively opened lock file between processes.
  private async ValueTask<Gate> EnterAsync(CancellationToken cancellationToken)
  {
    await _local.WaitAsync(cancellationToken).ConfigureAwait(false);
    var lockPath = Path.Combine(_directory, LockName);
    try
    {
      for (var attempt = 0; ; attempt++)
      {
        try
        {
          var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
          if (!OperatingSystem.IsWindows())
          {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
          }

          return new Gate(_local, new FileStream(lockPath, options));
        }
        catch (IOException) when (attempt < 200)
        {
          // Another process holds the lock; waiting briefly is cheaper than failing the request.
          await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
      }
    }
    catch
    {
      _local.Release();
      throw;
    }
  }

  private readonly struct Gate(SemaphoreSlim local, FileStream lockFile) : IAsyncDisposable
  {
    public async ValueTask DisposeAsync()
    {
      await lockFile.DisposeAsync().ConfigureAwait(false);
      local.Release();
    }
  }
}
