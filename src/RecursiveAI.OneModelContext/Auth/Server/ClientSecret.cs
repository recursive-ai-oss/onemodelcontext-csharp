using System;
using System.Security.Cryptography;
using System.Text;

namespace RecursiveAI.OneModelContext.Auth.Server;

/// <summary>
/// Client secrets are stored as salted PBKDF2-HMAC-SHA256 hashes (RFC 8018), never in clear, and compared in constant time.
/// </summary>
public static class ClientSecret
{
  private const string Prefix = "pbkdf2-sha256";
  private const int DefaultIterations = 100_000;
  private const int SaltBytes = 16;
  private const int HashBytes = 32;

  /// <summary>Makes a new random secret of 256 bits, base64url. Give it to the client once; store only its <see cref="Hash"/>.</summary>
  public static string Generate() => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

  /// <summary>Hashes a secret for storage in <see cref="RegisteredClient.SecretHash"/>.</summary>
  /// <param name="secret">The secret.</param>
  /// <returns>A string of the form <c>pbkdf2-sha256$iterations$salt$hash</c>.</returns>
  public static string Hash(string secret)
  {
    ArgumentException.ThrowIfNullOrEmpty(secret);
    var salt = RandomNumberGenerator.GetBytes(SaltBytes);
    var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, DefaultIterations, HashAlgorithmName.SHA256, HashBytes);
    return string.Join('$', Prefix, DefaultIterations.ToString(System.Globalization.CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(hash));
  }

  /// <summary>Checks a presented secret against a stored hash in constant time. A malformed hash never matches.</summary>
  /// <param name="secret">The presented secret.</param>
  /// <param name="storedHash">The value of <see cref="RegisteredClient.SecretHash"/>, or null.</param>
  public static bool Verify(string secret, string? storedHash)
  {
    ArgumentNullException.ThrowIfNull(secret);

    // Unknown clients and wrong secrets take the same time: hash against a fixed throwaway when there is nothing to compare to.
    var parts = storedHash?.Split('$');
    if (parts is not { Length: 4 } || parts[0] != Prefix
      || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var iterations)
      || iterations is < 1000 or > 10_000_000)
    {
      _ = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), new byte[SaltBytes], DefaultIterations, HashAlgorithmName.SHA256, HashBytes);
      return false;
    }

    try
    {
      var salt = Convert.FromBase64String(parts[2]);
      var expected = Convert.FromBase64String(parts[3]);
      var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
      return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
    catch (FormatException)
    {
      return false;
    }
    catch (ArgumentException)
    {
      return false;
    }
  }
}
