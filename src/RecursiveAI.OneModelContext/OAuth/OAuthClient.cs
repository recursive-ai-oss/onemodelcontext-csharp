using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.OAuth;

/// <summary>
/// An OAuth 2.1 client for one protected resource. It discovers the authorization server from the resource's challenge (RFC 9728, RFC 8414,
/// OpenID Connect Discovery), registers (pre-registered credentials, then a Client ID Metadata Document, then dynamic registration), runs
/// the authorization code flow with PKCE S256, the client credentials grant or the enterprise-managed grant, keeps the tokens in an
/// <see cref="ITokenStore"/>, refreshes them, and widens the scope on step-up. Use it through <see cref="OAuthHttpHandler"/>, or call it directly.
/// The class is thread-safe; concurrent authorizations run one at a time and share the result.
/// </summary>
/// <remarks>
/// Tokens, secrets, authorization codes and PKCE verifiers never appear in an exception message, a URL or a log. Nothing here follows a
/// redirect. A token is stored with the issuer that issued it and is used only for the resource it was requested for.
/// </remarks>
public sealed class OAuthClient : IDisposable
{
  private const string IdJagTokenType = "urn:ietf:params:oauth:token-type:id-jag";
  private const string IdJagProfile = "urn:ietf:params:oauth:grant-profile:id-jag";

  private readonly OAuthClientOptions _options;
  private readonly TimeProvider _time;
  private readonly HttpClient _http;
  private readonly bool _ownsHttp;
  private readonly bool _loopbackResource;
  private readonly SemaphoreSlim _gate = new(1, 1);
  private readonly string _tokenKey;
  private string _indicator;
  private TokenRecord? _record;
  private bool _loaded;

  /// <summary>Creates a client.</summary>
  /// <param name="options">The configuration.</param>
  public OAuthClient(OAuthClientOptions options)
  {
    ArgumentNullException.ThrowIfNull(options);
    _options = options;
    _time = options.TimeProvider ?? TimeProvider.System;
    _loopbackResource = OAuthUri.IsLoopbackHost(options.Resource);
    if (options.HttpHandler is null)
    {
      // Redirects are never followed: a credential must not travel to a place the server merely pointed at.
      _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
    }
    else
    {
      // A caller's handler is accepted only when it can be shown not to follow redirects (a 307 would replay the token request body).
      RedirectPolicy.Require(options.HttpHandler, nameof(options));
      _http = new HttpClient(options.HttpHandler, disposeHandler: false);
    }

    _ownsHttp = true;

    _tokenKey = "token:" + options.Canonical;
    _indicator = options.Canonical;
  }

  /// <summary>The configuration.</summary>
  public OAuthClientOptions Options => _options;

  /// <summary>Returns a usable access token without any interaction: the stored one, or one refreshed with the refresh token. Null when there is none.</summary>
  /// <param name="cancellationToken">Cancels the wait.</param>
  public async ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
  {
    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      var record = await LoadAsync(cancellationToken).ConfigureAwait(false);
      if (record?.AccessToken is null)
      {
        return null;
      }

      if (IsFresh(record))
      {
        return record.AccessToken;
      }

      // Another process sharing the store may have refreshed (and rotated the refresh token) since this one read it. Look again before
      // spending a refresh token that may already be spent.
      record = await ReloadAsync(cancellationToken).ConfigureAwait(false);
      if (record?.AccessToken is null)
      {
        return null;
      }

      if (IsFresh(record))
      {
        return record.AccessToken;
      }

      if (record.RefreshToken is null || record.Issuer is null)
      {
        return null;
      }

      try
      {
        RequirePinnedIssuer();
        return await RefreshAsync(record, cancellationToken).ConfigureAwait(false);
      }
      catch (OAuthException)
      {
        // The refresh failed; the caller's next request is challenged and signs in again.
        return null;
      }
    }
    finally
    {
      _gate.Release();
    }
  }

  /// <summary>
  /// Creates an <see cref="HttpClient"/> that carries this client's tokens and answers the resource's challenges (see
  /// <see cref="OAuthHttpHandler"/>). Pass it to <c>ContextTransport.Http</c>: a decorating hook alone cannot answer a <c>401</c> or
  /// <c>403</c> by authorizing and replaying the request, so the handler is the way to use this client with the HTTP transport. The
  /// returned client has no overall timeout (event streams stay open) and never follows redirects. It owns its handler, not this object.
  /// </summary>
  public HttpClient CreateHttpClient() => new(new OAuthHttpHandler(this), disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

  /// <summary>
  /// Adds the stored (or refreshed) access token to a request, with no interaction and no replay. Use it as a transport's request hook only
  /// when the token is obtained some other way; it does nothing for a request to another origin than <see cref="OAuthClientOptions.Resource"/>.
  /// </summary>
  /// <param name="request">The request to add the <c>Authorization</c> header to.</param>
  /// <param name="cancellationToken">Cancels the wait.</param>
  public async ValueTask AuthorizeRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    if (request.RequestUri is null || !OAuthUri.SameOrigin(request.RequestUri, _options.Resource))
    {
      return;
    }

    if (await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false) is { } token)
    {
      request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
  }

  /// <summary>Forgets the stored tokens for this resource (the client registration is kept).</summary>
  /// <param name="cancellationToken">Cancels the write.</param>
  public async ValueTask SignOutAsync(CancellationToken cancellationToken = default)
  {
    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      _record = null;
      _loaded = true;
      await _options.Store.RemoveAsync(_tokenKey, cancellationToken).ConfigureAwait(false);
    }
    finally
    {
      _gate.Release();
    }
  }

  /// <summary>
  /// Runs the configured grant after the resource refused a request, and returns the new access token.
  /// </summary>
  /// <param name="challenge">The <c>Bearer</c> challenge of the 401 or 403 response, or null when it had none.</param>
  /// <param name="rejectedToken">The access token the resource refused, if one was sent. When another caller has already replaced it, the replacement is returned and nothing is run.</param>
  /// <param name="stepUp">True for an <c>insufficient_scope</c> answer: the new scope is the union of the scopes already requested and the challenged ones.</param>
  /// <param name="cancellationToken">Cancels the flow, including the wait for the user.</param>
  /// <returns>The access token.</returns>
  /// <exception cref="OAuthException">Discovery, registration, the authorization response or the token request failed or was refused for safety reasons; <c>issuer_required</c> when a secret or signing key is configured with no pinned issuer.</exception>
  public async ValueTask<string> AuthorizeAsync(BearerChallenge? challenge, string? rejectedToken, bool stepUp, CancellationToken cancellationToken = default)
  {
    RequirePinnedIssuer();
    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      var existing = await LoadAsync(cancellationToken).ConfigureAwait(false);
      if (!stepUp && rejectedToken is not null && existing?.AccessToken is { } current
        && !string.Equals(current, rejectedToken, StringComparison.Ordinal) && IsFresh(existing))
      {
        return current;
      }

      var discovery = await DiscoverResourceAsync(challenge?.ResourceMetadata, cancellationToken).ConfigureAwait(false);
      var issuer = SelectIssuer(discovery);
      var server = await DiscoverServerAsync(issuer, discovery.Metadata is null, cancellationToken).ConfigureAwait(false);

      // Tokens belong to the issuer that issued them. A different server means the old ones are not ours to send.
      if (existing is not null && !string.Equals(existing.Issuer, issuer, StringComparison.Ordinal))
      {
        existing = null;
      }

      var scope = ChooseScope(existing?.Scope, challenge?.Scope, discovery.Metadata?.ScopesSupported, server);
      TokenRecord result;
      if (_options.ClientCredentials is { } grant)
      {
        result = await ClientCredentialsAsync(grant, issuer, server, scope, cancellationToken).ConfigureAwait(false);
      }
      else if (_options.Enterprise is { } enterprise)
      {
        result = await EnterpriseAsync(enterprise, issuer, server, scope, cancellationToken).ConfigureAwait(false);
      }
      else if (_options.UserAgent is { } agent)
      {
        result = await AuthorizationCodeAsync(agent, issuer, server, discovery.Metadata is null, scope, cancellationToken).ConfigureAwait(false);
      }
      else
      {
        throw new OAuthException("The server requires authorization, and no user agent, client credentials or enterprise grant is configured to obtain it.", "authorization_required");
      }

      _record = result;
      await _options.Store.SetAsync(_tokenKey, result, cancellationToken).ConfigureAwait(false);
      return result.AccessToken!;
    }
    finally
    {
      _gate.Release();
    }
  }

  /// <summary>The scopes requested so far for this resource, space-separated, or null.</summary>
  /// <param name="cancellationToken">Cancels the read.</param>
  public async ValueTask<string?> GetRequestedScopeAsync(CancellationToken cancellationToken = default)
  {
    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      return (await LoadAsync(cancellationToken).ConfigureAwait(false))?.Scope;
    }
    finally
    {
      _gate.Release();
    }
  }

  /// <summary>Releases the private HTTP client, if this instance created one.</summary>
  public void Dispose()
  {
    if (_ownsHttp)
    {
      _http.Dispose();
    }

    _gate.Dispose();
  }

  private bool IsFresh(TokenRecord record) =>
    record.AccessToken is not null && (record.ExpiresAt is null || record.ExpiresAt.Value - _options.ExpirySkew > _time.GetUtcNow());

  private async ValueTask<TokenRecord?> ReloadAsync(CancellationToken cancellationToken)
  {
    _record = await _options.Store.GetAsync(_tokenKey, cancellationToken).ConfigureAwait(false);
    _loaded = true;
    return _record;
  }

  // A client secret, a signing key or an identity assertion goes only to an authorization server the application named. Left to the
  // resource's metadata (RFC 9728 section 7.6 lets the client choose among the listed servers), a hostile or compromised resource could
  // choose its own and receive the credential. Checked before any request is made.
  private void RequirePinnedIssuer()
  {
    var missing = _options.ClientCredentials is { ExpectedIssuer: null }
      || _options.Enterprise is { ExpectedIssuer: null }
      || (_options.ClientCredentials is null && _options.Enterprise is null && _options.PreRegistered is { ClientSecret: not null, Issuer: null });
    if (missing)
    {
      throw new OAuthException("A client secret, signing key or identity assertion is configured without the issuer of the authorization server it belongs to, so nothing was sent. Set ExpectedIssuer (or PreRegisteredClient.Issuer).", "issuer_required");
    }
  }

  private async ValueTask<TokenRecord?> LoadAsync(CancellationToken cancellationToken)
  {
    if (!_loaded)
    {
      _record = await _options.Store.GetAsync(_tokenKey, cancellationToken).ConfigureAwait(false);
      _loaded = true;
    }

    return _record;
  }

  // ----- discovery -------------------------------------------------------------------------------------------------------------------

  private sealed record ResourceDiscovery(ResourceMetadata? Metadata, string Origin);

  // 2026-07-28 authorization-server-discovery, Protected Resource Metadata Discovery Requirements: the challenge's resource_metadata when
  // present, otherwise the well-known URIs, path-inserted first and then the root.
  private async Task<ResourceDiscovery> DiscoverResourceAsync(string? fromChallenge, CancellationToken cancellationToken)
  {
    var resource = _options.Resource;
    var origin = resource.GetLeftPart(UriPartial.Authority);
    var candidates = new List<Uri>();
    if (fromChallenge is not null && OAuthUri.TryHttp(fromChallenge, _loopbackResource, out var announced))
    {
      candidates.Add(announced);
    }

    var path = resource.AbsolutePath == "/" ? string.Empty : resource.AbsolutePath;
    if (path.Length > 0)
    {
      candidates.Add(new Uri(origin + "/.well-known/oauth-protected-resource" + path));
    }

    candidates.Add(new Uri(origin + "/.well-known/oauth-protected-resource"));

    foreach (var candidate in candidates.Distinct())
    {
      var (status, body) = await FetchAsync(new HttpRequestMessage(HttpMethod.Get, candidate), cancellationToken).ConfigureAwait(false);
      if (status != 200 || ResourceMetadata.Parse(body) is not { } metadata)
      {
        continue;
      }

      // RFC 9728 section 3.3: the resource in the document must be the resource that was asked about. Without this check a malicious
      // server could point a client at an authorization server on behalf of a different resource. A document served from the root
      // well-known location names the origin or a parent path of the endpoint, and is accepted as that: the token is then requested for
      // the resource exactly as the document publishes it.
      var published = OAuthUri.CanonicalResource(metadata.Resource);
      if (published is null || !IsSameOrAncestor(published, _options.Canonical))
      {
        throw new OAuthException("The protected resource metadata names a different resource than the one requested, so it was not used.", "resource_mismatch");
      }

      _indicator = published;

      if (metadata.AuthorizationServers.Count == 0)
      {
        throw new OAuthException("The protected resource metadata lists no authorization server.", "invalid_resource_metadata");
      }

      return new ResourceDiscovery(metadata, origin);
    }

    // 2025-03-26 authorization: a server with no protected resource metadata is its own authorization server.
    return new ResourceDiscovery(null, origin);
  }

  private static bool IsSameOrAncestor(string published, string requested) =>
    string.Equals(published, requested, StringComparison.Ordinal)
    || (requested.StartsWith(published, StringComparison.Ordinal) && requested[published.Length] == '/' && !published.Contains('?', StringComparison.Ordinal));

  private string SelectIssuer(ResourceDiscovery discovery)
  {
    if (discovery.Metadata is null)
    {
      return discovery.Origin;
    }

    // RFC 9728 section 7.6 leaves the choice to the client. A configured expectation wins; otherwise the first listed.
    var pinned = _options.ClientCredentials?.ExpectedIssuer ?? _options.Enterprise?.ExpectedIssuer ?? _options.PreRegistered?.Issuer;
    if (pinned is not null)
    {
      return discovery.Metadata.AuthorizationServers.FirstOrDefault(s => string.Equals(s, pinned, StringComparison.Ordinal))
        ?? throw new OAuthException("The resource does not list the authorization server these credentials were issued by, so they were not used.", "issuer_not_expected");
    }

    return discovery.Metadata.AuthorizationServers[0];
  }

  // 2026-07-28 authorization-server-discovery, Authorization Server Metadata Discovery.
  private async Task<AuthorizationServerMetadata> DiscoverServerAsync(string issuer, bool legacy, CancellationToken cancellationToken)
  {
    if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) || !OAuthUri.IsAllowedEndpoint(issuerUri, _loopbackResource) || issuerUri.Query.Length != 0)
    {
      throw new OAuthException("The authorization server's issuer is not an HTTPS URL.", "invalid_issuer");
    }

    var origin = issuerUri.GetLeftPart(UriPartial.Authority);
    var path = issuerUri.AbsolutePath == "/" ? string.Empty : issuerUri.AbsolutePath;
    var urls = new List<Uri>();
    if (path.Length > 0)
    {
      urls.Add(new Uri(origin + "/.well-known/oauth-authorization-server" + path));
      urls.Add(new Uri(origin + "/.well-known/openid-configuration" + path));
      urls.Add(new Uri(issuer.TrimEnd('/') + "/.well-known/openid-configuration"));
    }
    else
    {
      urls.Add(new Uri(origin + "/.well-known/oauth-authorization-server"));
      urls.Add(new Uri(origin + "/.well-known/openid-configuration"));
    }

    foreach (var url in urls)
    {
      var (status, body) = await FetchAsync(new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);
      if (status != 200 || AuthorizationServerMetadata.Parse(body) is not { } metadata)
      {
        continue;
      }

      // RFC 8414 section 3.3: the issuer in the document must be identical to the one the URL was built from. A mismatch means the document
      // is not about this server; it is not used and the search does not go on, because the next location is no more trustworthy.
      if (!string.Equals(metadata.Issuer, issuer, StringComparison.Ordinal))
      {
        throw new OAuthException("The authorization server metadata names a different issuer than the one it was fetched for, so it was not used.", "metadata_issuer_mismatch");
      }

      if (!OAuthUri.TryHttp(metadata.TokenEndpoint, _loopbackResource, out _))
      {
        throw new OAuthException("The authorization server metadata has no usable token endpoint.", "invalid_server_metadata");
      }

      return metadata;
    }

    if (!legacy)
    {
      throw new OAuthException("No authorization server metadata could be found for the issuer.", "server_metadata_not_found");
    }

    // 2025-03-26 authorization, fallbacks: a server with no metadata uses /authorize, /token and /register on its own origin.
    return new AuthorizationServerMetadata
    {
      AuthorizationEndpoint = origin + "/authorize",
      TokenEndpoint = origin + "/token",
      RegistrationEndpoint = origin + "/register",
    };
  }

  private string? ChooseScope(string? previous, string? challenged, IReadOnlyList<string>? supportedByResource, AuthorizationServerMetadata server)
  {
    // 2026-07-28 authorization, Scope Selection Strategy and Step-Up: the challenge is authoritative for this operation, and the scopes
    // asked for before stay in the set. With neither, scopes_supported; with none of those, no scope parameter at all.
    var set = new List<string>();
    void AddAll(string? scopes)
    {
      foreach (var scope in (scopes ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
      {
        if (!set.Contains(scope))
        {
          set.Add(scope);
        }
      }
    }

    AddAll(previous);
    AddAll(challenged);
    if (set.Count == 0 && supportedByResource is { Count: > 0 })
    {
      AddAll(string.Join(' ', supportedByResource));
    }

    // Refresh tokens: offline_access only when the server advertises it (2026-07-28 authorization, Refresh Tokens).
    if (_options.RequestOfflineAccess && !_options.IsNonInteractive && server.ScopesSupported is { } offered && offered.Contains("offline_access") && !set.Contains("offline_access"))
    {
      set.Add("offline_access");
    }

    return set.Count == 0 ? null : string.Join(' ', set);
  }

  // ----- authorization code ----------------------------------------------------------------------------------------------------------

  private sealed record ClientIdentity(string ClientId, string? Secret, string? PreferredMethod);

  private async Task<TokenRecord> AuthorizationCodeAsync(IAuthorizationUserAgent agent, string issuer, AuthorizationServerMetadata server, bool legacy, string? scope, CancellationToken cancellationToken)
  {
    // Security considerations, Authorization Code Protection: PKCE S256 is mandatory, and its support is read from the metadata. Servers
    // found by the 2025-03-26 rules predate that field.
    if (!legacy && server.CodeChallengeMethodsSupported?.Contains("S256") != true)
    {
      throw new OAuthException("The authorization server does not advertise PKCE with S256, so authorization was not started.", "pkce_unsupported");
    }

    if (!OAuthUri.TryHttp(server.AuthorizationEndpoint, _loopbackResource, out var authorizationEndpoint))
    {
      throw new OAuthException("The authorization server has no usable authorization endpoint.", "invalid_server_metadata");
    }

    var redirect = agent.RedirectUri;
    var client = await ResolveClientAsync(issuer, server, redirect, cancellationToken).ConfigureAwait(false);
    var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
    var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    var state = Base64Url(RandomNumberGenerator.GetBytes(24));

    var query = new StringBuilder();
    Append(query, "response_type", "code");
    Append(query, "client_id", client.ClientId);
    Append(query, "redirect_uri", redirect.AbsoluteUri);
    Append(query, "code_challenge", challenge);
    Append(query, "code_challenge_method", "S256");
    Append(query, "state", state);
    Append(query, "resource", _indicator);
    if (scope is not null)
    {
      Append(query, "scope", scope);
    }

    var url = new Uri(authorizationEndpoint.AbsoluteUri + (authorizationEndpoint.Query.Length == 0 ? "?" : "&") + query);

    // The issuer is recorded here, from validated metadata, before the user agent leaves (2026-07-28 authorization, Authorization Response Validation).
    var callback = await agent.AuthorizeAsync(url, cancellationToken).ConfigureAwait(false);
    if (!callback.IsAbsoluteUri || !OAuthUri.SameOrigin(callback, redirect) || !string.Equals(callback.AbsolutePath, redirect.AbsolutePath, StringComparison.Ordinal))
    {
      throw new OAuthException("The authorization response did not arrive at the redirect URI, so it was refused.", "invalid_response");
    }

    var response = AuthorizationResponse.Validate(callback, state, server.Issuer, server.AuthorizationResponseIssParameterSupported);

    var form = new List<KeyValuePair<string, string>>
    {
      new("grant_type", "authorization_code"),
      new("code", response.Code),
      new("redirect_uri", redirect.AbsoluteUri),
      new("code_verifier", verifier),
      new("resource", _indicator),
    };
    var token = await TokenRequestAsync(server, form, client, cancellationToken).ConfigureAwait(false);
    return ToRecord(token, issuer, client, scope, requireBearer: true);
  }

  private async Task<ClientIdentity> ResolveClientAsync(string issuer, AuthorizationServerMetadata server, Uri redirect, CancellationToken cancellationToken)
  {
    // 2026-07-28 client-registration, priority order.
    if (_options.PreRegistered is { } fixedClient)
    {
      await BindPreRegisteredAsync(fixedClient, issuer, cancellationToken).ConfigureAwait(false);
      return new ClientIdentity(fixedClient.ClientId, fixedClient.ClientSecret, null);
    }

    if (_options.ClientIdMetadataUrl is { } metadataUrl && server.ClientIdMetadataDocumentSupported)
    {
      return new ClientIdentity(metadataUrl, null, "none");
    }

    var key = "client:" + issuer;
    var stored = await _options.Store.GetAsync(key, cancellationToken).ConfigureAwait(false);
    if (stored?.ClientId is not null && string.Equals(stored.Issuer, issuer, StringComparison.Ordinal))
    {
      return new ClientIdentity(stored.ClientId, stored.ClientSecret, null);
    }

#pragma warning disable CS0618, OMC0004 // Dynamic registration is deprecated by the specification; it is the documented last resort.
    var allowed = _options.AllowDynamicRegistration;
#pragma warning restore CS0618, OMC0004
    if (allowed && OAuthUri.TryHttp(server.RegistrationEndpoint, _loopbackResource, out var registrationEndpoint))
    {
      var registered = await RegisterAsync(registrationEndpoint, redirect, cancellationToken).ConfigureAwait(false);
      await _options.Store.SetAsync(key, new TokenRecord { Issuer = issuer, ClientId = registered.ClientId, ClientSecret = registered.Secret, Dynamic = true }, cancellationToken).ConfigureAwait(false);
      return registered;
    }

    throw new OAuthException("No client registration is available: configure pre-registered credentials or a Client ID Metadata Document URL, or use a server that supports dynamic registration.", "no_client_registration");
  }

  // 2026-07-28 client-registration, Authorization Server Binding: credentials belong to the issuer that issued them.
  private async Task BindPreRegisteredAsync(PreRegisteredClient client, string issuer, CancellationToken cancellationToken)
  {
    if (client.Issuer is not null)
    {
      if (!string.Equals(client.Issuer, issuer, StringComparison.Ordinal))
      {
        throw new OAuthException("The pre-registered client belongs to a different authorization server than the one this resource now uses, so it was not used.", "issuer_not_expected");
      }

      return;
    }

    var key = "bound:" + client.ClientId;
    var bound = await _options.Store.GetAsync(key, cancellationToken).ConfigureAwait(false);
    if (bound?.Issuer is null)
    {
      await _options.Store.SetAsync(key, new TokenRecord { Issuer = issuer, ClientId = client.ClientId }, cancellationToken).ConfigureAwait(false);
    }
    else if (!string.Equals(bound.Issuer, issuer, StringComparison.Ordinal))
    {
      throw new OAuthException("The pre-registered client was first used with a different authorization server, so it was not used with this one.", "issuer_not_expected");
    }
  }

  // RFC 7591 with the OpenID Connect application_type (2026-07-28 client-registration, Dynamic Client Registration).
  private async Task<ClientIdentity> RegisterAsync(Uri endpoint, Uri redirect, CancellationToken cancellationToken)
  {
    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer))
    {
      writer.WriteStartObject();
      writer.WriteString("client_name", _options.ClientName);
      writer.WriteStartArray("redirect_uris");
      writer.WriteStringValue(redirect.AbsoluteUri);
      writer.WriteEndArray();
      writer.WriteStartArray("grant_types");
      writer.WriteStringValue("authorization_code");
      writer.WriteStringValue("refresh_token");
      writer.WriteEndArray();
      writer.WriteStartArray("response_types");
      writer.WriteStringValue("code");
      writer.WriteEndArray();
      writer.WriteString("token_endpoint_auth_method", "none");
      writer.WriteString("application_type", OAuthUri.IsLoopbackHost(redirect) ? "native" : "web");
      writer.WriteEndObject();
    }

    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(buffer.ToArray()) };
    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    var (status, body) = await FetchAsync(request, cancellationToken).ConfigureAwait(false);
    if (status is not (200 or 201))
    {
      throw new OAuthException("Dynamic client registration was refused (HTTP " + status.ToString(CultureInfo.InvariantCulture) + ").", ErrorOf(body));
    }

    using var document = MetadataJson.TryParse(body) ?? throw new OAuthException("Dynamic client registration returned an unreadable answer.", "invalid_response");
    var clientId = MetadataJson.GetString(document.RootElement, "client_id");
    if (string.IsNullOrEmpty(clientId))
    {
      throw new OAuthException("Dynamic client registration returned no client identifier.", "invalid_response");
    }

    return new ClientIdentity(clientId, MetadataJson.GetString(document.RootElement, "client_secret"), MetadataJson.GetString(document.RootElement, "token_endpoint_auth_method"));
  }

  // ----- client credentials and enterprise-managed grants -----------------------------------------------------------------------------

  // oauth-client-credentials extension, Flow Steps and Examples.
  private async Task<TokenRecord> ClientCredentialsAsync(ClientCredentialsGrant grant, string issuer, AuthorizationServerMetadata server, string? scope, CancellationToken cancellationToken)
  {
    if (grant.ExpectedIssuer is not null && !string.Equals(grant.ExpectedIssuer, issuer, StringComparison.Ordinal))
    {
      throw new OAuthException("The resource uses a different authorization server than the one these client credentials were issued by, so they were not sent.", "issuer_not_expected");
    }

    var form = new List<KeyValuePair<string, string>> { new("grant_type", "client_credentials"), new("resource", _indicator) };
    var requested = grant.Scope ?? scope;
    if (requested is not null)
    {
      form.Add(new("scope", requested));
    }

    ClientIdentity identity;
    if (grant.SigningKey is { } key)
    {
      if (server.TokenEndpointAuthMethodsSupported is { } methods && !methods.Contains("private_key_jwt"))
      {
        throw new OAuthException("The authorization server does not accept private_key_jwt client authentication.", "unsupported_client_authentication");
      }

      var algorithm = ClientAssertion.ChooseAlgorithm(key, server.TokenEndpointAuthSigningAlgValuesSupported);
      var audience = server.Issuer is null || server.Issuer == server.TokenEndpoint ? [server.TokenEndpoint!] : new[] { server.Issuer, server.TokenEndpoint! };
      form.Add(new("client_assertion_type", ClientAssertion.AssertionType));
      form.Add(new("client_assertion", ClientAssertion.Create(grant.ClientId, audience, key, algorithm, grant.KeyId, _time.GetUtcNow())));
      identity = new ClientIdentity(grant.ClientId, null, "private_key_jwt");
    }
    else
    {
      identity = new ClientIdentity(grant.ClientId, grant.ClientSecret, null);
    }

    var token = await TokenRequestAsync(server, form, identity, cancellationToken).ConfigureAwait(false);
    return ToRecord(token, issuer, identity with { Secret = null }, requested, requireBearer: true);
  }

  // enterprise-managed-authorization extension, sections 4 and 5.
  private async Task<TokenRecord> EnterpriseAsync(EnterpriseManagedAuthorization enterprise, string issuer, AuthorizationServerMetadata server, string? scope, CancellationToken cancellationToken)
  {
    if (enterprise.ExpectedIssuer is not null && !string.Equals(enterprise.ExpectedIssuer, issuer, StringComparison.Ordinal))
    {
      throw new OAuthException("The resource uses a different authorization server than the one this enterprise grant is for, so nothing was sent.", "issuer_not_expected");
    }

    if (server.AuthorizationGrantProfilesSupported is { } profiles && !profiles.Contains(IdJagProfile))
    {
      throw new OAuthException("The authorization server does not support the identity assertion authorization grant profile.", "unsupported_grant_profile");
    }

    if (!OAuthUri.IsAllowedEndpoint(enterprise.IdentityProviderTokenEndpoint, _loopbackResource))
    {
      throw new OAuthException("The identity provider token endpoint must be HTTPS.", "invalid_server_metadata");
    }

    var requested = enterprise.Scope ?? scope;
    var assertion = await enterprise.IdentityAssertion(cancellationToken).ConfigureAwait(false);
    var exchange = new List<KeyValuePair<string, string>>
    {
      new("grant_type", "urn:ietf:params:oauth:grant-type:token-exchange"),
      new("requested_token_type", IdJagTokenType),
      new("audience", issuer),
      new("resource", _indicator),
      new("subject_token", assertion),
      new("subject_token_type", enterprise.IdentityAssertionType),
      new("client_id", enterprise.IdentityProviderClientId ?? enterprise.ClientId),
    };
    if (requested is not null)
    {
      exchange.Add(new("scope", requested));
    }

    if (enterprise.IdentityProviderClientSecret is { } idpSecret)
    {
      exchange.Add(new("client_secret", idpSecret));
    }

    var exchanged = await PostFormAsync(enterprise.IdentityProviderTokenEndpoint, exchange, null, cancellationToken).ConfigureAwait(false);
    if (!string.Equals(exchanged.IssuedTokenType, IdJagTokenType, StringComparison.Ordinal) || exchanged.AccessToken is not { Length: > 0 } idJag)
    {
      throw new OAuthException("The identity provider did not return an identity assertion authorization grant.", "invalid_response");
    }

    var redeem = new List<KeyValuePair<string, string>>
    {
      new("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
      new("assertion", idJag),
      new("resource", _indicator),
    };
    if (requested is not null)
    {
      redeem.Add(new("scope", requested));
    }

    var identity = new ClientIdentity(enterprise.ClientId, enterprise.ClientSecret, null);
    var token = await TokenRequestAsync(server, redeem, identity, cancellationToken).ConfigureAwait(false);
    return ToRecord(token, issuer, identity with { Secret = null }, requested, requireBearer: true);
  }

  // ----- refresh ---------------------------------------------------------------------------------------------------------------------

  private async Task<string?> RefreshAsync(TokenRecord record, CancellationToken cancellationToken, bool retried = false)
  {
    var issuer = record.Issuer!;
    _indicator = record.Resource ?? _options.Canonical;
    // An issuer equal to the resource origin is the 2025-03-26 case: the server is its own authorization server.
    var legacy = string.Equals(issuer, _options.Resource.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal);
    var server = await DiscoverServerAsync(issuer, legacy, cancellationToken).ConfigureAwait(false);
    var identity = await ClientForRefreshAsync(record, issuer, cancellationToken).ConfigureAwait(false);
    var form = new List<KeyValuePair<string, string>>
    {
      new("grant_type", "refresh_token"),
      new("refresh_token", record.RefreshToken!),
      new("resource", record.Resource ?? _options.Canonical),
    };
    TokenResponse token;
    try
    {
      token = await TokenRequestAsync(server, form, identity, cancellationToken).ConfigureAwait(false);
    }
    catch (OAuthException ex) when (ex.Error is "invalid_grant" or "invalid_client")
    {
      // Another process may have rotated the refresh token first (the server then refuses ours as spent). If the store now holds a
      // different refresh token, that process's sign-in is the truth: adopt it rather than erasing it.
      if (!retried && await _options.Store.GetAsync(_tokenKey, cancellationToken).ConfigureAwait(false) is { RefreshToken: { } newer } stored
        && !string.Equals(newer, record.RefreshToken, StringComparison.Ordinal))
      {
        _record = stored;
        _loaded = true;
        return IsFresh(stored) ? stored.AccessToken : await RefreshAsync(stored, cancellationToken, retried: true).ConfigureAwait(false);
      }

      if (ex.Error == "invalid_client")
      {
        // The client registration itself is refused (deleted at the server, or its secret rotated): forget the stored one so the next
        // sign-in registers again instead of presenting the same rejected identity.
        await _options.Store.RemoveAsync("client:" + issuer, cancellationToken).ConfigureAwait(false);
      }

      // The refresh token is spent or revoked: forget it so it is not sent again.
      var cleared = new TokenRecord { Issuer = record.Issuer, ClientId = record.ClientId, Resource = record.Resource, Scope = record.Scope };
      _record = cleared;
      await _options.Store.SetAsync(_tokenKey, cleared, cancellationToken).ConfigureAwait(false);
      return null;
    }

    // OAuth 2.1 section 4.3.1: a rotated refresh token replaces the old one; if none is returned the old one stays.
    var updated = ToRecord(token, issuer, identity, record.Scope, requireBearer: true);
    if (updated.RefreshToken is null)
    {
      updated = new TokenRecord
      {
        Issuer = updated.Issuer,
        ClientId = updated.ClientId,
        AccessToken = updated.AccessToken,
        RefreshToken = record.RefreshToken,
        ExpiresAt = updated.ExpiresAt,
        Resource = updated.Resource,
        Scope = updated.Scope,
      };
    }

    _record = updated;
    await _options.Store.SetAsync(_tokenKey, updated, cancellationToken).ConfigureAwait(false);
    return updated.AccessToken;
  }

  private async Task<ClientIdentity> ClientForRefreshAsync(TokenRecord record, string issuer, CancellationToken cancellationToken)
  {
    if (_options.PreRegistered is { } fixedClient && (fixedClient.Issuer is null || string.Equals(fixedClient.Issuer, issuer, StringComparison.Ordinal)))
    {
      return new ClientIdentity(fixedClient.ClientId, fixedClient.ClientSecret, null);
    }

    var stored = await _options.Store.GetAsync("client:" + issuer, cancellationToken).ConfigureAwait(false);
    if (stored?.ClientId is not null && string.Equals(stored.Issuer, issuer, StringComparison.Ordinal))
    {
      return new ClientIdentity(stored.ClientId, stored.ClientSecret, null);
    }

    return new ClientIdentity(record.ClientId ?? throw new OAuthException("The stored authorization has no client.", "invalid_client"), null, null);
  }

  // ----- token endpoint --------------------------------------------------------------------------------------------------------------

  private sealed record TokenResponse(string? AccessToken, string? TokenType, string? IssuedTokenType, long? ExpiresIn, string? RefreshToken, string? Scope);

  private Task<TokenResponse> TokenRequestAsync(AuthorizationServerMetadata server, List<KeyValuePair<string, string>> form, ClientIdentity client, CancellationToken cancellationToken)
  {
    if (!OAuthUri.TryHttp(server.TokenEndpoint, _loopbackResource, out var endpoint))
    {
      throw new OAuthException("The authorization server has no usable token endpoint.", "invalid_server_metadata");
    }

    // RFC 8414 section 2: absent token_endpoint_auth_methods_supported means client_secret_basic.
    string? basic = null;
    var assertionAlreadyInForm = form.Exists(p => p.Key == "client_assertion");
    if (!assertionAlreadyInForm)
    {
      var methods = server.TokenEndpointAuthMethodsSupported;
      if (client.Secret is null)
      {
        form.Add(new("client_id", client.ClientId));
      }
      else if (client.PreferredMethod == "client_secret_post" || (methods is not null && !methods.Contains("client_secret_basic") && methods.Contains("client_secret_post")))
      {
        form.Add(new("client_id", client.ClientId));
        form.Add(new("client_secret", client.Secret));
      }
      else
      {
        // RFC 6749 section 2.3.1: the identifier and secret are form-encoded before they are joined.
        basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.EscapeDataString(client.ClientId).Replace("%20", "+", StringComparison.Ordinal) + ":" + Uri.EscapeDataString(client.Secret).Replace("%20", "+", StringComparison.Ordinal)));
      }
    }

    return PostFormAsync(endpoint, form, basic, cancellationToken);
  }

  private async Task<TokenResponse> PostFormAsync(Uri endpoint, List<KeyValuePair<string, string>> form, string? basicCredentials, CancellationToken cancellationToken)
  {
    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(form) };
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    if (basicCredentials is not null)
    {
      request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicCredentials);
    }

    var (status, body) = await FetchAsync(request, cancellationToken).ConfigureAwait(false);
    if (status != 200)
    {
      var error = ErrorOf(body);
      var text = body.Length == 0 ? null : RedactBasic(Redact(DescriptionOf(body), form), basicCredentials);
      throw new OAuthException(
        "The token endpoint refused the request (HTTP " + status.ToString(CultureInfo.InvariantCulture) + (error is null ? string.Empty : ", " + error) + (text is null ? string.Empty : ": " + text) + ").",
        error);
    }

    using var document = MetadataJson.TryParse(body) ?? throw new OAuthException("The token endpoint returned an unreadable answer.", "invalid_response");
    var root = document.RootElement;
    long? expiresIn = null;
    if (root.TryGetProperty("expires_in", out var expires) && expires.ValueKind == JsonValueKind.Number && expires.TryGetInt64(out var seconds) && seconds >= 0)
    {
      expiresIn = seconds;
    }

    return new TokenResponse(
      MetadataJson.GetString(root, "access_token"),
      MetadataJson.GetString(root, "token_type"),
      MetadataJson.GetString(root, "issued_token_type"),
      expiresIn,
      MetadataJson.GetString(root, "refresh_token"),
      MetadataJson.GetString(root, "scope"));
  }

  private TokenRecord ToRecord(TokenResponse token, string issuer, ClientIdentity client, string? requestedScope, bool requireBearer)
  {
    if (string.IsNullOrEmpty(token.AccessToken))
    {
      throw new OAuthException("The token endpoint returned no access token.", "invalid_response");
    }

    // RFC 6750: only the Bearer type is used here. Any other type (DPoP, MAC) would need proof this client does not make.
    if (requireBearer && !string.Equals(token.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
    {
      throw new OAuthException("The token endpoint issued a token of a type other than Bearer, which this client does not use.", "unsupported_token_type");
    }

    return new TokenRecord
    {
      Issuer = issuer,
      ClientId = client.ClientId,
      AccessToken = token.AccessToken,
      RefreshToken = token.RefreshToken,
      ExpiresAt = token.ExpiresIn is { } seconds ? _time.GetUtcNow().AddSeconds(seconds) : null,
      Resource = _indicator,
      Scope = requestedScope,
    };
  }

  // A hostile server can echo what it was sent into its error text; nothing secret from the request may come back out in a message.
  private static string? Redact(string? text, List<KeyValuePair<string, string>> form)
  {
    if (text is null)
    {
      return null;
    }

    foreach (var (name, value) in form)
    {
      if (value.Length >= 4 && name is "code" or "code_verifier" or "refresh_token" or "client_secret" or "assertion" or "client_assertion" or "subject_token")
      {
        text = text.Replace(value, "[redacted]", StringComparison.Ordinal);
      }
    }

    return text;
  }

  private static string? RedactBasic(string? text, string? basicCredentials)
  {
    if (text is null || basicCredentials is null)
    {
      return text;
    }

    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(basicCredentials));
    var colon = decoded.IndexOf(':', StringComparison.Ordinal);
    if (colon >= 0 && colon + 1 < decoded.Length)
    {
      var secret = Uri.UnescapeDataString(decoded[(colon + 1)..].Replace('+', ' '));
      text = text.Replace(secret, "[redacted]", StringComparison.Ordinal).Replace(decoded[(colon + 1)..], "[redacted]", StringComparison.Ordinal);
    }

    return text;
  }

  private static string? ErrorOf(byte[] body)
  {
    using var document = body.Length == 0 ? null : MetadataJson.TryParse(body);
    return document is null ? null : MetadataJson.GetString(document.RootElement, "error") is { } error ? AuthorizationResponse.Sanitize(error) : null;
  }

  private static string? DescriptionOf(byte[] body)
  {
    using var document = MetadataJson.TryParse(body);
    return document is null ? null : MetadataJson.GetString(document.RootElement, "error_description") is { } text ? AuthorizationResponse.Sanitize(text) : null;
  }

  // ----- HTTP ------------------------------------------------------------------------------------------------------------------------

  // One bounded request: never follows a redirect (a 3xx is returned as a status and treated as a failure by callers), never reads more
  // than MaxResponseBytes, never waits longer than RequestTimeout. Errors from the HTTP stack are replaced, not wrapped, because their
  // messages are not ours to vouch for.
  private async Task<(int Status, byte[] Body)> FetchAsync(HttpRequestMessage request, CancellationToken cancellationToken)
  {
    if (request.RequestUri is null || !OAuthUri.IsAllowedEndpoint(request.RequestUri, _loopbackResource))
    {
      throw new OAuthException("An OAuth endpoint is not an HTTPS URL.", "invalid_endpoint");
    }

    using var timeout = new CancellationTokenSource(_options.RequestTimeout, _time);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
    try
    {
      using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
      var status = (int)response.StatusCode;
      if (status is >= 300 and < 400)
      {
        return (status, []);
      }

      await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
      using var buffer = new MemoryStream();
      var chunk = new byte[8192];
      int read;
      while ((read = await stream.ReadAsync(chunk, linked.Token).ConfigureAwait(false)) > 0)
      {
        if (buffer.Length + read > _options.MaxResponseBytes)
        {
          throw new OAuthException("An OAuth response was larger than the allowed " + _options.MaxResponseBytes.ToString(CultureInfo.InvariantCulture) + " bytes.", "response_too_large");
        }

        buffer.Write(chunk, 0, read);
      }

      return (status, buffer.ToArray());
    }
    catch (HttpRequestException)
    {
      throw new OAuthException("An OAuth endpoint could not be reached.", "server_unreachable");
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
      throw new OAuthException("An OAuth endpoint did not answer in time.", "timeout");
    }
  }

  private static void Append(StringBuilder query, string name, string value)
  {
    if (query.Length > 0)
    {
      query.Append('&');
    }

    query.Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
  }

  private static string Base64Url(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);
}
