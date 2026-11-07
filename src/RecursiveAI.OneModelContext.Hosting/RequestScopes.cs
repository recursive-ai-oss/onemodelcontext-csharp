using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace RecursiveAI.OneModelContext.Hosting;

/// <summary>Reaches the container's per-request scope from inside a tool, resource or prompt.</summary>
public static class RequestServicesExtensions
{
  /// <summary>
  /// The service provider of the scope created for this request. Scoped services (a database context, say) resolve from it and are
  /// disposed when the request ends.
  /// </summary>
  /// <param name="context">The request context injected into the handler.</param>
  /// <returns>The request's scoped provider.</returns>
  /// <exception cref="InvalidOperationException">The server was not registered with <c>AddContextServer</c>, so no scope exists.</exception>
  public static IServiceProvider GetRequestServices(this RequestContext context)
  {
    ArgumentNullException.ThrowIfNull(context);
    return RequestScopes.Current ?? throw new InvalidOperationException("No request scope: register the server with AddContextServer to get one per request.");
  }

  /// <summary>Resolves a required service from the request's scope.</summary>
  /// <typeparam name="T">The service type.</typeparam>
  /// <param name="context">The request context.</param>
  /// <returns>The service.</returns>
  /// <exception cref="InvalidOperationException">No request scope, or the service is not registered.</exception>
  public static T GetRequiredService<T>(this RequestContext context)
    where T : notnull =>
    context.GetRequestServices().GetRequiredService<T>();
}

/// <summary>
/// Wraps every registered handler so each request runs in its own DI scope. The generated invokers capture their tool class once, so
/// the scope is reached through an ambient value that flows with the request's async calls, not through the constructor.
/// </summary>
internal static class RequestScopes
{
  private static readonly AsyncLocal<IServiceProvider?> s_current = new();

  public static IServiceProvider? Current => s_current.Value;

  public static void Apply(ContextServer server, IServiceProvider root)
  {
    var scopes = root.GetRequiredService<IServiceScopeFactory>();
    foreach (var definition in server.Tools.Definitions)
    {
      if (server.Tools.TryGet(definition.Name, out var d, out var inner))
      {
        server.Tools.Add(d, async (arguments, context) =>
        {
          await using var scope = Enter(scopes);
          return await inner(arguments, context).ConfigureAwait(false);
        });
      }
    }

    foreach (var definition in server.Resources.Definitions)
    {
      if (server.Resources.TryGet(definition.Uri, out var d, out var inner))
      {
        server.Resources.Add(d, async (uri, context) =>
        {
          await using var scope = Enter(scopes);
          return await inner(uri, context).ConfigureAwait(false);
        });
      }
    }

    foreach (var definition in server.ResourceTemplates.Definitions)
    {
      if (server.ResourceTemplates.TryGet(definition.UriTemplate, out var d, out var inner))
      {
        server.ResourceTemplates.Add(d, async (uri, context) =>
        {
          await using var scope = Enter(scopes);
          return await inner(uri, context).ConfigureAwait(false);
        });
      }
    }

    foreach (var definition in server.Prompts.Definitions)
    {
      if (server.Prompts.TryGet(definition.Name, out var d, out var inner))
      {
        server.Prompts.Add(d, async (arguments, context) =>
        {
          await using var scope = Enter(scopes);
          return await inner(arguments, context).ConfigureAwait(false);
        });
      }
    }
  }

  private static ScopeHandle Enter(IServiceScopeFactory factory)
  {
    var previous = s_current.Value;
    var scope = factory.CreateAsyncScope();
    s_current.Value = scope.ServiceProvider;
    return new ScopeHandle(scope, previous);
  }

  private readonly struct ScopeHandle(AsyncServiceScope scope, IServiceProvider? previous) : IAsyncDisposable
  {
    public ValueTask DisposeAsync()
    {
      s_current.Value = previous;
      return scope.DisposeAsync();
    }
  }
}
