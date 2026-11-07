using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

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
public static partial class ModelContext
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
    var (defaultName, defaultVersion) = EntryAssemblyIdentity();
    var server = new ContextServer(name ?? defaultName, version ?? defaultVersion);
    foreach (var registrar in tools)
    {
      registrar.AddTo(server);
    }

    return ServeAsync(server, cancellationToken);
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
