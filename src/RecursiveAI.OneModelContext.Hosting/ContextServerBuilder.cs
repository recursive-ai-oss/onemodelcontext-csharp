using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace RecursiveAI.OneModelContext.Hosting;

/// <summary>
/// Collects what the host's <see cref="ContextServer"/> is made of. Returned by
/// <see cref="ContextHostingExtensions.AddContextServer(IServiceCollection, string?, string?, Func{IServiceProvider, ContextServerOptions}?)"/>.
/// </summary>
/// <remarks>Everything added here runs once, in order, when the server is first resolved from the container.</remarks>
public sealed class ContextServerBuilder
{
  private readonly ContextServerSetup _setup;

  internal ContextServerBuilder(IServiceCollection services, ContextServerSetup setup)
  {
    Services = services;
    _setup = setup;
  }

  /// <summary>The service collection the server is registered in.</summary>
  public IServiceCollection Services { get; }

  /// <summary>
  /// Adds the tools, resources and prompts of a generated tool class. The class is created by the container
  /// (<see cref="ActivatorUtilities"/>), so its constructor can take any registered service, and lives for the life of the server.
  /// </summary>
  /// <typeparam name="T">A partial class that declares <c>[Tool]</c>, <c>[Resource]</c> or <c>[Prompt]</c> methods; the generator makes it an <see cref="IContextRegistrar"/>.</typeparam>
  /// <remarks>
  /// A service the class needs per request (a scoped one) is not a constructor parameter: ask for it through
  /// <see cref="RequestServicesExtensions.GetRequestServices(RequestContext)"/>. If <typeparamref name="T"/> is already registered in the
  /// container, that registration is used instead of creating a new instance.
  /// </remarks>
  /// <returns>This builder.</returns>
  public ContextServerBuilder AddTools<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
    where T : class, IContextRegistrar
  {
    _setup.Add((server, provider) =>
    {
      var tools = provider.GetService<T>() ?? ActivatorUtilities.CreateInstance<T>(provider);
      tools.AddTo(server);
    });
    return this;
  }

  /// <summary>Adds a generated tool class created by <paramref name="factory"/>, with no reflection at all.</summary>
  /// <param name="factory">Creates the instance from the container.</param>
  /// <returns>This builder.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
  public ContextServerBuilder AddTools(Func<IServiceProvider, IContextRegistrar> factory)
  {
    ArgumentNullException.ThrowIfNull(factory);
    _setup.Add((server, provider) => factory(provider).AddTo(server));
    return this;
  }

  /// <summary>Adds a tool class you already have.</summary>
  /// <param name="tools">The instance.</param>
  /// <returns>This builder.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="tools"/> is null.</exception>
  public ContextServerBuilder AddTools(IContextRegistrar tools)
  {
    ArgumentNullException.ThrowIfNull(tools);
    _setup.Add((server, _) => tools.AddTo(server));
    return this;
  }

  /// <summary>Configures the server directly, for dynamic registration (<c>server.Tools.Add</c>) and <see cref="ContextServer.Instructions"/>.</summary>
  /// <param name="configure">Called with the server and the container, once.</param>
  /// <returns>This builder.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
  public ContextServerBuilder Configure(Action<ContextServer, IServiceProvider> configure)
  {
    ArgumentNullException.ThrowIfNull(configure);
    _setup.Add(configure);
    return this;
  }

  /// <summary>
  /// Serves the server on standard input and output while the host runs. Standard output carries protocol only: a host that
  /// logs to the console must send the console logger to standard error (<c>LogToStandardErrorThreshold</c>).
  /// The host stops when the client closes the stream.
  /// </summary>
  /// <returns>This builder.</returns>
  public ContextServerBuilder UseStdio() => UseTransport(static _ => ContextTransport.Stdio());

  /// <summary>Serves the server over a transport of your choosing (a duplex stream, a pipe) while the host runs. The host stops when the transport closes.</summary>
  /// <param name="transport">Creates the transport when the host starts.</param>
  /// <returns>This builder.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="transport"/> is null.</exception>
  public ContextServerBuilder UseTransport(Func<IServiceProvider, ContextTransport> transport)
  {
    ArgumentNullException.ThrowIfNull(transport);
    Services.AddSingleton<IHostedService>(provider => new ContextTransportHostedService(
      provider.GetRequiredService<ContextServer>(),
      () => transport(provider),
      provider.GetService<IHostApplicationLifetime>()));
    return this;
  }

  /// <summary>
  /// Serves Streamable HTTP on the built-in listener while the host runs: loopback only, with the listener's default limits and
  /// Origin allow-list. The bound address is available from <see cref="ContextListener.Started"/>.
  /// </summary>
  /// <param name="port">The port; 0 picks a free one. Default 3001, like <see cref="ModelContext.ServeHttpAsync(ContextServer, int, string, Action{Uri}?, Auth.ResourceServer?, System.Threading.CancellationToken)"/>.</param>
  /// <param name="path">The endpoint path, starting with '/'. Default "/mcp".</param>
  /// <param name="resourceServer">Token validation for the endpoint; null for none.</param>
  /// <returns>This builder.</returns>
  /// <exception cref="ArgumentException"><paramref name="path"/> does not start with '/'.</exception>
  public ContextServerBuilder UseListener(int port = 3001, string path = "/mcp", Auth.ResourceServer? resourceServer = null)
  {
    if (string.IsNullOrEmpty(path) || path[0] != '/')
    {
      throw new ArgumentException("The path starts with '/'.", nameof(path));
    }

    Services.TryAddSingleton(provider => new ContextListener(provider.GetRequiredService<ContextServer>(), port, path, resourceServer));
    Services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ContextListener>());
    return this;
  }
}

/// <summary>The ordered registrations the container turns into one <see cref="ContextServer"/>.</summary>
internal sealed class ContextServerSetup
{
  private readonly List<Action<ContextServer, IServiceProvider>> _steps = [];

  public void Add(Action<ContextServer, IServiceProvider> step)
  {
    lock (_steps)
    {
      _steps.Add(step);
    }
  }

  public Action<ContextServer, IServiceProvider>[] Steps()
  {
    lock (_steps)
    {
      return [.. _steps];
    }
  }
}
