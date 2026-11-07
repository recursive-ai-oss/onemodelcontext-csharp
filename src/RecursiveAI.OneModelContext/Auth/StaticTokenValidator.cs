using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.Auth;

/// <summary>
/// Accepts a fixed set of opaque tokens, each belonging to one user with a set of scopes: the validator for local servers and small
/// deployments. Only the SHA-256 hash of a token is kept, and a presented token is compared against every entry in constant time, so
/// neither memory contents nor timing reveal a token. Tokens are bound to this server by construction (the host issued them for it), so
/// there is no audience to check.
/// </summary>
/// <remarks>Use tokens with at least 128 bits of randomness, such as 32 random bytes in base64url; <see cref="Add"/> refuses anything shorter than 16 characters.</remarks>
public sealed class StaticTokenValidator : ITokenValidator
{
  private readonly object _gate = new();
  private readonly TimeProvider _time;
  private Entry[] _entries = [];

  /// <summary>Creates an empty validator.</summary>
  /// <param name="timeProvider">The clock for token expiry; null for the system clock.</param>
  public StaticTokenValidator(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

  /// <summary>Computes the hash <see cref="AddHashed"/> expects, for keeping hashes in configuration instead of tokens.</summary>
  /// <param name="token">The token.</param>
  public static byte[] Hash(string token)
  {
    ArgumentNullException.ThrowIfNull(token);
    return SHA256.HashData(Encoding.UTF8.GetBytes(token));
  }

  /// <summary>Adds a token. Only its hash is kept.</summary>
  /// <param name="token">The token, at least 16 characters.</param>
  /// <param name="user">The principal's name (the <c>sub</c>).</param>
  /// <param name="scopes">The scopes it grants; none by default.</param>
  /// <param name="expiresAt">When it stops working; never by default.</param>
  /// <exception cref="ArgumentException">The token is shorter than 16 characters or the user is empty.</exception>
  public StaticTokenValidator Add(string token, string user, IEnumerable<string>? scopes = null, DateTimeOffset? expiresAt = null)
  {
    ArgumentNullException.ThrowIfNull(token);
    if (token.Length < 16)
    {
      throw new ArgumentException("A static token must be at least 16 characters; use a long random value.", nameof(token));
    }

    return AddHashed(Hash(token), user, scopes, expiresAt);
  }

  /// <summary>Adds a token by its SHA-256 hash (see <see cref="Hash"/>).</summary>
  /// <param name="sha256">The 32-byte hash.</param>
  /// <param name="user">The principal's name.</param>
  /// <param name="scopes">The scopes it grants.</param>
  /// <param name="expiresAt">When it stops working.</param>
  public StaticTokenValidator AddHashed(ReadOnlySpan<byte> sha256, string user, IEnumerable<string>? scopes = null, DateTimeOffset? expiresAt = null)
  {
    if (sha256.Length != SHA256.HashSizeInBytes)
    {
      throw new ArgumentException("The hash must be 32 bytes (SHA-256).", nameof(sha256));
    }

    ArgumentException.ThrowIfNullOrEmpty(user);
    var entry = new Entry(sha256.ToArray(), user, [.. scopes ?? []], expiresAt);
    lock (_gate)
    {
      _entries = [.. _entries, entry];
    }

    return this;
  }

  /// <inheritdoc />
  public ValueTask<TokenValidationResult> ValidateAsync(string accessToken, TokenValidationRequest request, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(accessToken);
    var presented = Hash(accessToken);
    Entry? match = null;

    // Visit every entry whatever the outcome, so the time taken does not say which entry matched.
    foreach (var entry in Volatile.Read(ref _entries))
    {
      if (CryptographicOperations.FixedTimeEquals(presented, entry.Hash))
      {
        match = entry;
      }
    }

    if (match is null)
    {
      return ValueTask.FromResult(TokenValidationResult.Failure("The token is not valid."));
    }

    if (match.ExpiresAt is { } expiry && expiry <= _time.GetUtcNow())
    {
      return ValueTask.FromResult(TokenValidationResult.Failure("The token has expired."));
    }

    var identity = new ClaimsIdentity("Bearer", AuthClaimTypes.Subject, null);
    identity.AddClaim(new Claim(AuthClaimTypes.Subject, match.User));
    foreach (var scope in match.Scopes)
    {
      identity.AddClaim(new Claim(AuthClaimTypes.Scope, scope));
    }

    return ValueTask.FromResult(TokenValidationResult.Success(new ClaimsPrincipal(identity), match.Scopes, match.ExpiresAt));
  }

  private sealed record Entry(byte[] Hash, string User, string[] Scopes, DateTimeOffset? ExpiresAt);
}
