using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RecursiveAI.OneModelContext.Auth.Server;

/// <summary>
/// Client secrets are stored as salted hashes, never in clear, and compared in constant time. The secrets <see cref="Generate"/> makes
/// are 256 random bits, so a fast keyed hash (HMAC-SHA-256 with a per-secret salt as the key) is enough and costs an unauthenticated
/// caller's request next to nothing; a slow password hash would only be a CPU sink at the token endpoint. Hashes written by earlier
/// versions (<c>pbkdf2-sha256</c>, RFC 8018) are still read. Do not use this for human-chosen passwords.
/// </summary>
public static class ClientSecret
{
  private const string Prefix = "hmac-sha256";
  private const string LegacyPrefix = "pbkdf2-sha256";
  private const int SaltBytes = 16;

  /// <summary>Makes a new random secret of 256 bits, base64url. Give it to the client once; store only its <see cref="Hash"/>.</summary>
  public static string Generate() => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

  /// <summary>Hashes a secret for storage in <see cref="RegisteredClient.SecretHash"/>. The secret should be random and long, like <see cref="Generate"/> makes.</summary>
  /// <param name="secret">The secret.</param>
  /// <returns>A string of the form <c>hmac-sha256$salt$hash</c>.</returns>
  public static string Hash(string secret)
  {
    ArgumentException.ThrowIfNullOrEmpty(secret);
    var salt = RandomNumberGenerator.GetBytes(SaltBytes);
    var hash = HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(secret));
    return string.Join('$', Prefix, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
  }

  /// <summary>Checks a presented secret against a stored hash in constant time. A malformed hash never matches.</summary>
  /// <param name="secret">The presented secret.</param>
  /// <param name="storedHash">The value of <see cref="RegisteredClient.SecretHash"/>, or null.</param>
  public static bool Verify(string secret, string? storedHash)
  {
    ArgumentNullException.ThrowIfNull(secret);
    var data = Encoding.UTF8.GetBytes(secret);

    // Unknown clients and wrong secrets take the same time: hash against a fixed throwaway when there is nothing to compare to.
    var parts = storedHash?.Split('$');
    try
    {
      if (parts is { Length: 3 } && parts[0] == Prefix)
      {
        var expected = Convert.FromBase64String(parts[2]);
        return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Convert.FromBase64String(parts[1]), data), expected);
      }

      if (parts is { Length: 4 } && parts[0] == LegacyPrefix
        && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) && iterations is >= 1000 and <= 10_000_000)
      {
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(data, Convert.FromBase64String(parts[2]), iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
      }
    }
    catch (FormatException)
    {
      return false;
    }
    catch (ArgumentException)
    {
      return false;
    }

    _ = HMACSHA256.HashData(new byte[SaltBytes], data);
    return false;
  }
}
