using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using RecursiveAI.OneModelContext.Auth.Server;

namespace RecursiveAI.OneModelContext.AspNetCore;

/// <summary>Serves an <see cref="AuthorizationServer"/> on the ASP.NET Core endpoint routing pipeline.</summary>
public static class AuthorizationServerEndpointRouteBuilderExtensions
{
  /// <summary>
  /// Maps every endpoint of <paramref name="server"/>: the metadata documents (including the <c>/.well-known/</c> forms), authorization,
  /// token, revocation, key set and, when enabled, registration. They accept every HTTP method so that the server can answer the
  /// unsupported ones itself, and they are anonymous: an authorization server is called by clients that have no token yet.
  /// </summary>
  /// <param name="endpoints">The route builder, for example the <c>WebApplication</c>.</param>
  /// <param name="server">The server. Dispose it when the application stops.</param>
  /// <param name="configure">Applies conventions to each endpoint, for example <c>e => e.RequireRateLimiting("oauth")</c>; null for none.
  /// Do not require authorization on them.</param>
  /// <remarks>
  /// Put a rate limit on the token, registration and authorization endpoints in the host: the server bounds each request but does not
  /// count requests. The user is signed in by the host's own middleware and cookie; the <see cref="AuthorizationServerOptions.AuthorizeUser"/>
  /// callback reads it from <see cref="AuthorizationRequestContext.Exchange"/>.
  /// </remarks>
  /// <exception cref="ArgumentNullException">An argument is null.</exception>
  public static void MapAuthorizationServer(this IEndpointRouteBuilder endpoints, AuthorizationServer server, Action<IEndpointConventionBuilder>? configure = null)
  {
    ArgumentNullException.ThrowIfNull(endpoints);
    ArgumentNullException.ThrowIfNull(server);
    foreach (var path in server.Paths)
    {
      var builder = endpoints.Map(path, async context => await server.HandleAsync(new AspNetCoreExchange(context)).ConfigureAwait(false)).AllowAnonymous();
      configure?.Invoke(builder);
    }
  }
}
