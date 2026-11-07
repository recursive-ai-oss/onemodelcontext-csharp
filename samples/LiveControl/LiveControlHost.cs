using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;

namespace LiveControl;

/// <summary>What an operation produced: JSON text for the model, and optionally a screenshot.</summary>
/// <param name="Json">The result as JSON text.</param>
/// <param name="Png">A PNG image to show beside the text, or null.</param>
public sealed record OperationResult(string Json, byte[]? Png = null);

/// <summary>An operation failing in a way the caller should read, such as an unknown entity. It becomes a tool error, not a protocol error.</summary>
/// <param name="code">A short machine-readable code.</param>
/// <param name="message">What went wrong, for a person or a model.</param>
public sealed class OperationException(string code, string message) : Exception(message)
{
  /// <summary>The machine-readable code.</summary>
  public string Code { get; } = code;
}

/// <summary>
/// One thing the running application lets an outside agent do. The list is only known at runtime: a scene may add operations when
/// it loads and remove them when it unloads, so nothing here can be a method with an attribute.
/// </summary>
/// <param name="Name">The tool name.</param>
/// <param name="Description">What it does, written for a model.</param>
/// <param name="InputSchema">A JSON Schema object describing the arguments.</param>
/// <param name="ChangesState">False marks the tool read-only, which hosts use to skip confirmation.</param>
/// <param name="Invoke">Runs it. The argument is the arguments object as JSON text, or null when the call had none.</param>
public sealed record Operation(string Name, string Description, string InputSchema, bool ChangesState, Func<string?, Task<OperationResult>> Invoke);

/// <summary>
/// An application's live-control endpoint: its operations served as tools to an agent over any duplex stream, or to an in-page
/// channel one message at a time. It is the shape a game, a simulator or a desktop app takes when it lets an agent poke at it.
/// </summary>
public sealed class LiveControlHost : IDisposable
{
  // Parsed schemas must outlive the tool definitions that point into them; Remove disposes them.
  private readonly Dictionary<string, JsonDocument> _schemas = new(StringComparer.Ordinal);

  /// <summary>Creates a host with no operations.</summary>
  /// <param name="name">The server name clients see.</param>
  /// <param name="version">The server version clients see.</param>
  /// <param name="instructions">A short note on how to use the application, shown to the agent at connect time.</param>
  public LiveControlHost(string name, string version, string? instructions = null) =>
    Server = new ContextServer(name, version) { Instructions = instructions };

  /// <summary>The server, for anything this class does not wrap (resources, prompts, options).</summary>
  public ContextServer Server { get; }

  /// <summary>Adds an operation, replacing one with the same name. Connected agents are told the tool list changed.</summary>
  /// <param name="operation">The operation.</param>
  public void Add(Operation operation)
  {
    var schema = JsonDocument.Parse(operation.InputSchema);
    lock (_schemas)
    {
      _schemas[operation.Name] = schema;
    }

    var definition = new ToolDefinition(operation.Name, operation.Description, schema)
    {
      Annotations = new ToolAnnotations { ReadOnlyHint = !operation.ChangesState },
    };
    Server.Tools.Add(definition, (arguments, _) => RunAsync(operation, arguments));
  }

  /// <summary>Removes an operation. Connected agents are told the tool list changed.</summary>
  /// <param name="name">The tool name.</param>
  /// <returns>True when there was one.</returns>
  public bool Remove(string name)
  {
    var removed = Server.Tools.Remove(name);
    lock (_schemas)
    {
      if (_schemas.Remove(name, out var schema))
      {
        schema.Dispose();
      }
    }

    return removed;
  }

  /// <summary>Opens a Unix domain socket that only the current user can connect to. Windows has Unix sockets too.</summary>
  /// <param name="path">Where to create the socket; an old file there is replaced.</param>
  public static Socket Listen(string path)
  {
    File.Delete(path);
    var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    listener.Bind(new UnixDomainSocketEndPoint(path));
    if (!OperatingSystem.IsWindows())
    {
      File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    listener.Listen(8);
    return listener;
  }

  /// <summary>Serves every connection to <paramref name="listener"/> until cancelled. Each connection is an independent session.</summary>
  /// <param name="listener">A listening socket, such as the one <see cref="Listen"/> returns.</param>
  /// <param name="cancellationToken">Stops accepting and closes every connection.</param>
  public async Task ServeAsync(Socket listener, CancellationToken cancellationToken)
  {
    var connections = new List<Task>();
    try
    {
      while (true)
      {
        var socket = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
        connections.Add(ServeConnectionAsync(socket, cancellationToken));
      }
    }
    catch (OperationCanceledException)
    {
      // Asked to stop: fall through and wait for the connections, which were given the same token.
    }

    await Task.WhenAll(connections).ConfigureAwait(false);
  }

  // The stream is the whole transport: ContextTransport.Stream frames newline-delimited JSON-RPC over it, whatever it is.
  private async Task ServeConnectionAsync(Socket socket, CancellationToken cancellationToken)
  {
    await using var stream = new NetworkStream(socket, ownsSocket: true);
    try
    {
      await Server.RunAsync(ContextTransport.Stream(stream), cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      // Shutdown.
    }
  }

  /// <summary>Starts a page: a channel that carries whole JSON-RPC messages and has no stream, such as a browser tab's message port.</summary>
  public Page OpenPage() => new(Server);

  private static async ValueTask<CallToolResult> RunAsync(Operation operation, JsonElement arguments)
  {
    try
    {
      var result = await operation.Invoke(arguments.ValueKind == JsonValueKind.Object ? arguments.GetRawText() : null).ConfigureAwait(false);
      List<ContentBlock> content = [TextContent.Of(result.Json)];
      if (result.Png is { Length: > 0 } png)
      {
        // The server drops images over its inline limit (ContextServerOptions.MaxInlineContentBytes, 4 MiB by default).
        content.Add(new ImageContent { Data = Convert.ToBase64String(png), MimeType = "image/png" });
      }

      return new CallToolResult { Content = content };
    }
    catch (OperationException ex)
    {
      // 2026-07-28 server/tools: a failure the model can act on is a result with isError, not a protocol error.
      return CallToolResult.FromError(ErrorJson(ex));
    }
  }

  private static string ErrorJson(OperationException ex)
  {
    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer))
    {
      writer.WriteStartObject();
      writer.WriteStartObject("error");
      writer.WriteString("code", ex.Code);
      writer.WriteString("message", ex.Message);
      writer.WriteEndObject();
      writer.WriteEndObject();
    }

    return Encoding.UTF8.GetString(buffer.ToArray());
  }

  /// <inheritdoc />
  public void Dispose()
  {
    lock (_schemas)
    {
      foreach (var schema in _schemas.Values)
      {
        schema.Dispose();
      }

      _schemas.Clear();
    }
  }

  /// <summary>One in-page peer. It keeps the session state a legacy client's handshake builds up, so use one page per peer.</summary>
  public sealed class Page
  {
    private readonly ContextServer _server;
    private readonly ServerSessionState _session = new();

    internal Page(ContextServer server) => _server = server;

    /// <summary>Handles one JSON-RPC message and returns the reply, or null for a notification. Bad input becomes a JSON-RPC error and never throws.</summary>
    /// <param name="message">The message text.</param>
    /// <param name="cancellationToken">Cancels the handler.</param>
    public ValueTask<string?> SendAsync(string message, CancellationToken cancellationToken = default) =>
      _server.HandleMessageAsync(message, _session, sink: null, principal: null, cancellationToken);
  }
}
