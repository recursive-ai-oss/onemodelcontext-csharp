using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneLite;
using RecursiveAI.OneModelContext.OAuth;

namespace RecursiveAI.OneModelContext.OneLite;

[OneLiteRow]
internal sealed class TokenDataRow
{
  public byte[] Data { get; set; } = [];
}

/// <summary>
/// An <see cref="ITokenStore"/> in one embedded OneLite database file, for applications that sign in to upstream servers on behalf of
/// their users. Each record is one row holding the whole <see cref="TokenRecord"/> as a single value, sealed by the
/// <see cref="ITokenProtector"/> when one is given; the row key is a hash of the store key, so a key never needs a length limit.
/// </summary>
/// <remarks>
/// Access tokens, refresh tokens and client secrets are in that value. Without a protector they are stored as plain JSON, so give the
/// store a protector (or protect the file) wherever more than the application can read it. A value the protector cannot open, or that is
/// not a record, reads as absent, and the user signs in again; its content is never put in an error. Writes replace the whole record, so
/// the last writer wins. Dispose the store to close the file when it opened it.
/// </remarks>
public sealed class OneLiteTokenStore : ITokenStore, IAsyncDisposable
{
  private readonly StoreDatabase _store;
  private readonly ITokenProtector? _protector;
  private readonly TimeProvider _time;

  private OneLiteTokenStore(StoreDatabase store, ITokenProtector? protector, TimeProvider? timeProvider)
  {
    _store = store;
    _protector = protector;
    _time = timeProvider ?? TimeProvider.System;
  }

  private Database Db => _store.Db;

  /// <summary>Opens (creating when missing) a database file and its store tables, and returns a store that owns the connection.</summary>
  /// <param name="path">The database file.</param>
  /// <param name="protector">Seals each record; null stores plain JSON.</param>
  /// <param name="timeProvider">The clock for the rows' update time; default the system clock.</param>
  /// <param name="cancellationToken">Cancels the open.</param>
  /// <returns>The store. Dispose it to close the file.</returns>
  /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
  /// <exception cref="InvalidOperationException">The file's store schema is newer than this package understands.</exception>
  public static async ValueTask<OneLiteTokenStore> OpenAsync(string path, ITokenProtector? protector = null, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default) =>
    new(await StoreDatabase.OpenAsync(path, cancellationToken).ConfigureAwait(false), protector, timeProvider);

  /// <summary>Creates the store tables in a database you opened and returns a store over it. Disposing the store leaves the database open.</summary>
  /// <param name="database">An open OneLite database.</param>
  /// <param name="protector">Seals each record; null stores plain JSON.</param>
  /// <param name="timeProvider">The clock for the rows' update time; default the system clock.</param>
  /// <param name="cancellationToken">Cancels the schema check.</param>
  /// <returns>The store.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="database"/> is null.</exception>
  /// <exception cref="InvalidOperationException">The database's store schema is newer than this package understands.</exception>
  public static async ValueTask<OneLiteTokenStore> AttachAsync(Database database, ITokenProtector? protector = null, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default) =>
    new(await StoreDatabase.AttachAsync(database, cancellationToken).ConfigureAwait(false), protector, timeProvider);

  /// <inheritdoc />
  public ValueTask DisposeAsync() => _store.DisposeAsync();

  /// <inheritdoc />
  public async ValueTask<TokenRecord?> GetAsync(string key, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(key);
    var hash = StoreKeys.Hash(key);
    var row = (await Db.QueryAsync<TokenDataRow>($"SELECT Data FROM ModelContextTokenRecords WHERE KeyHash = {hash}", cancellationToken).ConfigureAwait(false)).FirstOrDefault();
    if (row is null)
    {
      return null;
    }

    try
    {
      var bytes = _protector is null ? row.Data : _protector.Unprotect(row.Data);
      var dto = JsonSerializer.Deserialize(bytes, StoreJson.Default.TokenRecordDto);
      return dto is null ? null : From(dto);
    }
    catch (Exception ex) when (ex is JsonException or CryptographicException)
    {
      return null;
    }
  }

  /// <inheritdoc />
  public async ValueTask SetAsync(string key, TokenRecord record, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(key);
    ArgumentNullException.ThrowIfNull(record);
    var bytes = JsonSerializer.SerializeToUtf8Bytes(To(record), StoreJson.Default.TokenRecordDto);
    if (_protector is not null)
    {
      bytes = _protector.Protect(bytes);
    }

    var hash = StoreKeys.Hash(key);
    var updated = _time.GetUtcNow().UtcTicks;
    await Db.ExecuteAsync($"""
      INSERT INTO ModelContextTokenRecords (KeyHash, Data, UpdatedAtTicks) VALUES ({hash}, {bytes}, {updated})
      ON CONFLICT (KeyHash) DO UPDATE SET Data = excluded.Data, UpdatedAtTicks = excluded.UpdatedAtTicks
      """, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(key);
    var hash = StoreKeys.Hash(key);
    await Db.ExecuteAsync($"DELETE FROM ModelContextTokenRecords WHERE KeyHash = {hash}", cancellationToken).ConfigureAwait(false);
  }

  private static TokenRecordDto To(TokenRecord r) => new()
  {
    Issuer = r.Issuer,
    ClientId = r.ClientId,
    ClientSecret = r.ClientSecret,
    AccessToken = r.AccessToken,
    RefreshToken = r.RefreshToken,
    ExpiresAtTicks = r.ExpiresAt?.UtcTicks,
    Resource = r.Resource,
    Scope = r.Scope,
    Dynamic = r.Dynamic,
  };

  private static TokenRecord From(TokenRecordDto d) => new()
  {
    Issuer = d.Issuer,
    ClientId = d.ClientId,
    ClientSecret = d.ClientSecret,
    AccessToken = d.AccessToken,
    RefreshToken = d.RefreshToken,
    ExpiresAt = d.ExpiresAtTicks is { } t ? StoreKeys.At(t) : null,
    Resource = d.Resource,
    Scope = d.Scope,
    Dynamic = d.Dynamic,
  };
}
