using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.Http;

namespace RecursiveAI.OneModelContext.Auth;

/// <summary>Settings of a <see cref="ResourceServer"/>.</summary>
/// <param name="Resource">
/// The canonical URI of this MCP server, for example <c>https://mcp.example.com/mcp</c> (RFC 8707 section 2; RFC 9728 <c>resource</c>).
/// It is configured, never taken from a request's <c>Host</c> header. Tokens must have been issued for it. HTTPS, or HTTP on a loopback host.
/// </param>
/// <param name="Validator">Decides whether a bearer token is valid for <paramref name="Resource"/>.</param>
public sealed record ResourceServerOptions(Uri Resource, ITokenValidator Validator)
{
  /// <summary>The authorization servers clients may get tokens from (<c>authorization_servers</c>, RFC 9728). Each is an issuer identifier URL.</summary>
  public IReadOnlyList<string> AuthorizationServers { get; init; } = [];

  /// <summary>Scopes the server understands (<c>scopes_supported</c>). Clients without a challenge scope request these, so list the minimal set for basic use. Do not list <c>offline_access</c>.</summary>
  public IReadOnlyList<string> ScopesSupported { get; init; } = [];

  /// <summary>Scopes every request needs, whatever it does. A valid token without them gets 403 <c>insufficient_scope</c>, and 401 challenges name them. Default none.</summary>
  public IReadOnlyList<string> RequiredScopes { get; init; } = [];

  /// <summary>Which scopes imply others, for deciding whether a token is sufficient. Default none: scopes match exactly.</summary>
  public ScopeHierarchy? ScopeHierarchy { get; init; }

  /// <summary>A human-readable name for the server (<c>resource_name</c>).</summary>
  public string? ResourceName { get; init; }

  /// <summary>A page that documents the server (<c>resource_documentation</c>).</summary>
  public Uri? ResourceDocumentation { get; init; }

  /// <summary>
  /// The URL of the metadata document to put in challenges, when it is served somewhere other than the default
  /// (<c>/.well-known/oauth-protected-resource</c> inserted before the resource's path). Set it behind a proxy that rewrites paths.
  /// </summary>
  public Uri? MetadataUrl { get; init; }

  /// <summary>Longest <c>Authorization</c> credential examined, in characters; longer is refused unread. Default 8192.</summary>
  public int MaxTokenLength { get; init; } = 8192;
}

/// <summary>The outcome of authenticating or authorizing a request.</summary>
public sealed class AuthenticationOutcome
{
  internal AuthenticationOutcome(ClaimsPrincipal principal) => Principal = principal;

  internal AuthenticationOutcome(int status, string challenge, string? error, string? description)
  {
    StatusCode = status;
    WwwAuthenticate = challenge;
    Error = error;
    Description = description;
  }

  /// <summary>Whether the request may proceed.</summary>
  public bool Succeeded => Principal is not null;

  /// <summary>The authenticated caller, when <see cref="Succeeded"/>.</summary>
  public ClaimsPrincipal? Principal { get; }

  /// <summary>The HTTP status to answer with when the request may not proceed: 400, 401, 403 or 503.</summary>
  public int StatusCode { get; }

  /// <summary>The <c>WWW-Authenticate</c> value (RFC 6750 section 3) to send with <see cref="StatusCode"/>; null for 503.</summary>
  public string? WwwAuthenticate { get; }

  /// <summary>The OAuth error code, for example <c>invalid_token</c>; null when the request carried no credentials.</summary>
  public string? Error { get; }

  /// <summary>A short explanation that is safe to show the caller. It never contains the token.</summary>
  public string? Description { get; }
}

/// <summary>
/// The OAuth 2.1 resource server role of an MCP server: protected resource metadata (RFC 9728), bearer challenges (RFC 6750), token
/// validation through an <see cref="ITokenValidator"/>, audience binding (RFC 8707) and scope checks with step-up challenges. Set it on
/// <see cref="Hosting.StreamableHttpOptions.ResourceServer"/> and the HTTP endpoint enforces it; the ASP.NET Core adapter also exposes it
/// as an authentication handler.
/// </summary>
/// <remarks>
/// A token is accepted only if the validator says it was issued for <see cref="ResourceServerOptions.Resource"/>. Tokens are never
/// forwarded: handlers receive a principal, not the credential.
/// </remarks>
public sealed class ResourceServer
{
  private const string WellKnown = "/.well-known/oauth-protected-resource";
  private readonly ResourceServerOptions _options;
  private readonly string _resource;
  private readonly byte[] _metadataJson;

  /// <summary>Creates a resource server.</summary>
  /// <param name="options">The settings.</param>
  /// <exception cref="ArgumentException">The resource is not an absolute HTTPS URI (HTTP on a loopback host is allowed) or has a fragment, or an authorization server is not an absolute HTTPS URL.</exception>
  public ResourceServer(ResourceServerOptions options)
  {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(options.Validator);
    var canonical = ResourceIdentifier.Canonicalize(options.Resource?.OriginalString);
    if (canonical is null || !(options.Resource!.Scheme == Uri.UriSchemeHttps || options.Resource.IsLoopback))
    {
      throw new ArgumentException("The resource must be an absolute https URI (http only on a loopback host) without a fragment.", nameof(options));
    }

    foreach (var issuer in options.AuthorizationServers)
    {
      if (!Uri.TryCreate(issuer, UriKind.Absolute, out var uri) || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
      {
        throw new ArgumentException("An authorization server must be an absolute https issuer URL.", nameof(options));
      }
    }

    _options = options;
    _resource = canonical;
    var parsed = new Uri(canonical);
    var pathSuffix = parsed.AbsolutePath == "/" ? string.Empty : parsed.AbsolutePath;
    var origin = parsed.GetLeftPart(UriPartial.Authority);

    // RFC 9728 section 3.1: the well-known segment goes between the host and the path of the resource identifier.
    PathInsertedMetadataPath = WellKnown + pathSuffix;
    MetadataUrl = options.MetadataUrl?.AbsoluteUri ?? origin + PathInsertedMetadataPath;
    _metadataJson = BuildMetadata();
  }

  /// <summary>The canonical resource URI tokens must have been issued for.</summary>
  public string Resource => _resource;

  /// <summary>The URL of the metadata document, as challenges give it to clients.</summary>
  public string MetadataUrl { get; }

  /// <summary>The path-suffixed metadata path (<c>/.well-known/oauth-protected-resource/mcp</c> for a resource at <c>/mcp</c>); the same as <see cref="RootMetadataPath"/> when the resource has no path.</summary>
  public string PathInsertedMetadataPath { get; }

  /// <summary>The root metadata path, <c>/.well-known/oauth-protected-resource</c>. It serves the same document, for clients that try the root form.</summary>
  public static string RootMetadataPath => WellKnown;

  /// <summary>The settings.</summary>
  public ResourceServerOptions Options => _options;

  /// <summary>The metadata document (RFC 9728 section 2) as UTF-8 JSON.</summary>
  public ReadOnlyMemory<byte> MetadataDocument => _metadataJson;

  /// <summary>Whether <paramref name="path"/> is one of the paths the metadata document is served at.</summary>
  /// <param name="path">A request path.</param>
  public bool IsMetadataPath(string path) =>
    string.Equals(path, PathInsertedMetadataPath, StringComparison.Ordinal) || string.Equals(path, WellKnown, StringComparison.Ordinal);

  /// <summary>
  /// Authenticates a request from its <c>Authorization</c> headers: finds the Bearer credential, validates it for this resource and
  /// checks the server-wide required scopes. It never throws on bad input.
  /// </summary>
  /// <param name="authorizationHeaders">Every <c>Authorization</c> header value of the request.</param>
  /// <param name="cancellationToken">Cancels validation.</param>
  /// <returns>
  /// Success with the principal; otherwise the status and challenge to send: 400 <c>invalid_request</c> for several credentials, 401 with
  /// no error code when none was sent (RFC 6750 section 3.1), 401 <c>invalid_token</c> when it is not valid for this server, 403
  /// <c>insufficient_scope</c> when it lacks the server-wide scopes, 503 when the issuer cannot be reached.
  /// </returns>
  public async ValueTask<AuthenticationOutcome> AuthenticateAsync(IReadOnlyList<string> authorizationHeaders, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(authorizationHeaders);
    if (authorizationHeaders.Count > 1)
    {
      return Reject(400, "invalid_request", "Send one Authorization header.", null);
    }

    var token = authorizationHeaders.Count == 1 ? ExtractBearer(authorizationHeaders[0]) : null;
    if (token is null)
    {
      // No credentials, or a scheme other than Bearer: challenge without an error code (RFC 6750 section 3.1).
      return Reject(401, null, null, _options.RequiredScopes);
    }

    if (token.Length > _options.MaxTokenLength)
    {
      return Reject(401, "invalid_token", "The token is not valid.", _options.RequiredScopes);
    }

    TokenValidationResult result;
    try
    {
      result = await _options.Validator.ValidateAsync(token, new TokenValidationRequest(_resource), cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      throw;
    }
    catch (Exception ex)
    {
      // Only the type is reported: a validator's message could echo the credential.
      ContextDiagnostics.Publish(DiagnosticKind.AuthRejected, "The token validator failed with " + ex.GetType().Name + ".");
      return Unavailable();
    }

    // A validator that echoed the credential into its explanation must not leak it to the caller or to diagnostics.
    var reason = Scrub(result?.Description, token);
    if (result is null || (!result.IsValid && !result.Unavailable))
    {
      ContextDiagnostics.Publish(DiagnosticKind.AuthRejected, "A bearer token was rejected: " + (reason ?? "not valid") + ".");
      return Reject(401, "invalid_token", Clean(reason) ?? "The token is not valid.", _options.RequiredScopes);
    }

    if (result.Unavailable)
    {
      ContextDiagnostics.Publish(DiagnosticKind.AuthRejected, "A bearer token could not be checked: " + reason + ".");
      return Unavailable();
    }

    return Authorize(result.Principal!, _options.RequiredScopes);
  }

  /// <summary>
  /// Checks that an authenticated caller holds the scopes an operation needs, honouring the scope hierarchy.
  /// </summary>
  /// <param name="principal">The caller, as returned by <see cref="AuthenticateAsync"/> (its <see cref="AuthClaimTypes.Scope"/> claims are the granted scopes).</param>
  /// <param name="requiredScopes">What the operation needs. Null or empty always succeeds.</param>
  /// <returns>Success, or 403 with <c>insufficient_scope</c> naming all of the required scopes in one challenge, so a client can step up once.</returns>
  public AuthenticationOutcome Authorize(ClaimsPrincipal principal, IReadOnlyList<string>? requiredScopes)
  {
    ArgumentNullException.ThrowIfNull(principal);
    if (requiredScopes is null || requiredScopes.Count == 0)
    {
      return new AuthenticationOutcome(principal);
    }

    var granted = AuthClaimTypes.GetScopes(principal);
    foreach (var required in requiredScopes)
    {
      var ok = _options.ScopeHierarchy is { } hierarchy ? hierarchy.Satisfies(granted, required) : Contains(granted, required);
      if (!ok)
      {
        return Reject(403, "insufficient_scope", "The token lacks a scope this operation needs.", requiredScopes);
      }
    }

    return new AuthenticationOutcome(principal);
  }

  /// <summary>The challenge for a request that carried no usable credentials (what an authentication scheme sends when asked to challenge).</summary>
  public AuthenticationOutcome Challenge() => Reject(401, null, null, _options.RequiredScopes);

  /// <summary>The 403 for an authenticated caller that a host policy refused: <c>insufficient_scope</c> with the server-wide scopes, if any.</summary>
  public AuthenticationOutcome Forbidden() => Reject(403, "insufficient_scope", "The token is not permitted to do this.", _options.RequiredScopes);

  private static bool Contains(IReadOnlyList<string> granted, string required)
  {
    foreach (var scope in granted)
    {
      if (string.Equals(scope, required, StringComparison.Ordinal))
      {
        return true;
      }
    }

    return false;
  }

  private static AuthenticationOutcome Unavailable() => new(503, string.Empty, "temporarily_unavailable", "The token could not be checked now.");

  private AuthenticationOutcome Reject(int status, string? error, string? description, IReadOnlyList<string>? scopes)
  {
    // RFC 6750 section 3: Bearer with auth-params. resource_metadata is from RFC 9728 section 5.1.
    var sb = new StringBuilder("Bearer");
    var first = true;
    void Param(string name, string value)
    {
      sb.Append(first ? " " : ", ").Append(name).Append("=\"").Append(value).Append('"');
      first = false;
    }

    if (error is not null)
    {
      Param("error", error);
    }

    if (description is not null && error is not null)
    {
      Param("error_description", Clean(description)!);
    }

    if (scopes is { Count: > 0 })
    {
      Param("scope", Clean(string.Join(' ', scopes))!);
    }

    Param("resource_metadata", Clean(MetadataUrl)!);
    return new AuthenticationOutcome(status, sb.ToString(), error, description);
  }

  private static string? Scrub(string? text, string token) =>
    text is null ? null : text.Replace(token, "[token]", StringComparison.Ordinal);

  // A quoted-string cannot hold a quote, a backslash or a control character without escaping; dropping them is simpler and loses nothing here.
  private static string? Clean(string? value)
  {
    if (value is null)
    {
      return null;
    }

    var sb = new StringBuilder(Math.Min(value.Length, 256));
    foreach (var c in value)
    {
      if (sb.Length >= 256)
      {
        break;
      }

      sb.Append(c is '"' or '\\' || c < ' ' || c == 0x7f ? ' ' : c);
    }

    return sb.ToString();
  }

  // RFC 6750 section 2.1: the scheme name is case-insensitive (RFC 9110 section 11.1); one space, then the token (token68 characters).
  private static string? ExtractBearer(string header)
  {
    const string Scheme = "Bearer";
    if (header.Length <= Scheme.Length + 1 || !header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) || header[Scheme.Length] != ' ')
    {
      return null;
    }

    var token = header.AsSpan(Scheme.Length + 1).Trim(' ');
    if (token.Length == 0)
    {
      return null;
    }

    foreach (var c in token)
    {
      // token68 = 1*( ALPHA / DIGIT / "-" / "." / "_" / "~" / "+" / "/" ) *"="
      if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '+' or '/' or '='))
      {
        return null;
      }
    }

    return token.ToString();
  }

  private byte[] BuildMetadata()
  {
    using var buffer = new System.IO.MemoryStream();
    using (var w = new Utf8JsonWriter(buffer))
    {
      w.WriteStartObject();
      w.WriteString("resource", _resource);
      if (_options.AuthorizationServers.Count > 0)
      {
        w.WriteStartArray("authorization_servers");
        foreach (var issuer in _options.AuthorizationServers)
        {
          w.WriteStringValue(issuer);
        }

        w.WriteEndArray();
      }

      if (_options.ScopesSupported.Count > 0)
      {
        w.WriteStartArray("scopes_supported");
        foreach (var scope in _options.ScopesSupported)
        {
          w.WriteStringValue(scope);
        }

        w.WriteEndArray();
      }

      // RFC 9728 section 2: the token is sent in the Authorization header only (RFC 6750 section 2.1); never in a query or form.
      w.WriteStartArray("bearer_methods_supported");
      w.WriteStringValue("header");
      w.WriteEndArray();
      if (_options.ResourceName is not null)
      {
        w.WriteString("resource_name", _options.ResourceName);
      }

      if (_options.ResourceDocumentation is not null)
      {
        w.WriteString("resource_documentation", _options.ResourceDocumentation.AbsoluteUri);
      }

      w.WriteEndObject();
    }

    return buffer.ToArray();
  }
}
