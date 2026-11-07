using RecursiveAI.OneLite;
using RecursiveAI.OneModelContext.OAuth;

namespace RecursiveAI.OneModelContext.OneLite.Tests;

/// <summary>One database file in a temp folder, opened by as many stores as a test wants: each store is one "instance" with its own connection.</summary>
public sealed class StoreFile : IAsyncDisposable
{
  private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "omc-onelite-" + Guid.NewGuid().ToString("N"));
  private readonly List<IAsyncDisposable> _open = [];

  public StoreFile()
  {
    Directory.CreateDirectory(_directory);
    Path = System.IO.Path.Combine(_directory, "stores.db");
  }

  public string Path { get; }

  public async Task<OneLiteAuthorizationStore> AuthorizationAsync()
  {
    var store = await OneLiteAuthorizationStore.OpenAsync(Path);
    _open.Add(store);
    return store;
  }

  public async Task<OneLiteTokenStore> TokenAsync(ITokenProtector? protector = null)
  {
    var store = await OneLiteTokenStore.OpenAsync(Path, protector);
    _open.Add(store);
    return store;
  }

  public async Task<OneLiteTaskStore> TaskAsync(int capacity = 10_000)
  {
    var store = await OneLiteTaskStore.OpenAsync(Path, capacity);
    _open.Add(store);
    return store;
  }

  /// <summary>A plain connection to the same file, to look at what is stored or to change it behind the stores' backs.</summary>
  public Database Raw() => Database.OpenDefault(Path);

  public async Task DisposeStoresAsync()
  {
    foreach (var s in _open)
    {
      await s.DisposeAsync();
    }

    _open.Clear();
  }

  public async ValueTask DisposeAsync()
  {
    await DisposeStoresAsync();
    try
    {
      Directory.Delete(_directory, recursive: true);
    }
    catch (IOException)
    {
      // The folder is in the temp directory; a test host may hold the file for a moment.
    }
  }
}

/// <summary>A clock the tests move by hand.</summary>
public sealed class ManualClock : TimeProvider
{
  private DateTimeOffset _now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

  public override DateTimeOffset GetUtcNow() => _now;

  public void Advance(TimeSpan by) => _now += by;
}
