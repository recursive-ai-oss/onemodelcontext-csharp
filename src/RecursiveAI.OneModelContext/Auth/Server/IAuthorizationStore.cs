using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.Auth.Server;

/// <summary>A client the authorization server knows: pre-registered by the host, registered dynamically, or read from its metadata document.</summary>
public sealed class RegisteredClient
{
  /// <summary>The <c>client_id</c>. For a client identified by a metadata document it is that document's HTTPS URL.</summary>
  public required string ClientId { get; init; }

  /// <summary>A name to show the user on a consent screen, or null.</summary>
  public string? Name { get; init; }

  /// <summary>The redirect URIs, compared exactly (loopback addresses may differ in port only, RFC 8252 section 7.3).</summary>
  public IReadOnlyList<string> RedirectUris { get; init; } = [];

  /// <summary>
  /// How the client authenticates at the token endpoint: <c>none</c> (a public client), <c>client_secret_basic</c>,
  /// <c>client_secret_post</c> or <c>private_key_jwt</c>. Default <c>none</c>.
  /// </summary>
  public string TokenEndpointAuthMethod { get; init; } = "none";

  /// <summary>The salted hash of the client secret (see <see cref="ClientSecret"/>); never the secret itself. Null for clients that do not use a secret.</summary>
  public string? SecretHash { get; init; }

  /// <summary>The client's public keys as a JWKS document, for <c>private_key_jwt</c>; or null.</summary>
  public string? JwksJson { get; init; }

  /// <summary>Where the client publishes its JWKS, for <c>private_key_jwt</c> (fetched under the address guard); or null.</summary>
  public string? JwksUri { get; init; }

  /// <summary>The grant types the client may use. Default <c>authorization_code</c> and <c>refresh_token</c>.</summary>
  public IReadOnlyList<string> GrantTypes { get; init; } = ["authorization_code", "refresh_token"];

  /// <summary>The scopes the client may be granted; null for any scope the server supports.</summary>
  public IReadOnlyList<string>? Scopes { get; init; }

  /// <summary>When the client was registered.</summary>
  public DateTimeOffset CreatedAt { get; init; }

  /// <summary>Whether the client was read from its metadata document rather than registered. Not stored.</summary>
  [System.Text.Json.Serialization.JsonIgnore]
  public bool IsMetadataDocument { get; init; }
}

/// <summary>An authorization code issued and not yet redeemed. The code itself is never stored, only its hash.</summary>
public sealed class AuthorizationCodeGrant
{
  /// <summary>SHA-256 of the code, base64url.</summary>
  public required string CodeHash { get; init; }

  /// <summary>The client the code was issued to.</summary>
  public required string ClientId { get; init; }

  /// <summary>The redirect URI of the authorization request (the token request must repeat it when it was sent).</summary>
  public required string RedirectUri { get; init; }

  /// <summary>Whether the authorization request carried the redirect URI. If it did, the token request must repeat it exactly (OAuth 2.1 section 4.1.3).</summary>
  public bool RedirectUriSent { get; init; }

  /// <summary>The authenticated user, as the host named them.</summary>
  public required string Subject { get; init; }

  /// <summary>The granted scopes, space-delimited.</summary>
  public string Scope { get; init; } = string.Empty;

  /// <summary>The resource the access token is for (RFC 8707); becomes the token's audience.</summary>
  public required string Resource { get; init; }

  /// <summary>The PKCE challenge (S256, RFC 7636).</summary>
  public required string CodeChallenge { get; init; }

  /// <summary>The identifier shared by every token descended from this grant; revoking it revokes them all.</summary>
  public required string FamilyId { get; init; }

  /// <summary>Extra claims the host asked for in the access token.</summary>
  public IReadOnlyDictionary<string, string> Claims { get; init; } = new Dictionary<string, string>();

  /// <summary>When the code stops being valid.</summary>
  public DateTimeOffset ExpiresAt { get; init; }

  /// <summary>
  /// When the refresh family this code starts stops being valid. A redeemed code is kept as a spent marker until then, so a replay
  /// after <see cref="ExpiresAt"/> still revokes what the code produced. Default: no later than <see cref="ExpiresAt"/>.
  /// </summary>
  public DateTimeOffset FamilyExpiresAt { get; init; }

  /// <summary>Whether the code was redeemed. A second redemption means the code leaked, and revokes the family.</summary>
  public bool Redeemed { get; set; }
}

/// <summary>A refresh token. Only the hash is stored.</summary>
public sealed class RefreshTokenGrant
{
  /// <summary>SHA-256 of the token, base64url.</summary>
  public required string TokenHash { get; init; }

  /// <summary>The token family this belongs to.</summary>
  public required string FamilyId { get; init; }

  /// <summary>The client it was issued to.</summary>
  public required string ClientId { get; init; }

  /// <summary>The authenticated user.</summary>
  public required string Subject { get; init; }

  /// <summary>The granted scopes, space-delimited.</summary>
  public string Scope { get; init; } = string.Empty;

  /// <summary>The resource (audience) of the access tokens it yields.</summary>
  public required string Resource { get; init; }

  /// <summary>Extra claims for the access tokens.</summary>
  public IReadOnlyDictionary<string, string> Claims { get; init; } = new Dictionary<string, string>();

  /// <summary>When this token stops being valid.</summary>
  public DateTimeOffset ExpiresAt { get; init; }

  /// <summary>When the whole family stops being valid, however often it is refreshed.</summary>
  public DateTimeOffset FamilyExpiresAt { get; init; }

  /// <summary>Whether the token was already exchanged for a newer one.</summary>
  public bool Used { get; set; }
}

/// <summary>The outcome of redeeming an authorization code.</summary>
public enum RedemptionStatus
{
  /// <summary>The code was valid and is now spent.</summary>
  Redeemed,

  /// <summary>No such code.</summary>
  Unknown,

  /// <summary>The code existed but its lifetime ended.</summary>
  Expired,

  /// <summary>The code was redeemed before: a replay.</summary>
  AlreadyRedeemed,
}

/// <summary>The result of <see cref="IAuthorizationStore.RedeemCodeAsync"/>.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Grant">The grant, for every status except <see cref="RedemptionStatus.Unknown"/>.</param>
public readonly record struct CodeRedemption(RedemptionStatus Status, AuthorizationCodeGrant? Grant);

/// <summary>The result of <see cref="IAuthorizationStore.RedeemRefreshTokenAsync"/>.</summary>
/// <param name="Status">What happened; <see cref="RedemptionStatus.AlreadyRedeemed"/> means the token was exchanged before.</param>
/// <param name="Grant">The grant, for every status except <see cref="RedemptionStatus.Unknown"/>.</param>
public readonly record struct RefreshRedemption(RedemptionStatus Status, RefreshTokenGrant? Grant);

/// <summary>
/// Everything the authorization server must remember: clients, authorization codes, refresh tokens, revocations and used assertion
/// identifiers. Each method is one atomic step, so an implementation over a relational store needs a row or a unique key per record
/// and no shared object identity. Implementations must be thread-safe. Codes and tokens arrive as hashes; a store never sees the secret.
/// </summary>
public interface IAuthorizationStore
{
  /// <summary>Finds a registered client.</summary>
  /// <param name="clientId">The <c>client_id</c>.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  ValueTask<RegisteredClient?> FindClientAsync(string clientId, CancellationToken cancellationToken);

  /// <summary>Adds or replaces a client.</summary>
  /// <param name="client">The client.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  ValueTask SaveClientAsync(RegisteredClient client, CancellationToken cancellationToken);

  /// <summary>Stores a newly issued authorization code.</summary>
  /// <param name="grant">The code's grant.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  ValueTask SaveCodeAsync(AuthorizationCodeGrant grant, CancellationToken cancellationToken);

  /// <summary>
  /// Atomically marks a code redeemed and returns it. Exactly one of two concurrent calls with the same hash gets
  /// <see cref="RedemptionStatus.Redeemed"/>; the other gets <see cref="RedemptionStatus.AlreadyRedeemed"/>. A redeemed code stays
  /// until its <see cref="AuthorizationCodeGrant.FamilyExpiresAt"/>, so a replay is recognised and revokes its family even after the code's own lifetime.
  /// </summary>
  /// <param name="codeHash">The code's hash.</param>
  /// <param name="now">The current time.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  ValueTask<CodeRedemption> RedeemCodeAsync(string codeHash, DateTimeOffset now, CancellationToken cancellationToken);

  /// <summary>Stores a newly issued refresh token.</summary>
  /// <param name="token">The token's grant.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  ValueTask SaveRefreshTokenAsync(RefreshTokenGrant token, CancellationToken cancellationToken);

  /// <summary>
  /// Atomically marks a refresh token used and returns it, with the same exactly-once rule as <see cref="RedeemCodeAsync"/>. A used
  /// token stays until its family expires so that its reuse is detected.
  /// </summary>
  /// <param name="tokenHash">The token's hash.</param>
  /// <param name="now">The current time.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  ValueTask<RefreshRedemption> RedeemRefreshTokenAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken);

  /// <summary>Reads a refresh token without using it (the revocation endpoint needs its client and family).</summary>
  /// <param name="tokenHash">The token's hash.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  ValueTask<RefreshTokenGrant?> FindRefreshTokenAsync(string tokenHash, CancellationToken cancellationToken);

  /// <summary>Records a revocation: <c>jti:</c> plus an access token's id, or <c>family:</c> plus a family id. It may be forgotten after <paramref name="expiresAt"/>.</summary>
  /// <param name="key">What is revoked.</param>
  /// <param name="expiresAt">When nothing the revocation could affect is still valid.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  ValueTask RevokeAsync(string key, DateTimeOffset expiresAt, CancellationToken cancellationToken);

  /// <summary>Whether <see cref="RevokeAsync"/> recorded the key.</summary>
  /// <param name="key">What to look up.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  ValueTask<bool> IsRevokedAsync(string key, CancellationToken cancellationToken);

  /// <summary>
  /// Records that an assertion (a client authentication JWT or an ID-JAG) with this identity was seen. Atomic: of two concurrent calls
  /// with the same key one gets true.
  /// </summary>
  /// <param name="key">Issuer and <c>jti</c> combined.</param>
  /// <param name="expiresAt">When the assertion expires; the record may then be forgotten.</param>
  /// <param name="now">The current time.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  /// <returns>True the first time; false for a replay.</returns>
  ValueTask<bool> TryRecordAssertionAsync(string key, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken cancellationToken);

  /// <summary>Forgets codes, tokens, revocations and assertion records whose time has passed.</summary>
  /// <param name="now">The current time.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  ValueTask PurgeAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
