using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Hosting;

/// <summary>Registers OneModelContext servers and clients in a generic host.</summary>
public static class ContextHostingExtensions
{
  /// <summary>
  /// Registers one <see cref="ContextServer"/> as a singleton, with the diagnostics-to-<see cref="ILogger"/> bridge, and returns a
  /// builder to say what it serves and how. Calling it again returns a builder for the same server.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="name">The server name sent to clients. Defaults to the host's application name, then the entry assembly's name.</param>
  /// <param name="version">The server version. Defaults to the entry assembly's informational version, then "1.0.0".</param>
  /// <param name="options">Creates the server options from the container. Default: the core defaults, with the container's
  /// <see cref="TimeProvider"/> when one is registered (so tests can inject a fake clock).</param>
  /// <returns>The builder.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
  /// <example>
  /// <code>
  /// builder.Services.AddSingleton&lt;SearchIndex&gt;();
  /// builder.Services.AddContextServer().AddTools&lt;IndexTools&gt;().UseStdio();
  /// </code>
  /// </example>
  public static ContextServerBuilder AddContextServer(this IServiceCollection services, string? name = null, string? version = null, Func<IServiceProvider, ContextServerOptions>? options = null)
  {
    ArgumentNullException.ThrowIfNull(services);
    foreach (var descriptor in services)
    {
      if (descriptor.ServiceType == typeof(ContextServerSetup) && descriptor.ImplementationInstance is ContextServerSetup existing)
      {
        return new ContextServerBuilder(services, existing);
      }
    }

    var setup = new ContextServerSetup();
    services.AddSingleton(setup);
    services.AddSingleton(provider => CreateServer(provider, setup, name, version, options));
    services.AddSingleton<IHostedService, ContextDiagnosticsLogger>();
    return new ContextServerBuilder(services, setup);
  }

  /// <summary>Registers the server with one generated tool class: <c>AddContextServer().AddTools&lt;T&gt;()</c>.</summary>
  /// <typeparam name="T">A partial class with <c>[Tool]</c>, <c>[Resource]</c> or <c>[Prompt]</c> methods.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <returns>The builder, to add a transport or more tools.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
  public static ContextServerBuilder AddContextServer<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)] T>(this IServiceCollection services)
    where T : class, IContextRegistrar =>
    services.AddContextServer().AddTools<T>();

  /// <summary>
  /// Registers a <see cref="ContextClientProvider"/> that connects to a server on first use, for apps that consume a server.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="transport">Creates the transport for each connection.</param>
  /// <param name="options">Creates the client options; null for the defaults (with the container's <see cref="TimeProvider"/> when registered).</param>
  /// <returns>The service collection.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="transport"/> is null.</exception>
  public static IServiceCollection AddContextClient(this IServiceCollection services, Func<IServiceProvider, ContextTransport> transport, Func<IServiceProvider, ContextClientOptions>? options = null)
  {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(transport);
    services.TryAddSingleton(provider => new ContextClientProvider(
      () => transport(provider),
      options is null
        ? provider.GetService<TimeProvider>() is { } time ? new ContextClientOptions { TimeProvider = time } : new ContextClientOptions()
        : options(provider)));
    return services;
  }

  /// <summary>Registers a <see cref="ContextClientProvider"/> for a server named by a command line (stdio) or an http(s) URL.</summary>
  /// <param name="services">The service collection.</param>
  /// <param name="commandOrUrl">The same text <c>ModelContext.ConnectAsync</c> accepts.</param>
  /// <param name="options">Creates the client options; null for the defaults.</param>
  /// <returns>The service collection.</returns>
  /// <exception cref="ArgumentException"><paramref name="commandOrUrl"/> is empty.</exception>
  public static IServiceCollection AddContextClient(this IServiceCollection services, string commandOrUrl, Func<IServiceProvider, ContextClientOptions>? options = null)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(commandOrUrl);
    var text = commandOrUrl.Trim();
    return services.AddContextClient(
      _ => text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        ? ContextTransport.Http(new Uri(text, UriKind.Absolute))
        : ContextTransport.ProcessFromCommandLine(text),
      options);
  }

  private static ContextServer CreateServer(IServiceProvider provider, ContextServerSetup setup, string? name, string? version, Func<IServiceProvider, ContextServerOptions>? options)
  {
    var entry = Assembly.GetEntryAssembly();
    var resolvedName = name ?? provider.GetService<IHostEnvironment>()?.ApplicationName ?? entry?.GetName().Name ?? "server";
    var resolvedVersion = version ?? entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0";
    var resolvedOptions = options is not null
      ? options(provider)
      : provider.GetService<TimeProvider>() is { } time ? new ContextServerOptions { TimeProvider = time } : new ContextServerOptions();

    var server = new ContextServer(resolvedName, resolvedVersion, resolvedOptions);
    foreach (var step in setup.Steps())
    {
      step(server, provider);
    }

    RequestScopes.Apply(server, provider);
    return server;
  }
}
