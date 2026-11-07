using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneLite;

namespace RecursiveAI.OneModelContext.OneLite;

/// <summary>
/// The OneLite database behind the stores: opens or attaches to it and creates or checks the schema. The stores' tables carry their own
/// version in a small table of ours, not in <c>PRAGMA user_version</c>, so the file can also hold the host's own tables and migrations.
/// </summary>
internal sealed class StoreDatabase : IAsyncDisposable
{
  /// <summary>The schema version this build writes and the newest it reads. Append to <see cref="Migrations"/> to raise it.</summary>
  internal static int CurrentVersion => Migrations.Length;

  // Never edit a script that has shipped; append a new one. Version n is applied when the stored version is below n.
  private static readonly string[] Migrations =
  [
    """
    CREATE TABLE ModelContextClients (
      ClientId TEXT NOT NULL PRIMARY KEY,
      Name TEXT,
      RedirectUrisJson TEXT NOT NULL,
      TokenEndpointAuthMethod TEXT NOT NULL,
      SecretHash TEXT,
      JwksJson TEXT,
      JwksUri TEXT,
      GrantTypesJson TEXT NOT NULL,
      ScopesJson TEXT,
      CreatedAtTicks INTEGER NOT NULL
    ) STRICT;
    CREATE TABLE ModelContextAuthorizationCodes (
      CodeHash TEXT NOT NULL PRIMARY KEY,
      ClientId TEXT NOT NULL,
      RedirectUri TEXT NOT NULL,
      RedirectUriSent INTEGER NOT NULL,
      Subject TEXT NOT NULL,
      Scope TEXT NOT NULL,
      Resource TEXT NOT NULL,
      CodeChallenge TEXT NOT NULL,
      FamilyId TEXT NOT NULL,
      ClaimsJson TEXT NOT NULL,
      ExpiresAtTicks INTEGER NOT NULL,
      FamilyExpiresAtTicks INTEGER NOT NULL,
      Redeemed INTEGER NOT NULL
    ) STRICT;
    CREATE TABLE ModelContextRefreshTokens (
      TokenHash TEXT NOT NULL PRIMARY KEY,
      FamilyId TEXT NOT NULL,
      ClientId TEXT NOT NULL,
      Subject TEXT NOT NULL,
      Scope TEXT NOT NULL,
      Resource TEXT NOT NULL,
      ClaimsJson TEXT NOT NULL,
      ExpiresAtTicks INTEGER NOT NULL,
      FamilyExpiresAtTicks INTEGER NOT NULL,
      Used INTEGER NOT NULL
    ) STRICT;
    CREATE TABLE ModelContextRevocations (
      KeyHash TEXT NOT NULL PRIMARY KEY,
      ExpiresAtTicks INTEGER NOT NULL
    ) STRICT;
    CREATE TABLE ModelContextSeenAssertions (
      KeyHash TEXT NOT NULL PRIMARY KEY,
      ExpiresAtTicks INTEGER NOT NULL
    ) STRICT;
    CREATE TABLE ModelContextTokenRecords (
      KeyHash TEXT NOT NULL PRIMARY KEY,
      Data BLOB NOT NULL,
      UpdatedAtTicks INTEGER NOT NULL
    ) STRICT;
    CREATE TABLE ModelContextTasks (
      TaskId TEXT NOT NULL PRIMARY KEY,
      Owner TEXT NOT NULL,
      ToolName TEXT NOT NULL,
      Status TEXT NOT NULL,
      StatusMessage TEXT,
      CreatedAtTicks INTEGER NOT NULL,
      LastUpdatedAtTicks INTEGER NOT NULL,
      TtlMs INTEGER,
      ExpiresAtTicks INTEGER,
      PollIntervalMs INTEGER,
      ResultJson TEXT,
      ErrorJson TEXT,
      InputRequestsJson TEXT,
      InputResponsesJson TEXT,
      UsedInputKeysJson TEXT,
      CancelRequested INTEGER NOT NULL,
      Version INTEGER NOT NULL
    ) STRICT;
    """,
  ];

  private readonly bool _owns;

  private StoreDatabase(Database db, bool owns)
  {
    Db = db;
    _owns = owns;
  }

  internal Database Db { get; }

  /// <summary>Opens (creating when missing) the database file and brings the store schema up to date.</summary>
  internal static async ValueTask<StoreDatabase> OpenAsync(string path, CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrEmpty(path);
    var db = await Database.OpenAsync(path, ct: cancellationToken).ConfigureAwait(false);
    var store = new StoreDatabase(db, owns: true);
    try
    {
      await store.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
      return store;
    }
    catch
    {
      await store.DisposeAsync().ConfigureAwait(false);
      throw;
    }
  }

  /// <summary>Uses a database the host opened, which stays the host's to dispose.</summary>
  internal static async ValueTask<StoreDatabase> AttachAsync(Database db, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(db);
    var store = new StoreDatabase(db, owns: false);
    await store.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
    return store;
  }

  private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
  {
    // One write transaction takes the database's write lock, so processes opening the same file at once create the tables once.
    await Db.WriteAsync(async tx =>
    {
      await tx.ExecuteScriptAsync(
        "CREATE TABLE IF NOT EXISTS ModelContextSchema (Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1), Version INTEGER NOT NULL) STRICT",
        cancellationToken).ConfigureAwait(false);
      var found = await tx.ScalarAsync<long?>($"SELECT Version FROM ModelContextSchema WHERE Id = 1", cancellationToken).ConfigureAwait(false);
      var current = found ?? 0;
      if (current > CurrentVersion)
      {
        throw new InvalidOperationException(
          $"The OneModelContext store tables in this database are schema version {current}, but this build understands up to {CurrentVersion}. "
          + "Update the RecursiveAI.OneModelContext.OneLite package; the database was written by a newer version.");
      }

      for (var n = (int)current + 1; n <= CurrentVersion; n++)
      {
        await tx.ExecuteScriptAsync(Migrations[n - 1], cancellationToken).ConfigureAwait(false);
      }

      if (current < CurrentVersion)
      {
        var version = (long)CurrentVersion;
        await tx.ExecuteAsync($"INSERT INTO ModelContextSchema (Id, Version) VALUES (1, {version}) ON CONFLICT (Id) DO UPDATE SET Version = excluded.Version", cancellationToken).ConfigureAwait(false);
      }
    }, cancellationToken).ConfigureAwait(false);
  }

  public async ValueTask DisposeAsync()
  {
    if (_owns)
    {
      await Db.DisposeAsync().ConfigureAwait(false);
    }
  }
}

[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(TokenRecordDto))]
internal sealed partial class StoreJson : JsonSerializerContext;

/// <summary>The stored form of a <see cref="OAuth.TokenRecord"/>; the record's own writer is internal to the core package.</summary>
internal sealed class TokenRecordDto
{
  public string? Issuer { get; set; }

  public string? ClientId { get; set; }

  public string? ClientSecret { get; set; }

  public string? AccessToken { get; set; }

  public string? RefreshToken { get; set; }

  public long? ExpiresAtTicks { get; set; }

  public string? Resource { get; set; }

  public string? Scope { get; set; }

  public bool Dynamic { get; set; }
}

internal static class StoreKeys
{
  internal const int MaxClientIdLength = 450;

  internal const int MaxTaskIdLength = 128;

  /// <summary>SHA-256, base64url: a fixed-length key for values whose length a caller controls (assertion ids, token store keys).</summary>
  internal static string Hash(string key) => Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

  internal static string[] ReadList(string json) => JsonSerializer.Deserialize(json, StoreJson.Default.StringArray) ?? [];

  internal static string WriteList(IEnumerable<string> values) =>
    JsonSerializer.Serialize(System.Linq.Enumerable.ToArray(values), StoreJson.Default.StringArray);

  internal static Dictionary<string, string> ReadMap(string json) =>
    JsonSerializer.Deserialize(json, StoreJson.Default.DictionaryStringString) ?? [];

  internal static string WriteMap(IReadOnlyDictionary<string, string> values) =>
    JsonSerializer.Serialize(new Dictionary<string, string>(values), StoreJson.Default.DictionaryStringString);

  internal static DateTimeOffset At(long ticks) => new(ticks, TimeSpan.Zero);
}
