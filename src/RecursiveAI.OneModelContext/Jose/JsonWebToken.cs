using System;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RecursiveAI.OneModelContext.Jose;

/// <summary>A JWS signature algorithm this library verifies (RFC 7518 section 3.1). HMAC and <c>none</c> are deliberately absent.</summary>
internal sealed class JwsAlgorithm
{
  private static readonly JwsAlgorithm[] s_all =
  [
    new("RS256", "RSA", HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, null),
    new("RS384", "RSA", HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1, null),
    new("RS512", "RSA", HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1, null),
    new("PS256", "RSA", HashAlgorithmName.SHA256, RSASignaturePadding.Pss, null),
    new("PS384", "RSA", HashAlgorithmName.SHA384, RSASignaturePadding.Pss, null),
    new("PS512", "RSA", HashAlgorithmName.SHA512, RSASignaturePadding.Pss, null),
    new("ES256", "EC", HashAlgorithmName.SHA256, null, "P-256"),
    new("ES384", "EC", HashAlgorithmName.SHA384, null, "P-384"),
    new("ES512", "EC", HashAlgorithmName.SHA512, null, "P-521"),
  ];

  private JwsAlgorithm(string name, string keyType, HashAlgorithmName hash, RSASignaturePadding? padding, string? curve)
  {
    Name = name;
    KeyType = keyType;
    Hash = hash;
    Padding = padding;
    Curve = curve;
  }

  internal string Name { get; }

  internal string KeyType { get; }

  internal HashAlgorithmName Hash { get; }

  internal RSASignaturePadding? Padding { get; }

  internal string? Curve { get; }

  internal static JwsAlgorithm? Find(string name)
  {
    foreach (var algorithm in s_all)
    {
      // Ordinal and case-sensitive: "rs256" and "None" are not algorithms (RFC 7515 section 4.1.1).
      if (string.Equals(algorithm.Name, name, StringComparison.Ordinal))
      {
        return algorithm;
      }
    }

    return null;
  }
}

/// <summary>Why a compact JWS was refused before any key was tried.</summary>
public enum JwtParseError
{
  /// <summary>It was read.</summary>
  None,

  /// <summary>It is not three base64url segments of JSON, or it is longer than the limit.</summary>
  Malformed,

  /// <summary>The header lists <c>crit</c> extensions, which this library does not understand (RFC 7515 section 4.1.11).</summary>
  UnsupportedCritical,
}

/// <summary>
/// A JSON Web Token in JWS compact form (RFC 7515 section 7.1, RFC 7519), read but not yet trusted. The signature is checked with
/// <see cref="VerifySignature"/> against a key chosen by the caller; the claims mean nothing until it passes.
/// </summary>
public sealed class JsonWebToken
{
  private readonly byte[] _signingInput;
  private readonly byte[] _signature;

  private JsonWebToken(string algorithm, string? keyId, string? type, JsonElement payload, byte[] signingInput, byte[] signature)
  {
    Algorithm = algorithm;
    Type = type;
    KeyId = keyId;
    Payload = payload;
    _signingInput = signingInput;
    _signature = signature;
  }

  /// <summary>The header's <c>alg</c>, unvalidated.</summary>
  public string Algorithm { get; }

  /// <summary>The header's <c>kid</c>, or null.</summary>
  public string? KeyId { get; }

  /// <summary>The header's <c>typ</c>, or null (RFC 7515 section 4.1.9). Compare it before trusting the token for a purpose, as RFC 8725 section 3.11 asks.</summary>
  public string? Type { get; }

  /// <summary>The claims set (a JSON object). Untrusted until <see cref="VerifySignature"/> succeeds.</summary>
  public JsonElement Payload { get; }

  /// <summary>Whether the algorithm is one this library can verify (RS, PS and ES at 256, 384 and 512 bits).</summary>
  public bool IsSupportedAlgorithm => JwsAlgorithm.Find(Algorithm) is not null;

  /// <summary>Reads a compact JWS. It never throws on bad input.</summary>
  /// <param name="token">The token.</param>
  /// <param name="maxLength">Longest token accepted, in characters.</param>
  /// <param name="error">Why it was refused, or <see cref="JwtParseError.None"/>.</param>
  /// <returns>The token, or null.</returns>
  public static JsonWebToken? Parse(string token, int maxLength, out JwtParseError error)
  {
    ArgumentNullException.ThrowIfNull(token);
    error = JwtParseError.Malformed;
    if (token.Length == 0 || token.Length > maxLength)
    {
      return null;
    }

    var first = token.IndexOf('.', StringComparison.Ordinal);
    var second = first < 0 ? -1 : token.IndexOf('.', first + 1);
    if (first <= 0 || second <= first + 1 || second == token.Length - 1 || token.IndexOf('.', second + 1) >= 0)
    {
      return null;
    }

    try
    {
      var header = Base64Url.DecodeFromChars(token.AsSpan(0, first));
      var payload = Base64Url.DecodeFromChars(token.AsSpan(first + 1, second - first - 1));
      var signature = Base64Url.DecodeFromChars(token.AsSpan(second + 1));

      using var headerDoc = JsonDocument.Parse(header, new JsonDocumentOptions { MaxDepth = 8 });
      var h = headerDoc.RootElement;
      if (h.ValueKind != JsonValueKind.Object || !h.TryGetProperty("alg", out var alg) || alg.ValueKind != JsonValueKind.String)
      {
        return null;
      }

      if (h.TryGetProperty("crit", out _))
      {
        error = JwtParseError.UnsupportedCritical;
        return null;
      }

      var kid = h.TryGetProperty("kid", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
      var typ = h.TryGetProperty("typ", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
      using var payloadDoc = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
      if (payloadDoc.RootElement.ValueKind != JsonValueKind.Object)
      {
        return null;
      }

      // The signature covers the ASCII of the first two segments exactly as received (RFC 7515 section 5.2, step 8).
      var input = Encoding.ASCII.GetBytes(token[..second]);
      error = JwtParseError.None;
      return new JsonWebToken(alg.GetString()!, kid, typ, payloadDoc.RootElement.Clone(), input, signature);
    }
    catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
    {
      error = JwtParseError.Malformed;
      return null;
    }
  }

  /// <summary>
  /// Whether the signature verifies under <paramref name="key"/> with this token's algorithm. An algorithm outside the supported set,
  /// and a key of the wrong type or curve for the algorithm, never verify; this is what stops algorithm confusion.
  /// </summary>
  /// <param name="key">The candidate key.</param>
  public bool VerifySignature(JsonWebKey key)
  {
    ArgumentNullException.ThrowIfNull(key);
    if (JwsAlgorithm.Find(Algorithm) is not { } algorithm || !key.Fits(algorithm) || !key.IsSigningKey)
    {
      return false;
    }

    return key.Verify(algorithm, _signingInput, _signature);
  }
}
