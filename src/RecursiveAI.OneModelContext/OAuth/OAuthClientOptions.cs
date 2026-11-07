using System;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.OAuth;

/// <summary>
/// Sends the user to the authorization endpoint and brings the answer back. Desktop and command-line hosts use
/// <see cref="LoopbackRedirectReceiver"/>; a test or a headless host can implement this itself.
/// </summary>
public interface IAuthorizationUserAgent
{
  /// <summary>The redirect URI registered for the client and sent in every authorization request. It must be HTTPS or a loopback address.</summary>
  Uri RedirectUri { get; }

  /// <summary>Opens <paramref name="authorizationUrl"/> for the user and waits for the redirect to <see cref="RedirectUri"/>.</summary>
  /// <param name="authorizationUrl">The authorization request. It holds a PKCE challenge and a state value, never a secret.</param>
  /// <param name="cancellationToken">Cancels the wait.</param>
  /// <returns>The full URI the browser was redirected to, query included.</returns>
  ValueTask<Uri> AuthorizeAsync(Uri authorizationUrl, CancellationToken cancellationToken);
}

/// <summary>Credentials issued to the client at one authorization server, out of band.</summary>
/// <param name="ClientId">The client identifier.</param>
/// <param name="ClientSecret">The client secret, or null for a public client. Never logged.</param>
/// <param name="Issuer">The issuer identifier of the authorization server that issued them. When null, the credentials bind to the first server they are used with and are refused at any other.</param>
public sealed record PreRegisteredClient(string ClientId, string? ClientSecret = null, string? Issuer = null)
{
  /// <inheritdoc />
  public override string ToString() => $"PreRegisteredClient(client: {ClientId}, issuer: {Issuer})";
}

/// <summary>The OAuth client credentials grant (RFC 6749 section 4.4) with the client authenticating by secret or by signed assertion.</summary>
public sealed class ClientCredentialsGrant
{
  /// <summary>Authenticates with a client secret.</summary>
  /// <param name="clientId">The client identifier.</param>
  /// <param name="clientSecret">The client secret. Sent as HTTP Basic when the server lists <c>client_secret_basic</c> (or says nothing), otherwise in the body.</param>
  public ClientCredentialsGrant(string clientId, string clientSecret)
  {
    ArgumentException.ThrowIfNullOrEmpty(clientId);
    ArgumentException.ThrowIfNullOrEmpty(clientSecret);
    ClientId = clientId;
    ClientSecret = clientSecret;
  }

  /// <summary>Authenticates with a signed JWT assertion (<c>private_key_jwt</c>, RFC 7523 section 2.2), the recommended form.</summary>
  /// <param name="clientId">The client identifier.</param>
  /// <param name="signingKey">An RSA or ECDSA private key. The key is used, not copied or disposed.</param>
  /// <param name="keyId">The <c>kid</c> the server knows the public key by, or null.</param>
  public ClientCredentialsGrant(string clientId, AsymmetricAlgorithm signingKey, string? keyId = null)
  {
    ArgumentException.ThrowIfNullOrEmpty(clientId);
    ArgumentNullException.ThrowIfNull(signingKey);
    ClientId = clientId;
    SigningKey = signingKey;
    KeyId = keyId;
  }

  /// <summary>The client identifier.</summary>
  public string ClientId { get; }

  internal string? ClientSecret { get; }

  internal AsymmetricAlgorithm? SigningKey { get; }

  internal string? KeyId { get; }

  /// <summary>
  /// The issuer of the authorization server that provisioned the credentials. When set, the client refuses to send them to any other
  /// server the resource might advertise. Strongly recommended.
  /// </summary>
  public string? ExpectedIssuer { get; init; }

  /// <summary>The scopes to request, space-separated. When null, the scope selection of the authorization page applies (challenge, then <c>scopes_supported</c>, then none).</summary>
  public string? Scope { get; init; }

  /// <inheritdoc />
  public override string ToString() => $"ClientCredentialsGrant(client: {ClientId})";
}

/// <summary>
/// Enterprise-Managed Authorization (<c>io.modelcontextprotocol/enterprise-managed-authorization</c>): the client exchanges the user's
/// identity assertion at the enterprise identity provider for an Identity Assertion JWT Authorization Grant (ID-JAG, RFC 8693), then
/// redeems it at the MCP authorization server with the JWT bearer grant (RFC 7523).
/// </summary>
public sealed class EnterpriseManagedAuthorization
{
  /// <summary>Creates the configuration.</summary>
  /// <param name="identityProviderTokenEndpoint">The enterprise IdP's token endpoint (HTTPS, or loopback HTTP).</param>
  /// <param name="identityAssertion">Returns the user's current OpenID Connect ID token (or other assertion of <see cref="IdentityAssertionType"/>). Called for each exchange so it can refresh.</param>
  /// <param name="clientId">The client identifier used at the MCP authorization server (and, unless <see cref="IdentityProviderClientId"/> says otherwise, at the IdP).</param>
  public EnterpriseManagedAuthorization(Uri identityProviderTokenEndpoint, Func<CancellationToken, ValueTask<string>> identityAssertion, string clientId)
  {
    ArgumentNullException.ThrowIfNull(identityProviderTokenEndpoint);
    ArgumentNullException.ThrowIfNull(identityAssertion);
    ArgumentException.ThrowIfNullOrEmpty(clientId);
    IdentityProviderTokenEndpoint = identityProviderTokenEndpoint;
    IdentityAssertion = identityAssertion;
    ClientId = clientId;
  }

  /// <summary>The IdP token endpoint.</summary>
  public Uri IdentityProviderTokenEndpoint { get; }

  internal Func<CancellationToken, ValueTask<string>> IdentityAssertion { get; }

  /// <summary>The client identifier at the MCP authorization server. A Client ID Metadata Document URL is allowed.</summary>
  public string ClientId { get; }

  /// <summary>The client secret at the MCP authorization server, if the client is confidential there. Never logged.</summary>
  public string? ClientSecret { get; init; }

  /// <summary>The client identifier at the IdP, when it differs from <see cref="ClientId"/>.</summary>
  public string? IdentityProviderClientId { get; init; }

  /// <summary>The client secret at the IdP, if it requires client authentication. Sent in the request body. Never logged.</summary>
  public string? IdentityProviderClientSecret { get; init; }

  /// <summary>The token type URI of the identity assertion. Default <c>urn:ietf:params:oauth:token-type:id_token</c>.</summary>
  public string IdentityAssertionType { get; init; } = "urn:ietf:params:oauth:token-type:id_token";

  /// <summary>The scopes to request, space-separated, or null for the scope selection of the authorization page.</summary>
  public string? Scope { get; init; }

  /// <summary>The issuer of the MCP authorization server this configuration is for; the client refuses any other. Strongly recommended.</summary>
  public string? ExpectedIssuer { get; init; }

  /// <inheritdoc />
  public override string ToString() => $"EnterpriseManagedAuthorization(client: {ClientId})";
}

/// <summary>
/// Everything an <see cref="OAuthClient"/> needs. The only required value is the resource: the canonical URI of the server the tokens are
/// for. Choose how the client gets a token by setting <see cref="UserAgent"/> (authorization code with PKCE),
/// <see cref="ClientCredentials"/> or <see cref="Enterprise"/>.
/// </summary>
public sealed class OAuthClientOptions
{
  /// <summary>Creates options for a protected resource.</summary>
  /// <param name="resource">The URL of the server's endpoint, such as <c>https://mcp.example.com/mcp</c>. It becomes the <c>resource</c> parameter (RFC 8707) on every authorization and token request, and tokens are sent only to its origin.</param>
  /// <exception cref="ArgumentException">The URL is not absolute HTTP(S), has a fragment or carries user info.</exception>
  public OAuthClientOptions(Uri resource)
  {
    ArgumentNullException.ThrowIfNull(resource);
    Canonical = OAuthUri.CanonicalResource(resource) ?? throw new ArgumentException("The resource must be an absolute http(s) URL without a fragment or credentials.", nameof(resource));
    Resource = resource;
  }

  /// <summary>The protected resource.</summary>
  public Uri Resource { get; }

  internal string Canonical { get; }

  /// <summary>Opens the browser and returns the redirect, for user-delegated authorization. Null when only a non-interactive grant is configured.</summary>
  public IAuthorizationUserAgent? UserAgent { get; init; }

  /// <summary>Authenticate as the application itself, with no user (the OAuth Client Credentials extension).</summary>
  public ClientCredentialsGrant? ClientCredentials { get; init; }

  /// <summary>Obtain tokens through the enterprise identity provider (the Enterprise-Managed Authorization extension).</summary>
  public EnterpriseManagedAuthorization? Enterprise { get; init; }

  /// <summary>
  /// The HTTPS URL of this client's OAuth Client ID Metadata Document, used as <c>client_id</c> when the authorization server advertises
  /// support. The document is hosted by the application, not by this library; its <c>redirect_uris</c> must include the user agent's redirect URI.
  /// </summary>
  public string? ClientIdMetadataUrl { get; init; }

  /// <summary>Credentials issued out of band. They win over a metadata document and over dynamic registration.</summary>
  public PreRegisteredClient? PreRegistered { get; init; }

  /// <summary>Where tokens and registrations are kept. Default: in memory. Use <see cref="FileTokenStore"/> to stay signed in between runs.</summary>
  public ITokenStore Store { get; init; } = new MemoryTokenStore();

  /// <summary>
  /// Allows dynamic client registration (RFC 7591) as the last resort when the server supports no Client ID Metadata Documents. The
  /// authorization specification deprecates it. Default true, so older servers keep working.
  /// </summary>
  [Obsolete("Dynamic Client Registration is deprecated by the 2026-07-28 authorization specification in favour of Client ID Metadata Documents.", DiagnosticId = "OMC0004")]
  public bool AllowDynamicRegistration { get; init; } = true;

  /// <summary>The client name shown to the user during consent and sent when registering. Default <c>OneModelContext client</c>.</summary>
  public string ClientName { get; init; } = "OneModelContext client";

  /// <summary>Add <c>offline_access</c> to the scope when the server lists it in <c>scopes_supported</c>, so it may issue a refresh token. Default true.</summary>
  public bool RequestOfflineAccess { get; init; } = true;

  /// <summary>
  /// The most authorization runs one request may cause (the first sign-in and then each step-up). The request ends with an error after
  /// that, or earlier when a challenge asks for scopes already requested. Default 3.
  /// </summary>
  public int MaxAuthorizationAttempts { get; init; } = 3;

  /// <summary>How long one metadata, registration or token request may take. Default 30 seconds.</summary>
  public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

  /// <summary>Largest metadata or token response read, in bytes. Default 256 KiB.</summary>
  public int MaxResponseBytes { get; init; } = 256 * 1024;

  /// <summary>
  /// How long before expiry an access token is replaced. Default 30 seconds.
  /// </summary>
  public TimeSpan ExpirySkew { get; init; } = TimeSpan.FromSeconds(30);

  /// <summary>Clock for expiry and timeouts; null for the system clock.</summary>
  public TimeProvider? TimeProvider { get; init; }

  /// <summary>
  /// The HTTP client for metadata, registration and token requests. Null creates a private one that never follows redirects. A client
  /// supplied here must not follow redirects either: a 3xx answer is treated as a failure, and no credential is ever re-sent.
  /// </summary>
  public HttpClient? HttpClient { get; init; }

  /// <summary>Whether any grant that can run without a person is configured.</summary>
  internal bool IsNonInteractive => ClientCredentials is not null || Enterprise is not null;
}

internal static class OAuthUri
{
  /// <summary>RFC 8707 canonical form: lower-case scheme and host, default port dropped, no fragment, and no slash for an empty path.</summary>
  internal static string? CanonicalResource(Uri uri)
  {
    if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
    {
      return null;
    }

    var path = uri.AbsolutePath == "/" ? string.Empty : uri.AbsolutePath;
    return uri.Scheme + "://" + uri.IdnHost + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)) + path + uri.Query;
  }

  internal static string? CanonicalResource(string? value) =>
    value is not null && Uri.TryCreate(value, UriKind.Absolute, out var uri) && !value.Contains('#', StringComparison.Ordinal) ? CanonicalResource(uri) : null;

  /// <summary>OAuth 2.1 section 1.5: every endpoint is HTTPS; loopback HTTP is allowed so local development and tests work.</summary>
  internal static bool IsAllowedEndpoint(Uri uri) =>
    uri.IsAbsoluteUri && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
    && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(uri)));

  internal static bool IsLoopbackHost(Uri uri) =>
    uri.IsLoopback || string.Equals(uri.IdnHost, "localhost", StringComparison.OrdinalIgnoreCase);

  internal static bool SameOrigin(Uri a, Uri b) =>
    string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

  internal static bool TryHttp([NotNullWhen(true)] string? text, [NotNullWhen(true)] out Uri? uri)
  {
    if (text is not null && Uri.TryCreate(text, UriKind.Absolute, out uri) && IsAllowedEndpoint(uri))
    {
      return true;
    }

    uri = null;
    return false;
  }
}
