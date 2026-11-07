using System;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RecursiveAI.OneModelContext.Auth;

namespace RecursiveAI.OneModelContext.AspNetCore;

/// <summary>Settings of the resource server authentication scheme.</summary>
public sealed class ResourceServerAuthenticationOptions : AuthenticationSchemeOptions
{
  /// <summary>The resource server whose validator authenticates requests and whose metadata URL and scopes appear in challenges. Required.</summary>
  public ResourceServer? ResourceServer { get; set; }
}

/// <summary>Names used with the resource server authentication scheme.</summary>
public static class ResourceServerDefaults
{
  /// <summary>The default scheme name, <c>OneModelContext</c>.</summary>
  public const string AuthenticationScheme = "OneModelContext";
}

/// <summary>
/// Authenticates bearer tokens with a <see cref="ResourceServer"/> so that the framework's authorization (<c>RequireAuthorization</c>,
/// policies, <c>[Authorize]</c>) works on the protocol endpoint. A failed request is challenged with the same <c>WWW-Authenticate</c> values
/// as the endpoint's own enforcement: <c>resource_metadata</c>, the required scope, and <c>error</c> for a bad token.
/// </summary>
public sealed class ResourceServerAuthenticationHandler(
  IOptionsMonitor<ResourceServerAuthenticationOptions> options,
  ILoggerFactory logger,
  UrlEncoder encoder)
  : AuthenticationHandler<ResourceServerAuthenticationOptions>(options, logger, encoder)
{
  private const string OutcomeKey = "RecursiveAI.OneModelContext.AuthenticationOutcome";

  private ResourceServer Server => Options.ResourceServer ?? throw new InvalidOperationException("ResourceServerAuthenticationOptions.ResourceServer is not set.");

  /// <inheritdoc />
  protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
  {
    var headers = Request.Headers.Authorization;
    if (headers.Count == 0)
    {
      return AuthenticateResult.NoResult();
    }

    var values = new string[headers.Count];
    for (var i = 0; i < values.Length; i++)
    {
      values[i] = headers[i] ?? string.Empty;
    }

    var outcome = await Server.AuthenticateAsync(values, Context.RequestAborted).ConfigureAwait(false);
    if (outcome.Succeeded)
    {
      return AuthenticateResult.Success(new AuthenticationTicket(outcome.Principal!, Scheme.Name));
    }

    Context.Items[OutcomeKey] = outcome;

    // A scheme other than Bearer is somebody else's credential: not ours to fail.
    return outcome.Error is null ? AuthenticateResult.NoResult() : AuthenticateResult.Fail(outcome.Description ?? "The token is not valid.");
  }

  /// <inheritdoc />
  protected override Task HandleChallengeAsync(AuthenticationProperties properties)
  {
    var outcome = Context.Items.TryGetValue(OutcomeKey, out var stored) && stored is AuthenticationOutcome o ? o : Server.Challenge();
    Write(outcome);
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
  {
    // A policy refused an authenticated caller. Which scope would help is not known here; the endpoint names it when a tool needs one.
    Write(Server.Forbidden());
    return Task.CompletedTask;
  }

  private void Write(AuthenticationOutcome outcome)
  {
    Response.StatusCode = outcome.StatusCode;
    if (outcome.WwwAuthenticate is { Length: > 0 } challenge)
    {
      Response.Headers.WWWAuthenticate = challenge;
    }

    Response.Headers.CacheControl = "no-store";
  }
}

/// <summary>Registers the resource server authentication scheme.</summary>
public static class ResourceServerAuthenticationExtensions
{
  /// <summary>
  /// Adds bearer token authentication backed by <paramref name="resourceServer"/>. Pair it with
  /// <c>MapModelContext(...).RequireAuthorization()</c>; the same resource server goes in <see cref="RecursiveAI.OneModelContext.Hosting.StreamableHttpOptions.ResourceServer"/>
  /// so the endpoint serves the metadata and enforces per-tool scopes.
  /// </summary>
  /// <param name="builder">The authentication builder.</param>
  /// <param name="resourceServer">The resource server.</param>
  /// <param name="scheme">The scheme name; <see cref="ResourceServerDefaults.AuthenticationScheme"/> by default.</param>
  public static AuthenticationBuilder AddResourceServer(this AuthenticationBuilder builder, ResourceServer resourceServer, string scheme = ResourceServerDefaults.AuthenticationScheme)
  {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentNullException.ThrowIfNull(resourceServer);
    return builder.AddScheme<ResourceServerAuthenticationOptions, ResourceServerAuthenticationHandler>(scheme, options => options.ResourceServer = resourceServer);
  }
}
