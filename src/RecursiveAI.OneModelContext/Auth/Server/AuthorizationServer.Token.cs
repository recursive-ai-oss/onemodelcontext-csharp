using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.Jose;

namespace RecursiveAI.OneModelContext.Auth.Server;

public sealed partial class AuthorizationServer
{
  private static readonly TimeSpan s_clockSkew = TimeSpan.FromSeconds(60);

  private sealed record ClientAuthentication(RegisteredClient? Client, bool Confidential, string? Error, string? Description, bool UsedBasic)
  {
    internal static ClientAuthentication Invalid(string description, bool basic = false) => new(null, false, "invalid_client", description, basic);

    internal static ClientAuthentication BadRequest(string description) => new(null, false, "invalid_request", description, false);
  }

  private async Task TokenAsync(IHttpExchange exchange, CancellationToken cancellationToken)
  {
    if (exchange.Request.Method != "POST")
    {
      exchange.Response.StatusCode = 405;
      exchange.Response.SetHeader("Allow", "POST");
      return;
    }

    var form = await ReadFormAsync(exchange, cancellationToken).ConfigureAwait(false);
    if (form is null)
    {
      await WriteOAuthErrorAsync(exchange, 400, "invalid_request", "The request must be application/x-www-form-urlencoded and small.", null, cancellationToken).ConfigureAwait(false);
      return;
    }

    if (form.HasDuplicates)
    {
      await WriteOAuthErrorAsync(exchange, 400, "invalid_request", "A parameter was sent more than once.", null, cancellationToken).ConfigureAwait(false);
      return;
    }

    var now = _time.GetUtcNow();
    await MaybePurgeAsync(now, cancellationToken).ConfigureAwait(false);
    var auth = await AuthenticateClientAsync(exchange, form, now, cancellationToken).ConfigureAwait(false);
    if (auth.Client is null)
    {
      await WriteClientErrorAsync(exchange, auth, cancellationToken).ConfigureAwait(false);
      return;
    }

    var client = auth.Client;
    var grantType = form.Get("grant_type");
    if (grantType is null || !client.GrantTypes.Contains(grantType, StringComparer.Ordinal))
    {
      // RFC 6749 section 5.2: unsupported_grant_type for an unknown type, unauthorized_client when this client may not use a known one.
      var known = grantType is "authorization_code" or "refresh_token" or "client_credentials" || (grantType == JwtBearerGrant && _options.EnterpriseAuthorization is not null);
      await WriteOAuthErrorAsync(exchange, 400, known ? "unauthorized_client" : "unsupported_grant_type", known ? "This client may not use that grant type." : "That grant type is not supported.", null, cancellationToken).ConfigureAwait(false);
      return;
    }

    switch (grantType)
    {
      case "authorization_code":
        await CodeGrantAsync(exchange, form, client, now, cancellationToken).ConfigureAwait(false);
        break;
      case "refresh_token":
        await RefreshGrantAsync(exchange, form, client, now, cancellationToken).ConfigureAwait(false);
        break;
      case "client_credentials":
        await ClientCredentialsGrantAsync(exchange, form, client, auth.Confidential, now, cancellationToken).ConfigureAwait(false);
        break;
      case JwtBearerGrant when _options.EnterpriseAuthorization is { } enterprise:
        await JwtBearerGrantAsync(exchange, form, client, auth.Confidential, enterprise, now, cancellationToken).ConfigureAwait(false);
        break;
      default:
        await WriteOAuthErrorAsync(exchange, 400, "unsupported_grant_type", "That grant type is not supported.", null, cancellationToken).ConfigureAwait(false);
        break;
    }
  }

  private static Task WriteClientErrorAsync(IHttpExchange exchange, ClientAuthentication auth, CancellationToken cancellationToken) =>
    WriteOAuthErrorAsync(
      exchange,
      auth.Error == "invalid_client" ? 401 : 400,
      auth.Error!,
      auth.Description!,
      auth.Error == "invalid_client" && auth.UsedBasic ? "Basic realm=\"oauth\"" : null,
      cancellationToken);

  // ------------------------------------------------------------------------------------------------ client authentication

  /// <summary>
  /// Identifies the client and checks the credentials it presented against how it registered (OAuth 2.1 section 2.4; RFC 7523 section 2.2
  /// for private_key_jwt). A secret is accepted only from a client registered to use one; more than one method in a request is refused.
  /// </summary>
  private async Task<ClientAuthentication> AuthenticateClientAsync(IHttpExchange exchange, Parameters form, DateTimeOffset now, CancellationToken cancellationToken)
  {
    string? clientId = form.Get("client_id");
    string? secret = null;
    var basic = false;
    var header = exchange.Request.Headers.GetFirst("Authorization");
    if (header is not null)
    {
      if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
      {
        return ClientAuthentication.Invalid("Unsupported client authentication.");
      }

      basic = true;
      try
      {
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
        var colon = decoded.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
          return ClientAuthentication.Invalid("The client credentials are malformed.", basic: true);
        }

        // RFC 6749 section 2.3.1: both parts are form-urlencoded before being joined.
        var headerId = Parameters.Decode(decoded[..colon]);
        if (clientId is not null && clientId != headerId)
        {
          return ClientAuthentication.BadRequest("The client_id does not match the credentials.");
        }

        clientId = headerId;
        secret = Parameters.Decode(decoded[(colon + 1)..]);
      }
      catch (FormatException)
      {
        return ClientAuthentication.Invalid("The client credentials are malformed.", basic: true);
      }
    }

    var bodySecret = form.Get("client_secret");
    var assertion = form.Get("client_assertion");
    if ((basic ? 1 : 0) + (bodySecret is not null ? 1 : 0) + (assertion is not null ? 1 : 0) > 1)
    {
      return ClientAuthentication.BadRequest("Use one client authentication method.");
    }

    secret ??= bodySecret;
    if (assertion is not null)
    {
      if (form.Get("client_assertion_type") != ClientAssertionType)
      {
        return ClientAuthentication.Invalid("The client assertion type is not supported.");
      }

      // The assertion names the client in iss and sub; client_id is optional beside it (RFC 7523 section 3).
      clientId ??= PeekAssertionSubject(assertion);
    }

    if (string.IsNullOrEmpty(clientId))
    {
      return ClientAuthentication.Invalid("The client is not identified.", basic);
    }

    var client = await FindClientAsync(clientId, cancellationToken).ConfigureAwait(false);
    if (client is null)
    {
      if (secret is not null)
      {
        _ = ClientSecret.Verify(secret, null);
      }

      SecurityEvent("client authentication failed: unknown client");
      return ClientAuthentication.Invalid("Client authentication failed.", basic);
    }

    var method = client.TokenEndpointAuthMethod;
    if (assertion is not null)
    {
      if (method != "private_key_jwt" || !await VerifyClientAssertionAsync(client, assertion, now, cancellationToken).ConfigureAwait(false))
      {
        SecurityEvent("client authentication failed: assertion");
        return ClientAuthentication.Invalid("Client authentication failed.");
      }

      return new ClientAuthentication(client, true, null, null, false);
    }

    if (secret is not null)
    {
      // Both secret-based methods are accepted for a client that has a secret; a public or key-based client never authenticates with one.
      if (method is not ("client_secret_basic" or "client_secret_post") || !ClientSecret.Verify(secret, client.SecretHash))
      {
        SecurityEvent("client authentication failed: secret");
        return ClientAuthentication.Invalid("Client authentication failed.", basic);
      }

      return new ClientAuthentication(client, true, null, null, false);
    }

    if (method != "none")
    {
      SecurityEvent("client authentication failed: credentials missing");
      return ClientAuthentication.Invalid("This client must authenticate.", basic);
    }

    return new ClientAuthentication(client, false, null, null, false);
  }

  private static string? PeekAssertionSubject(string assertion)
  {
    var token = JsonWebToken.Parse(assertion, 8192, out _);
    return token is not null && token.Payload.TryGetProperty("sub", out var sub) && sub.ValueKind == JsonValueKind.String ? sub.GetString() : null;
  }

  /// <summary>
  /// RFC 7523 section 3 and OpenID Connect Core section 9: <c>iss</c> and <c>sub</c> are the client, <c>aud</c> is this server's issuer or
  /// token endpoint, the assertion has not expired, and its <c>jti</c> was never seen. Only RS, PS and ES algorithms verify.
  /// </summary>
  private async Task<bool> VerifyClientAssertionAsync(RegisteredClient client, string assertion, DateTimeOffset now, CancellationToken cancellationToken)
  {
    var token = JsonWebToken.Parse(assertion, 8192, out _);
    if (token is null || !token.IsSupportedAlgorithm)
    {
      return false;
    }

    JsonWebKeySet? keys = null;
    if (client.JwksJson is { } json)
    {
      keys = JsonWebKeySet.Parse(Encoding.UTF8.GetBytes(json));
    }
    else if (client.JwksUri is { } uri)
    {
      keys = await _clients.GetKeysAsync(uri, client.IsMetadataDocument, cancellationToken).ConfigureAwait(false);
    }

    if (keys is null || !VerifyWithAny(keys, token))
    {
      return false;
    }

    var claims = token.Payload;
    if (!StringClaim(claims, "iss", client.ClientId) || !StringClaim(claims, "sub", client.ClientId))
    {
      return false;
    }

    if (!AudienceIs(claims, _issuer) && !AudienceIs(claims, _issuer + _options.TokenPath))
    {
      return false;
    }

    if (!TryDate(claims, "exp", out var exp) || exp + s_clockSkew <= now || exp > now + TimeSpan.FromMinutes(10) + s_clockSkew)
    {
      return false;
    }

    if (claims.TryGetProperty("nbf", out _) && (!TryDate(claims, "nbf", out var nbf) || nbf - s_clockSkew > now))
    {
      return false;
    }

    if (!claims.TryGetProperty("jti", out var jti) || jti.ValueKind != JsonValueKind.String || jti.GetString() is not { Length: > 0 and <= 256 } id)
    {
      return false;
    }

    // RFC 7523 section 3 item 7: an assertion is used once.
    return await _store.TryRecordAssertionAsync("client:" + client.ClientId + "\n" + id, exp + s_clockSkew, now, cancellationToken).ConfigureAwait(false);
  }

  private static bool VerifyWithAny(JsonWebKeySet keys, JsonWebToken token)
  {
    var tried = 0;
    foreach (var key in keys.Keys)
    {
      if (key.IsEdwards || !key.IsSigningKey || (token.KeyId is not null && key.KeyId != token.KeyId))
      {
        continue;
      }

      if (++tried > 8)
      {
        return false;
      }

      if (token.VerifySignature(key))
      {
        return true;
      }
    }

    return false;
  }

  private static bool StringClaim(JsonElement claims, string name, string expected) =>
    claims.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), expected, StringComparison.Ordinal);

  // aud is a string or an array of strings (RFC 7519 section 4.1.3).
  private static bool AudienceIs(JsonElement claims, string expected)
  {
    if (!claims.TryGetProperty("aud", out var aud))
    {
      return false;
    }

    if (aud.ValueKind == JsonValueKind.String)
    {
      return string.Equals(aud.GetString(), expected, StringComparison.Ordinal);
    }

    return aud.ValueKind == JsonValueKind.Array && aud.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.String && string.Equals(a.GetString(), expected, StringComparison.Ordinal));
  }

  private static bool TryDate(JsonElement claims, string name, out DateTimeOffset date)
  {
    date = default;
    if (!claims.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var seconds) || seconds < 0 || seconds > 253_402_300_799)
    {
      return false;
    }

    date = DateTimeOffset.FromUnixTimeSeconds(seconds);
    return true;
  }

  // ------------------------------------------------------------------------------------------------ authorization_code

  private async Task CodeGrantAsync(IHttpExchange exchange, Parameters form, RegisteredClient client, DateTimeOffset now, CancellationToken cancellationToken)
  {
    var code = form.Get("code");
    var verifier = form.Get("code_verifier");
    if (string.IsNullOrEmpty(code) || verifier is null || code.Length > 256)
    {
      await WriteOAuthErrorAsync(exchange, 400, "invalid_request", "The code and code_verifier are required.", null, cancellationToken).ConfigureAwait(false);
      return;
    }

    var redemption = await _store.RedeemCodeAsync(Hash(code), now, cancellationToken).ConfigureAwait(false);
    var grant = redemption.Grant;
    if (redemption.Status == RedemptionStatus.AlreadyRedeemed && grant is not null)
    {
      // OAuth 2.1 section 4.1.3: a code used twice means it leaked; what it produced is revoked.
      await RevokeFamilyAsync(grant.FamilyId, cancellationToken).ConfigureAwait(false);
      SecurityEvent("authorization code replayed; its token family was revoked");
    }

    if (redemption.Status != RedemptionStatus.Redeemed || grant is null)
    {
      await WriteOAuthErrorAsync(exchange, 400, "invalid_grant", "The code is not valid.", null, cancellationToken).ConfigureAwait(false);
      return;
    }

    var redirect = form.Get("redirect_uri");
    if (!string.Equals(grant.ClientId, client.ClientId, StringComparison.Ordinal)
      || (grant.RedirectUriSent && !string.Equals(redirect, grant.RedirectUri, StringComparison.Ordinal))
      || (redirect is not null && !string.Equals(redirect, grant.RedirectUri, StringComparison.Ordinal)))
    {
      await WriteOAuthErrorAsync(exchange, 400, "invalid_grant", "The code is not valid for this client or redirect URI.", null, cancellationToken).ConfigureAwait(false);
      return;
    }

    // RFC 7636 section 4.6: S256 compares BASE64URL(SHA256(verifier)) with the challenge, here in constant time.
    if (!IsValidPkceValue(verifier)
      || !CryptographicOperations.FixedTimeEquals(
        Encoding.ASCII.GetBytes(System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))),
        Encoding.ASCII.GetBytes(grant.CodeChallenge)))
    {
      SecurityEvent("code verifier did not match");
      await WriteOAuthErrorAsync(exchange, 400, "invalid_grant", "The code verifier does not match.", null, cancellationToken).ConfigureAwait(false);
      return;
    }

    if (form.Has("resource") && ResourceIdentifier.Canonicalize(form.Get("resource")) != grant.Resource)
    {
      await WriteOAuthErrorAsync(exchange, 400, "invalid_target", "The resource differs from the authorization request.", null, cancellationToken).ConfigureAwait(false);
      return;
    }

    var accessToken = IssueAccessToken(grant.Subject, client.ClientId, grant.Scope, grant.Resource, grant.FamilyId, grant.Claims, now);
    string? refresh = null;
    if (_options.IssueRefreshTokens && client.GrantTypes.Contains("refresh_token", StringComparer.Ordinal))
    {
      refresh = await NewRefreshTokenAsync(grant.FamilyId, client.ClientId, grant.Subject, grant.Scope, grant.Resource, grant.Claims, now, now + _options.MaxRefreshFamilyLifetime, cancellationToken).ConfigureAwait(false);
    }

    await WriteTokenResponseAsync(exchange, accessToken, grant.Scope, refresh, null, cancellationToken).ConfigureAwait(false);
  }

  private async Task<string> NewRefreshTokenAsync(string familyId, string clientId, string subject, string scope, string resource, IReadOnlyDictionary<string, string> claims, DateTimeOffset now, DateTimeOffset familyExpiresAt, CancellationToken cancellationToken)
  {
    var token = RandomToken();
    var expires = now + _options.RefreshTokenLifetime;
    await _store.SaveRefreshTokenAsync(new RefreshTokenGrant
    {
      TokenHash = Hash(token),
      FamilyId = familyId,
      ClientId = clientId,
      Subject = subject,
      Scope = scope,
      Resource = resource,
      Claims = claims,
      ExpiresAt = expires < familyExpiresAt ? expires : familyExpiresAt,
      FamilyExpiresAt = familyExpiresAt,
    }, cancellationToken).ConfigureAwait(false);
    return token;
  }

  // ------------------------------------------------------------------------------------------------ refresh_token

  private async Task RefreshGrantAsync(IHttpExchange exchange, Parameters form, RegisteredClient client, DateTimeOffset now, CancellationToken cancellationToken)
  {
    var presented = form.Get("refresh_token");
    if (string.IsNullOrEmpty(presented) || presented.Length > 256)
    {
      await WriteOAuthErrorAsync(exchange, 400, "invalid_request", "The refresh_token is required.", null, cancellationToken).ConfigureAwait(false);
      return;
    }

    var hash = Hash(presented);

    // Look before using: a token presented by the wrong client must not be spent, and a revoked family must not be refreshed.
    var existing = await _store.FindRefreshTokenAsync(hash, cancellationToken).ConfigureAwait(false);
    if (existing is null
      || !string.Equals(existing.ClientId, client.ClientId, StringComparison.Ordinal)
      || await _store.IsRevokedAsync("family:" + existing.FamilyId, cancellationToken).ConfigureAwait(false))
    {
      await WriteOAuthErrorAsync(exchange, 400, "invalid_grant", "The refresh token is not valid.", null, cancellationToken).ConfigureAwait(false);
      return;
    }

    // RFC 6749 section 6: a scope in the request may only narrow the grant.
    var scope = existing.Scope;
    if (form.Has("scope"))
    {
      var narrowed = SplitScopes(form.Get("scope"));
      var original = SplitScopes(existing.Scope);
      if (narrowed.Any(s => !original.Contains(s, StringComparer.Ordinal)))
      {
        await WriteOAuthErrorAsync(exchange, 400, "invalid_scope", "A refresh cannot widen the granted scope.", null, cancellationToken).ConfigureAwait(false);
        return;
      }

      scope = string.Join(' ', narrowed);
    }

    if (form.Has("resource") && ResourceIdentifier.Canonicalize(form.Get("resource")) != existing.Resource)
    {
      await WriteOAuthErrorAsync(exchange, 400, "invalid_target", "The resource differs from the original grant.", null, cancellationToken).ConfigureAwait(false);
      return;
    }

    var redemption = await _store.RedeemRefreshTokenAsync(hash, now, cancellationToken).ConfigureAwait(false);
    if (redemption.Status == RedemptionStatus.AlreadyRedeemed)
    {
      // OAuth 2.1 section 4.3.1: a spent refresh token shown again means two parties hold it; the whole family is revoked.
      await RevokeFamilyAsync(existing.FamilyId, cancellationToken).ConfigureAwait(false);
      SecurityEvent("refresh token reused; its token family was revoked");
    }

    if (redemption.Status != RedemptionStatus.Redeemed || redemption.Grant is not { } token)
    {
      await WriteOAuthErrorAsync(exchange, 400, "invalid_grant", "The refresh token is not valid.", null, cancellationToken).ConfigureAwait(false);
      return;
    }

    // Rotation: every refresh yields a new refresh token in the same family; the old one is spent.
    var next = await NewRefreshTokenAsync(token.FamilyId, token.ClientId, token.Subject, token.Scope, token.Resource, token.Claims, now, token.FamilyExpiresAt, cancellationToken).ConfigureAwait(false);
    var access = IssueAccessToken(token.Subject, token.ClientId, scope, token.Resource, token.FamilyId, token.Claims, now);
    await WriteTokenResponseAsync(exchange, access, scope, next, null, cancellationToken).ConfigureAwait(false);
  }

  // ------------------------------------------------------------------------------------------------ client_credentials

  private async Task ClientCredentialsGrantAsync(IHttpExchange exchange, Parameters form, RegisteredClient client, bool confidential, DateTimeOffset now, CancellationToken cancellationToken)
  {
    // The Client Credentials extension is for pre-registered, authenticated clients; a metadata document never earns it.
    if (!confidential || client.IsMetadataDocument)
    {
      await WriteOAuthErrorAsync(exchange, 400, "unauthorized_client", "Client credentials need a registered, authenticated client.", null, cancellationToken).ConfigureAwait(false);
      return;
    }

    var resource = ChooseResource(form.Get("resource"), false, out var resourceError);
    if (resource is null)
    {
      await WriteOAuthErrorAsync(exchange, 400, "invalid_target", resourceError, null, cancellationToken).ConfigureAwait(false);
      return;
    }

    var asked = SplitScopes(form.Get("scope"));
    var scopes = AllowedScopes(asked.Count > 0 ? asked : _options.DefaultScopes, client);
    if (asked.Count > 0 && scopes.Count == 0)
    {
      await WriteOAuthErrorAsync(exchange, 400, "invalid_scope", "None of the requested scopes is available.", null, cancellationToken).ConfigureAwait(false);
      return;
    }

    var scope = string.Join(' ', scopes);

    // RFC 6749 section 4.4.3: no refresh token for this grant; the client simply asks again.
    var access = IssueAccessToken(client.ClientId, client.ClientId, scope, resource, null, new Dictionary<string, string>(), now);
    await WriteTokenResponseAsync(exchange, access, scope, null, null, cancellationToken).ConfigureAwait(false);
  }

  // ------------------------------------------------------------------------------------------------ jwt-bearer (ID-JAG)

  private async Task JwtBearerGrantAsync(IHttpExchange exchange, Parameters form, RegisteredClient client, bool confidential, EnterpriseAuthorizationOptions enterprise, DateTimeOffset now, CancellationToken cancellationToken)
  {
    Task Invalid(string description) => WriteOAuthErrorAsync(exchange, 400, "invalid_grant", description, null, cancellationToken);

    if (enterprise.RequireConfidentialClient && !confidential)
    {
      await WriteOAuthErrorAsync(exchange, 400, "unauthorized_client", "This grant needs an authenticated client.", null, cancellationToken).ConfigureAwait(false);
      return;
    }

    var assertion = form.Get("assertion");
    if (string.IsNullOrEmpty(assertion))
    {
      await WriteOAuthErrorAsync(exchange, 400, "invalid_request", "The assertion is required.", null, cancellationToken).ConfigureAwait(false);
      return;
    }

    var token = JsonWebToken.Parse(assertion, 8192, out _);
    if (token is null)
    {
      await Invalid("The assertion is not valid.").ConfigureAwait(false);
      return;
    }

    // draft-ietf-oauth-identity-assertion-authz-grant section 4.4.1: the typ is oauth-id-jag+jwt (RFC 8725 section 3.11).
    var type = token.Type?.StartsWith("application/", StringComparison.OrdinalIgnoreCase) == true ? token.Type[12..] : token.Type;
    if (!string.Equals(type, "oauth-id-jag+jwt", StringComparison.OrdinalIgnoreCase) || !token.IsSupportedAlgorithm)
    {
      await Invalid("The assertion is not an identity assertion grant.").ConfigureAwait(false);
      return;
    }

    var claims = token.Payload;
    var issuerName = claims.TryGetProperty("iss", out var iss) && iss.ValueKind == JsonValueKind.String ? iss.GetString() : null;
    var idp = enterprise.IdentityProviders.FirstOrDefault(p => string.Equals(p.Issuer, issuerName, StringComparison.Ordinal));
    if (idp is null)
    {
      SecurityEvent("identity assertion from an untrusted issuer");
      await Invalid("The assertion's issuer is not trusted.").ConfigureAwait(false);
      return;
    }

    var keys = await idp.Keys.GetKeysAsync(false, cancellationToken).ConfigureAwait(false);
    var verified = keys is not null && VerifyWithAny(keys, token);
    if (!verified && token.KeyId is not null)
    {
      // An unknown kid may mean the IdP rotated its keys; the provider limits how often it refetches.
      keys = await idp.Keys.GetKeysAsync(true, cancellationToken).ConfigureAwait(false);
      verified = keys is not null && VerifyWithAny(keys, token);
    }

    if (!verified)
    {
      SecurityEvent("identity assertion signature rejected");
      await Invalid("The assertion's signature is not valid.").ConfigureAwait(false);
      return;
    }

    // aud: the issuer of this server, alone (a string or an array with exactly that one element).
    var audOk = claims.TryGetProperty("aud", out var aud)
      && ((aud.ValueKind == JsonValueKind.String && aud.GetString() == _issuer)
        || (aud.ValueKind == JsonValueKind.Array && aud.GetArrayLength() == 1 && aud[0].ValueKind == JsonValueKind.String && aud[0].GetString() == _issuer));
    if (!audOk)
    {
      await Invalid("The assertion was issued for a different authorization server.").ConfigureAwait(false);
      return;
    }

    if (!StringClaim(claims, "client_id", client.ClientId))
    {
      await Invalid("The assertion was issued for a different client.").ConfigureAwait(false);
      return;
    }

    if (!TryDate(claims, "exp", out var exp) || !TryDate(claims, "iat", out var iat)
      || exp + s_clockSkew <= now || iat - s_clockSkew > now || exp - iat > enterprise.MaxAssertionLifetime
      || (claims.TryGetProperty("nbf", out _) && (!TryDate(claims, "nbf", out var nbf) || nbf - s_clockSkew > now)))
    {
      await Invalid("The assertion has expired or is not valid yet.").ConfigureAwait(false);
      return;
    }

    if (!claims.TryGetProperty("sub", out var sub) || sub.ValueKind != JsonValueKind.String || sub.GetString() is not { Length: > 0 and <= 256 } subject
      || !claims.TryGetProperty("jti", out var jtiElement) || jtiElement.ValueKind != JsonValueKind.String || jtiElement.GetString() is not { Length: > 0 and <= 256 } jti)
    {
      await Invalid("The assertion lacks a subject or identifier.").ConfigureAwait(false);
      return;
    }

    // Resource: the request may name one, within what the assertion allows and what this server serves (RFC 8707; the draft section 4.4.1).
    var fromAssertion = ReadResources(claims);
    string? resource;
    var requestedResource = form.Get("resource");
    if (requestedResource is not null)
    {
      resource = ResourceIdentifier.Canonicalize(requestedResource);
      if (resource is null || !_resources.Contains(resource) || (fromAssertion.Count > 0 && !fromAssertion.Contains(resource)))
      {
        await WriteOAuthErrorAsync(exchange, 400, "invalid_target", "The resource is not available for this assertion.", null, cancellationToken).ConfigureAwait(false);
        return;
      }
    }
    else if (fromAssertion.Count > 0)
    {
      var usable = fromAssertion.Where(_resources.Contains).ToList();
      if (usable.Count != 1)
      {
        await WriteOAuthErrorAsync(exchange, 400, "invalid_target", "Name the resource to use.", null, cancellationToken).ConfigureAwait(false);
        return;
      }

      resource = usable[0];
    }
    else
    {
      resource = ChooseResource(null, false, out var resourceError);
      if (resource is null)
      {
        await WriteOAuthErrorAsync(exchange, 400, "invalid_target", resourceError, null, cancellationToken).ConfigureAwait(false);
        return;
      }
    }

    // Scope: the assertion's scopes are an upper bound; the request may ask for fewer; local policy trims the rest.
    var assertionScopes = claims.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.String ? SplitScopes(sc.GetString()) : null;
    var requestedScopes = SplitScopes(form.Get("scope"));
    IEnumerable<string> candidate = requestedScopes.Count > 0 ? requestedScopes : assertionScopes ?? (IReadOnlyList<string>)_options.DefaultScopes;
    if (assertionScopes is not null)
    {
      candidate = candidate.Where(s => assertionScopes.Contains(s, StringComparer.Ordinal));
    }

    var wanted = candidate.ToList();
    var granted = AllowedScopes(wanted, client);
    if ((requestedScopes.Count > 0 || assertionScopes is { Count: > 0 }) && granted.Count == 0)
    {
      await WriteOAuthErrorAsync(exchange, 400, "invalid_scope", "None of the requested scopes is available.", null, cancellationToken).ConfigureAwait(false);
      return;
    }

    var mapped = subject;
    if (enterprise.ResolveSubject is { } resolve)
    {
      mapped = await resolve(new IdentityAssertion(idp.Issuer, subject, client.ClientId, assertionScopes ?? []), cancellationToken).ConfigureAwait(false);
      if (string.IsNullOrEmpty(mapped))
      {
        await Invalid("The user is not permitted here.").ConfigureAwait(false);
        return;
      }
    }

    // draft section 4.4.1 and RFC 7523 section 3: an assertion is accepted once. Recorded last so a request refused for its resource or
    // scope does not spend an assertion the client could still use correctly.
    if (!await _store.TryRecordAssertionAsync("idjag:" + idp.Issuer + "\n" + jti, exp + s_clockSkew, now, cancellationToken).ConfigureAwait(false))
    {
      SecurityEvent("identity assertion replayed");
      await Invalid("The assertion was already used.").ConfigureAwait(false);
      return;
    }

    // The draft section 4.4.3: no refresh token; the client presents the assertion again, or a new one.
    var scope = string.Join(' ', granted);
    var access = IssueAccessToken(mapped, client.ClientId, scope, resource, null, new Dictionary<string, string>(), now);
    await WriteTokenResponseAsync(exchange, access, scope, null, resource, cancellationToken).ConfigureAwait(false);
  }

  private static List<string> ReadResources(JsonElement claims)
  {
    var list = new List<string>();
    if (!claims.TryGetProperty("resource", out var value))
    {
      return list;
    }

    void Add(JsonElement e)
    {
      if (e.ValueKind == JsonValueKind.String && ResourceIdentifier.Canonicalize(e.GetString()) is { } canonical && list.Count < 16)
      {
        list.Add(canonical);
      }
    }

    if (value.ValueKind == JsonValueKind.Array)
    {
      foreach (var item in value.EnumerateArray())
      {
        Add(item);
      }
    }
    else
    {
      Add(value);
    }

    return list;
  }

  // ------------------------------------------------------------------------------------------------ revocation (RFC 7009)

  private async Task RevokeAsync(IHttpExchange exchange, CancellationToken cancellationToken)
  {
    if (exchange.Request.Method != "POST")
    {
      exchange.Response.StatusCode = 405;
      exchange.Response.SetHeader("Allow", "POST");
      return;
    }

    var form = await ReadFormAsync(exchange, cancellationToken).ConfigureAwait(false);
    if (form is null || form.HasDuplicates)
    {
      await WriteOAuthErrorAsync(exchange, 400, "invalid_request", "The request must be a small application/x-www-form-urlencoded body without repeated parameters.", null, cancellationToken).ConfigureAwait(false);
      return;
    }

    var now = _time.GetUtcNow();
    var auth = await AuthenticateClientAsync(exchange, form, now, cancellationToken).ConfigureAwait(false);
    if (auth.Client is null)
    {
      await WriteClientErrorAsync(exchange, auth, cancellationToken).ConfigureAwait(false);
      return;
    }

    var presented = form.Get("token");
    if (string.IsNullOrEmpty(presented) || presented.Length > 8192)
    {
      await WriteOAuthErrorAsync(exchange, 400, "invalid_request", "The token is required.", null, cancellationToken).ConfigureAwait(false);
      return;
    }

    // Both kinds are tried whatever the hint says; a token that is unknown, expired or someone else's is a quiet success (section 2.2).
    var refresh = await _store.FindRefreshTokenAsync(Hash(presented), cancellationToken).ConfigureAwait(false);
    if (refresh is not null)
    {
      if (refresh.ClientId == auth.Client.ClientId)
      {
        await RevokeFamilyAsync(refresh.FamilyId, cancellationToken).ConfigureAwait(false);
      }
    }
    else
    {
      var jwt = JsonWebToken.Parse(presented, 8192, out _);
      if (jwt is not null && jwt.IsSupportedAlgorithm
        && VerifyWithAny(new JsonWebKeySet([.. _options.SigningKeys.Select(k => k.ToPublicKey())]), jwt)
        && StringClaim(jwt.Payload, "iss", _issuer)
        && StringClaim(jwt.Payload, "client_id", auth.Client.ClientId)
        && jwt.Payload.TryGetProperty("jti", out var jti) && jti.ValueKind == JsonValueKind.String
        && TryDate(jwt.Payload, "exp", out var exp))
      {
        await _store.RevokeAsync("jti:" + jti.GetString(), exp + s_clockSkew, cancellationToken).ConfigureAwait(false);
      }
    }

    exchange.Response.StatusCode = 200;
    exchange.Response.SetHeader("Cache-Control", "no-store");
    exchange.Response.SetHeader("Content-Length", "0");
    await exchange.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
  }

  // ------------------------------------------------------------------------------------------------ registration (RFC 7591, deprecated)

  private async Task RegisterAsync(IHttpExchange exchange, CancellationToken cancellationToken)
  {
    if (exchange.Request.Method != "POST")
    {
      exchange.Response.StatusCode = 405;
      exchange.Response.SetHeader("Allow", "POST");
      return;
    }

    var contentType = exchange.Request.Headers.GetFirst("Content-Type");
    var body = contentType is not null && contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
      ? await ReadBodyAsync(exchange.Request, cancellationToken).ConfigureAwait(false)
      : null;
    Task Bad(string error, string description) => WriteOAuthErrorAsync(exchange, 400, error, description, null, cancellationToken);
    if (body is null)
    {
      await Bad("invalid_client_metadata", "Send a small application/json body.").ConfigureAwait(false);
      return;
    }

    JsonDocument doc;
    try
    {
      doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
    }
    catch (JsonException)
    {
      await Bad("invalid_client_metadata", "The body is not valid JSON.").ConfigureAwait(false);
      return;
    }

    using (doc)
    {
      var root = doc.RootElement;
      if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("redirect_uris", out var uris) || uris.ValueKind != JsonValueKind.Array
        || uris.GetArrayLength() is 0 or > 10)
      {
        await Bad("invalid_redirect_uri", "One to ten redirect_uris are required.").ConfigureAwait(false);
        return;
      }

      var applicationType = root.TryGetProperty("application_type", out var at) && at.ValueKind == JsonValueKind.String ? at.GetString() : "web";
      if (applicationType is not ("web" or "native"))
      {
        await Bad("invalid_client_metadata", "application_type must be web or native.").ConfigureAwait(false);
        return;
      }

      var redirects = new List<string>();
      foreach (var u in uris.EnumerateArray())
      {
        // OpenID Connect Registration section 2: a web client's redirect URIs are HTTPS and not on a loopback host.
        if (u.ValueKind != JsonValueKind.String || u.GetString() is not { } text || !RedirectUriRules.IsValidRegistration(text)
          || (applicationType == "web" && !text.StartsWith("https://", StringComparison.Ordinal)))
        {
          await Bad("invalid_redirect_uri", "A redirect URI is not acceptable.").ConfigureAwait(false);
          return;
        }

        redirects.Add(text);
      }

      var method = root.TryGetProperty("token_endpoint_auth_method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : "client_secret_basic";
      if (method is not ("none" or "client_secret_basic" or "client_secret_post"))
      {
        await Bad("invalid_client_metadata", "token_endpoint_auth_method is not supported for registration.").ConfigureAwait(false);
        return;
      }

      var grants = new List<string>();
      if (root.TryGetProperty("grant_types", out var g) && g.ValueKind == JsonValueKind.Array)
      {
        foreach (var item in g.EnumerateArray())
        {
          if (item.ValueKind != JsonValueKind.String || item.GetString() is not ("authorization_code" or "refresh_token") || grants.Count >= 2)
          {
            await Bad("invalid_client_metadata", "Only authorization_code and refresh_token can be registered.").ConfigureAwait(false);
            return;
          }

          grants.Add(item.GetString()!);
        }
      }

      if (grants.Count == 0)
      {
        grants = ["authorization_code", "refresh_token"];
      }

      var name = root.TryGetProperty("client_name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
      if (name is { Length: > 100 } || (name is not null && name.Any(char.IsControl)))
      {
        await Bad("invalid_client_metadata", "client_name is too long or has control characters.").ConfigureAwait(false);
        return;
      }

      var now = _time.GetUtcNow();
      var clientId = "dcr_" + System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(18));
      string? secret = null;
      if (method != "none")
      {
        secret = ClientSecret.Generate();
      }

      await _store.SaveClientAsync(new RegisteredClient
      {
        ClientId = clientId,
        Name = name,
        RedirectUris = redirects,
        TokenEndpointAuthMethod = method,
        SecretHash = secret is null ? null : ClientSecret.Hash(secret),
        GrantTypes = grants,
        CreatedAt = now,
      }, cancellationToken).ConfigureAwait(false);

      await WriteJsonAsync(exchange, 201, w =>
      {
        w.WriteStartObject();
        w.WriteString("client_id", clientId);
        if (secret is not null)
        {
          w.WriteString("client_secret", secret);
          w.WriteNumber("client_secret_expires_at", 0);
        }

        w.WriteNumber("client_id_issued_at", now.ToUnixTimeSeconds());
        w.WriteStartArray("redirect_uris");
        foreach (var r in redirects)
        {
          w.WriteStringValue(r);
        }

        w.WriteEndArray();
        w.WriteString("token_endpoint_auth_method", method);
        w.WriteStartArray("grant_types");
        foreach (var gt in grants)
        {
          w.WriteStringValue(gt);
        }

        w.WriteEndArray();
        w.WriteStartArray("response_types");
        w.WriteStringValue("code");
        w.WriteEndArray();
        w.WriteString("application_type", applicationType);
        if (name is not null)
        {
          w.WriteString("client_name", name);
        }

        w.WriteEndObject();
      }, cancellationToken).ConfigureAwait(false);
    }
  }
}
