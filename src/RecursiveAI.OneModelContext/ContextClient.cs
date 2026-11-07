using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.Extensions;
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

  /// <summary>
  /// What the client supports; only list what the handlers can answer. Default: nothing. Setting <see cref="OnElicit"/>, <see cref="OnConfirm"/>,
  /// <see cref="OnSample"/> or <see cref="OnListRoots"/> adds the matching capability, so it need not be listed here.
  /// </summary>
  public ClientCapabilities Capabilities { get; init; } = new();

  /// <summary>
  /// The extensions the client declares to servers: on 2026-07-28 in the <c>_meta</c> of every request, earlier in <c>initialize</c>. Default
  /// none, so no extension is declared unless the host opts in (for example <c>AppsExtension.Client()</c>).
  /// </summary>
  public IReadOnlyList<ClientExtension> Extensions { get; init; } = [];

  /// <summary>The revisions the client speaks, newest first. Default <see cref="ProtocolVersions.Supported"/>.</summary>
  public IReadOnlyList<string> SupportedVersions { get; init; } = ProtocolVersions.Supported;

  /// <summary>Which era to use. Default <see cref="EraPreference.Auto"/>.</summary>
  public EraPreference Era { get; init; } = EraPreference.Auto;

  /// <summary>Answers what the server asks of the client: elicitation, and the deprecated sampling and roots.</summary>
  public IClientRequestHandler? RequestHandler { get; init; }

  /// <summary>
  /// Answers the server's questions to the user (form and URL elicitation). Setting it declares the elicitation capability. Return
  /// <see cref="ElicitResult.Accept"/>, <see cref="ElicitResult.Decline"/> or <see cref="ElicitResult.Cancel"/>. Takes precedence over
  /// <see cref="RequestHandler"/> for elicitation. Default: none.
  /// </summary>
  public Func<ElicitRequestParams, CancellationToken, ValueTask<ElicitResult>>? OnElicit { get; init; }

  /// <summary>
  /// Answers the server's yes/no questions (the ones <see cref="RequestContext.ConfirmAsync"/> asks) without the host knowing the schema:
  /// it gets the message and returns true for yes. Other elicitation goes to <see cref="OnElicit"/>; without that, it is declined.
  /// Setting it declares the elicitation capability. Default: none.
  /// </summary>
  public Func<string, CancellationToken, ValueTask<bool>>? OnConfirm { get; init; }

  /// <summary>Answers the server's requests for a model completion (<c>sampling/createMessage</c>). Setting it declares the sampling capability. Default: none.</summary>
  [Obsolete(DeprecatedFeatures.Sampling, DiagnosticId = DeprecatedFeatures.SamplingId)]
  public Func<CreateMessageRequestParams, CancellationToken, ValueTask<CreateMessageResult>>? OnSample { get; init; }

  /// <summary>Answers the server's request for the client's roots (<c>roots/list</c>). Setting it declares the roots capability. Default: none.</summary>
  [Obsolete(DeprecatedFeatures.Roots, DiagnosticId = DeprecatedFeatures.RootsId)]
  public Func<CancellationToken, ValueTask<ListRootsResult>>? OnListRoots { get; init; }

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

  /// <summary>
  /// Most times one call may be sent again because the server asked for input (2026-07-28 multi round-trip requests). The call fails after
  /// that many rounds. Default 8.
  /// </summary>
  public int MaxInputRounds { get; init; } = 8;

  /// <summary>
  /// Reuse list and read results for as long as the server says they stay fresh (2026-07-28 <c>ttlMs</c>), and drop them when the server
  /// announces a change. Default <c>true</c>. A server that sends no <c>ttlMs</c>, or zero, is never cached.
  /// </summary>
  public bool CacheResults { get; init; } = true;

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
  private readonly IClientRequestHandler? _handler;
  private readonly Task<JsonRpcCloseReason> _run;
  private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, IProgress<ProgressNotificationParams>> _progress = new(StringComparer.Ordinal);
  private long _progressCounter;
  private Transports.HttpChannel? _httpChannel;

  private ContextClient(JsonRpcConnection rpc, ClientSession session, ClientHello hello, ContextClientOptions options, IClientRequestHandler? handler, Task<JsonRpcCloseReason> run)
  {
    _handler = handler;
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
        holder.Client?.OnNotification(notification);
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
      Capabilities = CallbackRequestHandler.Declare(options),
      SupportedVersions = options.SupportedVersions,
      TimeProvider = options.TimeProvider,
    };

    try
    {
      using var timeout = new CancellationTokenSource(options.ConnectTimeout, options.TimeProvider);
      using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
      var session = await NegotiateAsync(rpc, hello, options, options.ServerKey ?? transport.ServerKey, linked.Token).ConfigureAwait(false);
      foreach (var extension in options.Extensions)
      {
        if (extension.Required && (session.ServerCapabilities.Extensions is not { } offered || !offered.ContainsKey(extension.Id)))
        {
          throw new ExtensionRequiredException(extension.Id);
        }
      }

      var client = new ContextClient(rpc, session, hello, options, CallbackRequestHandler.For(options), run) { _httpChannel = channel as Transports.HttpChannel };
      holder.Client = client;
      return client;
    }
    catch (Exception ex) when (ex is JsonRpcClosedException)
    {
      await rpc.DisposeAsync().ConfigureAwait(false);
      throw (Exception?)AuthorizationFrom(ex) ?? (Exception?)SignInFailureFrom(ex) ?? new TransportException("The connection closed during negotiation.", ex);
    }
    catch
    {
      await rpc.DisposeAsync().ConfigureAwait(false);
      throw;
    }
  }

  /// <summary>Lists every tool, following pages up to <see cref="ContextClientOptions.MaxListPages"/>. <see cref="ListedItems{T}.FromCache"/> says whether no request was needed.</summary>
  /// <param name="cancellationToken">Cancels the call.</param>
  public async ValueTask<ListedItems<Tool>> ListToolsAsync(CancellationToken cancellationToken = default)
  {
    var all = new List<Tool>();
    var cached = true;
    string? cursor = null;
    for (var page = 0; page < _options.MaxListPages; page++)
    {
      var result = await ListToolsPageAsync(cursor, cancellationToken).ConfigureAwait(false);
      all.AddRange(result.Tools);
      cached &= result.FromCache;
      cursor = result.NextCursor;
      if (cursor is null)
      {
        return new ListedItems<Tool>(all, cached);
      }
    }

    throw new ContextException("The tool list has more than " + _options.MaxListPages + " pages.");
  }

  /// <summary>Lists one page of tools.</summary>
  /// <param name="cursor">The previous page's <c>nextCursor</c>, or null for the first page.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  public async ValueTask<ListToolsResult> ListToolsPageAsync(string? cursor = null, CancellationToken cancellationToken = default)
  {
    var page = await CallAsync<ListToolsResult>(Methods.ToolsList, new PaginatedRequestParams { Cursor = cursor }, cancellationToken, cacheKey: Methods.ToolsList + "\0" + cursor).ConfigureAwait(false);
    return _httpChannel is not null && Era == ProtocolEra.Modern ? MirrorToolHeaders(page) : page;
  }

  /// <summary>Lists every resource.</summary>
  /// <param name="cancellationToken">Cancels the call.</param>
  public async ValueTask<ListedItems<Resource>> ListResourcesAsync(CancellationToken cancellationToken = default)
  {
    var all = new List<Resource>();
    var cached = true;
    string? cursor = null;
    for (var page = 0; page < _options.MaxListPages; page++)
    {
      var result = await ListResourcesPageAsync(cursor, cancellationToken).ConfigureAwait(false);
      all.AddRange(result.Resources);
      cached &= result.FromCache;
      cursor = result.NextCursor;
      if (cursor is null)
      {
        return new ListedItems<Resource>(all, cached);
      }
    }

    throw new ContextException("The resource list has more than " + _options.MaxListPages + " pages.");
  }

  /// <summary>Lists one page of resources.</summary>
  /// <param name="cursor">The previous page's <c>nextCursor</c>, or null for the first page.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  public ValueTask<ListResourcesResult> ListResourcesPageAsync(string? cursor = null, CancellationToken cancellationToken = default) =>
    CallAsync<ListResourcesResult>(Methods.ResourcesList, new PaginatedRequestParams { Cursor = cursor }, cancellationToken, cacheKey: Methods.ResourcesList + "\0" + cursor);

  /// <summary>Lists every resource template.</summary>
  /// <param name="cancellationToken">Cancels the call.</param>
  public async ValueTask<ListedItems<ResourceTemplate>> ListResourceTemplatesAsync(CancellationToken cancellationToken = default)
  {
    var all = new List<ResourceTemplate>();
    var cached = true;
    string? cursor = null;
    for (var page = 0; page < _options.MaxListPages; page++)
    {
      var result = await CallAsync<ListResourceTemplatesResult>(Methods.ResourcesTemplatesList, new PaginatedRequestParams { Cursor = cursor }, cancellationToken, cacheKey: Methods.ResourcesTemplatesList + "\0" + cursor).ConfigureAwait(false);
      all.AddRange(result.ResourceTemplates);
      cached &= result.FromCache;
      cursor = result.NextCursor;
      if (cursor is null)
      {
        return new ListedItems<ResourceTemplate>(all, cached);
      }
    }

    throw new ContextException("The resource template list has more than " + _options.MaxListPages + " pages.");
  }

  /// <summary>Lists every prompt.</summary>
  /// <param name="cancellationToken">Cancels the call.</param>
  public async ValueTask<ListedItems<Prompt>> ListPromptsAsync(CancellationToken cancellationToken = default)
  {
    var all = new List<Prompt>();
    var cached = true;
    string? cursor = null;
    for (var page = 0; page < _options.MaxListPages; page++)
    {
      var result = await CallAsync<ListPromptsResult>(Methods.PromptsList, new PaginatedRequestParams { Cursor = cursor }, cancellationToken, cacheKey: Methods.PromptsList + "\0" + cursor).ConfigureAwait(false);
      all.AddRange(result.Prompts);
      cached &= result.FromCache;
      cursor = result.NextCursor;
      if (cursor is null)
      {
        return new ListedItems<Prompt>(all, cached);
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
    return CallToolCoreAsync(name, arguments, null, cancellationToken);
  }

  /// <summary>Calls a tool and reports the progress the server sends while it works.</summary>
  /// <param name="name">The tool name.</param>
  /// <param name="arguments">The arguments object, or <c>default</c> for none.</param>
  /// <param name="progress">Receives each progress notification of this call, in order. It is called on the connection's read loop, so it should return quickly.</param>
  /// <param name="cancellationToken">Cancels the call; the server is told.</param>
  /// <exception cref="ProtocolException">The server rejected the call (unknown tool, bad arguments).</exception>
  public ValueTask<CallToolResult> CallToolAsync(string name, JsonElement arguments, IProgress<ProgressNotificationParams>? progress, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(name);
    return CallToolCoreAsync(name, arguments, progress, cancellationToken);
  }

  private async ValueTask<CallToolResult> CallToolCoreAsync(string name, JsonElement arguments, IProgress<ProgressNotificationParams>? progress, CancellationToken cancellationToken)
  {
    var request = new CallToolRequestParams { Name = name, Arguments = arguments };
    try
    {
      return await CallAsync<CallToolResult>(Methods.ToolsCall, request, cancellationToken, progress).ConfigureAwait(false);
    }
    catch (HeaderMismatchException) when (_httpChannel is not null && Era == ProtocolEra.Modern)
    {
      // 2026-07-28 transports, "Client Behavior": a rejection for missing Mcp-Param headers means the tool's schema may have changed;
      // list the tools again to learn the headers, then retry the call once.
      InvalidateCache();
      await ListToolsAsync(cancellationToken).ConfigureAwait(false);
      return await CallAsync<CallToolResult>(Methods.ToolsCall, request, cancellationToken, progress).ConfigureAwait(false);
    }
  }

  // 2026-07-28 transports, "Custom Headers from Tool Parameters": over HTTP a tool whose x-mcp-header annotations break the rules is
  // left out of the list (and reported), and the valid annotations are remembered so calls to the tool carry their headers.
  private ListToolsResult MirrorToolHeaders(ListToolsResult page)
  {
    List<Tool>? kept = null;
    for (var i = 0; i < page.Tools.Count; i++)
    {
      var tool = page.Tools[i];
      if (Transports.HeaderMirroring.TryRead(tool, out var parameters, out var reason))
      {
        _httpChannel!.SetToolHeaders(tool.Name, parameters);
        kept?.Add(tool);
        continue;
      }

      Diagnostics.ContextDiagnostics.Publish(Diagnostics.DiagnosticKind.InvalidMessage, "Tool '" + tool.Name + "' was left out of the tool list: " + reason + ".", Methods.ToolsList);
      kept ??= [.. page.Tools.GetRange(0, i)];
    }

    return kept is null
      ? page
      : new ListToolsResult { Tools = kept, NextCursor = page.NextCursor, TtlMs = page.TtlMs, CacheScope = page.CacheScope, Meta = page.Meta, ResultType = page.ResultType, AdditionalProperties = page.AdditionalProperties };
  }

  /// <summary>Forgets every cached list and read result, so the next call asks the server again.</summary>
  public void InvalidateCache() => _cache.Clear();

  /// <summary>Reads a resource.</summary>
  /// <param name="uri">The resource URI.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  /// <exception cref="ProtocolException">The resource was not found or the server refused.</exception>
  public ValueTask<ReadResourceResult> ReadResourceAsync(string uri, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(uri);
    return CallAsync<ReadResourceResult>(Methods.ResourcesRead, new ReadResourceRequestParams { Uri = uri }, cancellationToken, cacheKey: Methods.ResourcesRead + "\0" + uri);
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

  /// <summary>The extensions the server offers, by identifier, with their settings. Empty when it offers none. Read on connect, for either era.</summary>
  public IReadOnlyDictionary<string, JsonElement> ServerExtensions =>
    _session.ServerCapabilities.Extensions ?? (IReadOnlyDictionary<string, JsonElement>)EmptyExtensions;

  private static readonly Dictionary<string, JsonElement> EmptyExtensions = [];

  /// <summary>True when the server offers the extension.</summary>
  /// <param name="extensionId">The extension identifier.</param>
  public bool ServerSupportsExtension(string extensionId)
  {
    ArgumentNullException.ThrowIfNull(extensionId);
    return ServerExtensions.ContainsKey(extensionId);
  }

  /// <summary>Calls a request an extension added to the protocol. Declare the extension in <see cref="ContextClientOptions.Extensions"/> first, or the server answers as if the method did not exist.</summary>
  /// <param name="method">The extension's method, for example <c>tasks/get</c>.</param>
  /// <param name="parameters">The params object, or <c>default</c> for none. Its <c>_meta</c>, if any, is kept.</param>
  /// <param name="cancellationToken">Cancels the call; the server is told.</param>
  /// <returns>The result object as received, with <c>resultType</c> and <c>_meta</c> as the server sent them.</returns>
  /// <exception cref="ArgumentException"><paramref name="method"/> is a core method, or <paramref name="parameters"/> is not an object.</exception>
  /// <exception cref="ProtocolException">The server rejected the call (method not found when it lacks the extension).</exception>
  public async ValueTask<JsonElement> CallExtensionAsync(string method, JsonElement parameters = default, CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrEmpty(method);
    if (parameters.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
    {
      throw new ArgumentException("The params of a request are a JSON object.", nameof(parameters));
    }

    var typed = parameters.ValueKind == JsonValueKind.Object ? parameters.Deserialize(WireJsonContext.Default.RequestParams)! : new RequestParams();
    var result = await CallAsync<ExtensionResult>(method, typed, cancellationToken).ConfigureAwait(false);
    return JsonSerializer.SerializeToElement(result, WireJsonContext.Default.ExtensionResult);
  }

  /// <summary>Sends a notification an extension added to the protocol.</summary>
  /// <param name="method">The extension's notification method.</param>
  /// <param name="parameters">The params object, or <c>default</c> for none.</param>
  /// <param name="cancellationToken">Cancels the send.</param>
  public async ValueTask SendExtensionNotificationAsync(string method, JsonElement parameters = default, CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrEmpty(method);
    if (parameters.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
    {
      throw new ArgumentException("The params of a notification are a JSON object.", nameof(parameters));
    }

    var typed = parameters.ValueKind == JsonValueKind.Object ? parameters.Deserialize(WireJsonContext.Default.RequestParams)! : new RequestParams();

    // On 2026-07-28 the notification names the extensions the client declares in its own _meta, since there is no session to hold them.
    var encoded = _session.Codec.EncodeParams(_session, _hello, method, typed);
    await _rpc.SendNotificationAsync(method, encoded, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Closes the connection.</summary>
  public async ValueTask DisposeAsync()
  {
    await _rpc.DisposeAsync().ConfigureAwait(false);
    EndSubscriptions();
  }

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
    throw new ProtocolVersionException(options.SupportedVersions, hello.ServerVersionsSeen);
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

  private async ValueTask<T> CallAsync<T>(string method, RequestParams parameters, CancellationToken cancellationToken, IProgress<ProgressNotificationParams>? progress = null, string? cacheKey = null)
    where T : Result
  {
    var started = _options.TimeProvider.GetTimestamp();
    using var activity = ContextDiagnostics.Source.HasListeners() ? ContextDiagnostics.Source.StartActivity(method, System.Diagnostics.ActivityKind.Client) : null;
    string? progressToken = null;
    try
    {
      if (cacheKey is not null && _options.CacheResults && TryGetFresh(cacheKey) is T cached)
      {
        return cached is CacheableResult shared ? (T)(object)shared.AsCached() : cached;
      }

      if (progress is not null)
      {
        progressToken = "progress-" + Interlocked.Increment(ref _progressCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _progress[progressToken] = progress;
        parameters = WithProgressToken(parameters, progressToken);
      }

      var rounds = 0;
      while (true)
      {
        var encoded = _session.Codec.EncodeParams(_session, _hello, method, parameters);
        JsonElement raw;
        try
        {
          raw = await _rpc.SendRequestAsync(method, encoded, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcRemoteException remote) when (IsTransportFailure(remote.Error))
        {
          throw (Exception?)AuthorizationFrom(remote.Error) ?? new TransportException(remote.Error.Message);
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
        if (result is not InputRequiredResult inputRequired)
        {
          var typed = result as T ?? throw new ProtocolException(JsonRpcErrorCodes.InternalError, "The result of '" + method + "' has an unexpected type.");

          // 2026-07-28 caching: a result produced by a retry depends on inputs that are not in the cache key, so it is never kept.
          if (cacheKey is not null && rounds == 0 && _options.CacheResults)
          {
            Store(cacheKey, typed);
          }

          return typed;
        }

        // 2026-07-28 basic/patterns/mrtr: answer what the server asked, then send the same request again with the answers and the
        // server's state echoed back untouched. The retry is a new request with a new id.
        if (++rounds > _options.MaxInputRounds)
        {
          throw new ContextException("The server asked for input more than " + _options.MaxInputRounds + " times to finish '" + method + "'.");
        }

        parameters = await AnswerAsync(method, parameters, inputRequired, cancellationToken).ConfigureAwait(false);
      }
    }
    finally
    {
      if (progressToken is not null)
      {
        _progress.TryRemove(progressToken, out _);
      }

      if (ContextDiagnostics.ClientDuration.Enabled)
      {
        ContextDiagnostics.ClientDuration.Record(
          _options.TimeProvider.GetElapsedTime(started).TotalSeconds,
          new KeyValuePair<string, object?>("mcp.method.name", method),
          new KeyValuePair<string, object?>("mcp.protocol.version", ProtocolVersion),
          new KeyValuePair<string, object?>("mcp.protocol.era", Era.ToString().ToLowerInvariant()));
      }
    }
  }

  // A 401 or 403 from the HTTP transport, as the typed error that carries the server's challenge.
  private static AuthorizationException? AuthorizationFrom(Exception closed)
  {
    for (var inner = closed; inner is not null; inner = inner.InnerException)
    {
      if (inner is Transports.HttpAuthFailure failure)
      {
        return new AuthorizationException(failure.Message, failure.Status, ParseChallenges(failure.Challenge));
      }
    }

    return null;
  }

  // The OAuth client could not sign in (no user agent, discovery failed, the user refused): that error, not a dropped connection.
  private static OAuth.OAuthException? SignInFailureFrom(Exception closed)
  {
    for (var inner = closed; inner is not null; inner = inner.InnerException)
    {
      if (inner is OAuth.OAuthException failure)
      {
        return failure;
      }
    }

    return null;
  }

  private static AuthorizationException? AuthorizationFrom(JsonRpcError error)
  {
    if (error.Data.ValueKind != JsonValueKind.Object || !error.Data.TryGetProperty("httpStatus", out var status) || !status.TryGetInt32(out var code) || code is not (401 or 403))
    {
      return null;
    }

    var header = error.Data.TryGetProperty("wwwAuthenticate", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    return new AuthorizationException(error.Message, code, ParseChallenges(header));
  }

  private static IReadOnlyList<OAuth.BearerChallenge> ParseChallenges(string? header) =>
    header is null ? [] : OAuth.BearerChallenge.Parse([header]);

  // The HTTP transport reports what went wrong below the protocol (no connection, an HTTP error status) as an error marked in its data.
  private static bool IsTransportFailure(JsonRpcError error) =>
    error.Code == Transports.HttpChannel.HttpErrorCode && error.Data.ValueKind == JsonValueKind.Object
    && error.Data.TryGetProperty("transport", out var marker) && marker.ValueKind == JsonValueKind.True;

  private async ValueTask<RequestParams> AnswerAsync(string method, RequestParams parameters, InputRequiredResult asked, CancellationToken cancellationToken)
  {
    if (asked.InputRequests is not { Count: > 0 } && asked.RequestState is null)
    {
      throw new ProtocolException(JsonRpcErrorCodes.InternalError, "The server's input_required result for '" + method + "' has neither inputRequests nor requestState.");
    }

    var responses = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    if (asked.InputRequests is { } requests)
    {
      foreach (var (key, request) in requests)
      {
        if (request.Method is not (Methods.ElicitationCreate or Methods.SamplingCreateMessage or Methods.RootsList))
        {
          throw new ProtocolException(JsonRpcErrorCodes.InternalError, "The server asked for input with the unknown method '" + request.Method + "'.");
        }

        if (_handler is not { } handler)
        {
          throw new ContextException("The server asked the client for input (" + request.Method + ") to finish '" + method + "', and the client has no handler for it (set OnElicit, OnConfirm, OnSample, OnListRoots or RequestHandler).");
        }

        var answer = await handler.HandleAsync(request.Method, request.Params, cancellationToken).ConfigureAwait(false);
        var info = WireJsonContext.Default.GetTypeInfo(answer.GetType())
          ?? throw new InvalidOperationException("The handler returned a result type that is not a protocol record.");
        responses[key] = JsonSerializer.SerializeToElement(answer, info);
      }
    }

    // The state is echoed exactly when the server sent one, and not at all when it did not.
    return parameters switch
    {
      CallToolRequestParams p => new CallToolRequestParams { Name = p.Name, Arguments = p.Arguments, Meta = p.Meta, AdditionalProperties = p.AdditionalProperties, InputResponses = responses, RequestState = asked.RequestState },
      GetPromptRequestParams p => new GetPromptRequestParams { Name = p.Name, Arguments = p.Arguments, Meta = p.Meta, AdditionalProperties = p.AdditionalProperties, InputResponses = responses, RequestState = asked.RequestState },
      ReadResourceRequestParams p => new ReadResourceRequestParams { Uri = p.Uri, Meta = p.Meta, AdditionalProperties = p.AdditionalProperties, InputResponses = responses, RequestState = asked.RequestState },
      _ => throw new ProtocolException(JsonRpcErrorCodes.InternalError, "The server asked for input to finish '" + method + "', which does not support it."),
    };
  }

  private static RequestParams WithProgressToken(RequestParams parameters, string token)
  {
    var meta = new MetaObject { ProgressToken = JsonRpcId.FromString(token), AdditionalProperties = parameters.Meta?.AdditionalProperties };
    return parameters switch
    {
      CallToolRequestParams p => new CallToolRequestParams { Name = p.Name, Arguments = p.Arguments, Meta = meta, AdditionalProperties = p.AdditionalProperties, InputResponses = p.InputResponses, RequestState = p.RequestState },
      _ => parameters,
    };
  }

  private void OnNotification(JsonRpcNotification notification)
  {
    switch (notification.Method)
    {
      case Methods.NotificationsProgress:
        if (!_progress.IsEmpty && notification.Params.ValueKind == JsonValueKind.Object)
        {
          try
          {
            var update = notification.Params.Deserialize(WireJsonContext.Default.ProgressNotificationParams);
            if (update is not null && _progress.TryGetValue(update.ProgressToken.Text, out var sink))
            {
              sink.Report(update);
            }
          }
          catch (JsonException)
          {
            // A malformed progress notification is dropped; it never ends the connection.
          }
        }

        break;
      case Methods.NotificationsToolsListChanged:
        DropCached(Methods.ToolsList);
        break;
      case Methods.NotificationsPromptsListChanged:
        DropCached(Methods.PromptsList);
        break;
      case Methods.NotificationsResourcesListChanged:
        DropCached(Methods.ResourcesList);
        DropCached(Methods.ResourcesTemplatesList);
        break;
      case Methods.NotificationsResourcesUpdated:
        if (notification.Params.ValueKind == JsonValueKind.Object && notification.Params.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String)
        {
          _cache.TryRemove(Methods.ResourcesRead + "\0" + uri.GetString(), out _);
        }

        break;
    }

    RouteToSubscriptions(notification);
    RouteToExtensions(notification);
    NotificationReceived?.Invoke(notification);
  }

  // A notification an extension declared reaches that extension's handler; a handler that throws never ends the connection.
  private void RouteToExtensions(JsonRpcNotification notification)
  {
    foreach (var extension in _options.Extensions)
    {
      if (extension.TryGetNotification(notification.Method, out var handler))
      {
        try
        {
          handler(notification.Params.ValueKind == JsonValueKind.Object ? notification.Params : ContextServer.EmptyObject);
        }
        catch (Exception ex)
        {
          ContextDiagnostics.Publish(DiagnosticKind.HandlerFault, "An extension notification handler threw " + ex.GetType().Name + ".", notification.Method, ex);
        }

        return;
      }
    }
  }

  // 2026-07-28 server/utilities/caching: fresh while now < received + ttlMs; a missing, zero or negative ttlMs means never reuse.
  private Result? TryGetFresh(string key)
  {
    if (_cache.TryGetValue(key, out var entry))
    {
      if (_options.TimeProvider.GetElapsedTime(entry.Received).TotalMilliseconds < entry.TtlMs)
      {
        return entry.Result;
      }

      _cache.TryRemove(key, out _);
    }

    return null;
  }

  private void Store(string key, Result result)
  {
    if (result is CacheableResult { TtlMs: > 0 } cacheable && cacheable.EffectiveResultType == WireNames.ResultTypeComplete)
    {
      _cache[key] = new CacheEntry(result, _options.TimeProvider.GetTimestamp(), cacheable.TtlMs.Value);
    }
  }

  private void DropCached(string method)
  {
    var prefix = method + "\0";
    foreach (var key in _cache.Keys)
    {
      if (key.StartsWith(prefix, StringComparison.Ordinal))
      {
        _cache.TryRemove(key, out _);
      }
    }
  }

  private sealed record CacheEntry(Result Result, long Received, long TtlMs);

  // Server-initiated requests (legacy sampling, roots and elicitation) go to the handler the application supplied.
  private async ValueTask<JsonRpcMessage?> HandleServerRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken)
  {
    if (request.Method == Methods.Ping && Era == ProtocolEra.Legacy)
    {
      // utilities/ping: either side may ping, and the receiver answers at once with an empty result.
      return new JsonRpcResultResponse(request.Id, ContextServer.EmptyObject);
    }

    if (_handler is not { } handler)
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
