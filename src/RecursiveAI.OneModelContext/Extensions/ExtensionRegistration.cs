using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;

namespace RecursiveAI.OneModelContext.Extensions;

/// <summary>Answers a request an extension added to the protocol.</summary>
/// <param name="parameters">The request's <c>params</c> as received (with its <c>_meta</c>), or an empty object when there were none.</param>
/// <param name="context">The request; <see cref="RequestContext.SupportsExtension"/> and the settings of the peer are available on it.</param>
/// <returns>The members of the result object, as a JSON object. <c>_meta</c> and <c>resultType</c> in it are honoured; the rest is sent as is.</returns>
public delegate ValueTask<JsonElement> ExtensionRequestHandler(JsonElement parameters, RequestContext context);

/// <summary>Handles a notification an extension added to the protocol.</summary>
/// <param name="parameters">The notification's <c>params</c>, or an empty object when there were none.</param>
/// <param name="peer">What is known of the sender: its era, revision and the capabilities it declared.</param>
/// <param name="cancellationToken">Cancelled when the connection closes.</param>
public delegate ValueTask ExtensionNotificationHandler(JsonElement parameters, PeerContext peer, CancellationToken cancellationToken);

/// <summary>
/// Reshapes a tool listing entry for one caller. Return the entry to send (the same object when nothing changes), or <c>null</c> to hide the
/// tool from this caller. Also decides whether the caller may call the tool: a hidden tool is unknown to it.
/// </summary>
/// <param name="tool">The listing entry.</param>
/// <param name="context">The request, for what the caller declared.</param>
public delegate Tool? ToolAdapter(Tool tool, RequestContext context);

/// <summary>
/// An extension a server offers: its identifier, settings, the core revisions it works on, the requests and notifications it adds, and the
/// <c>_meta</c> keys it uses. Extensions are off by default: a server advertises one only when its registration is added to
/// <see cref="ContextServer.Extensions"/>.
/// </summary>
/// <remarks>
/// <para>
/// Negotiation (extensions overview): on 2026-07-28 the client lists the extensions it supports in <c>_meta</c> on every request and the
/// server lists its own in the <c>server/discover</c> result; on 2025-11-25 and earlier both sides carry them in
/// <c>capabilities.extensions</c> of <c>initialize</c>. The same registration serves both.
/// </para>
/// <para>
/// Requests and notifications an extension adds are answered only for peers that declared the extension. For any other peer the request is
/// as if the method did not exist (-32601), unless <see cref="Required"/> is set. Handlers can read the peer's settings from the context.
/// </para>
/// </remarks>
public sealed class ExtensionRegistration
{
  private static readonly HashSet<string> CoreMethods = new(StringComparer.Ordinal)
  {
    Methods.ServerDiscover, Methods.Initialize, Methods.Ping, Methods.ToolsList, Methods.ToolsCall, Methods.ResourcesList,
    Methods.ResourcesTemplatesList, Methods.ResourcesRead, Methods.ResourcesSubscribe, Methods.ResourcesUnsubscribe, Methods.PromptsList,
    Methods.PromptsGet, Methods.CompletionComplete, Methods.LoggingSetLevel, Methods.SubscriptionsListen, Methods.SamplingCreateMessage,
    Methods.RootsList, Methods.ElicitationCreate,
  };

  private readonly Dictionary<string, ExtensionRequestHandler> _requests = new(StringComparer.Ordinal);
  private readonly Dictionary<string, ExtensionNotificationHandler> _notifications = new(StringComparer.Ordinal);
  private volatile bool _frozen;

  /// <summary>Creates a registration.</summary>
  /// <param name="id">The identifier, <c>{vendor-prefix}/{name}</c>. The vendor prefix is a reversed domain name you own; <c>io.modelcontextprotocol</c> and other protocol-reserved prefixes are refused.</param>
  /// <param name="settings">The settings object advertised to peers; <c>default</c> advertises an empty object.</param>
  /// <param name="revisions">The core revisions the extension works on, for example <see cref="ProtocolVersions.V20260728"/>; <c>null</c> means every revision.</param>
  /// <exception cref="ArgumentException">The identifier is not well formed or is reserved, or the settings are not a JSON object.</exception>
  public ExtensionRegistration(string id, JsonElement settings = default, IReadOnlyList<string>? revisions = null)
    : this(id, settings, revisions, official: false)
  {
  }

  internal ExtensionRegistration(string id, JsonElement settings, IReadOnlyList<string>? revisions, bool official)
  {
    if (!ExtensionId.IsValid(id, out var reason))
    {
      throw new ArgumentException("'" + id + "' is not a valid extension identifier. " + reason, nameof(id));
    }

    if (!official && ExtensionId.IsReserved(id))
    {
      throw new ArgumentException("'" + id + "' uses a vendor prefix the protocol reserves for official extensions. Use a reversed domain name you own.", nameof(id));
    }

    if (settings.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
    {
      throw new ArgumentException("An extension's settings are a JSON object.", nameof(settings));
    }

    Id = id;
    Settings = settings.ValueKind == JsonValueKind.Object ? settings.Clone() : ContextServer.EmptyObject;
    Revisions = revisions is null ? null : [.. revisions];
  }

  /// <summary>Creates a registration whose settings come from a record, serialized with source-generated metadata.</summary>
  /// <typeparam name="T">The settings type.</typeparam>
  /// <param name="id">The identifier; see <see cref="ExtensionRegistration(string, JsonElement, IReadOnlyList{string})"/>.</param>
  /// <param name="settings">The settings.</param>
  /// <param name="typeInfo">Source-generated metadata for <typeparamref name="T"/>.</param>
  /// <param name="revisions">The core revisions the extension works on; <c>null</c> means every revision.</param>
  public static ExtensionRegistration Create<T>(string id, T settings, JsonTypeInfo<T> typeInfo, IReadOnlyList<string>? revisions = null)
  {
    ArgumentNullException.ThrowIfNull(typeInfo);
    return new ExtensionRegistration(id, JsonSerializer.SerializeToElement(settings, typeInfo), revisions);
  }

  /// <summary>The identifier.</summary>
  public string Id { get; }

  /// <summary>The settings object advertised to peers.</summary>
  public JsonElement Settings { get; }

  /// <summary>The core revisions the extension works on, or <c>null</c> for every revision. A peer on another revision is treated as not supporting it.</summary>
  public IReadOnlyList<string>? Revisions { get; }

  /// <summary>
  /// When true, a request from a peer that did not declare the extension is refused with error -32021 (<c>MissingRequiredClientCapability</c>)
  /// naming the extension. Default <c>false</c>: the server degrades to core behaviour. Legacy revisions have no -32021, so a legacy peer gets
  /// -32602 (invalid params) with the same message.
  /// </summary>
  public bool Required { get; init; }

  /// <summary>
  /// The <c>_meta</c> keys the extension adds, for documentation and inventory. They are not checked. Keys follow the same rules as the
  /// extension identifier.
  /// </summary>
  public IReadOnlyList<string> MetaKeys { get; init; } = [];

  /// <summary>Reshapes tool listings per caller (the way Apps hides its metadata from hosts that do not render it). Default none.</summary>
  public ToolAdapter? ToolAdapter { get; init; }

  /// <summary>The requests the extension adds.</summary>
  public IReadOnlyCollection<string> RequestMethods => _requests.Keys;

  /// <summary>The notifications the extension adds.</summary>
  public IReadOnlyCollection<string> NotificationMethods => _notifications.Keys;

  /// <summary>Adds a request the extension answers.</summary>
  /// <param name="method">The method, for example <c>tasks/get</c>. It must not be a core method.</param>
  /// <param name="handler">Answers it.</param>
  /// <returns>This registration.</returns>
  /// <exception cref="ArgumentException">The method is empty, a core method, or already added.</exception>
  /// <exception cref="InvalidOperationException">The registration is already in use by a server.</exception>
  public ExtensionRegistration Request(string method, ExtensionRequestHandler handler)
  {
    ArgumentNullException.ThrowIfNull(handler);
    CheckMethod(method);
    if (CoreMethods.Contains(method))
    {
      throw new ArgumentException("'" + method + "' is a core method; an extension adds new ones.", nameof(method));
    }

    if (!_requests.TryAdd(method, handler))
    {
      throw new ArgumentException("'" + method + "' is already added.", nameof(method));
    }

    return this;
  }

  /// <summary>Adds a notification the extension handles.</summary>
  /// <param name="method">The method, for example <c>notifications/tasks/status</c>.</param>
  /// <param name="handler">Handles it. An exception it throws is reported as a diagnostic and otherwise ignored.</param>
  /// <returns>This registration.</returns>
  /// <exception cref="ArgumentException">The method is empty or already added.</exception>
  /// <exception cref="InvalidOperationException">The registration is already in use by a server.</exception>
  public ExtensionRegistration Notification(string method, ExtensionNotificationHandler handler)
  {
    ArgumentNullException.ThrowIfNull(handler);
    CheckMethod(method);
    if (!_notifications.TryAdd(method, handler))
    {
      throw new ArgumentException("'" + method + "' is already added.", nameof(method));
    }

    return this;
  }

  /// <summary>True when the extension works on the revision (every revision when <see cref="Revisions"/> is null).</summary>
  /// <param name="protocolVersion">The revision.</param>
  public bool SupportsRevision(string protocolVersion)
  {
    if (Revisions is null)
    {
      return true;
    }

    foreach (var revision in Revisions)
    {
      if (revision == protocolVersion)
      {
        return true;
      }
    }

    return false;
  }

  internal bool TryGetRequest(string method, out ExtensionRequestHandler handler) => _requests.TryGetValue(method, out handler!);

  internal bool TryGetNotification(string method, out ExtensionNotificationHandler handler) => _notifications.TryGetValue(method, out handler!);

  internal void Freeze() => _frozen = true;

  private void CheckMethod(string method)
  {
    ArgumentException.ThrowIfNullOrEmpty(method);
    if (_frozen)
    {
      throw new InvalidOperationException("The extension is already registered with a server; add its methods first.");
    }
  }
}

/// <summary>
/// An extension a client declares to servers. Off by default: a client sends an extension only when it is listed in
/// <see cref="ContextClientOptions.Extensions"/>.
/// </summary>
public sealed class ClientExtension
{
  private readonly Dictionary<string, Action<JsonElement>> _notifications = new(StringComparer.Ordinal);

  /// <summary>Creates a client extension.</summary>
  /// <param name="id">The identifier, <c>{vendor-prefix}/{name}</c>.</param>
  /// <param name="settings">The settings object declared to servers; <c>default</c> declares an empty object.</param>
  /// <exception cref="ArgumentException">The identifier is not well formed or is reserved, or the settings are not a JSON object.</exception>
  public ClientExtension(string id, JsonElement settings = default)
    : this(id, settings, official: false)
  {
  }

  internal ClientExtension(string id, JsonElement settings, bool official)
  {
    if (!ExtensionId.IsValid(id, out var reason))
    {
      throw new ArgumentException("'" + id + "' is not a valid extension identifier. " + reason, nameof(id));
    }

    if (!official && ExtensionId.IsReserved(id))
    {
      throw new ArgumentException("'" + id + "' uses a vendor prefix the protocol reserves for official extensions. Use a reversed domain name you own.", nameof(id));
    }

    if (settings.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
    {
      throw new ArgumentException("An extension's settings are a JSON object.", nameof(settings));
    }

    Id = id;
    Settings = settings.ValueKind == JsonValueKind.Object ? settings.Clone() : ContextServer.EmptyObject;
  }

  /// <summary>Creates a client extension whose settings come from a record, serialized with source-generated metadata.</summary>
  /// <typeparam name="T">The settings type.</typeparam>
  /// <param name="id">The identifier.</param>
  /// <param name="settings">The settings.</param>
  /// <param name="typeInfo">Source-generated metadata for <typeparamref name="T"/>.</param>
  public static ClientExtension Create<T>(string id, T settings, JsonTypeInfo<T> typeInfo)
  {
    ArgumentNullException.ThrowIfNull(typeInfo);
    return new ClientExtension(id, JsonSerializer.SerializeToElement(settings, typeInfo));
  }

  /// <summary>The identifier.</summary>
  public string Id { get; }

  /// <summary>The settings object declared to servers.</summary>
  public JsonElement Settings { get; }

  /// <summary>
  /// When true, <see cref="ContextClient.ConnectAsync"/> fails with <see cref="ExtensionRequiredException"/> if the server does not offer the
  /// extension. Default <c>false</c>: the client works with the server's core behaviour.
  /// </summary>
  public bool Required { get; init; }

  /// <summary>Handles a notification the server sends for this extension. It runs on the connection's read loop, so it should return quickly.</summary>
  /// <param name="method">The notification method.</param>
  /// <param name="handler">Receives its <c>params</c>.</param>
  /// <returns>This extension.</returns>
  public ClientExtension Notification(string method, Action<JsonElement> handler)
  {
    ArgumentException.ThrowIfNullOrEmpty(method);
    ArgumentNullException.ThrowIfNull(handler);
    if (!_notifications.TryAdd(method, handler))
    {
      throw new ArgumentException("'" + method + "' is already added.", nameof(method));
    }

    return this;
  }

  internal bool TryGetNotification(string method, out Action<JsonElement> handler) => _notifications.TryGetValue(method, out handler!);
}

/// <summary>A client asked for an extension (<see cref="ClientExtension.Required"/>) that the server does not offer.</summary>
public sealed class ExtensionRequiredException : ContextException
{
  /// <summary>Creates the exception.</summary>
  /// <param name="extensionId">The extension the server lacks.</param>
  public ExtensionRequiredException(string extensionId)
    : base("The server does not support the required extension '" + extensionId + "'.")
  {
    ExtensionId = extensionId;
  }

  /// <summary>The extension the server lacks.</summary>
  public string ExtensionId { get; }
}

/// <summary>
/// The result of a request an extension added: the members the handler returned, kept as they are. Read them from
/// <see cref="WireObject.AdditionalProperties"/>.
/// </summary>
public sealed class ExtensionResult : Result;
