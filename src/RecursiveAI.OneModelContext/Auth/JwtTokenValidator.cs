using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Jose;

namespace RecursiveAI.OneModelContext.Auth;

/// <summary>
/// Validates JWT access tokens (RFC 9068 shape) against an issuer's key set: signature, <c>iss</c>, <c>aud</c> (the resource, RFC 8707),
/// <c>exp</c>, <c>nbf</c> and <c>iat</c>. RS256/384/512, PS256/384/512 and ES256/384/512 are verified with the BCL; every other
/// algorithm, <c>none</c> and the HMAC family included, is refused, so a token cannot be checked against the public key as if it were a
/// shared secret.
/// </summary>
/// <remarks>
/// An issuer that signs only with EdDSA (RFC 8037) needs the <c>RecursiveAI.OneModelContext.OneCrypto</c> package; such a token gets a
/// failure that says so. Failure descriptions never contain the token.
/// </remarks>
public sealed class JwtTokenValidator : ITokenValidator
{
  private readonly string _issuer;
  private readonly IJwksProvider _keys;
  private readonly TimeProvider _time;

  /// <summary>Creates a validator.</summary>
  /// <param name="issuer">The <c>iss</c> every token must carry, compared exactly (no normalisation).</param>
  /// <param name="keys">The issuer's keys, for example an <see cref="HttpJwksProvider"/>.</param>
  /// <param name="timeProvider">The clock; null for the system clock.</param>
  public JwtTokenValidator(string issuer, IJwksProvider keys, TimeProvider? timeProvider = null)
  {
    ArgumentException.ThrowIfNullOrEmpty(issuer);
    ArgumentNullException.ThrowIfNull(keys);
    _issuer = issuer;
    _keys = keys;
    _time = timeProvider ?? TimeProvider.System;
  }

  /// <summary>How far the clocks of this server and the issuer may differ when checking <c>exp</c>, <c>nbf</c> and <c>iat</c>. Default 60 seconds; at most 5 minutes.</summary>
  public TimeSpan ClockSkew { get; init; } = TimeSpan.FromSeconds(60);

  /// <summary>Longest token accepted, in characters. A longer one is refused before it is parsed. Default 8192.</summary>
  public int MaxTokenLength { get; init; } = 8192;

  /// <summary>Whether a token with no <c>exp</c> is refused. Default true; a token that never expires is rarely what a resource server wants.</summary>
  public bool RequireExpiration { get; init; } = true;

  /// <summary>
  /// Whether the JOSE header <c>typ</c> must be <c>at+jwt</c> (or <c>application/at+jwt</c>), as RFC 9068 section 4 requires of JWT access
  /// tokens; it keeps an ID token or another JWT of the same issuer from being replayed as an access token. Default false, because
  /// issuers that predate RFC 9068 omit it; turn it on for an issuer you know sets it.
  /// </summary>
  public bool RequireAccessTokenType { get; init; }

  /// <inheritdoc />
  public async ValueTask<TokenValidationResult> ValidateAsync(string accessToken, TokenValidationRequest request, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(accessToken);
    var token = JsonWebToken.Parse(accessToken, MaxTokenLength, out var parseError);
    if (token is null)
    {
      return TokenValidationResult.Failure(parseError == JwtParseError.UnsupportedCritical ? "The token uses critical header extensions that are not supported." : "The token is not a valid JWT.");
    }

    var keys = await _keys.GetKeysAsync(false, cancellationToken).ConfigureAwait(false);
    if (keys is null)
    {
      return TokenValidationResult.TemporarilyUnavailable("The token issuer's signing keys are not available.");
    }

    if (!token.IsSupportedAlgorithm)
    {
      // RFC 8725 section 3.1: the verifier, not the token, decides which algorithms are acceptable.
      return token.Algorithm == "EdDSA"
        ? TokenValidationResult.Failure(EdDsaMessage)
        : TokenValidationResult.Failure("The token's signing algorithm is not supported.");
    }

    var candidates = Candidates(keys, token);
    if (candidates.Count == 0 && token.KeyId is not null)
    {
      // An unknown kid may mean the issuer rotated its keys: refetch once (the provider rate-limits), then give up.
      keys = await _keys.GetKeysAsync(true, cancellationToken).ConfigureAwait(false) ?? keys;
      candidates = Candidates(keys, token);
    }

    if (candidates.Count == 0)
    {
      return keys.OnlyEdwardsKeys
        ? TokenValidationResult.Failure(EdDsaMessage)
        : TokenValidationResult.Failure("The token was not signed by a known key.");
    }

    var verified = false;
    foreach (var key in candidates)
    {
      if (token.VerifySignature(key))
      {
        verified = true;
        break;
      }
    }

    if (!verified)
    {
      return TokenValidationResult.Failure("The token's signature is not valid.");
    }

    // RFC 9068 section 4: the typ header is at+jwt, with or without the application/ prefix, compared case-insensitively.
    if (RequireAccessTokenType)
    {
      var type = token.Type is { } t && t.StartsWith("application/", StringComparison.OrdinalIgnoreCase) ? t[12..] : token.Type;
      if (!string.Equals(type, "at+jwt", StringComparison.OrdinalIgnoreCase))
      {
        return TokenValidationResult.Failure("The token is not an access token.");
      }
    }

    return CheckClaims(token.Payload, request);
  }

  private const string EdDsaMessage = "The issuer signs with EdDSA, which needs the RecursiveAI.OneModelContext.OneCrypto package.";

  private static List<JsonWebKey> Candidates(JsonWebKeySet keys, JsonWebToken token)
  {
    var list = new List<JsonWebKey>();
    foreach (var key in keys.Keys)
    {
      if (key.IsEdwards || !key.IsSigningKey)
      {
        continue;
      }

      // With a kid the key must be that one; without, any key of the right type is tried (at most a few).
      if (token.KeyId is not null ? string.Equals(key.KeyId, token.KeyId, StringComparison.Ordinal) : list.Count < 8)
      {
        list.Add(key);
      }
    }

    return list;
  }

  private TokenValidationResult CheckClaims(JsonElement claims, TokenValidationRequest request)
  {
    var skew = ClockSkew > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : ClockSkew;
    var now = _time.GetUtcNow();

    // RFC 8725 section 3.8 / RFC 9068 section 4: iss must match the expected issuer exactly.
    if (!claims.TryGetProperty("iss", out var iss) || iss.ValueKind != JsonValueKind.String || !string.Equals(iss.GetString(), _issuer, StringComparison.Ordinal))
    {
      return TokenValidationResult.Failure("The token was not issued by the expected issuer.");
    }

    // RFC 8707 section 2 and the MCP authorization page: the token must have been issued for this server.
    if (!claims.TryGetProperty("aud", out var aud) || !ResourceIdentifier.AudienceMatches(aud, request.Resource))
    {
      return TokenValidationResult.Failure("The token was not issued for this resource.");
    }

    DateTimeOffset? expiresAt = null;
    if (claims.TryGetProperty("exp", out var exp))
    {
      if (!TryDate(exp, out var expiry))
      {
        return TokenValidationResult.Failure("The token's expiry is not valid.");
      }

      if (expiry + skew <= now)
      {
        return TokenValidationResult.Failure("The token has expired.");
      }

      expiresAt = expiry;
    }
    else if (RequireExpiration)
    {
      return TokenValidationResult.Failure("The token has no expiry.");
    }

    if (claims.TryGetProperty("nbf", out var nbf) && (!TryDate(nbf, out var notBefore) || notBefore - skew > now))
    {
      return TokenValidationResult.Failure("The token is not valid yet.");
    }

    if (claims.TryGetProperty("iat", out var iat) && (!TryDate(iat, out var issuedAt) || issuedAt - skew > now))
    {
      return TokenValidationResult.Failure("The token was issued in the future.");
    }

    var scopes = new List<string>();
    AddScopes(claims, "scope", scopes);
    AddScopes(claims, "scp", scopes);

    var identity = new ClaimsIdentity("Bearer", AuthClaimTypes.Subject, null);
    var subject = Str(claims, "sub");
    if (subject is not null)
    {
      identity.AddClaim(new Claim(AuthClaimTypes.Subject, subject));
    }

    identity.AddClaim(new Claim(AuthClaimTypes.Issuer, _issuer));
    if ((Str(claims, "client_id") ?? Str(claims, "azp")) is { } client)
    {
      identity.AddClaim(new Claim(AuthClaimTypes.ClientId, client));
    }

    foreach (var scope in scopes)
    {
      identity.AddClaim(new Claim(AuthClaimTypes.Scope, scope));
    }

    return TokenValidationResult.Success(new ClaimsPrincipal(identity), scopes, expiresAt);
  }

  private static string? Str(JsonElement claims, string name) =>
    claims.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

  // RFC 8693 / RFC 9068 section 2.2.3: "scope" is a space-delimited string; some issuers send "scp" as a string or an array.
  private static void AddScopes(JsonElement claims, string name, List<string> into)
  {
    if (!claims.TryGetProperty(name, out var value))
    {
      return;
    }

    if (value.ValueKind == JsonValueKind.String)
    {
      into.AddRange(value.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
    else if (value.ValueKind == JsonValueKind.Array)
    {
      foreach (var item in value.EnumerateArray())
      {
        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } scope && into.Count < 256)
        {
          into.Add(scope);
        }
      }
    }
  }

  // NumericDate (RFC 7519 section 2): seconds since the epoch, possibly fractional.
  private static bool TryDate(JsonElement value, out DateTimeOffset date)
  {
    date = default;
    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var seconds) || double.IsNaN(seconds) || seconds < 0 || seconds > 253_402_300_799)
    {
      return false;
    }

    date = DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
    return true;
  }
}
