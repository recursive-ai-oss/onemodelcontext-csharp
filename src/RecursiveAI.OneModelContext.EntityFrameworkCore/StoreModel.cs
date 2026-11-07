using System;
using System.Linq;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace RecursiveAI.OneModelContext.EntityFrameworkCore;

// Rows are internal on purpose: the stores own the tables, and a host that wants other queries reads through the store interfaces.
// Instants are stored as UTC ticks (long) so that comparisons and updates run in SQL on every provider; some providers cannot compare
// DateTimeOffset in a WHERE clause.

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

internal sealed class CodeRow
{
  public string CodeHash { get; set; } = string.Empty;

  public string ClientId { get; set; } = string.Empty;

  public string RedirectUri { get; set; } = string.Empty;

  public bool RedirectUriSent { get; set; }

  public string Subject { get; set; } = string.Empty;

  public string Scope { get; set; } = string.Empty;

  public string Resource { get; set; } = string.Empty;

  public string CodeChallenge { get; set; } = string.Empty;

  public string FamilyId { get; set; } = string.Empty;

  public string ClaimsJson { get; set; } = "{}";

  public long ExpiresAtTicks { get; set; }

  public bool Redeemed { get; set; }
}

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

  public bool Used { get; set; }
}

internal sealed class RevocationRow
{
  public string KeyHash { get; set; } = string.Empty;

  public long ExpiresAtTicks { get; set; }
}

internal sealed class AssertionRow
{
  public string KeyHash { get; set; } = string.Empty;

  public long ExpiresAtTicks { get; set; }
}

internal sealed class TokenRecordRow
{
  public string KeyHash { get; set; } = string.Empty;

  public byte[] Data { get; set; } = [];

  public long UpdatedAtTicks { get; set; }
}

[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(System.Collections.Generic.Dictionary<string, string>))]
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

  internal static string WriteList(System.Collections.Generic.IEnumerable<string> values) =>
    JsonSerializer.Serialize(System.Linq.Enumerable.ToArray(values), StoreJson.Default.StringArray);

  internal static System.Collections.Generic.Dictionary<string, string> ReadMap(string json) =>
    JsonSerializer.Deserialize(json, StoreJson.Default.DictionaryStringString) ?? [];

  internal static string WriteMap(System.Collections.Generic.IReadOnlyDictionary<string, string> values) =>
    JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, string>(values), StoreJson.Default.DictionaryStringString);

  internal static long Ticks(DateTimeOffset value) => value.UtcTicks;

  internal static DateTimeOffset At(long ticks) => new(ticks, TimeSpan.Zero);
}

/// <summary>
/// Adds the OneModelContext store tables to your own <see cref="DbContext"/>. Call these from <c>OnModelCreating</c>, then create the
/// tables with <c>EnsureCreated</c> or a migration (see <c>docs/stores.md</c>).
/// </summary>
public static class ContextStoresModelBuilderExtensions
{
  /// <summary>The default table-name prefix.</summary>
  public const string DefaultTablePrefix = "ModelContext";

  /// <summary>Maps every store this package provides: the authorization store, the token store and the task store.</summary>
  /// <param name="modelBuilder">The model builder of your context.</param>
  /// <param name="tablePrefix">Prefix for the table names. Default <see cref="DefaultTablePrefix"/>.</param>
  /// <param name="schema">The database schema for the tables, or null for the provider's default.</param>
  /// <returns>The same builder.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="modelBuilder"/> is null.</exception>
  public static ModelBuilder ApplyContextStores(this ModelBuilder modelBuilder, string tablePrefix = DefaultTablePrefix, string? schema = null) =>
    modelBuilder.ApplyAuthorizationStore(tablePrefix, schema).ApplyTokenStore(tablePrefix, schema).ApplyTaskStore(tablePrefix, schema);

  /// <summary>
  /// Maps the tables of <see cref="EfAuthorizationStore{TContext}"/>: clients, authorization codes, refresh tokens, revocations and
  /// seen assertions. Codes, refresh tokens and assertion ids are stored as hashes, never as the secret.
  /// </summary>
  /// <param name="modelBuilder">The model builder of your context.</param>
  /// <param name="tablePrefix">Prefix for the table names. Default <see cref="DefaultTablePrefix"/>.</param>
  /// <param name="schema">The database schema for the tables, or null for the provider's default.</param>
  /// <returns>The same builder.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="modelBuilder"/> is null.</exception>
  public static ModelBuilder ApplyAuthorizationStore(this ModelBuilder modelBuilder, string tablePrefix = DefaultTablePrefix, string? schema = null)
  {
    ArgumentNullException.ThrowIfNull(modelBuilder);
    ArgumentNullException.ThrowIfNull(tablePrefix);

    modelBuilder.Entity<ClientRow>(e =>
    {
      e.ToTable(tablePrefix + "Clients", schema);
      e.HasKey(r => r.ClientId);
      // A relational key must be bounded (SQL Server limits index keys to 900 bytes). Longer ids are refused by the store.
      e.Property(r => r.ClientId).HasMaxLength(StoreKeys.MaxClientIdLength);
    });
    modelBuilder.Entity<CodeRow>(e =>
    {
      e.ToTable(tablePrefix + "AuthorizationCodes", schema);
      e.HasKey(r => r.CodeHash);
      e.Property(r => r.CodeHash).HasMaxLength(64);
      e.HasIndex(r => r.ExpiresAtTicks);
    });
    modelBuilder.Entity<RefreshTokenRow>(e =>
    {
      e.ToTable(tablePrefix + "RefreshTokens", schema);
      e.HasKey(r => r.TokenHash);
      e.Property(r => r.TokenHash).HasMaxLength(64);
      e.HasIndex(r => r.FamilyExpiresAtTicks);
    });
    modelBuilder.Entity<RevocationRow>(e =>
    {
      e.ToTable(tablePrefix + "Revocations", schema);
      e.HasKey(r => r.KeyHash);
      e.Property(r => r.KeyHash).HasMaxLength(64);
      e.HasIndex(r => r.ExpiresAtTicks);
    });
    modelBuilder.Entity<AssertionRow>(e =>
    {
      e.ToTable(tablePrefix + "SeenAssertions", schema);
      e.HasKey(r => r.KeyHash);
      e.Property(r => r.KeyHash).HasMaxLength(64);
      e.HasIndex(r => r.ExpiresAtTicks);
    });
    return modelBuilder;
  }

  /// <summary>Maps the table of <see cref="EfTokenStore{TContext}"/>.</summary>
  /// <param name="modelBuilder">The model builder of your context.</param>
  /// <param name="tablePrefix">Prefix for the table names. Default <see cref="DefaultTablePrefix"/>.</param>
  /// <param name="schema">The database schema for the tables, or null for the provider's default.</param>
  /// <returns>The same builder.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="modelBuilder"/> is null.</exception>
  public static ModelBuilder ApplyTokenStore(this ModelBuilder modelBuilder, string tablePrefix = DefaultTablePrefix, string? schema = null)
  {
    ArgumentNullException.ThrowIfNull(modelBuilder);
    ArgumentNullException.ThrowIfNull(tablePrefix);
    modelBuilder.Entity<TokenRecordRow>(e =>
    {
      e.ToTable(tablePrefix + "TokenRecords", schema);
      e.HasKey(r => r.KeyHash);
      e.Property(r => r.KeyHash).HasMaxLength(64);
    });
    return modelBuilder;
  }

  /// <summary>Maps the table of <see cref="EfTaskStore{TContext}"/>.</summary>
  /// <param name="modelBuilder">The model builder of your context.</param>
  /// <param name="tablePrefix">Prefix for the table names. Default <see cref="DefaultTablePrefix"/>.</param>
  /// <param name="schema">The database schema for the tables, or null for the provider's default.</param>
  /// <returns>The same builder.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="modelBuilder"/> is null.</exception>
  public static ModelBuilder ApplyTaskStore(this ModelBuilder modelBuilder, string tablePrefix = DefaultTablePrefix, string? schema = null)
  {
    ArgumentNullException.ThrowIfNull(modelBuilder);
    ArgumentNullException.ThrowIfNull(tablePrefix);
    modelBuilder.Entity<TaskRow>(e =>
    {
      e.ToTable(tablePrefix + "Tasks", schema);
      e.HasKey(r => r.TaskId);
      e.Property(r => r.TaskId).HasMaxLength(StoreKeys.MaxTaskIdLength);
      e.HasIndex(r => r.ExpiresAtTicks);
    });
    return modelBuilder;
  }
}
