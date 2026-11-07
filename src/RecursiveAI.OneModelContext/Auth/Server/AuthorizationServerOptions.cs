using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.Jose;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Auth.Server;

/// <summary>
/// What the authorization server knows when it asks the host to authenticate the user and obtain consent. The same context is passed again
/// when the host sends the user back to <see cref="ResumeUrl"/>, rebuilt from a signed token, so the host keeps no state for the server.
/// </summary>
public sealed class AuthorizationRequestContext
{
  internal AuthorizationRequestContext()
  {
  }

  /// <summary>The HTTP exchange of the user's browser request, for reading the host's own session cookie or sign-in state.</summary>
  public required IHttpExchange Exchange { get; init; }

  /// <summary>The <c>client_id</c> that asks for access.</summary>
  public required string ClientId { get; init; }

  /// <summary>The client's display name, or null. It comes from the client and proves nothing; show <see cref="ClientId"/> and the host of <see cref="RedirectUri"/> beside it.</summary>
  public string? ClientName { get; init; }

  /// <summary>Whether the client was identified by a Client ID Metadata Document, so that <see cref="ClientId"/> is a URL the client controls.</summary>
  public bool IsMetadataDocumentClient { get; init; }

  /// <summary>The redirect URI the user will be sent back to, already checked against the client's registration.</summary>
  public required Uri RedirectUri { get; init; }

  /// <summary>The scopes asked for (or the server's defaults), restricted to those the server supports and the client may have.</summary>
  public required IReadOnlyList<string> RequestedScopes { get; init; }

  /// <summary>The protected resource (RFC 8707) the access token will be for.</summary>
  public required string Resource { get; init; }

  /// <summary>
  /// Where to send the user's browser once the host has authenticated them (and obtained consent), as a GET. It carries a signed,
  /// expiring token of this exact request, so it cannot be altered or reused for a different client, redirect URI or scope. The host
  /// then sees this context again and returns <see cref="AuthorizationDecision.Approve"/>.
  /// </summary>
  public required Uri ResumeUrl { get; init; }
}

/// <summary>The host's answer to an authorization request.</summary>
public sealed class AuthorizationDecision
{
  private AuthorizationDecision()
  {
  }

  internal bool IsApproved { get; private init; }

  internal bool IsDenied { get; private init; }

  internal string? Subject { get; private init; }

  internal IReadOnlyList<string>? GrantedScopes { get; private init; }

  internal IReadOnlyDictionary<string, string> Claims { get; private init; } = new Dictionary<string, string>();

  internal string? DenyDescription { get; private init; }

  internal Func<IHttpResponse, CancellationToken, Task>? Interaction { get; private init; }

  /// <summary>The user is signed in and consents.</summary>
  /// <param name="subject">A stable identifier of the user at this server; becomes the access token's <c>sub</c>. It must not be empty.</param>
  /// <param name="grantedScopes">The scopes the user agreed to, a subset of those asked for; null grants all that were asked for.</param>
  /// <param name="claims">Extra string claims for the access token, for example <c>email</c>. The registered names (<c>iss</c>, <c>sub</c>, <c>aud</c>, <c>exp</c>, <c>nbf</c>, <c>iat</c>, <c>jti</c>, <c>scope</c>, <c>client_id</c>, <c>sid</c>) are refused.</param>
  /// <exception cref="ArgumentException">The subject is empty or a claim name is reserved.</exception>
  public static AuthorizationDecision Approve(string subject, IEnumerable<string>? grantedScopes = null, IReadOnlyDictionary<string, string>? claims = null)
  {
    ArgumentException.ThrowIfNullOrEmpty(subject);
    if (claims is not null)
    {
      foreach (var name in claims.Keys)
      {
        if (AuthorizationServer.ReservedClaims.Contains(name))
        {
          throw new ArgumentException($"The claim '{name}' is set by the server.", nameof(claims));
        }
      }
    }

    return new AuthorizationDecision
    {
      IsApproved = true,
      Subject = subject,
      GrantedScopes = grantedScopes is null ? null : [.. grantedScopes],
      Claims = claims ?? new Dictionary<string, string>(),
    };
  }

  /// <summary>The user declined, or may not use this client. The client is sent back with <c>error=access_denied</c>.</summary>
  /// <param name="description">A short reason for the client's logs (<c>error_description</c>); null for none. Do not put anything secret in it.</param>
  public static AuthorizationDecision Deny(string? description = null) => new() { IsDenied = true, DenyDescription = description };

  /// <summary>
  /// The user must sign in or consent first: the host writes a response of its own, a login page or a redirect to an upstream identity
  /// provider. The server adds nothing to it. When done the host redirects the browser to <see cref="AuthorizationRequestContext.ResumeUrl"/>.
  /// </summary>
  /// <param name="respond">Writes the status, headers and body. Called once.</param>
  public static AuthorizationDecision Interact(Func<IHttpResponse, CancellationToken, Task> respond)
  {
    ArgumentNullException.ThrowIfNull(respond);
    return new AuthorizationDecision { Interaction = respond };
  }

  /// <summary>Sends the browser to another URL, typically the sign-in page of the host or of an upstream OpenID Connect provider.</summary>
  /// <param name="location">The absolute URL. It is the host's responsibility that it is trusted.</param>
  public static AuthorizationDecision Redirect(Uri location)
  {
    ArgumentNullException.ThrowIfNull(location);
    return Interact((response, _) =>
    {
      response.StatusCode = 302;
      response.SetHeader("Location", location.AbsoluteUri);
      response.SetHeader("Cache-Control", "no-store");
      return Task.CompletedTask;
    });
  }
}

/// <summary>The host's login and consent step: authenticate the user and decide what they grant.</summary>
/// <param name="context">The request.</param>
/// <param name="cancellationToken">Cancelled when the browser disconnects.</param>
public delegate ValueTask<AuthorizationDecision> AuthorizeUserCallback(AuthorizationRequestContext context, CancellationToken cancellationToken);

/// <summary>Settings for clients identified by a Client ID Metadata Document (draft-ietf-oauth-client-id-metadata-document).</summary>
public sealed class ClientMetadataDocumentOptions
{
  /// <summary>Limits and address policy for the fetch. Default: public addresses over HTTPS, 16 KiB, 5 seconds, no redirects.</summary>
  public GuardedFetchOptions Fetch { get; init; } = new();

  /// <summary>How long a fetched document is kept when the response gives no lifetime. Default 5 minutes.</summary>
  public TimeSpan DefaultCacheLifetime { get; init; } = TimeSpan.FromMinutes(5);

  /// <summary>The longest a document is kept however long <c>Cache-Control</c> allows. Default 1 hour.</summary>
  public TimeSpan MaxCacheLifetime { get; init; } = TimeSpan.FromHours(1);

  /// <summary>How long a failed fetch is remembered, so that repeated requests for a bad URL do not repeat the fetch. Default 30 seconds.</summary>
  public TimeSpan FailureCacheLifetime { get; init; } = TimeSpan.FromSeconds(30);

  /// <summary>Most documents remembered at once. Default 1000; at the limit, expired entries go first, then the one that expires soonest.</summary>
  public int MaxCacheEntries { get; init; } = 1000;

  /// <summary>Most fetches in flight at once. Default 8; further requests wait, and give up with the fetch timeout.</summary>
  public int MaxConcurrentFetches { get; init; } = 8;

  /// <summary>
  /// Most fetches in flight to one host at once. Default 2; more are refused at once (the sign-in fails) instead of waiting, so a single
  /// slow host cannot take every slot of <see cref="MaxConcurrentFetches"/>.
  /// </summary>
  public int MaxFetchesPerHost { get; init; } = 2;

  /// <summary>
  /// How long a host is left alone after a fetch from it failed (timeout, error status, oversized body). Default 10 seconds; zero turns it
  /// off. Requests for other documents on that host fail at once during that time.
  /// </summary>
  public TimeSpan HostFailureBackoff { get; init; } = TimeSpan.FromSeconds(10);

  /// <summary>Decides which client identifier URLs are acceptable at all, before any fetch. Default: any HTTPS URL.</summary>
  public Func<Uri, bool>? AcceptClientId { get; init; }
}

/// <summary>An identity provider whose Identity Assertion JWT Authorization Grants (ID-JAG) this server accepts.</summary>
/// <param name="Issuer">The IdP's issuer identifier, compared exactly with the assertion's <c>iss</c>.</param>
/// <param name="Keys">The IdP's signing keys, for example an <see cref="HttpJwksProvider"/>.</param>
public sealed record TrustedIdentityProvider(string Issuer, IJwksProvider Keys);

/// <summary>What the server learned from a validated ID-JAG, for the host's subject mapping.</summary>
/// <param name="Issuer">The IdP.</param>
/// <param name="Subject">The user at the IdP (<c>sub</c>).</param>
/// <param name="ClientId">The authenticated client.</param>
/// <param name="Scopes">The scopes the assertion carries.</param>
public sealed record IdentityAssertion(string Issuer, string Subject, string ClientId, IReadOnlyList<string> Scopes);

/// <summary>Enterprise-Managed Authorization: accept ID-JAGs through the JWT-bearer grant (RFC 7523) from trusted identity providers.</summary>
public sealed class EnterpriseAuthorizationOptions
{
  /// <summary>The identity providers trusted to vouch for users. Required, at least one.</summary>
  public required IReadOnlyList<TrustedIdentityProvider> IdentityProviders { get; init; }

  /// <summary>
  /// Maps the IdP's user to the subject of the access token, or returns null to refuse the assertion. Default: the assertion's
  /// <c>sub</c> prefixed with the issuer and <c>#</c> (<c>https://idp.example#alice</c>), because a subject is only unique together with its issuer.
  /// </summary>
  public Func<IdentityAssertion, CancellationToken, ValueTask<string?>>? ResolveSubject { get; init; }

  /// <summary>
  /// Whether the client must authenticate with a secret or key. Default true: an ID-JAG is a credential for a user's access, and a public
  /// client has nothing that keeps another party from presenting it. Set false only to accept public clients that the extension allows.
  /// </summary>
  public bool RequireConfidentialClient { get; init; } = true;

  /// <summary>The longest an ID-JAG may live from <c>iat</c> to <c>exp</c>. Default 10 minutes; longer ones are refused.</summary>
  public TimeSpan MaxAssertionLifetime { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>Settings for <see cref="AuthorizationServer"/>.</summary>
public sealed class AuthorizationServerOptions
{
  /// <summary>
  /// The issuer identifier (RFC 8414): an absolute <c>https</c> URL without query or fragment (<c>http</c> only on a loopback host). Endpoint URLs
  /// and the <c>iss</c> of every token and response are built from it, never from request headers.
  /// </summary>
  public required Uri Issuer { get; init; }

  /// <summary>
  /// The protected resources this server issues tokens for (RFC 8707 resource indicators), such as the MCP endpoint URL. A request for
  /// any other resource is refused with <c>invalid_target</c>. At least one.
  /// </summary>
  public required IReadOnlyList<string> Resources { get; init; }

  /// <summary>
  /// The signing keys: the first one that is not retired signs, and all of them appear in the published key set until
  /// <see cref="AccessTokenLifetime"/> after their <see cref="JsonWebSigningKey.RetiredAt"/>. To rotate, put the new key first and set
  /// <c>RetiredAt</c> on the old one. Key identifiers must be unique.
  /// </summary>
  public required IReadOnlyList<JsonWebSigningKey> SigningKeys { get; init; }

  /// <summary>Authenticates the user and obtains consent; see <see cref="AuthorizationDecision"/>.</summary>
  public required AuthorizeUserCallback AuthorizeUser { get; init; }

  /// <summary>Where clients, codes, tokens and revocations live. Default: a new <see cref="MemoryAuthorizationStore"/>.</summary>
  public IAuthorizationStore Store { get; init; } = new MemoryAuthorizationStore();

  /// <summary>The scopes the server supports, published in its metadata. A request for others is trimmed to these. Empty means any scope is accepted.</summary>
  public IReadOnlyList<string> ScopesSupported { get; init; } = [];

  /// <summary>The scopes granted when a request names none. Default none.</summary>
  public IReadOnlyList<string> DefaultScopes { get; init; } = [];

  /// <summary>How long access tokens live. Default 10 minutes.</summary>
  public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromMinutes(10);

  /// <summary>How long a refresh token is valid until it is used. Default 30 days.</summary>
  public TimeSpan RefreshTokenLifetime { get; init; } = TimeSpan.FromDays(30);

  /// <summary>How long a chain of refreshes may continue before the user must authorize again. Default 90 days.</summary>
  public TimeSpan MaxRefreshFamilyLifetime { get; init; } = TimeSpan.FromDays(90);

  /// <summary>How long an authorization code can be redeemed. Default 60 seconds.</summary>
  public TimeSpan AuthorizationCodeLifetime { get; init; } = TimeSpan.FromSeconds(60);

  /// <summary>How long the user has to sign in before <see cref="AuthorizationRequestContext.ResumeUrl"/> expires. Default 15 minutes.</summary>
  public TimeSpan ResumeLifetime { get; init; } = TimeSpan.FromMinutes(15);

  /// <summary>Whether authorization-code grants also yield refresh tokens. Default true.</summary>
  public bool IssueRefreshTokens { get; init; } = true;

  /// <summary>
  /// Whether an authorization request without <c>resource</c> is bound to the only configured resource, as clients of the 2025-03-26
  /// revision send none. Default false: <c>resource</c> is required. Ignored when more than one resource is configured.
  /// </summary>
  public bool AllowMissingResource { get; init; }

  /// <summary>The path of the authorization endpoint, below the issuer's path. Default <c>/authorize</c>.</summary>
  public string AuthorizePath { get; init; } = "/authorize";

  /// <summary>The path of the token endpoint. Default <c>/token</c>.</summary>
  public string TokenPath { get; init; } = "/token";

  /// <summary>The path of the revocation endpoint (RFC 7009). Default <c>/revoke</c>.</summary>
  public string RevocationPath { get; init; } = "/revoke";

  /// <summary>The path of the key set. Default <c>/jwks</c>.</summary>
  public string JwksPath { get; init; } = "/jwks";

  /// <summary>The path of the registration endpoint, when <see cref="DynamicClientRegistration"/> is on. Default <c>/register</c>.</summary>
  public string RegistrationPath { get; init; } = "/register";

  /// <summary>
  /// Whether the metadata document is also served at <c>/.well-known/oauth-authorization-server</c> on the origin when the issuer has a
  /// path. Clients of the 2025-03-26 revision look there on the MCP server's origin. Default false; an issuer without a path always serves it.
  /// </summary>
  public bool ServeMetadataAtOrigin { get; init; }

  /// <summary>
  /// A secret of at least 32 bytes that signs the tokens carried across the host's login step. Default: a random key made at start,
  /// which is lost on restart and not shared between instances; set it for several instances.
  /// </summary>
  public byte[]? ResumeKey { get; init; }

  /// <summary>Clients identified by a metadata document. Default: enabled with the default <see cref="ClientMetadataDocumentOptions"/>. Null turns it off.</summary>
  public ClientMetadataDocumentOptions? ClientMetadataDocuments { get; init; } = new();

  /// <summary>Fetch limits for client key sets (<c>jwks_uri</c>) of pre-registered clients. Default: public addresses only. Metadata-document clients use their own options.</summary>
  public GuardedFetchOptions ClientKeyFetch { get; init; } = new();

  /// <summary>Accepts Identity Assertion JWT Authorization Grants. Default null: the JWT-bearer grant is off.</summary>
  public EnterpriseAuthorizationOptions? EnterpriseAuthorization { get; init; }

  /// <summary>
  /// Turns on the registration endpoint (RFC 7591). Deprecated by the 2026-07-28 revision in favour of metadata documents and
  /// pre-registration. Default false. Registered clients are public or use <c>client_secret_basic</c>; nothing limits how many clients
  /// an anonymous caller can register, so put a rate limit in front of it.
  /// </summary>
  [Obsolete(DeprecatedFeatures.Registration, DiagnosticId = DeprecatedFeatures.RegistrationId)]
  public bool DynamicClientRegistration { get; init; }

  /// <summary>The largest request body read at any endpoint. Default 32 KiB.</summary>
  public int MaxRequestBodyBytes { get; init; } = 32 * 1024;

  /// <summary>The clock for lifetimes and expiry. Default the system clock.</summary>
  public TimeProvider? TimeProvider { get; init; }

  /// <summary>
  /// Called with the principal-less facts of security-relevant events, for audit: for example a refresh-token reuse that revoked a family.
  /// The text never contains a code, token or secret. Default none.
  /// </summary>
  public Action<string>? OnSecurityEvent { get; init; }
}
