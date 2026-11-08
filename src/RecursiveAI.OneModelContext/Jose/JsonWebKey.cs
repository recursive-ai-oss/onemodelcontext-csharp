using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;

namespace RecursiveAI.OneModelContext.Jose;

/// <summary>
/// A public key from a JSON Web Key Set (RFC 7517). Only verification keys the BCL can use are usable: RSA (RFC 7518 section 6.3) and
/// elliptic-curve keys on P-256, P-384 and P-521 (section 6.2). An <c>OKP</c> key (RFC 8037, Ed25519 and Ed448) is recognised so that a
/// caller can say why a token cannot be checked, but it cannot verify here.
/// </summary>
public sealed class JsonWebKey
{
  /// <summary>Smallest RSA modulus accepted, in bits. Shorter keys are skipped when a set is read (RFC 7518 section 3.3).</summary>
  public const int MinRsaBits = 2048;

  private readonly object _gate = new();
  private readonly RSA? _rsa;
  private readonly ECDsa? _ec;

  private JsonWebKey(string keyType, string? keyId, string? algorithm, string? use, string? curve, RSA? rsa, ECDsa? ec)
  {
    KeyType = keyType;
    KeyId = keyId;
    Algorithm = algorithm;
    Use = use;
    Curve = curve;
    _rsa = rsa;
    _ec = ec;
  }

  /// <summary>The <c>kty</c>: <c>RSA</c>, <c>EC</c> or <c>OKP</c>.</summary>
  public string KeyType { get; }

  /// <summary>The <c>kid</c>, or null.</summary>
  public string? KeyId { get; }

  /// <summary>The <c>alg</c> the key is restricted to, or null.</summary>
  public string? Algorithm { get; }

  /// <summary>The <c>use</c>, or null.</summary>
  public string? Use { get; }

  /// <summary>The <c>crv</c> of an <c>EC</c> or <c>OKP</c> key, or null.</summary>
  public string? Curve { get; }

  /// <summary>Whether this is an Edwards-curve key (RFC 8037), which the BCL cannot verify.</summary>
  public bool IsEdwards => KeyType == "OKP";

  /// <summary>Whether the key may be used for signatures: <c>use</c> is absent or <c>sig</c> (RFC 7517 section 4.2).</summary>
  public bool IsSigningKey => Use is null or "sig";

  /// <summary>Wraps an RSA public key. The key is not copied or disposed by this class.</summary>
  /// <param name="rsa">The key.</param>
  /// <param name="keyId">The <c>kid</c>, or null.</param>
  public static JsonWebKey FromRsa(RSA rsa, string? keyId = null)
  {
    ArgumentNullException.ThrowIfNull(rsa);
    return new JsonWebKey("RSA", keyId, null, null, null, rsa, null);
  }

  /// <summary>Wraps an elliptic-curve public key. The key is not copied or disposed by this class.</summary>
  /// <param name="ecdsa">The key.</param>
  /// <param name="keyId">The <c>kid</c>, or null.</param>
  public static JsonWebKey FromECDsa(ECDsa ecdsa, string? keyId = null)
  {
    ArgumentNullException.ThrowIfNull(ecdsa);
    return new JsonWebKey("EC", keyId, null, null, CurveName(ecdsa.ExportParameters(false).Curve), null, ecdsa);
  }

  internal static bool TryRead(JsonElement element, out JsonWebKey? key)
  {
    key = null;
    if (element.ValueKind != JsonValueKind.Object || Str(element, "kty") is not { } kty)
    {
      return false;
    }

    var kid = Str(element, "kid");
    var alg = Str(element, "alg");
    var use = Str(element, "use");
    try
    {
      switch (kty)
      {
        case "RSA":
          if (B64(element, "n") is not { } n || B64(element, "e") is not { } e || n.Length * 8 < MinRsaBits - 7 || n.Length > 1024)
          {
            return false;
          }

          var rsa = RSA.Create();
          try
          {
            rsa.ImportParameters(new RSAParameters { Modulus = n, Exponent = e });
          }
          catch
          {
            rsa.Dispose();
            throw;
          }
          if (rsa.KeySize < MinRsaBits)
          {
            rsa.Dispose();
            return false;
          }

          key = new JsonWebKey(kty, kid, alg, use, null, rsa, null);
          return true;
        case "EC":
          var crv = Str(element, "crv");
          var curve = crv switch
          {
            "P-256" => ECCurve.NamedCurves.nistP256,
            "P-384" => ECCurve.NamedCurves.nistP384,
            "P-521" => ECCurve.NamedCurves.nistP521,
            _ => default,
          };
          if (!curve.IsNamed || B64(element, "x") is not { } x || B64(element, "y") is not { } y)
          {
            return false;
          }

          // Importing validates that the point is on the curve; an invalid point throws.
          var ec = ECDsa.Create(new ECParameters { Curve = curve, Q = new ECPoint { X = x, Y = y } });
          key = new JsonWebKey(kty, kid, alg, use, crv, null, ec);
          return true;
        case "OKP":
          key = new JsonWebKey(kty, kid, alg, use, Str(element, "crv"), null, null);
          return true;
        default:
          return false;
      }
    }
    // Windows can report an invalid EC point as an unsupported curve rather than a CryptographicException.
    catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException or PlatformNotSupportedException)
    {
      return false;
    }
  }

  /// <summary>Whether this key is of the kind the algorithm needs (RFC 7518 section 3.1, and the curve of section 3.4 for ECDSA).</summary>
  internal bool Fits(JwsAlgorithm algorithm)
  {
    if (algorithm.KeyType != KeyType)
    {
      return false;
    }

    if (algorithm.Curve is not null && Curve != algorithm.Curve)
    {
      return false;
    }

    // A key restricted to one algorithm is not used for another (RFC 7517 section 4.4).
    return Algorithm is null || Algorithm == algorithm.Name;
  }

  internal bool Verify(JwsAlgorithm algorithm, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
  {
    try
    {
      lock (_gate)
      {
        if (_rsa is not null)
        {
          return _rsa.VerifyData(data, signature, algorithm.Hash, algorithm.Padding!);
        }

        // JWS carries ECDSA signatures as the fixed-width r || s concatenation (RFC 7518 section 3.4), not DER.
        return _ec is not null && _ec.VerifyData(data, signature, algorithm.Hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
      }
    }
    catch (CryptographicException)
    {
      return false;
    }
  }

  private static string CurveName(ECCurve curve) => curve.Oid?.FriendlyName switch
  {
    "nistP256" or "ECDSA_P256" => "P-256",
    "nistP384" or "ECDSA_P384" => "P-384",
    "nistP521" or "ECDSA_P521" => "P-521",
    _ => curve.Oid?.Value switch
    {
      "1.2.840.10045.3.1.7" => "P-256",
      "1.3.132.0.34" => "P-384",
      "1.3.132.0.35" => "P-521",
      _ => "unknown",
    },
  };

  private static string? Str(JsonElement e, string name) =>
    e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

  private static byte[]? B64(JsonElement e, string name)
  {
    if (Str(e, name) is not { } text || text.Length == 0)
    {
      return null;
    }

    try
    {
      return System.Buffers.Text.Base64Url.DecodeFromChars(text);
    }
    catch (FormatException)
    {
      return null;
    }
  }
}

/// <summary>A JSON Web Key Set (RFC 7517 section 5): the verification keys of an issuer.</summary>
public sealed class JsonWebKeySet
{
  /// <summary>Most keys read from one document; later ones are ignored. Default 64.</summary>
  public const int MaxKeys = 64;

  /// <summary>Creates a set.</summary>
  /// <param name="keys">The keys.</param>
  public JsonWebKeySet(IReadOnlyList<JsonWebKey> keys)
  {
    ArgumentNullException.ThrowIfNull(keys);
    Keys = keys;
  }

  /// <summary>The usable keys.</summary>
  public IReadOnlyList<JsonWebKey> Keys { get; }

  /// <summary>Whether the document held keys but every one is an Edwards-curve key this library cannot verify with.</summary>
  public bool OnlyEdwardsKeys
  {
    get
    {
      foreach (var key in Keys)
      {
        if (!key.IsEdwards)
        {
          return false;
        }
      }

      return Keys.Count > 0;
    }
  }

  /// <summary>Reads a JWKS document. Keys that are malformed, too weak or of an unknown type are skipped, never an error.</summary>
  /// <param name="utf8Json">The document.</param>
  /// <returns>The set, or null when the document is not a JSON object with a <c>keys</c> array.</returns>
  public static JsonWebKeySet? Parse(ReadOnlySpan<byte> utf8Json)
  {
    try
    {
      var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = 8 });
      using var doc = JsonDocument.ParseValue(ref reader);
      if (doc.RootElement.ValueKind != JsonValueKind.Object
        || !doc.RootElement.TryGetProperty("keys", out var array)
        || array.ValueKind != JsonValueKind.Array)
      {
        return null;
      }

      var keys = new List<JsonWebKey>();
      foreach (var item in array.EnumerateArray())
      {
        if (keys.Count >= MaxKeys)
        {
          break;
        }

        if (JsonWebKey.TryRead(item, out var key) && key is not null)
        {
          keys.Add(key);
        }
      }

      return new JsonWebKeySet(keys);
    }
    catch (JsonException)
    {
      return null;
    }
  }
}
