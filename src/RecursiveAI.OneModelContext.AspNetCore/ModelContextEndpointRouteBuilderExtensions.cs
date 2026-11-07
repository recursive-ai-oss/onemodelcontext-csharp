using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecursiveAI.OneModelContext.Auth;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.Hosting;

namespace RecursiveAI.OneModelContext.AspNetCore;

/// <summary>Serves a <see cref="ContextServer"/> on the ASP.NET Core endpoint routing pipeline.</summary>
public static class ModelContextEndpointRouteBuilderExtensions
{
  // The library's diagnostic events are process-wide, so one logging bridge per logger factory is enough however many endpoints are mapped.
  private static readonly ConditionalWeakTable<ILoggerFactory, IDisposable> s_logBridges = new();

  /// <summary>
  /// Maps a Streamable HTTP endpoint for <paramref name="server"/> at <paramref name="pattern"/>. One endpoint answers both protocol eras:
  /// 2026-07-28 requests and the older revisions' <c>initialize</c> sessions, GET stream and resumption.
  /// </summary>
  /// <param name="endpoints">The route builder, for example the <c>WebApplication</c>.</param>
  /// <param name="pattern">The route, for example <c>/mcp</c>. The endpoint accepts every HTTP method so that it can answer the unsupported ones itself.</param>
  /// <param name="server">The server to expose. It may be shared with other transports.</param>
  /// <param name="options">Limits and allow-lists; <c>null</c> for the defaults (loopback <c>Host</c> and <c>Origin</c> only, 8 MiB bodies).
  /// A server reached by a public name must list that name in <see cref="StreamableHttpOptions.AllowedHosts"/>, and a browser page's origin in <see cref="StreamableHttpOptions.AllowedOrigins"/>.</param>
  /// <returns>
  /// The builder for the endpoint. Conventions applied to it run in the host's pipeline before the protocol handler, so
  /// <c>RequireAuthorization()</c>, <c>RequireRateLimiting(...)</c>, <c>RequireCors(...)</c> and logging all apply. The authenticated
  /// <see cref="System.Security.Claims.ClaimsPrincipal"/> reaches handlers as <see cref="RequestContext.Principal"/>.
  /// </returns>
  /// <remarks>
  /// The endpoint is disposed, which ends legacy sessions and cancels running handlers, when the application begins to stop.
  /// Request cancellation maps through: a client that disconnects cancels the <see cref="RequestContext.CancellationToken"/> of its requests.
  /// Library diagnostics (handler faults, malformed messages) are written to <see cref="ILogger"/> under the category
  /// <c>RecursiveAI.OneModelContext</c>; they never contain tokens or message bodies.
  /// </remarks>
  /// <exception cref="ArgumentNullException"><paramref name="endpoints"/>, <paramref name="pattern"/> or <paramref name="server"/> is null.</exception>
  public static IEndpointConventionBuilder MapModelContext(
    this IEndpointRouteBuilder endpoints,
    string pattern,
    ContextServer server,
    StreamableHttpOptions? options = null)
  {
    ArgumentNullException.ThrowIfNull(endpoints);
    ArgumentNullException.ThrowIfNull(pattern);
    ArgumentNullException.ThrowIfNull(server);

    var endpoint = new StreamableHttpEndpoint(server, options);
    var services = endpoints.ServiceProvider;
    services.GetService<IHostApplicationLifetime>()?.ApplicationStopping.Register(endpoint.Dispose);
    if (services.GetService<ILoggerFactory>() is { } factory)
    {
      s_logBridges.GetValue(factory, static f => ContextDiagnostics.Subscribe(CreateBridge(f.CreateLogger("RecursiveAI.OneModelContext"))));
    }

    // The protected resource metadata is public by design (RFC 9728), so it is mapped beside the endpoint and outside its authorization.
    if (options?.ResourceServer is { } resourceServer)
    {
      endpoints.MapProtectedResourceMetadata(resourceServer, endpoint);
    }

    // A Server Card published with UseServerCard is public by design (browsers read it from other origins), so it is mapped beside the
    // endpoint, outside its authorization. Without a card the endpoint answers 404.
    endpoints.MapMethods(pattern.TrimEnd('/') + "/server-card", ["GET", "OPTIONS"], async context => await endpoint.HandleAsync(new AspNetCoreExchange(context)).ConfigureAwait(false)).AllowAnonymous();

    return endpoints.Map(pattern, async context => await endpoint.HandleAsync(new AspNetCoreExchange(context)).ConfigureAwait(false));
  }

  private const string Template = "{Kind}: {Message} (method {Method})";
  private static readonly Action<ILogger, DiagnosticKind, string, string?, Exception?> s_error = LoggerMessage.Define<DiagnosticKind, string, string?>(LogLevel.Error, new EventId(1, "Fault"), Template);
  private static readonly Action<ILogger, DiagnosticKind, string, string?, Exception?> s_warning = LoggerMessage.Define<DiagnosticKind, string, string?>(LogLevel.Warning, new EventId(2, "InvalidMessage"), Template);
  private static readonly Action<ILogger, DiagnosticKind, string, string?, Exception?> s_debug = LoggerMessage.Define<DiagnosticKind, string, string?>(LogLevel.Debug, new EventId(3, "Note"), Template);

  private static Action<DiagnosticEvent> CreateBridge(ILogger logger) => e =>
  {
    var write = e.Kind switch
    {
      DiagnosticKind.HandlerFault or DiagnosticKind.AuditFault => s_error,
      DiagnosticKind.InvalidMessage or DiagnosticKind.AuthRejected => s_warning,
      _ => s_debug,
    };
    write(logger, e.Kind, e.Message, e.Method, e.Exception);
  };

  /// <summary>
  /// Maps the protected resource metadata document (RFC 9728) of <paramref name="resourceServer"/> at its well-known paths. <see cref="MapModelContext"/>
  /// does this itself when <see cref="StreamableHttpOptions.ResourceServer"/> is set; call this only for a resource server served by your own handler.
  /// The routes are anonymous: clients read the document before they have a token.
  /// </summary>
  /// <param name="endpoints">The route builder.</param>
  /// <param name="resourceServer">The resource server whose document to serve.</param>
  /// <param name="endpoint">The endpoint that answers the document; null builds a private one with the default options and this resource server.</param>
  public static void MapProtectedResourceMetadata(this IEndpointRouteBuilder endpoints, ResourceServer resourceServer, StreamableHttpEndpoint? endpoint = null)
  {
    ArgumentNullException.ThrowIfNull(endpoints);
    ArgumentNullException.ThrowIfNull(resourceServer);
    var handler = endpoint ?? new StreamableHttpEndpoint(new ContextServer("metadata", "1"), new StreamableHttpOptions { ResourceServer = resourceServer });
    foreach (var path in new[] { resourceServer.PathInsertedMetadataPath, ResourceServer.RootMetadataPath }.Distinct(StringComparer.Ordinal))
    {
      endpoints.MapGet(path, async context => await handler.HandleAsync(new AspNetCoreExchange(context)).ConfigureAwait(false)).AllowAnonymous();
    }
  }
}
