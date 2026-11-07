using RecursiveAI.OneModelContext.Auth.Server;
using RecursiveAI.OneModelContext.OAuth;
using Xunit;

namespace RecursiveAI.OneModelContext.OneLite.Tests;

/// <summary>Two instances (two factories, each with its own connections) racing on one database.</summary>
public sealed class RaceTests : IAsyncDisposable
{
  private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  private readonly StoreFile _db = new();

  public async ValueTask DisposeAsync() => await _db.DisposeAsync();

  private async Task<OneLiteAuthorizationStore[]> InstancesAsync(int count)
  {
    var stores = new OneLiteAuthorizationStore[count];
    for (var i = 0; i < count; i++)
    {
      stores[i] = await _db.AuthorizationAsync();
    }

    return stores;
  }

  [Fact]
  public async Task A_code_redeemed_by_many_instances_at_once_succeeds_exactly_once()
  {
    var instances = await InstancesAsync(8);
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
    var instances = await InstancesAsync(8);
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
    var instances = await InstancesAsync(8);
    var results = await Task.WhenAll(instances.Select(s => Task.Run(async () => await s.TryRecordAssertionAsync("client:app\njti-1", Now.AddMinutes(5), Now, Ct), Ct)));
    Assert.Equal(1, results.Count(r => r));
  }

  [Fact]
  public async Task Revocation_and_saves_from_many_instances_at_once_do_not_fail()
  {
    var instances = await InstancesAsync(8);
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

public sealed class TokenStoreTests : IAsyncDisposable
{
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  private readonly StoreFile _db = new();

  public async ValueTask DisposeAsync() => await _db.DisposeAsync();

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

  private static string RawText(StoreFile file)
  {
    using var raw = file.Raw();
    var text = new System.Text.StringBuilder();
    foreach (var row in raw.Query<RawRow>($"SELECT KeyHash, Data FROM ModelContextTokenRecords"))
    {
      text.Append(row.KeyHash).Append(System.Text.Encoding.Latin1.GetString(row.Data));
    }

    return text.ToString();
  }

  [Fact]
  public async Task A_record_round_trips_and_is_replaced_and_removed()
  {
    var store = await _db.TokenAsync();
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
    await (await _db.TokenAsync()).SetAsync(key, Record(), Ct);
    Assert.NotNull(await (await _db.TokenAsync()).GetAsync(key, Ct));
    Assert.Null(await (await _db.TokenAsync()).GetAsync(key + "x", Ct));
  }

  [Fact]
  public async Task With_a_protector_no_token_is_readable_in_the_table()
  {
    var plain = await _db.TokenAsync();
    await plain.SetAsync("plain", Record(), Ct);
    Assert.Contains("refresh-token-value-456", RawText(_db));

    await using var other = new StoreFile();
    var sealedStore = await other.TokenAsync(new XorProtector());
    await sealedStore.SetAsync("k", Record(), Ct);
    Assert.Equal("access-token-value-123", (await sealedStore.GetAsync("k", Ct))!.AccessToken);

    var raw = RawText(other);
    Assert.DoesNotContain("token-value", raw);
    Assert.DoesNotContain("super-secret", raw);
  }

  [Fact]
  public async Task A_value_the_protector_cannot_open_reads_as_absent_without_leaking_it()
  {
    await (await _db.TokenAsync()).SetAsync("k", Record(), Ct);
    // Written plain, read through a protector: the protector rejects it and the store treats the entry as missing.
    Assert.Null(await (await _db.TokenAsync(new XorProtector())).GetAsync("k", Ct));
  }
}

[RecursiveAI.OneLite.OneLiteRow]
internal sealed class RawRow
{
  public string KeyHash { get; set; } = string.Empty;

  public byte[] Data { get; set; } = [];
}
