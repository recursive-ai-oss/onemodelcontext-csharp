using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecursiveAI.OneModelContext.Wire.Modern;

/// <summary>What a <c>requestState</c> says about the request it came from; replayed state must match it.</summary>
/// <param name="Method">The JSON-RPC method.</param>
/// <param name="Name">The tool, prompt or resource the request addressed.</param>
/// <param name="Principal">The authenticated caller, or empty.</param>
/// <param name="ArgumentsDigest">A digest of the request's own parameters, so a retry cannot change what was asked.</param>
internal readonly record struct StateBinding(string Method, string? Name, string Principal, string ArgumentsDigest);

/// <summary>The content of a <c>requestState</c>.</summary>
internal sealed class RequestStatePayload
{
  [JsonPropertyName("exp")]
  public long ExpiresAtUnixSeconds { get; set; }

  [JsonPropertyName("m")]
  public string Method { get; set; } = string.Empty;

  [JsonPropertyName("n")]
  public string? Name { get; set; }

  [JsonPropertyName("p")]
  public string Principal { get; set; } = string.Empty;

  [JsonPropertyName("d")]
  public string ArgumentsDigest { get; set; } = string.Empty;

  /// <summary>What the handler put in <see cref="RequestContext.State"/>.</summary>
  [JsonPropertyName("s")]
  public Dictionary<string, string>? State { get; set; }

  /// <summary>The answers given so far, so that a handler re-run from the start sees all of them.</summary>
  [JsonPropertyName("a")]
  public Dictionary<string, JsonElement>? Answers { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RequestStatePayload))]
internal sealed partial class ModernJsonContext : JsonSerializerContext;

/// <summary>
/// Protects <c>requestState</c> (2026-07-28 basic/patterns/mrtr, server requirements 4 and 5): the state passes through the client, so it
/// is treated as attacker-controlled. It is signed with HMAC-SHA256, or sealed with AES-GCM when the host asks for confidentiality, and it
/// carries an expiry, the caller and a digest of the request it belongs to. Verification compares in constant time.
/// </summary>
/// <remarks>
/// Token layout (before base64url): version byte (1), mode byte (1 = HMAC-SHA256, 2 = AES-256-GCM), then for HMAC the payload JSON and a
/// 32-byte tag over everything before it; for AES-GCM a 12-byte nonce, the ciphertext and a 16-byte tag, with the two header bytes as
/// associated data. The two keys are derived from the host key with HKDF-SHA256 so one secret never serves both purposes.
/// </remarks>
internal sealed class RequestStateProtector
{
  private const byte FormatVersion = 1;
  private const byte ModeHmac = 1;
  private const byte ModeGcm = 2;
  private const int TagBytes = 32;
  private const int NonceBytes = 12;
  private const int GcmTagBytes = 16;

  private readonly byte[] _macKey;
  private readonly byte[] _encryptionKey;
  private readonly bool _encrypt;
  private readonly TimeSpan _lifetime;
  private readonly TimeProvider _time;
  private readonly int _maxBytes;

  internal RequestStateProtector(byte[]? hostKey, bool encrypt, TimeSpan lifetime, int maxBytes, TimeProvider time)
  {
    // Without a host key the key lives only in this process: state from another instance or an earlier run is refused.
    var master = hostKey ?? RandomNumberGenerator.GetBytes(32);
    if (master.Length < 32)
    {
      throw new ArgumentException("The request state key must be at least 32 bytes.", nameof(hostKey));
    }

    _macKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: "onemodelcontext/request-state/mac"u8.ToArray());
    _encryptionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: "onemodelcontext/request-state/enc"u8.ToArray());
    _encrypt = encrypt;
    _lifetime = lifetime;
    _maxBytes = maxBytes;
    _time = time;
  }

  /// <summary>Builds the opaque string for a payload; the expiry and binding are filled in here.</summary>
  /// <exception cref="InvalidOperationException">The state is larger than the limit.</exception>
  internal string Seal(StateBinding binding, Dictionary<string, string>? state, Dictionary<string, JsonElement>? answers)
  {
    var payload = new RequestStatePayload
    {
      ExpiresAtUnixSeconds = _time.GetUtcNow().Add(_lifetime).ToUnixTimeSeconds(),
      Method = binding.Method,
      Name = binding.Name,
      Principal = binding.Principal,
      ArgumentsDigest = binding.ArgumentsDigest,
      State = state is { Count: > 0 } ? state : null,
      Answers = answers is { Count: > 0 } ? answers : null,
    };

    var json = JsonSerializer.SerializeToUtf8Bytes(payload, ModernJsonContext.Default.RequestStatePayload);
    if (json.Length > _maxBytes)
    {
      throw new InvalidOperationException("The request state is larger than the limit of " + _maxBytes + " bytes.");
    }

    byte[] token;
    if (_encrypt)
    {
      token = new byte[2 + NonceBytes + json.Length + GcmTagBytes];
      token[0] = FormatVersion;
      token[1] = ModeGcm;
      var nonce = token.AsSpan(2, NonceBytes);
      RandomNumberGenerator.Fill(nonce);
      using var gcm = new AesGcm(_encryptionKey, GcmTagBytes);
      gcm.Encrypt(nonce, json, token.AsSpan(2 + NonceBytes, json.Length), token.AsSpan(2 + NonceBytes + json.Length, GcmTagBytes), token.AsSpan(0, 2));
    }
    else
    {
      token = new byte[2 + json.Length + TagBytes];
      token[0] = FormatVersion;
      token[1] = ModeHmac;
      json.CopyTo(token.AsSpan(2));
      HMACSHA256.HashData(_macKey, token.AsSpan(0, 2 + json.Length), token.AsSpan(2 + json.Length, TagBytes));
    }

    return Base64Url.Encode(token);
  }

  /// <summary>Verifies a token against the request presenting it. Any failure (format, tag, expiry, binding) returns false; nothing throws.</summary>
  internal bool TryOpen(string token, StateBinding binding, out RequestStatePayload payload)
  {
    payload = null!;
    try
    {
      if (token.Length > (_maxBytes + 64) / 3 * 4 + 8 || !Base64Url.TryDecode(token, out var bytes) || bytes.Length < 2 || bytes[0] != FormatVersion)
      {
        return false;
      }

      byte[] json;
      switch (bytes[1])
      {
        case ModeHmac:
          if (bytes.Length < 2 + TagBytes)
          {
            return false;
          }

          var signedLength = bytes.Length - TagBytes;
          var expected = HMACSHA256.HashData(_macKey, bytes.AsSpan(0, signedLength));
          if (!CryptographicOperations.FixedTimeEquals(expected, bytes.AsSpan(signedLength)))
          {
            return false;
          }

          json = bytes[2..signedLength];
          break;
        case ModeGcm:
          if (bytes.Length < 2 + NonceBytes + GcmTagBytes)
          {
            return false;
          }

          var cipherLength = bytes.Length - 2 - NonceBytes - GcmTagBytes;
          json = new byte[cipherLength];
          using (var gcm = new AesGcm(_encryptionKey, GcmTagBytes))
          {
            gcm.Decrypt(bytes.AsSpan(2, NonceBytes), bytes.AsSpan(2 + NonceBytes, cipherLength), bytes.AsSpan(2 + NonceBytes + cipherLength, GcmTagBytes), json, bytes.AsSpan(0, 2));
          }

          break;
        default:
          return false;
      }

      var opened = JsonSerializer.Deserialize(json, ModernJsonContext.Default.RequestStatePayload);
      if (opened is null || opened.ExpiresAtUnixSeconds < _time.GetUtcNow().ToUnixTimeSeconds())
      {
        return false;
      }

      // The tag proved we wrote it; the binding proves it was written for this caller and this request.
      if (!string.Equals(opened.Method, binding.Method, StringComparison.Ordinal)
        || !string.Equals(opened.Name, binding.Name, StringComparison.Ordinal)
        || !string.Equals(opened.Principal, binding.Principal, StringComparison.Ordinal)
        || !string.Equals(opened.ArgumentsDigest, binding.ArgumentsDigest, StringComparison.Ordinal))
      {
        return false;
      }

      payload = opened;
      return true;
    }
    catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or ArgumentException)
    {
      return false;
    }
  }
}

/// <summary>Base64url without padding (RFC 4648 §5).</summary>
internal static class Base64Url
{
  internal static string Encode(ReadOnlySpan<byte> bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);

  internal static bool TryDecode(string text, out byte[] bytes)
  {
    try
    {
      bytes = System.Buffers.Text.Base64Url.DecodeFromChars(text);
      return true;
    }
    catch (FormatException)
    {
      bytes = [];
      return false;
    }
  }
}
