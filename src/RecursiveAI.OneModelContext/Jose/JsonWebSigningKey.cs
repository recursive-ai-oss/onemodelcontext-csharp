using System;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace RecursiveAI.OneModelContext.Jose;

/// <summary>
/// A private key that signs JWTs (RFC 7515), for an issuer such as the authorization server. RSA keys sign with RS or PS algorithms
/// and elliptic-curve keys with ES256, ES384 or ES512, all from the BCL. The class holds the key it is given and does not dispose it.
/// </summary>
public sealed class JsonWebSigningKey
{
  private const long NotRetired = long.MinValue;

  private readonly RSA? _rsa;
  private readonly ECDsa? _ec;
  private readonly JwsAlgorithm _algorithm;
  private long _retiredTicks = NotRetired;

  private JsonWebSigningKey(string keyId, JwsAlgorithm algorithm, RSA? rsa, ECDsa? ec)
  {
    KeyId = keyId;
    _algorithm = algorithm;
    _rsa = rsa;
    _ec = ec;
  }

  /// <summary>The <c>kid</c> placed in token headers and in the published key set. Unique among the keys of one issuer.</summary>
  public string KeyId { get; }

  /// <summary>The JWS algorithm this key signs with, for example <c>ES256</c>.</summary>
  public string Algorithm => _algorithm.Name;

  /// <summary>
  /// When the key stopped signing, or null while it is the signing key. A retired key stays in the published key set until the
  /// longest-lived token it signed has expired, so rotation never invalidates a token that is still good.
  /// </summary>
  public DateTimeOffset? RetiredAt
  {
    get => Volatile.Read(ref _retiredTicks) is var ticks && ticks != NotRetired ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    set => Volatile.Write(ref _retiredTicks, value is { } at ? at.UtcTicks : NotRetired);
  }

  /// <summary>Wraps an RSA private key. Keys under 2048 bits are refused (RFC 7518 section 3.3).</summary>
  /// <param name="rsa">The key. It is not copied or disposed here.</param>
  /// <param name="keyId">The <c>kid</c>.</param>
  /// <param name="algorithm">One of <c>RS256</c>, <c>RS384</c>, <c>RS512</c>, <c>PS256</c>, <c>PS384</c>, <c>PS512</c>. Default <c>RS256</c>, the algorithm every client must support.</param>
  /// <exception cref="ArgumentException">The key is too small, or the algorithm is not an RSA one this library signs with.</exception>
  public static JsonWebSigningKey FromRsa(RSA rsa, string keyId, string algorithm = "RS256")
  {
    ArgumentNullException.ThrowIfNull(rsa);
    ArgumentException.ThrowIfNullOrEmpty(keyId);
    if (JwsAlgorithm.Find(algorithm) is not { KeyType: "RSA" } found)
    {
      throw new ArgumentException("The algorithm must be RS256, RS384, RS512, PS256, PS384 or PS512.", nameof(algorithm));
    }

    if (rsa.KeySize < JsonWebKey.MinRsaBits)
    {
      throw new ArgumentException($"RSA keys must be at least {JsonWebKey.MinRsaBits} bits.", nameof(rsa));
    }

    return new JsonWebSigningKey(keyId, found, rsa, null);
  }

  /// <summary>Wraps an elliptic-curve private key on P-256, P-384 or P-521; the algorithm follows the curve (RFC 7518 section 3.4).</summary>
  /// <param name="ecdsa">The key. It is not copied or disposed here.</param>
  /// <param name="keyId">The <c>kid</c>.</param>
  /// <exception cref="ArgumentException">The curve is not one of the three.</exception>
  public static JsonWebSigningKey FromECDsa(ECDsa ecdsa, string keyId)
  {
    ArgumentNullException.ThrowIfNull(ecdsa);
    ArgumentException.ThrowIfNullOrEmpty(keyId);
    var algorithm = ecdsa.KeySize switch
    {
      256 => "ES256",
      384 => "ES384",
      521 => "ES512",
      _ => throw new ArgumentException("The key must be on P-256, P-384 or P-521.", nameof(ecdsa)),
    };
    return new JsonWebSigningKey(keyId, JwsAlgorithm.Find(algorithm)!, null, ecdsa);
  }

  /// <summary>Writes the public half as a JWK (RFC 7517) with <c>use</c> <c>sig</c> and this key's <c>alg</c>.</summary>
  /// <param name="writer">Receives one JSON object.</param>
  public void WritePublicJwk(Utf8JsonWriter writer)
  {
    ArgumentNullException.ThrowIfNull(writer);
    writer.WriteStartObject();
    if (_rsa is not null)
    {
      var p = _rsa.ExportParameters(false);
      writer.WriteString("kty", "RSA");
      writer.WriteString("use", "sig");
      writer.WriteString("alg", Algorithm);
      writer.WriteString("kid", KeyId);
      writer.WriteString("n", System.Buffers.Text.Base64Url.EncodeToString(p.Modulus!));
      writer.WriteString("e", System.Buffers.Text.Base64Url.EncodeToString(p.Exponent!));
    }
    else
    {
      var p = _ec!.ExportParameters(false);
      writer.WriteString("kty", "EC");
      writer.WriteString("use", "sig");
      writer.WriteString("alg", Algorithm);
      writer.WriteString("kid", KeyId);
      writer.WriteString("crv", _algorithm.Curve!);
      writer.WriteString("x", System.Buffers.Text.Base64Url.EncodeToString(p.Q.X!));
      writer.WriteString("y", System.Buffers.Text.Base64Url.EncodeToString(p.Q.Y!));
    }

    writer.WriteEndObject();
  }

  /// <summary>Gets the matching verification key, for checking tokens this key signed.</summary>
  public JsonWebKey ToPublicKey() => _rsa is not null ? JsonWebKey.FromRsa(_rsa, KeyId) : JsonWebKey.FromECDsa(_ec!, KeyId);

  /// <summary>Signs a JWT (RFC 7519) in compact form.</summary>
  /// <param name="type">The header <c>typ</c>, for example <c>at+jwt</c> (RFC 9068 section 2.1).</param>
  /// <param name="payloadUtf8">The claims set as UTF-8 JSON.</param>
  public string SignJwt(string type, ReadOnlySpan<byte> payloadUtf8)
  {
    ArgumentException.ThrowIfNullOrEmpty(type);
    var header = new ArrayBufferWriter<byte>();
    using (var w = new Utf8JsonWriter(header))
    {
      w.WriteStartObject();
      w.WriteString("alg", Algorithm);
      w.WriteString("typ", type);
      w.WriteString("kid", KeyId);
      w.WriteEndObject();
    }

    var input = System.Buffers.Text.Base64Url.EncodeToString(header.WrittenSpan) + "." + System.Buffers.Text.Base64Url.EncodeToString(payloadUtf8);
    byte[] signature = _rsa is not null
      ? _rsa.SignData(Encoding.ASCII.GetBytes(input), _algorithm.Hash, _algorithm.Padding!)
      // JWS carries ECDSA signatures as r || s (RFC 7518 section 3.4).
      : _ec!.SignData(Encoding.ASCII.GetBytes(input), _algorithm.Hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    return input + "." + System.Buffers.Text.Base64Url.EncodeToString(signature);
  }
}
