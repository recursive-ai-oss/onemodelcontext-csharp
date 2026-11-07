using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneLite;
using RecursiveAI.OneModelContext.Auth.Server;

namespace RecursiveAI.OneModelContext.OneLite;

[OneLiteRow]
internal sealed class ClientRow
{
  public string ClientId { get; set; } = string.Empty;

  public string? Name { get; set; }

  public string RedirectUrisJson { get; set; } = "[]";

  public string TokenEndpointAuthMethod { get; set; } = "none";

  public string? SecretHash { get; set; }

  public string? JwksJson { get; set; }

  public string? JwksUri { get; set; }

  public string GrantTypesJson { get; set; } = "[]";

  public string? ScopesJson { get; set; }

  public long CreatedAtTicks { get; set; }
}

[OneLiteRow]
internal sealed class CodeRow
{
  public string CodeHash { get; set; } = string.Empty;

  public string ClientId { get; set; } = string.Empty;

  public string RedirectUri { get; set; } = string.Empty;

  public long RedirectUriSent { get; set; }

  public string Subject { get; set; } = string.Empty;

  public string Scope { get; set; } = string.Empty;

  public string Resource { get; set; } = string.Empty;

  public string CodeChallenge { get; set; } = string.Empty;

  public string FamilyId { get; set; } = string.Empty;

  public string ClaimsJson { get; set; } = "{}";

  public long ExpiresAtTicks { get; set; }

  public long FamilyExpiresAtTicks { get; set; }

  public long Redeemed { get; set; }
}

[OneLiteRow]
internal sealed class RefreshTokenRow
{
  public string TokenHash { get; set; } = string.Empty;

  public string FamilyId { get; set; } = string.Empty;

  public string ClientId { get; set; } = string.Empty;

  public string Subject { get; set; } = string.Empty;

  public string Scope { get; set; } = string.Empty;

  public string Resource { get; set; } = string.Empty;

  public string ClaimsJson { get; set; } = "{}";

  public long ExpiresAtTicks { get; set; }

  public long FamilyExpiresAtTicks { get; set; }

  public long Used { get; set; }
}

/// <summary>
/// An <see cref="IAuthorizationStore"/> in one embedded OneLite database file, for an authorization server that runs as a single host
/// with no database server. Every "exactly once" step (redeeming a code, rotating a refresh token, accepting an assertion id) runs in
/// one write transaction, which takes the database's write lock, so two callers racing on one code get one winner and one replay. That
/// holds across threads and across processes that open the same file. Codes, refresh tokens and assertion ids are kept as SHA-256
/// hashes, as the server hands them over.
/// </summary>
/// <remarks>
/// <para>The store keeps one connection and runs its calls one at a time; a busy file (another process writing) is waited for up to
/// OneLite's busy timeout, 5 seconds by default. The tables are created and versioned by the store itself, apart from the host's own
/// tables and <c>PRAGMA user_version</c>, and a file written by a newer version of this package is refused with an
/// <see cref="InvalidOperationException"/>. Dispose the store to close the file when it opened it.</para>
/// </remarks>
public sealed class OneLiteAuthorizationStore : IAuthorizationStore, IAsyncDisposable
{
  private readonly StoreDatabase _store;

  private OneLiteAuthorizationStore(StoreDatabase store) => _store = store;

  private Database Db => _store.Db;

  /// <summary>Opens (creating when missing) a database file and its store tables, and returns a store that owns the connection.</summary>
  /// <param name="path">The database file.</param>
  /// <param name="cancellationToken">Cancels the open.</param>
  /// <returns>The store. Dispose it to close the file.</returns>
  /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
  /// <exception cref="InvalidOperationException">The file's store schema is newer than this package understands.</exception>
  public static async ValueTask<OneLiteAuthorizationStore> OpenAsync(string path, CancellationToken cancellationToken = default) =>
    new(await StoreDatabase.OpenAsync(path, cancellationToken).ConfigureAwait(false));

  /// <summary>Creates the store tables in a database you opened and returns a store over it. Disposing the store leaves the database open.</summary>
  /// <param name="database">An open OneLite database.</param>
  /// <param name="cancellationToken">Cancels the schema check.</param>
  /// <returns>The store.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="database"/> is null.</exception>
  /// <exception cref="InvalidOperationException">The database's store schema is newer than this package understands.</exception>
  public static async ValueTask<OneLiteAuthorizationStore> AttachAsync(Database database, CancellationToken cancellationToken = default) =>
    new(await StoreDatabase.AttachAsync(database, cancellationToken).ConfigureAwait(false));

  /// <inheritdoc />
  public ValueTask DisposeAsync() => _store.DisposeAsync();

  /// <inheritdoc />
  public async ValueTask<RegisteredClient?> FindClientAsync(string clientId, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(clientId);
    if (clientId.Length > StoreKeys.MaxClientIdLength)
    {
      return null;
    }

    var rows = await Db.QueryAsync<ClientRow>($"SELECT * FROM ModelContextClients WHERE ClientId = {clientId}", cancellationToken).ConfigureAwait(false);

    // The column compares bytes, so ids are case-sensitive; the check keeps that true if the schema ever changes.
    var row = rows.FirstOrDefault();
    return row is null || !string.Equals(row.ClientId, clientId, StringComparison.Ordinal) ? null : ToClient(row);
  }

  /// <summary>Adds or replaces a client.</summary>
  /// <param name="client">The client.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  /// <exception cref="ArgumentException">The client id is longer than 450 characters, the longest the stores accept.</exception>
  public async ValueTask SaveClientAsync(RegisteredClient client, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(client);
    if (client.ClientId.Length > StoreKeys.MaxClientIdLength)
    {
      throw new ArgumentException("The client id is longer than the store allows (450 characters).", nameof(client));
    }

    var redirects = StoreKeys.WriteList(client.RedirectUris);
    var grants = StoreKeys.WriteList(client.GrantTypes);
    var scopes = client.Scopes is null ? null : StoreKeys.WriteList(client.Scopes);
    var created = client.CreatedAt.UtcTicks;
    await Db.ExecuteAsync($"""
      INSERT INTO ModelContextClients (ClientId, Name, RedirectUrisJson, TokenEndpointAuthMethod, SecretHash, JwksJson, JwksUri, GrantTypesJson, ScopesJson, CreatedAtTicks)
      VALUES ({client.ClientId}, {client.Name}, {redirects}, {client.TokenEndpointAuthMethod}, {client.SecretHash}, {client.JwksJson}, {client.JwksUri}, {grants}, {scopes}, {created})
      ON CONFLICT (ClientId) DO UPDATE SET Name = excluded.Name, RedirectUrisJson = excluded.RedirectUrisJson,
        TokenEndpointAuthMethod = excluded.TokenEndpointAuthMethod, SecretHash = excluded.SecretHash, JwksJson = excluded.JwksJson,
        JwksUri = excluded.JwksUri, GrantTypesJson = excluded.GrantTypesJson, ScopesJson = excluded.ScopesJson, CreatedAtTicks = excluded.CreatedAtTicks
      """, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async ValueTask SaveCodeAsync(AuthorizationCodeGrant grant, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(grant);
    var sent = grant.RedirectUriSent ? 1L : 0L;
    var claims = StoreKeys.WriteMap(grant.Claims);
    var expires = grant.ExpiresAt.UtcTicks;
    var familyExpires = grant.FamilyExpiresAt.UtcTicks;
    var redeemed = grant.Redeemed ? 1L : 0L;
    await Db.ExecuteAsync($"""
      INSERT INTO ModelContextAuthorizationCodes (CodeHash, ClientId, RedirectUri, RedirectUriSent, Subject, Scope, Resource, CodeChallenge, FamilyId, ClaimsJson, ExpiresAtTicks, FamilyExpiresAtTicks, Redeemed)
      VALUES ({grant.CodeHash}, {grant.ClientId}, {grant.RedirectUri}, {sent}, {grant.Subject}, {grant.Scope}, {grant.Resource}, {grant.CodeChallenge}, {grant.FamilyId}, {claims}, {expires}, {familyExpires}, {redeemed})
      ON CONFLICT (CodeHash) DO UPDATE SET ClientId = excluded.ClientId, RedirectUri = excluded.RedirectUri, RedirectUriSent = excluded.RedirectUriSent,
        Subject = excluded.Subject, Scope = excluded.Scope, Resource = excluded.Resource, CodeChallenge = excluded.CodeChallenge,
        FamilyId = excluded.FamilyId, ClaimsJson = excluded.ClaimsJson, ExpiresAtTicks = excluded.ExpiresAtTicks,
        FamilyExpiresAtTicks = excluded.FamilyExpiresAtTicks, Redeemed = excluded.Redeemed
      """, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async ValueTask<CodeRedemption> RedeemCodeAsync(string codeHash, DateTimeOffset now, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(codeHash);
    var nowTicks = now.UtcTicks;

    // Read and mark inside one write transaction: it holds the write lock, so only one caller ever sees Redeemed = 0.
    return await Db.WriteAsync(async tx =>
    {
      var row = (await tx.QueryAsync<CodeRow>($"SELECT * FROM ModelContextAuthorizationCodes WHERE CodeHash = {codeHash}", cancellationToken).ConfigureAwait(false)).FirstOrDefault();
      if (row is null)
      {
        return new CodeRedemption(RedemptionStatus.Unknown, null);
      }

      if (row.Redeemed == 0)
      {
        var won = await tx.ExecuteAsync($"UPDATE ModelContextAuthorizationCodes SET Redeemed = 1 WHERE CodeHash = {codeHash} AND Redeemed = 0", cancellationToken).ConfigureAwait(false);
        if (won == 1)
        {
          // Like the memory store, an expired code is spent too; it just answers Expired.
          return new CodeRedemption(row.ExpiresAtTicks <= nowTicks ? RedemptionStatus.Expired : RedemptionStatus.Redeemed, ToCode(row, redeemed: true));
        }
      }

      return new CodeRedemption(RedemptionStatus.AlreadyRedeemed, ToCode(row, redeemed: true));
    }, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async ValueTask SaveRefreshTokenAsync(RefreshTokenGrant token, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(token);
    var claims = StoreKeys.WriteMap(token.Claims);
    var expires = token.ExpiresAt.UtcTicks;
    var familyExpires = token.FamilyExpiresAt.UtcTicks;
    var used = token.Used ? 1L : 0L;
    await Db.ExecuteAsync($"""
      INSERT INTO ModelContextRefreshTokens (TokenHash, FamilyId, ClientId, Subject, Scope, Resource, ClaimsJson, ExpiresAtTicks, FamilyExpiresAtTicks, Used)
      VALUES ({token.TokenHash}, {token.FamilyId}, {token.ClientId}, {token.Subject}, {token.Scope}, {token.Resource}, {claims}, {expires}, {familyExpires}, {used})
      ON CONFLICT (TokenHash) DO UPDATE SET FamilyId = excluded.FamilyId, ClientId = excluded.ClientId, Subject = excluded.Subject,
        Scope = excluded.Scope, Resource = excluded.Resource, ClaimsJson = excluded.ClaimsJson, ExpiresAtTicks = excluded.ExpiresAtTicks,
        FamilyExpiresAtTicks = excluded.FamilyExpiresAtTicks, Used = excluded.Used
      """, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async ValueTask<RefreshRedemption> RedeemRefreshTokenAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(tokenHash);
    var nowTicks = now.UtcTicks;
    return await Db.WriteAsync(async tx =>
    {
      var row = (await tx.QueryAsync<RefreshTokenRow>($"SELECT * FROM ModelContextRefreshTokens WHERE TokenHash = {tokenHash}", cancellationToken).ConfigureAwait(false)).FirstOrDefault();
      if (row is null)
      {
        return new RefreshRedemption(RedemptionStatus.Unknown, null);
      }

      if (row.Used == 0)
      {
        var won = await tx.ExecuteAsync($"UPDATE ModelContextRefreshTokens SET Used = 1 WHERE TokenHash = {tokenHash} AND Used = 0", cancellationToken).ConfigureAwait(false);
        if (won == 1)
        {
          var expired = row.ExpiresAtTicks <= nowTicks || row.FamilyExpiresAtTicks <= nowTicks;
          return new RefreshRedemption(expired ? RedemptionStatus.Expired : RedemptionStatus.Redeemed, ToRefresh(row, used: true));
        }
      }

      return new RefreshRedemption(RedemptionStatus.AlreadyRedeemed, ToRefresh(row, used: true));
    }, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async ValueTask<RefreshTokenGrant?> FindRefreshTokenAsync(string tokenHash, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(tokenHash);
    var row = (await Db.QueryAsync<RefreshTokenRow>($"SELECT * FROM ModelContextRefreshTokens WHERE TokenHash = {tokenHash}", cancellationToken).ConfigureAwait(false)).FirstOrDefault();
    return row is null ? null : ToRefresh(row, row.Used != 0);
  }

  /// <inheritdoc />
  public async ValueTask RevokeAsync(string key, DateTimeOffset expiresAt, CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrEmpty(key);
    var hash = StoreKeys.Hash(key);
    var ticks = expiresAt.UtcTicks;

    // Keep the later expiry, as the memory store does.
    await Db.ExecuteAsync($"""
      INSERT INTO ModelContextRevocations (KeyHash, ExpiresAtTicks) VALUES ({hash}, {ticks})
      ON CONFLICT (KeyHash) DO UPDATE SET ExpiresAtTicks = max(ModelContextRevocations.ExpiresAtTicks, excluded.ExpiresAtTicks)
      """, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async ValueTask<bool> IsRevokedAsync(string key, CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrEmpty(key);
    var hash = StoreKeys.Hash(key);
    return await Db.ScalarAsync<long>($"SELECT count(*) FROM ModelContextRevocations WHERE KeyHash = {hash}", cancellationToken).ConfigureAwait(false) > 0;
  }

  /// <inheritdoc />
  public async ValueTask<bool> TryRecordAssertionAsync(string key, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrEmpty(key);
    var hash = StoreKeys.Hash(key);
    var expires = expiresAt.UtcTicks;
    var nowTicks = now.UtcTicks;

    // One statement: a new id is inserted, a record that has run out is taken over, a live one is left alone (0 rows: a replay).
    var changed = await Db.ExecuteAsync($"""
      INSERT INTO ModelContextSeenAssertions (KeyHash, ExpiresAtTicks) VALUES ({hash}, {expires})
      ON CONFLICT (KeyHash) DO UPDATE SET ExpiresAtTicks = excluded.ExpiresAtTicks WHERE ModelContextSeenAssertions.ExpiresAtTicks <= {nowTicks}
      """, cancellationToken).ConfigureAwait(false);
    return changed == 1;
  }

  /// <inheritdoc />
  public async ValueTask PurgeAsync(DateTimeOffset now, CancellationToken cancellationToken)
  {
    var ticks = now.UtcTicks;
    await Db.WriteAsync(async tx =>
    {
      await tx.ExecuteAsync($"DELETE FROM ModelContextAuthorizationCodes WHERE ExpiresAtTicks <= {ticks} AND (Redeemed = 0 OR FamilyExpiresAtTicks <= {ticks})", cancellationToken).ConfigureAwait(false);
      await tx.ExecuteAsync($"DELETE FROM ModelContextRefreshTokens WHERE FamilyExpiresAtTicks <= {ticks}", cancellationToken).ConfigureAwait(false);
      await tx.ExecuteAsync($"DELETE FROM ModelContextRevocations WHERE ExpiresAtTicks <= {ticks}", cancellationToken).ConfigureAwait(false);
      await tx.ExecuteAsync($"DELETE FROM ModelContextSeenAssertions WHERE ExpiresAtTicks <= {ticks}", cancellationToken).ConfigureAwait(false);
    }, cancellationToken).ConfigureAwait(false);
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
    RedirectUriSent = r.RedirectUriSent != 0,
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
