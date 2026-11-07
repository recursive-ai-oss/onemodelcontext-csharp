using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RecursiveAI.OneModelContext.OAuth;

/// <summary>
/// Signs the JWT a client presents to prove its identity at a token endpoint (<c>private_key_jwt</c>, OpenID Connect Core section 9; RFC 7523
/// section 2.2). RSA keys sign with RS256 or PS256, elliptic-curve keys with ES256, ES384 or ES512 by curve.
/// </summary>
public static class ClientAssertion
{
  /// <summary>The <c>client_assertion_type</c> for a JWT assertion (RFC 7523 section 2.2).</summary>
  public const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

  /// <summary>Picks the signing algorithm for a key, honouring the server's list when it has one.</summary>
  /// <param name="key">An <see cref="RSA"/> or <see cref="ECDsa"/> private key.</param>
  /// <param name="supported"><c>token_endpoint_auth_signing_alg_values_supported</c>, or null.</param>
  /// <exception cref="OAuthException">The key type is not supported, or the server accepts none of the algorithms the key can sign with.</exception>
  public static string ChooseAlgorithm(AsymmetricAlgorithm key, IReadOnlyList<string>? supported)
  {
    ArgumentNullException.ThrowIfNull(key);
    string[] candidates;
    if (key is RSA)
    {
      candidates = ["RS256", "PS256"];
    }
    else if (key is ECDsa ec)
    {
      var name = ec.ExportParameters(false).Curve.Oid.FriendlyName;
      candidates = name switch
      {
        "nistP256" or "ECDSA_P256" => ["ES256"],
        "nistP384" or "ECDSA_P384" => ["ES384"],
        "nistP521" or "ECDSA_P521" => ["ES512"],
        _ => throw new OAuthException("The client key uses an elliptic curve that has no JOSE signing algorithm here (P-256, P-384 and P-521 do)."),
      };
    }
    else
    {
      throw new OAuthException("The client key must be an RSA or ECDSA private key.");
    }

    if (supported is null)
    {
      return candidates[0];
    }

    foreach (var candidate in candidates)
    {
      if (supported.Contains(candidate))
      {
        return candidate;
      }
    }

    throw new OAuthException("The authorization server accepts none of the signature algorithms this client key can produce.");
  }

  /// <summary>Builds and signs an assertion.</summary>
  /// <param name="clientId">The client identifier: becomes <c>iss</c> and <c>sub</c>.</param>
  /// <param name="audience">The values for <c>aud</c>: the authorization server's issuer and token endpoint.</param>
  /// <param name="key">The private key.</param>
  /// <param name="algorithm">The algorithm from <see cref="ChooseAlgorithm"/>.</param>
  /// <param name="keyId">The <c>kid</c> header, or null.</param>
  /// <param name="now">The current time.</param>
  /// <param name="lifetime">How long the assertion is valid; short, because it is single use. Default 60 seconds.</param>
  /// <returns>The compact JWS.</returns>
  public static string Create(string clientId, IReadOnlyList<string> audience, AsymmetricAlgorithm key, string algorithm, string? keyId, DateTimeOffset now, TimeSpan? lifetime = null)
  {
    ArgumentException.ThrowIfNullOrEmpty(clientId);
    ArgumentNullException.ThrowIfNull(audience);
    ArgumentNullException.ThrowIfNull(key);
    using var header = new System.IO.MemoryStream();
    using (var w = new Utf8JsonWriter(header))
    {
      w.WriteStartObject();
      w.WriteString("alg", algorithm);
      w.WriteString("typ", "JWT");
      if (keyId is not null)
      {
        w.WriteString("kid", keyId);
      }

      w.WriteEndObject();
    }

    using var payload = new System.IO.MemoryStream();
    using (var w = new Utf8JsonWriter(payload))
    {
      w.WriteStartObject();
      w.WriteString("iss", clientId);
      w.WriteString("sub", clientId);
      if (audience.Count == 1)
      {
        w.WriteString("aud", audience[0]);
      }
      else
      {
        w.WriteStartArray("aud");
        foreach (var a in audience)
        {
          w.WriteStringValue(a);
        }

        w.WriteEndArray();
      }

      w.WriteString("jti", Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
      w.WriteNumber("iat", now.ToUnixTimeSeconds());
      w.WriteNumber("exp", (now + (lifetime ?? TimeSpan.FromSeconds(60))).ToUnixTimeSeconds());
      w.WriteEndObject();
    }

    var signingInput = Base64Url(header.ToArray()) + "." + Base64Url(payload.ToArray());
    var data = Encoding.ASCII.GetBytes(signingInput);
    byte[] signature = algorithm switch
    {
      "RS256" when key is RSA rsa => rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
      "PS256" when key is RSA rsa => rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss),
      "ES256" when key is ECDsa ec => ec.SignData(data, HashAlgorithmName.SHA256),
      "ES384" when key is ECDsa ec => ec.SignData(data, HashAlgorithmName.SHA384),
      "ES512" when key is ECDsa ec => ec.SignData(data, HashAlgorithmName.SHA512),
      _ => throw new OAuthException("The signature algorithm does not match the client key."),
    };
    return signingInput + "." + Base64Url(signature);
  }

  private static string Base64Url(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);
}
