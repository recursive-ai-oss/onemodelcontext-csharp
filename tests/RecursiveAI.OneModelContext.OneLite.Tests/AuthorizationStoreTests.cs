using RecursiveAI.OneModelContext.Auth.Server;
using Xunit;

namespace RecursiveAI.OneModelContext.OneLite.Tests;

public sealed class AuthorizationStoreTests : IAsyncLifetime
{
  private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  private readonly StoreFile _db = new();
  private OneLiteAuthorizationStore _store = null!;

  public async ValueTask InitializeAsync() => _store = await _db.AuthorizationAsync();

  public async ValueTask DisposeAsync() => await _db.DisposeAsync();

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
  public async Task A_spent_code_is_remembered_until_its_family_expires_so_a_late_replay_is_recognised()
  {
    await _store.SaveCodeAsync(new AuthorizationCodeGrant
    {
      CodeHash = "spent",
      ClientId = "app",
      RedirectUri = "https://app.test/cb",
      Subject = "alice",
      Resource = "https://mcp.test/mcp",
      CodeChallenge = "challenge",
      FamilyId = "fam-1",
      ExpiresAt = Now.AddMinutes(1),
      FamilyExpiresAt = Now.AddDays(90),
    }, Ct);
    await _store.SaveCodeAsync(Code("unused"), Ct);
    await _store.RedeemCodeAsync("spent", Now, Ct);

    await _store.PurgeAsync(Now.AddDays(1), Ct);
    Assert.Equal(RedemptionStatus.AlreadyRedeemed, (await _store.RedeemCodeAsync("spent", Now.AddDays(1), Ct)).Status);
    Assert.Equal(RedemptionStatus.Unknown, (await _store.RedeemCodeAsync("unused", Now.AddDays(1), Ct)).Status);

    await _store.PurgeAsync(Now.AddDays(91), Ct);
    Assert.Equal(RedemptionStatus.Unknown, (await _store.RedeemCodeAsync("spent", Now.AddDays(91), Ct)).Status);
  }

  [Fact]
  public async Task Client_ids_are_matched_exactly_whatever_the_column_collation()
  {
    await _store.SaveClientAsync(new RegisteredClient { ClientId = "Client-A", RedirectUris = ["https://app.test/cb"] }, Ct);

    Assert.NotNull(await _store.FindClientAsync("Client-A", Ct));
    Assert.Null(await _store.FindClientAsync("client-a", Ct));
  }

  [Fact]
  public async Task A_file_written_by_a_newer_schema_is_refused_with_advice()
  {
    await _db.DisposeStoresAsync();
    using (var connection = _db.Raw())
    {
      connection.ExecuteScript("UPDATE ModelContextSchema SET Version = 99");
    }

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await OneLiteAuthorizationStore.OpenAsync(_db.Path, Ct));
    Assert.Contains("newer", ex.Message);
    Assert.Contains("99", ex.Message);
  }

  [Fact]
  public async Task The_hosts_own_tables_and_user_version_are_left_alone()
  {
    await _db.DisposeStoresAsync();
    using (var connection = _db.Raw())
    {
      connection.ExecuteScript("CREATE TABLE HostOwn (Id INTEGER PRIMARY KEY); PRAGMA user_version = 7");
    }

    await using var again = await OneLiteAuthorizationStore.OpenAsync(_db.Path, Ct);
    using var check = _db.Raw();
    Assert.Equal(7, check.Scalar<long>($"PRAGMA user_version"));
    Assert.Equal(1, check.Scalar<long>($"SELECT count(*) FROM sqlite_schema WHERE name = 'HostOwn'"));
  }

  [Fact]
  public async Task Nothing_secret_is_stored_in_the_clear()
  {
    // The server hands the store hashes; the tables must hold only what it was given, and keys that callers pick must be hashed.
    await _store.SaveCodeAsync(Code("hash-of-code"), Ct);
    await _store.RevokeAsync("jti:visible-jti", Now.AddHours(1), Ct);
    await _store.TryRecordAssertionAsync("idjag:issuer\nvisible-assertion", Now.AddHours(1), Now, Ct);

    using var raw = _db.Raw();
    var text = string.Join('|', raw.Query<string>($"SELECT KeyHash FROM ModelContextRevocations").Concat(raw.Query<string>($"SELECT KeyHash FROM ModelContextSeenAssertions")));

    Assert.DoesNotContain("visible", text);
    Assert.DoesNotContain("jti", text);
  }
}
