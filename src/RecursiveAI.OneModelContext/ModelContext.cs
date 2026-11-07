using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Hosting;
using RecursiveAI.OneModelContext.Http;

namespace RecursiveAI.OneModelContext;

/// <summary>
/// The happy path: serve tools or connect to a server in one line, on either protocol era. Everything here is a thin layer over
/// <see cref="ContextServer"/>, <see cref="ContextClient"/> and <see cref="ContextTransport"/>, which stay available for anything these
/// verbs do not cover. No options objects: each verb takes optional parameters.
/// </summary>
/// <example>
/// <code>
/// public partial class Greeter
/// {
///     /// &lt;summary&gt;Says hello.&lt;/summary&gt;
///     [Tool(ReadOnly = true)]
///     public string Greet(string name) =&gt; $"Hello, {name}!";
/// }
///
/// await ModelContext.ServeAsync(new Greeter());
/// </code>
/// </example>
public static class ModelContext
{
  /// <summary>Serves a generated tool class over standard input and output until the client disconnects.</summary>
  /// <param name="tools">An instance of a partial class with <see cref="ToolAttribute"/>, <see cref="ResourceAttribute"/> or <see cref="PromptAttribute"/> methods.</param>
  /// <param name="name">The server name reported to clients; defaults to the entry assembly's name.</param>
  /// <param name="version">The server version reported to clients; defaults to the entry assembly's version.</param>
  /// <param name="cancellationToken">Stops the server.</param>
  /// <returns>A task that completes when the connection closes or <paramref name="cancellationToken"/> is cancelled.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="tools"/> is null.</exception>
  public static Task ServeAsync(IContextRegistrar tools, string? name = null, string? version = null, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(tools);
    return ServeAsync([tools], name, version, cancellationToken);
  }

  /// <summary>Serves several generated tool classes over standard input and output until the client disconnects.</summary>
  /// <param name="tools">The tool classes. When two register the same name, the later one wins.</param>
  /// <param name="name">The server name reported to clients; defaults to the entry assembly's name.</param>
  /// <param name="version">The server version reported to clients; defaults to the entry assembly's version.</param>
  /// <param name="cancellationToken">Stops the server.</param>
  /// <returns>A task that completes when the connection closes or <paramref name="cancellationToken"/> is cancelled.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="tools"/> is null.</exception>
  public static Task ServeAsync(IEnumerable<IContextRegistrar> tools, string? name = null, string? version = null, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(tools);
    return ServeAsync(CreateServer(tools, name, version), cancellationToken);
  }

  /// <summary>Serves a server over standard input and output until the client disconnects. Use it when tools are added by hand or by the reflection quick path.</summary>
  /// <param name="server">The configured server.</param>
  /// <param name="cancellationToken">Stops the server.</param>
  /// <returns>A task that completes when the connection closes or <paramref name="cancellationToken"/> is cancelled.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="server"/> is null.</exception>
  public static Task ServeAsync(ContextServer server, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(server);
    return server.RunAsync(ContextTransport.Stdio(), cancellationToken);
  }

  /// <summary>
  /// Serves a generated tool class over Streamable HTTP on the loopback address until cancelled. Both protocol eras are answered on
  /// the one path. The built-in listener checks <c>Origin</c> and <c>Host</c> against loopback names, bounds headers and bodies, and
  /// times out slow clients; to listen elsewhere or add TLS, use <see cref="Http.HttpServer"/> directly.
  /// </summary>
  /// <param name="tools">An instance of a partial class with tool, resource or prompt methods.</param>
  /// <param name="port">The TCP port on 127.0.0.1; 0 picks a free one (read it from <paramref name="onListening"/>). Default 3001.</param>
  /// <param name="path">The URL path of the endpoint. Default <c>/mcp</c>. Other paths answer 404.</param>
  /// <param name="name">The server name reported to clients; defaults to the entry assembly's name.</param>
  /// <param name="version">The server version reported to clients; defaults to the entry assembly's version.</param>
  /// <param name="onListening">Called once with the full URL clients connect to, after the port is bound.</param>
  /// <param name="cancellationToken">Stops the server.</param>
  /// <returns>A task that completes, after the server has shut down, when <paramref name="cancellationToken"/> is cancelled.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="tools"/> is null.</exception>
  /// <exception cref="System.Net.Sockets.SocketException">The port cannot be bound.</exception>
  public static Task ServeHttpAsync(IContextRegistrar tools, int port = 3001, string path = "/mcp", string? name = null, string? version = null, Action<Uri>? onListening = null, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(tools);
    return ServeHttpAsync([tools], port, path, name, version, onListening, cancellationToken);
  }

  /// <summary>Serves several generated tool classes over Streamable HTTP on the loopback address until cancelled.</summary>
  /// <param name="tools">The tool classes. When two register the same name, the later one wins.</param>
  /// <param name="port">The TCP port on 127.0.0.1; 0 picks a free one. Default 3001.</param>
  /// <param name="path">The URL path of the endpoint. Default <c>/mcp</c>.</param>
  /// <param name="name">The server name reported to clients; defaults to the entry assembly's name.</param>
  /// <param name="version">The server version reported to clients; defaults to the entry assembly's version.</param>
  /// <param name="onListening">Called once with the full URL clients connect to, after the port is bound.</param>
  /// <param name="cancellationToken">Stops the server.</param>
  /// <returns>A task that completes, after the server has shut down, when <paramref name="cancellationToken"/> is cancelled.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="tools"/> is null.</exception>
  public static Task ServeHttpAsync(IEnumerable<IContextRegistrar> tools, int port = 3001, string path = "/mcp", string? name = null, string? version = null, Action<Uri>? onListening = null, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(tools);
    return ServeHttpAsync(CreateServer(tools, name, version), port, path, onListening, cancellationToken);
  }

  /// <summary>Serves a configured server over Streamable HTTP on the loopback address until cancelled.</summary>
  /// <param name="server">The configured server.</param>
  /// <param name="port">The TCP port on 127.0.0.1; 0 picks a free one. Default 3001.</param>
  /// <param name="path">The URL path of the endpoint, starting with <c>/</c>. Default <c>/mcp</c>.</param>
  /// <param name="onListening">Called once with the full URL clients connect to, after the port is bound.</param>
  /// <param name="cancellationToken">Stops the server.</param>
  /// <returns>A task that completes, after the server has shut down, when <paramref name="cancellationToken"/> is cancelled.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="server"/> is null.</exception>
  /// <exception cref="ArgumentException"><paramref name="path"/> does not start with <c>/</c>.</exception>
  public static async Task ServeHttpAsync(ContextServer server, int port = 3001, string path = "/mcp", Action<Uri>? onListening = null, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(server);
    if (string.IsNullOrEmpty(path) || path[0] != '/')
    {
      throw new ArgumentException("The path starts with '/'.", nameof(path));
    }

    using var endpoint = new StreamableHttpEndpoint(server);
    await using var http = new HttpServer(
      exchange => exchange.Request.Path == path ? endpoint.HandleAsync(exchange) : NotFoundAsync(exchange),
      new HttpServerOptions { Port = port },
      server.Options.TimeProvider);
    http.Start();
    onListening?.Invoke(new Uri(http.BaseAddress!, path));
    try
    {
      await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      // Cancelling is how a server is stopped.
    }
  }

  private static async ValueTask NotFoundAsync(IHttpExchange exchange)
  {
    exchange.Response.StatusCode = 404;
    await exchange.Response.Body.FlushAsync(exchange.Aborted).ConfigureAwait(false);
  }

  private static ContextServer CreateServer(IEnumerable<IContextRegistrar> tools, string? name, string? version)
  {
    var (defaultName, defaultVersion) = EntryAssemblyIdentity();
    var server = new ContextServer(name ?? defaultName, version ?? defaultVersion);
    foreach (var registrar in tools)
    {
      registrar.AddTo(server);
    }

    return server;
  }

  /// <summary>Connects to a server over any transport. The client negotiates the protocol era itself.</summary>
  /// <param name="transport">The transport, for example <see cref="ContextTransport.Stream(System.IO.Stream, int, bool)"/>.</param>
  /// <param name="era">Pin the era when a server is known to need it; <see cref="EraPreference.Auto"/> negotiates.</param>
  /// <param name="cancellationToken">Cancels the connection attempt.</param>
  /// <returns>The connected client; dispose it to disconnect.</returns>
  /// <exception cref="ProtocolVersionException">The server and client share no protocol revision.</exception>
  public static async Task<ContextClient> ConnectAsync(ContextTransport transport, EraPreference era = EraPreference.Auto, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(transport);
    return await ContextClient.ConnectAsync(transport, new ContextClientOptions { Era = era }, cancellationToken).ConfigureAwait(false);
  }

  private static (string Name, string Version) EntryAssemblyIdentity()
  {
    var assembly = Assembly.GetEntryAssembly();
    var name = assembly?.GetName().Name;
    var version = assembly?.GetName().Version?.ToString(3);
    return (string.IsNullOrEmpty(name) ? "server" : name, string.IsNullOrEmpty(version) ? "1.0.0" : version);
  }
}
