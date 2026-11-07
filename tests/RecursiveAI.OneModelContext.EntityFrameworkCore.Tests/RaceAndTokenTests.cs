using RecursiveAI.OneModelContext.Auth.Server;
using RecursiveAI.OneModelContext.OAuth;
using Xunit;

namespace RecursiveAI.OneModelContext.EntityFrameworkCore.Tests;

/// <summary>Two instances (two factories, each with its own connections) racing on one database.</summary>
public sealed class RaceTests : IDisposable
{
  private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  private readonly Database _db = new();

  public void Dispose() => _db.Dispose();

  private EfAuthorizationStore<AppDbContext>[] Instances(int count) =>
    Enumerable.Range(0, count).Select(_ => new EfAuthorizationStore<AppDbContext>(_db.NewFactory())).ToArray();

  [Fact]
  public async Task A_code_redeemed_by_many_instances_at_once_succeeds_exactly_once()
  {
    var instances = Instances(8);
    await instances[0].SaveCodeAsync(new AuthorizationCodeGrant
    {
      CodeHash = "c1", ClientId = "app", RedirectUri = "https://app.test/cb", Subject = "alice", Resource = "r",
      CodeChallenge = "x", FamilyId = "f", ExpiresAt = Now.AddMinutes(1),
    }, Ct);

    var results = await Task.WhenAll(instances.Select(async s => (await Task.Run(async () => await s.RedeemCodeAsync("c1", Now, Ct), Ct)).Status));

    Assert.Equal(1, results.Count(r => r == RedemptionStatus.Redeemed));
    Assert.Equal(7, results.Count(r => r == RedemptionStatus.AlreadyRedeemed));
  }

  [Fact]
  public async Task A_refresh_token_rotated_by_many_instances_at_once_has_one_winner_and_the_rest_see_reuse()
  {
    var instances = Instances(8);
    await instances[0].SaveRefreshTokenAsync(new RefreshTokenGrant
    {
      TokenHash = "r1", FamilyId = "f", ClientId = "app", Subject = "alice", Resource = "r",
      ExpiresAt = Now.AddDays(1), FamilyExpiresAt = Now.AddDays(30),
    }, Ct);

    var results = await Task.WhenAll(instances.Select(s => Task.Run(async () => (await s.RedeemRefreshTokenAsync("r1", Now, Ct)).Status, Ct)));

    Assert.Equal(1, results.Count(r => r == RedemptionStatus.Redeemed));
    Assert.Equal(7, results.Count(r => r == RedemptionStatus.AlreadyRedeemed));
  }

  [Fact]
  public async Task An_assertion_seen_by_many_instances_at_once_is_accepted_once()
  {
    var instances = Instances(8);
    var results = await Task.WhenAll(instances.Select(s => Task.Run(async () => await s.TryRecordAssertionAsync("client:app\njti-1", Now.AddMinutes(5), Now, Ct), Ct)));
    Assert.Equal(1, results.Count(r => r));
  }

  [Fact]
  public async Task Revocation_and_saves_from_many_instances_at_once_do_not_fail()
  {
    var instances = Instances(8);
    await Task.WhenAll(instances.Select((s, i) => Task.Run(async () =>
    {
      await s.RevokeAsync("family:f", Now.AddMinutes(i + 1), Ct);
      await s.SaveClientAsync(new RegisteredClient { ClientId = "same", Name = "n" + i, CreatedAt = Now }, Ct);
    }, Ct)));

    Assert.True(await instances[0].IsRevokedAsync("family:f", Ct));
    Assert.NotNull(await instances[1].FindClientAsync("same", Ct));
    // The latest expiry won, whatever order the writes arrived in.
    await instances[2].PurgeAsync(Now.AddMinutes(7), Ct);
    Assert.True(await instances[3].IsRevokedAsync("family:f", Ct));
    await instances[2].PurgeAsync(Now.AddMinutes(8), Ct);
    Assert.False(await instances[3].IsRevokedAsync("family:f", Ct));
  }
}

public sealed class TokenStoreTests : IDisposable
{
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  private readonly Database _db = new();

  public void Dispose() => _db.Dispose();

  private sealed class XorProtector : ITokenProtector
  {
    public byte[] Protect(byte[] plain) => [.. plain.Select(b => (byte)(b ^ 0x5A)), 0x01];

    public byte[] Unprotect(byte[] protectedBytes)
    {
      if (protectedBytes.Length == 0 || protectedBytes[^1] != 0x01)
      {
        throw new System.Security.Cryptography.CryptographicException("not ours");
      }

      return [.. protectedBytes[..^1].Select(b => (byte)(b ^ 0x5A))];
    }
  }

  private static TokenRecord Record() => new()
  {
    Issuer = "https://as.test",
    ClientId = "app",
    ClientSecret = "super-secret-client-value",
    AccessToken = "access-token-value-123",
    RefreshToken = "refresh-token-value-456",
    ExpiresAt = new DateTimeOffset(2026, 11, 7, 13, 0, 0, TimeSpan.Zero),
    Resource = "https://mcp.test/mcp",
    Scope = "a b",
    Dynamic = true,
  };

  private string RawText()
  {
    using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_db.ConnectionString);
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT KeyHash, Data FROM ModelContextTokenRecords";
    using var reader = command.ExecuteReader();
    var text = new System.Text.StringBuilder();
    while (reader.Read())
    {
      text.Append(reader.GetString(0)).Append(System.Text.Encoding.Latin1.GetString((byte[])reader[1]));
    }

    return text.ToString();
  }

  [Fact]
  public async Task A_record_round_trips_and_is_replaced_and_removed()
  {
    var store = new EfTokenStore<AppDbContext>(_db.NewFactory());
    Assert.Null(await store.GetAsync("k", Ct));

    await store.SetAsync("k", Record(), Ct);
    var read = await store.GetAsync("k", Ct);
    Assert.NotNull(read);
    Assert.Equal("access-token-value-123", read.AccessToken);
    Assert.Equal("refresh-token-value-456", read.RefreshToken);
    Assert.Equal("super-secret-client-value", read.ClientSecret);
    Assert.Equal(Record().ExpiresAt, read.ExpiresAt);
    Assert.True(read.Dynamic);
    Assert.Equal("a b", read.Scope);

    await store.SetAsync("k", new TokenRecord { AccessToken = "newer" }, Ct);
    Assert.Equal("newer", (await store.GetAsync("k", Ct))!.AccessToken);
    Assert.Null((await store.GetAsync("k", Ct))!.RefreshToken);

    await store.RemoveAsync("k", Ct);
    await store.RemoveAsync("k", Ct);
    Assert.Null(await store.GetAsync("k", Ct));
  }

  [Fact]
  public async Task A_second_instance_sees_what_the_first_wrote_and_keys_of_any_length_work()
  {
    var key = "https://mcp.test/" + new string('p', 3000);
    await new EfTokenStore<AppDbContext>(_db.NewFactory()).SetAsync(key, Record(), Ct);
    Assert.NotNull(await new EfTokenStore<AppDbContext>(_db.NewFactory()).GetAsync(key, Ct));
    Assert.Null(await new EfTokenStore<AppDbContext>(_db.NewFactory()).GetAsync(key + "x", Ct));
  }

  [Fact]
  public async Task With_a_protector_no_token_is_readable_in_the_table()
  {
    var plain = new EfTokenStore<AppDbContext>(_db.NewFactory());
    await plain.SetAsync("plain", Record(), Ct);
    Assert.Contains("refresh-token-value-456", RawText());

    using var other = new Database();
    var sealedStore = new EfTokenStore<AppDbContext>(other.NewFactory(), new XorProtector());
    await sealedStore.SetAsync("k", Record(), Ct);
    Assert.Equal("access-token-value-123", (await sealedStore.GetAsync("k", Ct))!.AccessToken);

    using var connection = new Microsoft.Data.Sqlite.SqliteConnection(other.ConnectionString);
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT Data FROM ModelContextTokenRecords";
    var raw = System.Text.Encoding.Latin1.GetString((byte[])command.ExecuteScalar()!);
    Assert.DoesNotContain("token-value", raw);
    Assert.DoesNotContain("super-secret", raw);
  }

  [Fact]
  public async Task A_value_the_protector_cannot_open_reads_as_absent_without_leaking_it()
  {
    await new EfTokenStore<AppDbContext>(_db.NewFactory()).SetAsync("k", Record(), Ct);
    // Written plain, read through a protector: the protector rejects it and the store treats the entry as missing.
    Assert.Null(await new EfTokenStore<AppDbContext>(_db.NewFactory(), new XorProtector()).GetAsync("k", Ct));
  }
}
