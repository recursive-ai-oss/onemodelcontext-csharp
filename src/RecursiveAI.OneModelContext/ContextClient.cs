using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;

namespace RecursiveAI.OneModelContext;

/// <summary>Which protocol era a client may use.</summary>
public enum EraPreference
{
  /// <summary>Try the modern era, fall back to legacy. The default.</summary>
  Auto,

  /// <summary>Only the modern era (2026-07-28).</summary>
  Modern,

  /// <summary>Only the legacy era (2024-11-05 to 2025-11-25).</summary>
  Legacy,
}

/// <summary>Settings of a <see cref="ContextClient"/>.</summary>
public sealed class ContextClientOptions
{
  /// <summary>Who the client is. Default: this library's name and version.</summary>
  public Implementation ClientInfo { get; init; } = new()
  {
    Name = "RecursiveAI.OneModelContext",
    Version = typeof(ContextClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0",
  };

  /// <summary>What the client supports; only list what <see cref="RequestHandler"/> can answer. Default: nothing.</summary>
  public ClientCapabilities Capabilities { get; init; } = new();

  /// <summary>The revisions the client speaks, newest first. Default <see cref="ProtocolVersions.Supported"/>.</summary>
  public IReadOnlyList<string> SupportedVersions { get; init; } = ProtocolVersions.Supported;

  /// <summary>Which era to use. Default <see cref="EraPreference.Auto"/>.</summary>
  public EraPreference Era { get; init; } = EraPreference.Auto;

  /// <summary>Answers what the server asks of the client: elicitation, and the deprecated sampling and roots.</summary>
  public IClientRequestHandler? RequestHandler { get; init; }

  /// <summary>How long negotiation may take. Default 30 seconds.</summary>
  public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// How long to wait for a server to answer the probe of one era before concluding it does not speak that era and trying the next one.
  /// A legacy server may answer a modern probe with an error, with nothing at all, or only after a long time; 2026-07-28 "stdio:
  /// Backward Compatibility" says silence means legacy. Applies to every era tried except the last. Default 5 seconds.
  /// </summary>
  public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);

  /// <summary>
  /// Remembers which era each server spoke, keyed by <see cref="ServerKey"/> or the transport's own key, so that reconnecting skips the
  /// probe. Default <see cref="EraCache.Shared"/>; <c>null</c> turns remembering off. Used only with <see cref="EraPreference.Auto"/>.
  /// </summary>
  public IEraCache? EraCache { get; init; } = RecursiveAI.OneModelContext.EraCache.Shared;

  /// <summary>
  /// The key the era cache uses for this server, in place of <see cref="ContextTransport.ServerKey"/>. Set it when the transport cannot name
  /// the server, or when two different servers share a command line.
  /// </summary>
  public string? ServerKey { get; init; }

  /// <summary>How long a request waits for its response. Default 5 minutes.</summary>
  public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);

  /// <summary>Most pages followed when listing everything. Default 100.</summary>
  public int MaxListPages { get; init; } = 100;

  /// <summary>Limits applied to every incoming message.</summary>
  public JsonRpcReaderOptions ReaderOptions { get; init; } = new();

  /// <summary>The clock for timeouts. Default <see cref="TimeProvider.System"/>.</summary>
  public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

  /// <summary>The codecs to use in place of the built-in ones. Default: the modern codec, then the legacy codec.</summary>
  public IReadOnlyList<IClientCodec>? Codecs { get; init; }
}

/// <summary>
/// A client connected to one server. The calls are the same on both eras; <see cref="Era"/> and <see cref="ProtocolVersion"/> say
/// which one was negotiated. Thread-safe: calls may run concurrently.
/// </summary>
public sealed partial class ContextClient : IAsyncDisposable
{
  private readonly JsonRpcConnection _rpc;
  private readonly ClientSession _session;
  private readonly ClientHello _hello;
  private readonly ContextClientOptions _options;
  private readonly Task<JsonRpcCloseReason> _run;

  private ContextClient(JsonRpcConnection rpc, ClientSession session, ClientHello hello, ContextClientOptions options, Task<JsonRpcCloseReason> run)
  {
    _rpc = rpc;
    _session = session;
    _hello = hello;
    _options = options;
    _run = run;
  }

  /// <summary>Raised for every notification the server sends (list changes, progress, logs), on the connection's read loop.</summary>
  public event Action<JsonRpcNotification>? NotificationReceived;

  /// <summary>The negotiated era.</summary>
  public ProtocolEra Era => _session.Era;

  /// <summary>The negotiated protocol revision.</summary>
  public string ProtocolVersion => _session.ProtocolVersion;

  /// <summary>Who the server is.</summary>
  public Implementation ServerInfo => _session.ServerInfo;

  /// <summary>What the server supports.</summary>
  public ServerCapabilities ServerCapabilities => _session.ServerCapabilities;

  /// <summary>Hints for the model on using the server.</summary>
  public string? Instructions => _session.Instructions;

  /// <summary>Completes with the reason when the connection ends.</summary>
  public Task<JsonRpcCloseReason> Completion => _run;

  /// <summary>Connects and negotiates the era and protocol revision.</summary>
  /// <param name="transport">The transport; opened here, closed when the client is disposed.</param>
  /// <param name="options">Settings; <c>null</c> for the defaults.</param>
  /// <param name="cancellationToken">Cancels connecting.</param>
  /// <exception cref="ProtocolVersionException">No revision is common to both sides; the message names the eras on both sides.</exception>
  /// <exception cref="ContextException">No codec is available for the requested era, or the server answered nothing usable.</exception>
  /// <exception cref="TransportException">The connection failed during negotiation.</exception>
  public static async ValueTask<ContextClient> ConnectAsync(ContextTransport transport, ContextClientOptions? options = null, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(transport);
    options ??= new ContextClientOptions();
    var channel = await transport.OpenAsync(cancellationToken).ConfigureAwait(false);
    var holder = new Holder();
    var rpc = new JsonRpcConnection(channel, new JsonRpcConnectionOptions
    {
      RequestHandler = (request, ct) => holder.Client?.HandleServerRequestAsync(request, ct) ?? ValueTask.FromResult<JsonRpcMessage?>(null),
      NotificationHandler = (notification, _) =>
      {
        holder.Client?.NotificationReceived?.Invoke(notification);
        return ValueTask.CompletedTask;
      },
      OutgoingRequestCancelled = id => NotifyCancelledAsync(holder, id),
      RequestTimeout = options.RequestTimeout,
      ReaderOptions = options.ReaderOptions,
      TimeProvider = options.TimeProvider,
    });
    holder.Rpc = rpc;
    var run = rpc.RunAsync(CancellationToken.None);
    var hello = new ClientHello
    {
      ClientInfo = options.ClientInfo,
      Capabilities = options.Capabilities,
      SupportedVersions = options.SupportedVersions,
      TimeProvider = options.TimeProvider,
    };

    try
    {
      using var timeout = new CancellationTokenSource(options.ConnectTimeout, options.TimeProvider);
      using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
      var session = await NegotiateAsync(rpc, hello, options, options.ServerKey ?? transport.ServerKey, linked.Token).ConfigureAwait(false);
      var client = new ContextClient(rpc, session, hello, options, run);
      holder.Client = client;
      return client;
    }
    catch (Exception ex) when (ex is JsonRpcClosedException)
    {
      await rpc.DisposeAsync().ConfigureAwait(false);
      throw new TransportException("The connection closed during negotiation.", ex);
    }
    catch
    {
      await rpc.DisposeAsync().ConfigureAwait(false);
      throw;
    }
  }

  /// <summary>Lists every tool, following pages up to <see cref="ContextClientOptions.MaxListPages"/>.</summary>
  /// <param name="cancellationToken">Cancels the call.</param>
  public async ValueTask<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken cancellationToken = default)
  {
    var all = new List<Tool>();
    string? cursor = null;
    for (var page = 0; page < _options.MaxListPages; page++)
    {
      var result = await ListToolsPageAsync(cursor, cancellationToken).ConfigureAwait(false);
      all.AddRange(result.Tools);
      cursor = result.NextCursor;
      if (cursor is null)
      {
        return all;
      }
    }

    throw new ContextException("The tool list has more than " + _options.MaxListPages + " pages.");
  }

  /// <summary>Lists one page of tools.</summary>
  /// <param name="cursor">The previous page's <c>nextCursor</c>, or null for the first page.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  public ValueTask<ListToolsResult> ListToolsPageAsync(string? cursor = null, CancellationToken cancellationToken = default) =>
    CallAsync<ListToolsResult>(Methods.ToolsList, new PaginatedRequestParams { Cursor = cursor }, cancellationToken);

  /// <summary>Lists every resource.</summary>
  /// <param name="cancellationToken">Cancels the call.</param>
  public async ValueTask<IReadOnlyList<Resource>> ListResourcesAsync(CancellationToken cancellationToken = default)
  {
    var all = new List<Resource>();
    string? cursor = null;
    for (var page = 0; page < _options.MaxListPages; page++)
    {
      var result = await ListResourcesPageAsync(cursor, cancellationToken).ConfigureAwait(false);
      all.AddRange(result.Resources);
      cursor = result.NextCursor;
      if (cursor is null)
      {
        return all;
      }
    }

    throw new ContextException("The resource list has more than " + _options.MaxListPages + " pages.");
  }

  /// <summary>Lists one page of resources.</summary>
  /// <param name="cursor">The previous page's <c>nextCursor</c>, or null for the first page.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  public ValueTask<ListResourcesResult> ListResourcesPageAsync(string? cursor = null, CancellationToken cancellationToken = default) =>
    CallAsync<ListResourcesResult>(Methods.ResourcesList, new PaginatedRequestParams { Cursor = cursor }, cancellationToken);

  /// <summary>Lists every resource template.</summary>
  /// <param name="cancellationToken">Cancels the call.</param>
  public async ValueTask<IReadOnlyList<ResourceTemplate>> ListResourceTemplatesAsync(CancellationToken cancellationToken = default)
  {
    var all = new List<ResourceTemplate>();
    string? cursor = null;
    for (var page = 0; page < _options.MaxListPages; page++)
    {
      var result = await CallAsync<ListResourceTemplatesResult>(Methods.ResourcesTemplatesList, new PaginatedRequestParams { Cursor = cursor }, cancellationToken).ConfigureAwait(false);
      all.AddRange(result.ResourceTemplates);
      cursor = result.NextCursor;
      if (cursor is null)
      {
        return all;
      }
    }

    throw new ContextException("The resource template list has more than " + _options.MaxListPages + " pages.");
  }

  /// <summary>Lists every prompt.</summary>
  /// <param name="cancellationToken">Cancels the call.</param>
  public async ValueTask<IReadOnlyList<Prompt>> ListPromptsAsync(CancellationToken cancellationToken = default)
  {
    var all = new List<Prompt>();
    string? cursor = null;
    for (var page = 0; page < _options.MaxListPages; page++)
    {
      var result = await CallAsync<ListPromptsResult>(Methods.PromptsList, new PaginatedRequestParams { Cursor = cursor }, cancellationToken).ConfigureAwait(false);
      all.AddRange(result.Prompts);
      cursor = result.NextCursor;
      if (cursor is null)
      {
        return all;
      }
    }

    throw new ContextException("The prompt list has more than " + _options.MaxListPages + " pages.");
  }

  /// <summary>Calls a tool. A tool that fails returns a result with <c>IsError</c> set; only a bad call throws.</summary>
  /// <param name="name">The tool name.</param>
  /// <param name="arguments">The arguments object, or <c>default</c> for none.</param>
  /// <param name="cancellationToken">Cancels the call; the server is told.</param>
  /// <exception cref="ProtocolException">The server rejected the call (unknown tool, bad arguments).</exception>
  public ValueTask<CallToolResult> CallToolAsync(string name, JsonElement arguments = default, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(name);
    return CallAsync<CallToolResult>(Methods.ToolsCall, new CallToolRequestParams { Name = name, Arguments = arguments }, cancellationToken);
  }

  /// <summary>Reads a resource.</summary>
  /// <param name="uri">The resource URI.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  /// <exception cref="ProtocolException">The resource was not found or the server refused.</exception>
  public ValueTask<ReadResourceResult> ReadResourceAsync(string uri, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(uri);
    return CallAsync<ReadResourceResult>(Methods.ResourcesRead, new ReadResourceRequestParams { Uri = uri }, cancellationToken);
  }

  /// <summary>Gets a prompt.</summary>
  /// <param name="name">The prompt name.</param>
  /// <param name="arguments">Argument values, or null for none.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  public ValueTask<GetPromptResult> GetPromptAsync(string name, IReadOnlyDictionary<string, string>? arguments = null, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(name);
    return CallAsync<GetPromptResult>(Methods.PromptsGet, new GetPromptRequestParams { Name = name, Arguments = arguments is null ? null : new Dictionary<string, string>(arguments) }, cancellationToken);
  }

  /// <summary>Asks the server for completions of a prompt or template argument.</summary>
  /// <param name="request">What to complete.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  public ValueTask<CompleteResult> CompleteAsync(CompleteRequestParams request, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    return CallAsync<CompleteResult>(Methods.CompletionComplete, request, cancellationToken);
  }

  /// <summary>Closes the connection.</summary>
  public async ValueTask DisposeAsync() => await _rpc.DisposeAsync().ConfigureAwait(false);

  // 2026-07-28 versioning, "Backward Compatibility with Initialization-Based Versions": try the modern probe first, treat a failure that is
  // not a recognized modern error (or silence) as a legacy server, remember what worked. The era cache only changes the order.
  private static async ValueTask<ClientSession> NegotiateAsync(JsonRpcConnection rpc, ClientHello hello, ContextClientOptions options, string? serverKey, CancellationToken cancellationToken)
  {
    var codecs = new List<IClientCodec>();
    foreach (var codec in options.Codecs ?? DefaultCodecs.Client())
    {
      if ((options.Era == EraPreference.Modern && codec.Era != ProtocolEra.Modern) || (options.Era == EraPreference.Legacy && codec.Era != ProtocolEra.Legacy))
      {
        continue;
      }

      codecs.Add(codec);
    }

    if (codecs.Count == 0)
    {
      throw new ContextException("No protocol codec is available for era preference " + options.Era + ".");
    }

    var cache = options.Era == EraPreference.Auto && serverKey is not null ? options.EraCache : null;
    if (cache is not null && cache.TryGet(serverKey!, out var remembered))
    {
      // The remembered era goes first; the others stay as the fallback in case the server changed.
      var index = codecs.FindIndex(c => c.Era == remembered);
      if (index > 0)
      {
        var first = codecs[index];
        codecs.RemoveAt(index);
        codecs.Insert(0, first);
      }
    }

    for (var i = 0; i < codecs.Count; i++)
    {
      var codec = codecs[i];
      var session = i == codecs.Count - 1
        ? await codec.NegotiateAsync(rpc, hello, cancellationToken).ConfigureAwait(false)
        : await ProbeAsync(codec, rpc, hello, options, cancellationToken).ConfigureAwait(false);
      if (session is not null)
      {
        cache?.Remember(serverKey!, session.Era);
        return session;
      }
    }

    cache?.Forget(serverKey!);
    throw new ProtocolVersionException(options.SupportedVersions, []);
  }

  private static async ValueTask<ClientSession?> ProbeAsync(IClientCodec codec, JsonRpcConnection rpc, ClientHello hello, ContextClientOptions options, CancellationToken cancellationToken)
  {
    if (options.ProbeTimeout <= TimeSpan.Zero || options.ProbeTimeout == Timeout.InfiniteTimeSpan)
    {
      return await codec.NegotiateAsync(rpc, hello, cancellationToken).ConfigureAwait(false);
    }

    using var probe = new CancellationTokenSource(options.ProbeTimeout, options.TimeProvider);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, probe.Token);
    try
    {
      return await codec.NegotiateAsync(rpc, hello, linked.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (probe.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
    {
      // Silence: the server does not speak this era.
      return null;
    }
  }

  private static async ValueTask NotifyCancelledAsync(Holder holder, JsonRpcId id)
  {
    var parameters = JsonSerializer.SerializeToElement(new CancelledNotificationParams { RequestId = id, Reason = "The caller cancelled the request." }, WireJsonContext.Default.CancelledNotificationParams);
    await holder.Rpc!.SendNotificationAsync(Methods.NotificationsCancelled, parameters).ConfigureAwait(false);
  }

  private async ValueTask<T> CallAsync<T>(string method, RequestParams parameters, CancellationToken cancellationToken)
    where T : Result
  {
    var started = _options.TimeProvider.GetTimestamp();
    using var activity = ContextDiagnostics.Source.StartActivity(method, System.Diagnostics.ActivityKind.Client);
    try
    {
      var encoded = _session.Codec.EncodeParams(_session, _hello, method, parameters);
      JsonElement raw;
      try
      {
        raw = await _rpc.SendRequestAsync(method, encoded, cancellationToken).ConfigureAwait(false);
      }
      catch (JsonRpcRemoteException remote)
      {
        throw ProtocolException.FromError(remote.Error);
      }
      catch (JsonRpcClosedException closed)
      {
        throw new TransportException("The connection closed before the server answered '" + method + "'.", closed);
      }

      var result = _session.Codec.DecodeResult(_session, method, raw);
      if (result is InputRequiredResult)
      {
        // Answering input requests and retrying is the session layer's job; until it exists the call cannot finish.
        throw new ContextException("The server asked for input to finish '" + method + "', which this client cannot answer yet.");
      }

      return result as T ?? throw new ProtocolException(JsonRpcErrorCodes.InternalError, "The result of '" + method + "' has an unexpected type.");
    }
    finally
    {
      if (ContextDiagnostics.ClientDuration.Enabled)
      {
        ContextDiagnostics.ClientDuration.Record(
          _options.TimeProvider.GetElapsedTime(started).TotalSeconds,
          new KeyValuePair<string, object?>("mcp.method.name", method),
          new KeyValuePair<string, object?>("mcp.protocol.era", Era.ToString().ToLowerInvariant()));
      }
    }
  }

  // Server-initiated requests (legacy sampling, roots and elicitation) go to the handler the application supplied.
  private async ValueTask<JsonRpcMessage?> HandleServerRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken)
  {
    if (request.Method == Methods.Ping && Era == ProtocolEra.Legacy)
    {
      // utilities/ping: either side may ping, and the receiver answers at once with an empty result.
      return new JsonRpcResultResponse(request.Id, ContextServer.EmptyObject);
    }

    if (_options.RequestHandler is not { } handler)
    {
      return new JsonRpcErrorResponse(request.Id, new JsonRpcError(JsonRpcErrorCodes.MethodNotFound, "Method not found: " + request.Method + "."));
    }

    try
    {
      var result = await handler.HandleAsync(request.Method, request.Params, cancellationToken).ConfigureAwait(false);
      var info = WireJsonContext.Default.GetTypeInfo(result.GetType())
        ?? throw new InvalidOperationException("The handler returned a result type that is not a protocol record.");
      return new JsonRpcResultResponse(request.Id, JsonSerializer.SerializeToElement(result, info));
    }
    catch (ProtocolException error)
    {
      return new JsonRpcErrorResponse(request.Id, error.ToError());
    }
  }

  private sealed class Holder
  {
    internal JsonRpcConnection? Rpc;
    internal ContextClient? Client;
  }
}
