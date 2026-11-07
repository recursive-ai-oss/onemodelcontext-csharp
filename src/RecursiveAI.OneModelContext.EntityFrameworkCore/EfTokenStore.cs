using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RecursiveAI.OneModelContext.OAuth;

namespace RecursiveAI.OneModelContext.EntityFrameworkCore;

/// <summary>
/// An <see cref="ITokenStore"/> in a relational database, for server-side applications that sign in to upstream servers on behalf of
/// their users and run as several instances. Each record is one row holding the whole <see cref="TokenRecord"/> as a single value, sealed
/// by the <see cref="ITokenProtector"/> when one is given; the row key is a hash of the store key, so the key never needs a length limit.
/// </summary>
/// <typeparam name="TContext">Your context. Its model must include <see cref="ContextStoresModelBuilderExtensions.ApplyTokenStore"/>.</typeparam>
/// <remarks>
/// Access tokens, refresh tokens and client secrets are in that value. Without a protector they are stored as plain JSON, so give the
/// store a protector (or encrypt the database) wherever more than the application can read the tables. A value the protector cannot
/// open, or that is not a record, reads as absent, and the user signs in again; its content is never put in an error. Writes replace
/// the whole record, so the last writer wins. Entity Framework Core is not trim- or AOT-safe, and neither is this adapter.
/// </remarks>
public sealed class EfTokenStore<TContext> : ITokenStore
  where TContext : DbContext
{
  private readonly IDbContextFactory<TContext> _factory;
  private readonly ITokenProtector? _protector;
  private readonly TimeProvider _time;

  /// <summary>Creates a store over a context factory (<c>AddDbContextFactory</c>).</summary>
  /// <param name="factory">Creates one short-lived context per call.</param>
  /// <param name="protector">Seals each record; null stores plain JSON.</param>
  /// <param name="timeProvider">The clock for the rows' update time; default the system clock.</param>
  /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
  public EfTokenStore(IDbContextFactory<TContext> factory, ITokenProtector? protector = null, TimeProvider? timeProvider = null)
  {
    ArgumentNullException.ThrowIfNull(factory);
    _factory = factory;
    _protector = protector;
    _time = timeProvider ?? TimeProvider.System;
  }

  private TContext Open() => StoreContext.Open(_factory, typeof(TokenRecordRow), "ApplyTokenStore");

  /// <inheritdoc />
  public async ValueTask<TokenRecord?> GetAsync(string key, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(key);
    var hash = StoreKeys.Hash(key);
    using var db = Open();
    var row = await db.Set<TokenRecordRow>().AsNoTracking().FirstOrDefaultAsync(r => r.KeyHash == hash, cancellationToken).ConfigureAwait(false);
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
    await StoreContext.UpsertAsync(Open, new TokenRecordRow { KeyHash = hash, Data = bytes, UpdatedAtTicks = _time.GetUtcNow().UtcTicks }, hash, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(key);
    var hash = StoreKeys.Hash(key);
    using var db = Open();
    await db.Set<TokenRecordRow>().Where(r => r.KeyHash == hash).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
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
