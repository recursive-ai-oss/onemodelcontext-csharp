using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Protocol;

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

  // A URL is an HTTP server, anything else a command line to start.
  private static ContextTransport TransportFor(string text) =>
    text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
      ? ContextTransport.Http(new Uri(text, UriKind.Absolute))
      : ContextTransport.ProcessFromCommandLine(text);

  /// <summary>Connects to a server over any transport. The client negotiates the protocol era itself.</summary>
  /// <param name="transport">The transport, for example <see cref="ContextTransport.Stream(System.IO.Stream, int, bool)"/>.</param>
  /// <param name="era">Pin the era when a server is known to need it; <see cref="EraPreference.Auto"/> negotiates.</param>
  /// <param name="onConfirm">Answers the server's yes/no questions (a tool asking before it acts): receives the question, returns true for yes. Setting it tells the server this client can be asked. See <see cref="ContextClientOptions.OnConfirm"/>.</param>
  /// <param name="onElicit">Answers the server's other questions to the user (forms, URLs). See <see cref="ContextClientOptions.OnElicit"/>.</param>
  /// <param name="cancellationToken">Cancels the connection attempt.</param>
  /// <returns>The connected client; dispose it to disconnect.</returns>
  /// <exception cref="ProtocolVersionException">The server and client share no protocol revision.</exception>
  public static async Task<ContextClient> ConnectAsync(
    ContextTransport transport,
    EraPreference era = EraPreference.Auto,
    Func<string, CancellationToken, ValueTask<bool>>? onConfirm = null,
    Func<ElicitRequestParams, CancellationToken, ValueTask<ElicitResult>>? onElicit = null,
    CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(transport);
    return await ContextClient.ConnectAsync(transport, new ContextClientOptions { Era = era, OnConfirm = onConfirm, OnElicit = onElicit }, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Connects to a server named by a command line and negotiates the protocol era (or uses the one you pin).</summary>
  /// <param name="commandOrUrl">
  /// A command line such as <c>npx -y some-server --flag "a b"</c>, started as a child process that speaks over standard input and output
  /// (see <see cref="ContextTransport.ProcessFromCommandLine"/>: no shell runs). An <c>http://</c> or <c>https://</c> URL connects over Streamable HTTP (see <see cref="ContextTransport.Http"/>), with the legacy HTTP+SSE fallback; to add a bearer token or tune limits, build the transport yourself.
  /// </param>
  /// <param name="era">Pin the era when a server is known to need it; <see cref="EraPreference.Auto"/> negotiates and remembers the answer per server.</param>
  /// <param name="onConfirm">Answers the server's yes/no questions (a tool asking before it acts): receives the question, returns true for yes. Setting it tells the server this client can be asked. See <see cref="ContextClientOptions.OnConfirm"/>.</param>
  /// <param name="onElicit">Answers the server's other questions to the user (forms, URLs). See <see cref="ContextClientOptions.OnElicit"/>.</param>
  /// <param name="cancellationToken">Cancels the connection attempt and ends the child process.</param>
  /// <returns>The connected client; dispose it to disconnect and stop the child process.</returns>
  /// <exception cref="ArgumentException"><paramref name="commandOrUrl"/> is empty or has an unclosed quote.</exception>
  /// <exception cref="ProtocolVersionException">The server and client share no protocol revision.</exception>
  public static Task<ContextClient> ConnectAsync(
    string commandOrUrl,
    EraPreference era = EraPreference.Auto,
    Func<string, CancellationToken, ValueTask<bool>>? onConfirm = null,
    Func<ElicitRequestParams, CancellationToken, ValueTask<ElicitResult>>? onElicit = null,
    CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(commandOrUrl);
    var text = commandOrUrl.Trim();
    return ConnectAsync(TransportFor(text), era, onConfirm, onElicit, cancellationToken);
  }

  /// <summary>Like the other <c>ConnectAsync</c> overload for a transport, and also answers requests for a model completion. Deprecated by 2026-07-28. To answer both sampling and roots, set both on <see cref="ContextClientOptions"/>.</summary>
  /// <param name="transport">The transport, as in the other overload.</param>
  /// <param name="onSample">The callback. See <see cref="ContextClientOptions.OnSample"/>.</param>
  /// <param name="era">Pin the era when a server is known to need it.</param>
  /// <param name="onConfirm">Answers yes/no questions.</param>
  /// <param name="onElicit">Answers the server's other questions.</param>
  /// <param name="cancellationToken">Cancels the connection attempt.</param>
  /// <returns>The connected client; dispose it to disconnect.</returns>
  [Obsolete(DeprecatedFeatures.Sampling, DiagnosticId = DeprecatedFeatures.SamplingId)]
  public static async Task<ContextClient> ConnectAsync(
    ContextTransport transport,
    Func<CreateMessageRequestParams, CancellationToken, ValueTask<CreateMessageResult>> onSample,
    EraPreference era = EraPreference.Auto,
    Func<string, CancellationToken, ValueTask<bool>>? onConfirm = null,
    Func<ElicitRequestParams, CancellationToken, ValueTask<ElicitResult>>? onElicit = null,
    CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(transport);
    ArgumentNullException.ThrowIfNull(onSample);
    return await ContextClient.ConnectAsync(transport, new ContextClientOptions { Era = era, OnConfirm = onConfirm, OnElicit = onElicit, OnSample = onSample }, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Like the other <c>ConnectAsync</c> overload for a command line, and also answers requests for a model completion. Deprecated by 2026-07-28. To answer both sampling and roots, set both on <see cref="ContextClientOptions"/>.</summary>
  /// <param name="commandOrUrl">The command line, as in the other overload.</param>
  /// <param name="onSample">The callback. See <see cref="ContextClientOptions.OnSample"/>.</param>
  /// <param name="era">Pin the era when a server is known to need it.</param>
  /// <param name="onConfirm">Answers yes/no questions.</param>
  /// <param name="onElicit">Answers the server's other questions.</param>
  /// <param name="cancellationToken">Cancels the connection attempt.</param>
  /// <returns>The connected client; dispose it to disconnect.</returns>
  [Obsolete(DeprecatedFeatures.Sampling, DiagnosticId = DeprecatedFeatures.SamplingId)]
  public static async Task<ContextClient> ConnectAsync(
    string commandOrUrl,
    Func<CreateMessageRequestParams, CancellationToken, ValueTask<CreateMessageResult>> onSample,
    EraPreference era = EraPreference.Auto,
    Func<string, CancellationToken, ValueTask<bool>>? onConfirm = null,
    Func<ElicitRequestParams, CancellationToken, ValueTask<ElicitResult>>? onElicit = null,
    CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(commandOrUrl);
    ArgumentNullException.ThrowIfNull(onSample);
    var text = commandOrUrl.Trim();
    return await ContextClient.ConnectAsync(TransportFor(text), new ContextClientOptions { Era = era, OnConfirm = onConfirm, OnElicit = onElicit, OnSample = onSample }, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Like the other <c>ConnectAsync</c> overload for a transport, and also answers the request for the client's roots. Deprecated by 2026-07-28. To answer both sampling and roots, set both on <see cref="ContextClientOptions"/>.</summary>
  /// <param name="transport">The transport, as in the other overload.</param>
  /// <param name="onListRoots">The callback. See <see cref="ContextClientOptions.OnListRoots"/>.</param>
  /// <param name="era">Pin the era when a server is known to need it.</param>
  /// <param name="onConfirm">Answers yes/no questions.</param>
  /// <param name="onElicit">Answers the server's other questions.</param>
  /// <param name="cancellationToken">Cancels the connection attempt.</param>
  /// <returns>The connected client; dispose it to disconnect.</returns>
  [Obsolete(DeprecatedFeatures.Roots, DiagnosticId = DeprecatedFeatures.RootsId)]
  public static async Task<ContextClient> ConnectAsync(
    ContextTransport transport,
    Func<CancellationToken, ValueTask<ListRootsResult>> onListRoots,
    EraPreference era = EraPreference.Auto,
    Func<string, CancellationToken, ValueTask<bool>>? onConfirm = null,
    Func<ElicitRequestParams, CancellationToken, ValueTask<ElicitResult>>? onElicit = null,
    CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(transport);
    ArgumentNullException.ThrowIfNull(onListRoots);
    return await ContextClient.ConnectAsync(transport, new ContextClientOptions { Era = era, OnConfirm = onConfirm, OnElicit = onElicit, OnListRoots = onListRoots }, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Like the other <c>ConnectAsync</c> overload for a command line, and also answers the request for the client's roots. Deprecated by 2026-07-28. To answer both sampling and roots, set both on <see cref="ContextClientOptions"/>.</summary>
  /// <param name="commandOrUrl">The command line, as in the other overload.</param>
  /// <param name="onListRoots">The callback. See <see cref="ContextClientOptions.OnListRoots"/>.</param>
  /// <param name="era">Pin the era when a server is known to need it.</param>
  /// <param name="onConfirm">Answers yes/no questions.</param>
  /// <param name="onElicit">Answers the server's other questions.</param>
  /// <param name="cancellationToken">Cancels the connection attempt.</param>
  /// <returns>The connected client; dispose it to disconnect.</returns>
  [Obsolete(DeprecatedFeatures.Roots, DiagnosticId = DeprecatedFeatures.RootsId)]
  public static async Task<ContextClient> ConnectAsync(
    string commandOrUrl,
    Func<CancellationToken, ValueTask<ListRootsResult>> onListRoots,
    EraPreference era = EraPreference.Auto,
    Func<string, CancellationToken, ValueTask<bool>>? onConfirm = null,
    Func<ElicitRequestParams, CancellationToken, ValueTask<ElicitResult>>? onElicit = null,
    CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(commandOrUrl);
    ArgumentNullException.ThrowIfNull(onListRoots);
    var text = commandOrUrl.Trim();
    return await ContextClient.ConnectAsync(TransportFor(text), new ContextClientOptions { Era = era, OnConfirm = onConfirm, OnElicit = onElicit, OnListRoots = onListRoots }, cancellationToken).ConfigureAwait(false);
  }

  private static (string Name, string Version) EntryAssemblyIdentity()
  {
    var assembly = Assembly.GetEntryAssembly();
    var name = assembly?.GetName().Name;
    var version = assembly?.GetName().Version?.ToString(3);
    return (string.IsNullOrEmpty(name) ? "server" : name, string.IsNullOrEmpty(version) ? "1.0.0" : version);
  }
}
