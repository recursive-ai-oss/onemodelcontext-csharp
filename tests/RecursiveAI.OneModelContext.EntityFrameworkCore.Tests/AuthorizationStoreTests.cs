using Microsoft.EntityFrameworkCore;
using RecursiveAI.OneModelContext.Auth.Server;
using Xunit;

namespace RecursiveAI.OneModelContext.EntityFrameworkCore.Tests;

public sealed class AuthorizationStoreTests : IDisposable
{
  private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  private readonly Database _db = new();
  private readonly EfAuthorizationStore<AppDbContext> _store;

  public AuthorizationStoreTests() => _store = new EfAuthorizationStore<AppDbContext>(_db.NewFactory());

  public void Dispose() => _db.Dispose();

  private static AuthorizationCodeGrant Code(string hash, DateTimeOffset? expires = null) => new()
  {
    CodeHash = hash,
    ClientId = "app",
    RedirectUri = "https://app.test/cb",
    RedirectUriSent = true,
    Subject = "alice",
    Scope = "files:read",
    Resource = "https://mcp.test/mcp",
    CodeChallenge = "challenge",
    FamilyId = "fam-1",
    Claims = new Dictionary<string, string> { ["tenant"] = "t1" },
    ExpiresAt = expires ?? Now.AddMinutes(1),
  };

  private static RefreshTokenGrant Refresh(string hash, int days = 1, int familyDays = 30) => new()
  {
    TokenHash = hash,
    FamilyId = "fam-1",
    ClientId = "app",
    Subject = "alice",
    Scope = "files:read",
    Resource = "https://mcp.test/mcp",
    Claims = new Dictionary<string, string> { ["tenant"] = "t1" },
    ExpiresAt = Now.AddDays(days),
    FamilyExpiresAt = Now.AddDays(familyDays),
  };

  [Fact]
  public async Task A_client_round_trips_with_every_field_and_a_save_replaces_it()
  {
    var client = new RegisteredClient
    {
      ClientId = "svc",
      Name = "Service",
      RedirectUris = ["https://app.test/cb", "http://127.0.0.1/cb"],
      TokenEndpointAuthMethod = "client_secret_basic",
      SecretHash = "hash",
      JwksJson = "{\"keys\":[]}",
      JwksUri = "https://app.test/jwks",
      GrantTypes = ["authorization_code", "client_credentials"],
      Scopes = ["a", "b"],
      CreatedAt = Now,
    };
    await _store.SaveClientAsync(client, Ct);

    var read = await _store.FindClientAsync("svc", Ct);
    Assert.NotNull(read);
    Assert.Equal(client.RedirectUris, read.RedirectUris);
    Assert.Equal(client.GrantTypes, read.GrantTypes);
    Assert.Equal(client.Scopes, read.Scopes);
    Assert.Equal("Service", read.Name);
    Assert.Equal("hash", read.SecretHash);
    Assert.Equal("client_secret_basic", read.TokenEndpointAuthMethod);
    Assert.Equal(Now, read.CreatedAt);

    await _store.SaveClientAsync(new RegisteredClient { ClientId = "svc", RedirectUris = ["https://other.test/cb"], CreatedAt = Now }, Ct);
    read = await _store.FindClientAsync("svc", Ct);
    Assert.Equal(["https://other.test/cb"], read!.RedirectUris);
    Assert.Null(read.Scopes);
    Assert.Null(read.Name);
    Assert.Null(await _store.FindClientAsync("nobody", Ct));
  }

  [Fact]
  public async Task Client_ids_beyond_the_key_limit_are_refused_on_save_and_unknown_on_find()
  {
    var id = new string('x', 451);
    await Assert.ThrowsAsync<ArgumentException>(async () => await _store.SaveClientAsync(new RegisteredClient { ClientId = id, CreatedAt = Now }, Ct));
    Assert.Null(await _store.FindClientAsync(id, Ct));
  }

  [Fact]
  public async Task A_code_redeems_once_and_a_replay_is_recognised()
  {
    await _store.SaveCodeAsync(Code("c1"), Ct);

    var first = await _store.RedeemCodeAsync("c1", Now, Ct);
    Assert.Equal(RedemptionStatus.Redeemed, first.Status);
    Assert.Equal("alice", first.Grant!.Subject);
    Assert.Equal("t1", first.Grant.Claims["tenant"]);
    Assert.True(first.Grant.RedirectUriSent);
    Assert.Equal(Now.AddMinutes(1), first.Grant.ExpiresAt);

    var second = await _store.RedeemCodeAsync("c1", Now, Ct);
    Assert.Equal(RedemptionStatus.AlreadyRedeemed, second.Status);
    Assert.Equal("fam-1", second.Grant!.FamilyId);

    Assert.Equal(RedemptionStatus.Unknown, (await _store.RedeemCodeAsync("nope", Now, Ct)).Status);
  }

  [Fact]
  public async Task An_expired_code_answers_expired_and_is_spent()
  {
    await _store.SaveCodeAsync(Code("c1", Now.AddSeconds(-1)), Ct);
    Assert.Equal(RedemptionStatus.Expired, (await _store.RedeemCodeAsync("c1", Now, Ct)).Status);
    Assert.Equal(RedemptionStatus.AlreadyRedeemed, (await _store.RedeemCodeAsync("c1", Now, Ct)).Status);
  }

  [Fact]
  public async Task A_refresh_token_redeems_once_and_stays_findable_after()
  {
    await _store.SaveRefreshTokenAsync(Refresh("r1"), Ct);
    Assert.False((await _store.FindRefreshTokenAsync("r1", Ct))!.Used);

    var first = await _store.RedeemRefreshTokenAsync("r1", Now, Ct);
    Assert.Equal(RedemptionStatus.Redeemed, first.Status);
    Assert.Equal(Now.AddDays(30), first.Grant!.FamilyExpiresAt);

    Assert.Equal(RedemptionStatus.AlreadyRedeemed, (await _store.RedeemRefreshTokenAsync("r1", Now, Ct)).Status);
    Assert.True((await _store.FindRefreshTokenAsync("r1", Ct))!.Used);
    Assert.Equal(RedemptionStatus.Unknown, (await _store.RedeemRefreshTokenAsync("zz", Now, Ct)).Status);
    Assert.Null(await _store.FindRefreshTokenAsync("zz", Ct));
  }

  [Fact]
  public async Task A_refresh_token_expires_by_its_own_time_or_its_familys()
  {
    await _store.SaveRefreshTokenAsync(Refresh("r1"), Ct);
    Assert.Equal(RedemptionStatus.Expired, (await _store.RedeemRefreshTokenAsync("r1", Now.AddDays(2), Ct)).Status);

    await _store.SaveRefreshTokenAsync(Refresh("r2", days: 40, familyDays: 1), Ct);
    Assert.Equal(RedemptionStatus.Expired, (await _store.RedeemRefreshTokenAsync("r2", Now.AddDays(1), Ct)).Status);
  }

  [Fact]
  public async Task Revocations_keep_the_later_expiry_and_are_forgotten_by_a_purge()
  {
    Assert.False(await _store.IsRevokedAsync("family:f", Ct));
    await _store.RevokeAsync("family:f", Now.AddHours(2), Ct);
    await _store.RevokeAsync("family:f", Now.AddHours(1), Ct);
    Assert.True(await _store.IsRevokedAsync("family:f", Ct));

    await _store.PurgeAsync(Now.AddHours(1.5), Ct);
    Assert.True(await _store.IsRevokedAsync("family:f", Ct));
    await _store.PurgeAsync(Now.AddHours(2), Ct);
    Assert.False(await _store.IsRevokedAsync("family:f", Ct));
  }

  [Fact]
  public async Task An_assertion_is_accepted_once_until_it_expires()
  {
    var key = "client:app\n" + new string('j', 5000);
    Assert.True(await _store.TryRecordAssertionAsync(key, Now.AddMinutes(5), Now, Ct));
    Assert.False(await _store.TryRecordAssertionAsync(key, Now.AddMinutes(5), Now.AddMinutes(1), Ct));
    Assert.True(await _store.TryRecordAssertionAsync(key, Now.AddMinutes(15), Now.AddMinutes(5), Ct));
    Assert.False(await _store.TryRecordAssertionAsync(key, Now.AddMinutes(15), Now.AddMinutes(6), Ct));
  }

  [Fact]
  public async Task Purge_drops_what_has_run_out_and_keeps_the_rest()
  {
    await _store.SaveCodeAsync(Code("old", Now.AddMinutes(1)), Ct);
    await _store.SaveCodeAsync(Code("new", Now.AddMinutes(30)), Ct);
    await _store.SaveRefreshTokenAsync(Refresh("r1"), Ct);

    await _store.PurgeAsync(Now.AddMinutes(10), Ct);
    Assert.Equal(RedemptionStatus.Unknown, (await _store.RedeemCodeAsync("old", Now, Ct)).Status);
    Assert.Equal(RedemptionStatus.Redeemed, (await _store.RedeemCodeAsync("new", Now, Ct)).Status);
    Assert.NotNull(await _store.FindRefreshTokenAsync("r1", Ct));

    await _store.PurgeAsync(Now.AddDays(31), Ct);
    Assert.Null(await _store.FindRefreshTokenAsync("r1", Ct));
  }

  [Fact]
  public async Task A_context_without_the_tables_fails_with_advice()
  {
    var options = new DbContextOptionsBuilder<BareDbContext>().UseSqlite("Data Source=:memory:").Options;
    var store = new EfAuthorizationStore<BareDbContext>(new BareFactory(options));
    var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.FindClientAsync("a", Ct));
    Assert.Contains("ApplyAuthorizationStore", ex.Message);
  }

  private sealed class BareFactory(DbContextOptions<BareDbContext> options) : IDbContextFactory<BareDbContext>
  {
    public BareDbContext CreateDbContext() => new(options);
  }

  [Fact]
  public async Task Nothing_secret_is_stored_in_the_clear()
  {
    // The server hands the store hashes; the tables must hold only what it was given, and keys that callers pick must be hashed.
    await _store.SaveCodeAsync(Code("hash-of-code"), Ct);
    await _store.RevokeAsync("jti:visible-jti", Now.AddHours(1), Ct);
    await _store.TryRecordAssertionAsync("idjag:issuer\nvisible-assertion", Now.AddHours(1), Now, Ct);

    using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_db.ConnectionString);
    await connection.OpenAsync(Ct);
    var text = new System.Text.StringBuilder();
    foreach (var table in new[] { "ModelContextRevocations", "ModelContextSeenAssertions" })
    {
      using var command = connection.CreateCommand();
      command.CommandText = $"SELECT KeyHash FROM {table}";
      using var reader = await command.ExecuteReaderAsync(Ct);
      while (await reader.ReadAsync(Ct))
      {
        text.Append(reader.GetString(0)).Append('|');
      }
    }

    Assert.DoesNotContain("visible", text.ToString());
    Assert.DoesNotContain("jti", text.ToString());
  }
}
