using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace RecursiveAI.OneModelContext.EntityFrameworkCore.Tests;

/// <summary>A host's own context with the stores added, as the documentation shows.</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
  protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyContextStores();
}

/// <summary>A context that forgot to apply the stores.</summary>
public sealed class BareDbContext(DbContextOptions<BareDbContext> options) : DbContext(options);

/// <summary>A SQLite file shared by as many factories as a test wants: each factory is one "instance" with its own connections.</summary>
public sealed class Database : IDisposable
{
  private readonly string _directory = Path.Combine(Path.GetTempPath(), "omc-ef-" + Guid.NewGuid().ToString("N"));

  public Database()
  {
    Directory.CreateDirectory(_directory);
    ConnectionString = $"Data Source={Path.Combine(_directory, "stores.db")};Default Timeout=30";
    using var db = NewFactory().CreateDbContext();
    db.Database.EnsureCreated();
  }

  public string ConnectionString { get; }

  /// <summary>A new factory over its own pool of connections, standing for one server instance.</summary>
  public IDbContextFactory<AppDbContext> NewFactory() => new Factory(ConnectionString);

  public void Dispose()
  {
    SqliteConnection.ClearAllPools();
    try
    {
      Directory.Delete(_directory, recursive: true);
    }
    catch (IOException)
    {
      // A test host may still hold the file for a moment; the folder is in the temp directory.
    }
  }

  private sealed class Factory(string connectionString) : IDbContextFactory<AppDbContext>
  {
    private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;

    public AppDbContext CreateDbContext() => new(_options);
  }
}

/// <summary>A clock the tests move by hand.</summary>
public sealed class ManualClock : TimeProvider
{
  private DateTimeOffset _now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

  public override DateTimeOffset GetUtcNow() => _now;

  public void Advance(TimeSpan by) => _now += by;
}
