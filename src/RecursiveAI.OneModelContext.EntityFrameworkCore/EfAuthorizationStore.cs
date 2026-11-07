using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RecursiveAI.OneModelContext.Auth.Server;

namespace RecursiveAI.OneModelContext.EntityFrameworkCore;

/// <summary>
/// An <see cref="IAuthorizationStore"/> in a relational database, for authorization servers that run as several instances. Every
/// "exactly once" step is one conditional <c>UPDATE</c> (<c>… WHERE redeemed = 0</c>) whose affected-row count says who won, so two
/// instances racing to redeem one code or rotate one refresh token get one winner and one replay, with no lock outside the database.
/// Codes, refresh tokens and assertion ids are held as SHA-256 hashes, as the server hands them over.
/// </summary>
/// <typeparam name="TContext">Your context. Its model must include <see cref="ContextStoresModelBuilderExtensions.ApplyAuthorizationStore"/>.</typeparam>
/// <remarks>
/// Each call opens a context from the factory and disposes it, so the store is safe to share across threads and instances. It needs a
/// provider that supports <c>ExecuteUpdate</c> and <c>ExecuteDelete</c> (every relational provider does). Entity Framework Core is not
/// trim- or AOT-safe, and neither is this adapter.
/// </remarks>
public sealed class EfAuthorizationStore<TContext> : IAuthorizationStore
  where TContext : DbContext
{
  private readonly IDbContextFactory<TContext> _factory;

  /// <summary>Creates a store over a context factory (<c>AddDbContextFactory</c>).</summary>
  /// <param name="factory">Creates one short-lived context per call.</param>
  /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
  public EfAuthorizationStore(IDbContextFactory<TContext> factory)
  {
    ArgumentNullException.ThrowIfNull(factory);
    _factory = factory;
  }

  private TContext Open() => StoreContext.Open(_factory, typeof(ClientRow), "ApplyAuthorizationStore");

  /// <inheritdoc />
  public async ValueTask<RegisteredClient?> FindClientAsync(string clientId, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(clientId);
    if (clientId.Length > StoreKeys.MaxClientIdLength)
    {
      return null;
    }

    using var db = Open();
    var row = await db.Set<ClientRow>().AsNoTracking().FirstOrDefaultAsync(r => r.ClientId == clientId, cancellationToken).ConfigureAwait(false);

    // A case-insensitive column collation would match "Client" to "client"; ids are compared exactly.
    return row is null || !string.Equals(row.ClientId, clientId, StringComparison.Ordinal) ? null : ToClient(row);
  }

  /// <summary>Adds or replaces a client.</summary>
  /// <param name="client">The client.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  /// <exception cref="ArgumentException">The client id is longer than 450 characters, the longest a portable key allows.</exception>
  public async ValueTask SaveClientAsync(RegisteredClient client, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(client);
    if (client.ClientId.Length > StoreKeys.MaxClientIdLength)
    {
      throw new ArgumentException("The client id is longer than the store allows (450 characters).", nameof(client));
    }

    await StoreContext.UpsertAsync(Open, new ClientRow
    {
      ClientId = client.ClientId,
      Name = client.Name,
      RedirectUrisJson = StoreKeys.WriteList(client.RedirectUris),
      TokenEndpointAuthMethod = client.TokenEndpointAuthMethod,
      SecretHash = client.SecretHash,
      JwksJson = client.JwksJson,
      JwksUri = client.JwksUri,
      GrantTypesJson = StoreKeys.WriteList(client.GrantTypes),
      ScopesJson = client.Scopes is null ? null : StoreKeys.WriteList(client.Scopes),
      CreatedAtTicks = StoreKeys.Ticks(client.CreatedAt),
    }, client.ClientId, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async ValueTask SaveCodeAsync(AuthorizationCodeGrant grant, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(grant);
    await StoreContext.UpsertAsync(Open, new CodeRow
    {
      CodeHash = grant.CodeHash,
      ClientId = grant.ClientId,
      RedirectUri = grant.RedirectUri,
      RedirectUriSent = grant.RedirectUriSent,
      Subject = grant.Subject,
      Scope = grant.Scope,
      Resource = grant.Resource,
      CodeChallenge = grant.CodeChallenge,
      FamilyId = grant.FamilyId,
      ClaimsJson = StoreKeys.WriteMap(grant.Claims),
      ExpiresAtTicks = StoreKeys.Ticks(grant.ExpiresAt),
      FamilyExpiresAtTicks = StoreKeys.Ticks(grant.FamilyExpiresAt),
      Redeemed = grant.Redeemed,
    }, grant.CodeHash, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async ValueTask<CodeRedemption> RedeemCodeAsync(string codeHash, DateTimeOffset now, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(codeHash);
    using var db = Open();
    var row = await db.Set<CodeRow>().AsNoTracking().FirstOrDefaultAsync(r => r.CodeHash == codeHash, cancellationToken).ConfigureAwait(false);
    if (row is null)
    {
      return new CodeRedemption(RedemptionStatus.Unknown, null);
    }

    if (!row.Redeemed)
    {
      // The single step that decides the race: only one UPDATE sees Redeemed = false.
      var won = await db.Set<CodeRow>().Where(r => r.CodeHash == codeHash && !r.Redeemed)
        .ExecuteUpdateAsync(s => s.SetProperty(r => r.Redeemed, true), cancellationToken).ConfigureAwait(false);
      if (won == 1)
      {
        // Like the memory store, an expired code is spent too; it just answers Expired.
        var grant = ToCode(row, redeemed: true);
        return new CodeRedemption(row.ExpiresAtTicks <= now.UtcTicks ? RedemptionStatus.Expired : RedemptionStatus.Redeemed, grant);
      }
    }

    return new CodeRedemption(RedemptionStatus.AlreadyRedeemed, ToCode(row, redeemed: true));
  }

  /// <inheritdoc />
  public async ValueTask SaveRefreshTokenAsync(RefreshTokenGrant token, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(token);
    await StoreContext.UpsertAsync(Open, new RefreshTokenRow
    {
      TokenHash = token.TokenHash,
      FamilyId = token.FamilyId,
      ClientId = token.ClientId,
      Subject = token.Subject,
      Scope = token.Scope,
      Resource = token.Resource,
      ClaimsJson = StoreKeys.WriteMap(token.Claims),
      ExpiresAtTicks = StoreKeys.Ticks(token.ExpiresAt),
      FamilyExpiresAtTicks = StoreKeys.Ticks(token.FamilyExpiresAt),
      Used = token.Used,
    }, token.TokenHash, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async ValueTask<RefreshRedemption> RedeemRefreshTokenAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(tokenHash);
    using var db = Open();
    var row = await db.Set<RefreshTokenRow>().AsNoTracking().FirstOrDefaultAsync(r => r.TokenHash == tokenHash, cancellationToken).ConfigureAwait(false);
    if (row is null)
    {
      return new RefreshRedemption(RedemptionStatus.Unknown, null);
    }

    if (!row.Used)
    {
      var won = await db.Set<RefreshTokenRow>().Where(r => r.TokenHash == tokenHash && !r.Used)
        .ExecuteUpdateAsync(s => s.SetProperty(r => r.Used, true), cancellationToken).ConfigureAwait(false);
      if (won == 1)
      {
        var expired = row.ExpiresAtTicks <= now.UtcTicks || row.FamilyExpiresAtTicks <= now.UtcTicks;
        return new RefreshRedemption(expired ? RedemptionStatus.Expired : RedemptionStatus.Redeemed, ToRefresh(row, used: true));
      }
    }

    return new RefreshRedemption(RedemptionStatus.AlreadyRedeemed, ToRefresh(row, used: true));
  }

  /// <inheritdoc />
  public async ValueTask<RefreshTokenGrant?> FindRefreshTokenAsync(string tokenHash, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(tokenHash);
    using var db = Open();
    var row = await db.Set<RefreshTokenRow>().AsNoTracking().FirstOrDefaultAsync(r => r.TokenHash == tokenHash, cancellationToken).ConfigureAwait(false);
    return row is null ? null : ToRefresh(row, row.Used);
  }

  /// <inheritdoc />
  public async ValueTask RevokeAsync(string key, DateTimeOffset expiresAt, CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrEmpty(key);
    var hash = StoreKeys.Hash(key);
    var ticks = expiresAt.UtcTicks;
    for (var attempt = 0; ; attempt++)
    {
      using var db = Open();

      // Keep the later expiry, as the memory store does: raise it if the row is older, else leave it.
      var raised = await db.Set<RevocationRow>().Where(r => r.KeyHash == hash && r.ExpiresAtTicks < ticks)
        .ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAtTicks, ticks), cancellationToken).ConfigureAwait(false);
      if (raised > 0 || await db.Set<RevocationRow>().AnyAsync(r => r.KeyHash == hash, cancellationToken).ConfigureAwait(false))
      {
        return;
      }

      db.Set<RevocationRow>().Add(new RevocationRow { KeyHash = hash, ExpiresAtTicks = ticks });
      try
      {
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return;
      }
      catch (DbUpdateException) when (attempt < 2)
      {
        // Another instance inserted the same revocation first; go round and keep the later expiry.
      }
    }
  }

  /// <inheritdoc />
  public async ValueTask<bool> IsRevokedAsync(string key, CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrEmpty(key);
    var hash = StoreKeys.Hash(key);
    using var db = Open();
    return await db.Set<RevocationRow>().AnyAsync(r => r.KeyHash == hash, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async ValueTask<bool> TryRecordAssertionAsync(string key, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrEmpty(key);
    var hash = StoreKeys.Hash(key);
    var nowTicks = now.UtcTicks;
    using var db = Open();

    // A record that has run out may be taken over; only one of two racing instances changes the row.
    var taken = await db.Set<AssertionRow>().Where(r => r.KeyHash == hash && r.ExpiresAtTicks <= nowTicks)
      .ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAtTicks, expiresAt.UtcTicks), cancellationToken).ConfigureAwait(false);
    if (taken == 1)
    {
      return true;
    }

    db.Set<AssertionRow>().Add(new AssertionRow { KeyHash = hash, ExpiresAtTicks = expiresAt.UtcTicks });
    try
    {
      await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
      return true;
    }
    catch (DbUpdateException)
    {
      // The primary key refused a second row: a replay. Anything else (the database is down) is not a replay.
      using var check = Open();
      if (await check.Set<AssertionRow>().AnyAsync(r => r.KeyHash == hash, cancellationToken).ConfigureAwait(false))
      {
        return false;
      }

      throw;
    }
  }

  /// <inheritdoc />
  public async ValueTask PurgeAsync(DateTimeOffset now, CancellationToken cancellationToken)
  {
    var ticks = now.UtcTicks;
    using var db = Open();
    await db.Set<CodeRow>().Where(r => r.ExpiresAtTicks <= ticks && (!r.Redeemed || r.FamilyExpiresAtTicks <= ticks)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    await db.Set<RefreshTokenRow>().Where(r => r.FamilyExpiresAtTicks <= ticks).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    await db.Set<RevocationRow>().Where(r => r.ExpiresAtTicks <= ticks).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    await db.Set<AssertionRow>().Where(r => r.ExpiresAtTicks <= ticks).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
  }

  private static RegisteredClient ToClient(ClientRow r) => new()
  {
    ClientId = r.ClientId,
    Name = r.Name,
    RedirectUris = StoreKeys.ReadList(r.RedirectUrisJson),
    TokenEndpointAuthMethod = r.TokenEndpointAuthMethod,
    SecretHash = r.SecretHash,
    JwksJson = r.JwksJson,
    JwksUri = r.JwksUri,
    GrantTypes = StoreKeys.ReadList(r.GrantTypesJson),
    Scopes = r.ScopesJson is null ? null : StoreKeys.ReadList(r.ScopesJson),
    CreatedAt = StoreKeys.At(r.CreatedAtTicks),
  };

  private static AuthorizationCodeGrant ToCode(CodeRow r, bool redeemed) => new()
  {
    CodeHash = r.CodeHash,
    ClientId = r.ClientId,
    RedirectUri = r.RedirectUri,
    RedirectUriSent = r.RedirectUriSent,
    Subject = r.Subject,
    Scope = r.Scope,
    Resource = r.Resource,
    CodeChallenge = r.CodeChallenge,
    FamilyId = r.FamilyId,
    Claims = StoreKeys.ReadMap(r.ClaimsJson),
    ExpiresAt = StoreKeys.At(r.ExpiresAtTicks),
    FamilyExpiresAt = StoreKeys.At(r.FamilyExpiresAtTicks),
    Redeemed = redeemed,
  };

  private static RefreshTokenGrant ToRefresh(RefreshTokenRow r, bool used) => new()
  {
    TokenHash = r.TokenHash,
    FamilyId = r.FamilyId,
    ClientId = r.ClientId,
    Subject = r.Subject,
    Scope = r.Scope,
    Resource = r.Resource,
    Claims = StoreKeys.ReadMap(r.ClaimsJson),
    ExpiresAt = StoreKeys.At(r.ExpiresAtTicks),
    FamilyExpiresAt = StoreKeys.At(r.FamilyExpiresAtTicks),
    Used = used,
  };
}

internal static class StoreContext
{
  /// <summary>Creates a context and checks that the host mapped the tables, so a forgotten call fails with advice and not an EF type name.</summary>
  internal static TContext Open<TContext>(IDbContextFactory<TContext> factory, Type probe, string applyMethod)
    where TContext : DbContext
  {
    var db = factory.CreateDbContext();
    if (db.Model.FindEntityType(probe) is null)
    {
      db.Dispose();
      throw new InvalidOperationException($"The context {typeof(TContext).Name} does not map the OneModelContext store tables. Call modelBuilder.{applyMethod}() (or ApplyContextStores()) in OnModelCreating.");
    }

    return db;
  }

  /// <summary>
  /// Inserts the row, or overwrites the row with the same key. Two instances inserting the same new key make one insert fail on the
  /// primary key; the loser goes round and updates instead.
  /// </summary>
  internal static async Task UpsertAsync<TContext, TRow>(Func<TContext> open, TRow row, object key, CancellationToken cancellationToken)
    where TContext : DbContext
    where TRow : class
  {
    for (var attempt = 0; ; attempt++)
    {
      using var db = open();
      var existing = await db.Set<TRow>().FindAsync([key], cancellationToken).ConfigureAwait(false);
      if (existing is null)
      {
        db.Set<TRow>().Add(row);
      }
      else
      {
        db.Entry(existing).CurrentValues.SetValues(row);
      }

      try
      {
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return;
      }
      catch (DbUpdateException) when (attempt < 2)
      {
        // Lost the insert race (or the row vanished under a purge): try again from a fresh context.
      }
    }
  }
}
