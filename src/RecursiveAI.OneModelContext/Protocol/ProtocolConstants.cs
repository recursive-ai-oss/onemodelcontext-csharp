using System;
using System.Collections.Generic;

namespace RecursiveAI.OneModelContext.Protocol;

/// <summary>The two families of protocol revisions OneModelContext speaks. One API sits on top; the era is a codec detail.</summary>
public enum ProtocolEra
{
  /// <summary>Revision 2026-07-28: stateless; version, capabilities and client info travel in each request's <c>_meta</c>.</summary>
  Modern,

  /// <summary>Revisions 2024-11-05 to 2025-11-25: an <c>initialize</c> handshake, then a session.</summary>
  Legacy,
}

/// <summary>Protocol revision identifiers and the default supported set.</summary>
public static class ProtocolVersions
{
  /// <summary>Revision 2026-07-28, the newest, negotiated first.</summary>
  public const string V20260728 = "2026-07-28";

  /// <summary>Revision 2025-11-25.</summary>
  public const string V20251125 = "2025-11-25";

  /// <summary>Revision 2025-06-18.</summary>
  public const string V20250618 = "2025-06-18";

  /// <summary>Revision 2025-03-26, the only revision that allows JSON-RPC batches.</summary>
  public const string V20250326 = "2025-03-26";

  /// <summary>Revision 2024-11-05.</summary>
  public const string V20241105 = "2024-11-05";

  /// <summary>The newest revision.</summary>
  public const string Latest = V20260728;

  /// <summary>The newest legacy revision.</summary>
  public const string LatestLegacy = V20251125;

  /// <summary>The revisions negotiated by default, newest first. A draft revision is never in this set.</summary>
  public static IReadOnlyList<string> Supported { get; } = [V20260728, V20251125, V20250618, V20250326, V20241105];

  /// <summary>The legacy revisions of <see cref="Supported"/>, newest first.</summary>
  public static IReadOnlyList<string> SupportedLegacy { get; } = [V20251125, V20250618, V20250326, V20241105];

  /// <summary>Whether a version string is one of the revisions OneModelContext knows.</summary>
  /// <param name="version">A revision identifier.</param>
  public static bool IsKnown(string? version) => version is V20260728 or V20251125 or V20250618 or V20250326 or V20241105;

  /// <summary>The era a known revision belongs to.</summary>
  /// <param name="version">A revision identifier from <see cref="Supported"/>.</param>
  /// <exception cref="ArgumentException">The version is not a known revision.</exception>
  public static ProtocolEra EraOf(string version)
  {
    if (version == V20260728)
    {
      return ProtocolEra.Modern;
    }

    return IsKnown(version) ? ProtocolEra.Legacy : throw new ArgumentException("Unknown protocol revision '" + version + "'.", nameof(version));
  }

  /// <summary>Whether the revision allows JSON-RPC batch arrays (only 2025-03-26 does).</summary>
  /// <param name="version">A revision identifier.</param>
  public static bool AllowsBatches(string? version) => version == V20250326;
}

/// <summary>Protocol error codes beyond those of JSON-RPC 2.0.</summary>
public static class ProtocolErrorCodes
{
  /// <summary>2026-07-28: the HTTP headers do not match the request body (-32020).</summary>
  public const int HeaderMismatch = -32020;

  /// <summary>2026-07-28: the client lacks a capability the request needs (-32021); <c>data.requiredCapabilities</c> lists them.</summary>
  public const int MissingRequiredClientCapability = -32021;

  /// <summary>2026-07-28: the requested protocol version is not supported (-32022); <c>data</c> has <c>supported</c> and <c>requested</c>.</summary>
  public const int UnsupportedProtocolVersion = -32022;

  /// <summary>2025-11-25 and earlier: a resource was not found (-32002). Replaced by -32602 in 2026-07-28.</summary>
  public const int ResourceNotFoundLegacy = -32002;

  /// <summary>2026-07-28: a resource was not found, reported as invalid params (-32602).</summary>
  public const int ResourceNotFoundModern = -32602;

  /// <summary>2025-11-25 only: the request needs a URL-mode elicitation first (-32042).</summary>
  public const int UrlElicitationRequired = -32042;

  /// <summary>The resource-not-found code of an era.</summary>
  /// <param name="era">The era of the connection.</param>
  public static int ResourceNotFound(ProtocolEra era) => era == ProtocolEra.Modern ? ResourceNotFoundModern : ResourceNotFoundLegacy;
}

/// <summary>Method names. Several exist in one era only; the codec decides which are available.</summary>
public static class Methods
{
  /// <summary>Modern: asks a server which versions and capabilities it has.</summary>
  public const string ServerDiscover = "server/discover";

  /// <summary>Legacy: starts a session.</summary>
  public const string Initialize = "initialize";

  /// <summary>Legacy notification: the client finished initializing.</summary>
  public const string Initialized = "notifications/initialized";

  /// <summary>Legacy: liveness check.</summary>
  public const string Ping = "ping";

  /// <summary>Lists tools.</summary>
  public const string ToolsList = "tools/list";

  /// <summary>Calls a tool.</summary>
  public const string ToolsCall = "tools/call";

  /// <summary>Lists resources.</summary>
  public const string ResourcesList = "resources/list";

  /// <summary>Lists resource templates.</summary>
  public const string ResourcesTemplatesList = "resources/templates/list";

  /// <summary>Reads a resource.</summary>
  public const string ResourcesRead = "resources/read";

  /// <summary>Legacy: subscribes to updates of one resource.</summary>
  public const string ResourcesSubscribe = "resources/subscribe";

  /// <summary>Legacy: ends such a subscription.</summary>
  public const string ResourcesUnsubscribe = "resources/unsubscribe";

  /// <summary>Lists prompts.</summary>
  public const string PromptsList = "prompts/list";

  /// <summary>Gets a prompt.</summary>
  public const string PromptsGet = "prompts/get";

  /// <summary>Completes an argument value.</summary>
  public const string CompletionComplete = "completion/complete";

  /// <summary>Legacy: sets the minimum log level.</summary>
  public const string LoggingSetLevel = "logging/setLevel";

  /// <summary>Modern: opens a stream of list-changed and resource-updated notifications.</summary>
  public const string SubscriptionsListen = "subscriptions/listen";

  /// <summary>Server-to-client sampling request (deprecated in 2026-07-28).</summary>
  public const string SamplingCreateMessage = "sampling/createMessage";

  /// <summary>Server-to-client roots request (deprecated in 2026-07-28).</summary>
  public const string RootsList = "roots/list";

  /// <summary>Server-to-client elicitation request.</summary>
  public const string ElicitationCreate = "elicitation/create";

  /// <summary>Notification: a request was cancelled.</summary>
  public const string NotificationsCancelled = "notifications/cancelled";

  /// <summary>Notification: progress on a request.</summary>
  public const string NotificationsProgress = "notifications/progress";

  /// <summary>Notification: a log message.</summary>
  public const string NotificationsMessage = "notifications/message";

  /// <summary>Notification: the tool list changed.</summary>
  public const string NotificationsToolsListChanged = "notifications/tools/list_changed";

  /// <summary>Notification: the resource list changed.</summary>
  public const string NotificationsResourcesListChanged = "notifications/resources/list_changed";

  /// <summary>Notification: the prompt list changed.</summary>
  public const string NotificationsPromptsListChanged = "notifications/prompts/list_changed";

  /// <summary>Notification: a subscribed resource changed.</summary>
  public const string NotificationsResourcesUpdated = "notifications/resources/updated";

  /// <summary>Modern notification: the server accepted a <c>subscriptions/listen</c> filter.</summary>
  public const string NotificationsSubscriptionsAcknowledged = "notifications/subscriptions/acknowledged";

  /// <summary>Legacy notification: the client's roots changed.</summary>
  public const string NotificationsRootsListChanged = "notifications/roots/list_changed";

  /// <summary>Legacy (2025-11-25) notification: a URL-mode elicitation finished.</summary>
  public const string NotificationsElicitationComplete = "notifications/elicitation/complete";
}

/// <summary>Reserved <c>_meta</c> keys and HTTP header names the protocol defines.</summary>
public static class WireNames
{
  /// <summary>2026-07-28 request <c>_meta</c>: the protocol version.</summary>
  public const string MetaProtocolVersion = "io.modelcontextprotocol/protocolVersion";

  /// <summary>2026-07-28 request <c>_meta</c>: the client's implementation info.</summary>
  public const string MetaClientInfo = "io.modelcontextprotocol/clientInfo";

  /// <summary>2026-07-28 request <c>_meta</c>: the client's capabilities.</summary>
  public const string MetaClientCapabilities = "io.modelcontextprotocol/clientCapabilities";

  /// <summary>2026-07-28 request <c>_meta</c>: the minimum log level the client wants.</summary>
  public const string MetaLogLevel = "io.modelcontextprotocol/logLevel";

  /// <summary>2026-07-28 notification <c>_meta</c>: the id of the <c>subscriptions/listen</c> request.</summary>
  public const string MetaSubscriptionId = "io.modelcontextprotocol/subscriptionId";

  /// <summary>2026-07-28 result <c>_meta</c>: the server's implementation info.</summary>
  public const string MetaServerInfo = "io.modelcontextprotocol/serverInfo";

  /// <summary>The <c>resultType</c> of an ordinary result. A result without the field is complete (a legacy server never sends it).</summary>
  public const string ResultTypeComplete = "complete";

  /// <summary>The <c>resultType</c> of a result asking the client for input (multi round-trip requests).</summary>
  public const string ResultTypeInputRequired = "input_required";

  /// <summary>HTTP header carrying the JSON-RPC method (2026-07-28).</summary>
  public const string HeaderMethod = "Mcp-Method";

  /// <summary>HTTP header carrying the tool, prompt or resource name (2026-07-28).</summary>
  public const string HeaderName = "Mcp-Name";

  /// <summary>HTTP header carrying the legacy session id.</summary>
  public const string HeaderSessionId = "Mcp-Session-Id";

  /// <summary>HTTP header carrying the negotiated version on legacy requests after <c>initialize</c>.</summary>
  public const string HeaderProtocolVersion = "MCP-Protocol-Version";
}

/// <summary>Log levels of the logging utility, least to most severe (RFC 5424 names).</summary>
public static class LoggingLevels
{
  /// <summary>Debug.</summary>
  public const string Debug = "debug";

  /// <summary>Info.</summary>
  public const string Info = "info";

  /// <summary>Notice.</summary>
  public const string Notice = "notice";

  /// <summary>Warning.</summary>
  public const string Warning = "warning";

  /// <summary>Error.</summary>
  public const string Error = "error";

  /// <summary>Critical.</summary>
  public const string Critical = "critical";

  /// <summary>Alert.</summary>
  public const string Alert = "alert";

  /// <summary>Emergency.</summary>
  public const string Emergency = "emergency";
}
