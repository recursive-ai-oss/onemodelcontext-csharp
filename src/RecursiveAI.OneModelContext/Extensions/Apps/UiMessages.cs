using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Extensions.Apps;

// The JSON-RPC dialect a View (the sandboxed app) and its Host speak over postMessage (ext-apps stable specification 2026-01-26,
// "Communication Protocol" to "MCP Apps Specific Messages"). These are the typed shapes only: a host that renders Apps bridges them to
// its iframe and to a ContextClient; this library renders nothing and carries no transport for them.

/// <summary>Method names of the View-to-Host dialect.</summary>
public static class UiMethods
{
  /// <summary>Request, View to Host: the handshake. Answered with <see cref="UiInitializeResult"/>.</summary>
  public const string Initialize = "ui/initialize";

  /// <summary>Notification, View to Host: the View finished initializing; the Host sends nothing before it.</summary>
  public const string Initialized = "ui/notifications/initialized";

  /// <summary>Request, View to Host: open an external URL (<see cref="UiOpenLinkParams"/>).</summary>
  public const string OpenLink = "ui/open-link";

  /// <summary>Request, View to Host: add a message to the conversation (<see cref="UiMessageParams"/>).</summary>
  public const string Message = "ui/message";

  /// <summary>Request, View to Host: change the display mode (<see cref="UiRequestDisplayModeParams"/>).</summary>
  public const string RequestDisplayMode = "ui/request-display-mode";

  /// <summary>Request, View to Host: replace the model context (<see cref="UiUpdateModelContextParams"/>).</summary>
  public const string UpdateModelContext = "ui/update-model-context";

  /// <summary>Notification, View to Host: the View's size changed (<see cref="UiSizeChangedParams"/>).</summary>
  public const string SizeChanged = "ui/notifications/size-changed";

  /// <summary>Notification, Host to View: the complete tool arguments (<see cref="UiToolInputParams"/>).</summary>
  public const string ToolInput = "ui/notifications/tool-input";

  /// <summary>Notification, Host to View: streamed, possibly incomplete tool arguments (<see cref="UiToolInputParams"/>).</summary>
  public const string ToolInputPartial = "ui/notifications/tool-input-partial";

  /// <summary>Notification, Host to View: the tool result; its params are a <see cref="CallToolResult"/>.</summary>
  public const string ToolResult = "ui/notifications/tool-result";

  /// <summary>Notification, Host to View: the tool run was cancelled (<see cref="UiToolCancelledParams"/>).</summary>
  public const string ToolCancelled = "ui/notifications/tool-cancelled";

  /// <summary>Request, Host to View: the View is about to be torn down (<see cref="UiResourceTeardownParams"/>).</summary>
  public const string ResourceTeardown = "ui/resource-teardown";

  /// <summary>Notification, Host to View: part of the host context changed (<see cref="UiHostContext"/>, partial).</summary>
  public const string HostContextChanged = "ui/notifications/host-context-changed";

  /// <summary>Notification, sandbox proxy to Host: the proxy is ready for <see cref="SandboxResourceReady"/>.</summary>
  public const string SandboxProxyReady = "ui/notifications/sandbox-proxy-ready";

  /// <summary>Notification, Host to sandbox proxy: the HTML to load (<see cref="UiSandboxResourceReadyParams"/>).</summary>
  public const string SandboxResourceReady = "ui/notifications/sandbox-resource-ready";

  /// <summary>
  /// True for a method the sandbox proxy consumes itself and never forwards (the spec's "sandbox" reserved prefix).
  /// </summary>
  /// <param name="method">The method.</param>
  public static bool IsSandboxReserved(string method) =>
    method.StartsWith("ui/notifications/sandbox-", System.StringComparison.Ordinal);
}

/// <summary>The display modes a View or Host may support.</summary>
public static class UiDisplayModes
{
  /// <summary>Embedded in the host's content flow.</summary>
  public const string Inline = "inline";

  /// <summary>Taking over the full screen or window.</summary>
  public const string Fullscreen = "fullscreen";

  /// <summary>A floating picture-in-picture overlay.</summary>
  public const string PictureInPicture = "pip";
}

/// <summary>Params of <c>ui/initialize</c>.</summary>
public sealed class UiInitializeRequestParams : WireObject
{
  /// <summary>The Apps specification revision the View speaks, for example <c>2026-01-26</c>.</summary>
  [JsonPropertyName("protocolVersion")]
  public string? ProtocolVersion { get; init; }

  /// <summary>Who the View is. (The specification's examples name it <c>clientInfo</c>.)</summary>
  [JsonPropertyName("clientInfo")]
  public Implementation? ClientInfo { get; init; }

  /// <summary>What the View supports; the specification says the View MUST include it.</summary>
  [JsonPropertyName("appCapabilities")]
  public UiAppCapabilities? AppCapabilities { get; init; }
}

/// <summary>The <c>tools</c> capability of a View or <c>serverTools</c>/<c>serverResources</c> of a Host.</summary>
public sealed class UiListChangedCapability : WireObject
{
  /// <summary>Supports list-changed notifications.</summary>
  [JsonPropertyName("listChanged")]
  public bool? ListChanged { get; init; }
}

/// <summary>What a View supports (<c>appCapabilities</c> of <c>ui/initialize</c>).</summary>
public sealed class UiAppCapabilities : WireObject
{
  /// <summary>Experimental features; structure not defined.</summary>
  [JsonPropertyName("experimental")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public JsonElement Experimental { get; init; }

  /// <summary>The View exposes MCP-style tools the Host can call.</summary>
  [JsonPropertyName("tools")]
  public UiListChangedCapability? Tools { get; init; }

  /// <summary>The display modes the View supports (<see cref="UiDisplayModes"/>).</summary>
  [JsonPropertyName("availableDisplayModes")]
  public List<string>? AvailableDisplayModes { get; init; }
}

/// <summary>Result of <c>ui/initialize</c>.</summary>
public sealed class UiInitializeResult : WireObject
{
  /// <summary>The Apps specification revision in force.</summary>
  [JsonPropertyName("protocolVersion")]
  public string? ProtocolVersion { get; init; }

  /// <summary>What the Host supports.</summary>
  [JsonPropertyName("hostCapabilities")]
  public UiHostCapabilities? HostCapabilities { get; init; }

  /// <summary>Who the Host is.</summary>
  [JsonPropertyName("hostInfo")]
  public Implementation? HostInfo { get; init; }

  /// <summary>Context for the View: theme, display mode, sizes and the tool call it renders.</summary>
  [JsonPropertyName("hostContext")]
  public UiHostContext? HostContext { get; init; }
}

/// <summary>Sandbox configuration the Host applied, inside <see cref="UiHostCapabilities"/>.</summary>
public sealed class UiHostSandbox : WireObject
{
  /// <summary>Permissions the Host granted.</summary>
  [JsonPropertyName("permissions")]
  public UiPermissions? Permissions { get; init; }

  /// <summary>Domains the Host approved.</summary>
  [JsonPropertyName("csp")]
  public UiResourceCsp? Csp { get; init; }
}

/// <summary>What a Host supports (<c>hostCapabilities</c>).</summary>
public sealed class UiHostCapabilities : WireObject
{
  /// <summary>Experimental features; structure not defined.</summary>
  [JsonPropertyName("experimental")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public JsonElement Experimental { get; init; }

  /// <summary>The Host opens external URLs (<see cref="UiMethods.OpenLink"/>).</summary>
  [JsonPropertyName("openLinks")]
  public UiPermissionRequest? OpenLinks { get; init; }

  /// <summary>The Host proxies tool calls to the server.</summary>
  [JsonPropertyName("serverTools")]
  public UiListChangedCapability? ServerTools { get; init; }

  /// <summary>The Host proxies resource reads to the server.</summary>
  [JsonPropertyName("serverResources")]
  public UiListChangedCapability? ServerResources { get; init; }

  /// <summary>The Host accepts log messages.</summary>
  [JsonPropertyName("logging")]
  public UiPermissionRequest? Logging { get; init; }

  /// <summary>The sandbox the Host applied.</summary>
  [JsonPropertyName("sandbox")]
  public UiHostSandbox? Sandbox { get; init; }
}

/// <summary>The tool call that created a View.</summary>
public sealed class UiToolInfo : WireObject
{
  /// <summary>The JSON-RPC id of the <c>tools/call</c> request.</summary>
  [JsonPropertyName("id")]
  public JsonRpcId? Id { get; init; }

  /// <summary>The tool: name, input schema and so on.</summary>
  [JsonPropertyName("tool")]
  public Tool? Tool { get; init; }
}

/// <summary>CSS the Host supplies, inside <see cref="UiHostStyles"/>.</summary>
public sealed class UiHostCss : WireObject
{
  /// <summary>Font loading CSS (<c>@font-face</c> or <c>@import</c>). Untrusted text from the Host's own side; the View applies it to its document.</summary>
  [JsonPropertyName("fonts")]
  public string? Fonts { get; init; }
}

/// <summary>Theming the Host supplies.</summary>
public sealed class UiHostStyles : WireObject
{
  /// <summary>CSS custom properties by name, for example <c>--color-background-primary</c>.</summary>
  [JsonPropertyName("variables")]
  public Dictionary<string, string>? Variables { get; init; }

  /// <summary>CSS blocks the View may inject.</summary>
  [JsonPropertyName("css")]
  public UiHostCss? Css { get; init; }
}

/// <summary>The size of the container holding a View. A fixed side has <c>height</c>/<c>width</c>; a flexible side has a maximum.</summary>
public sealed class UiContainerDimensions : WireObject
{
  /// <summary>Fixed height in pixels.</summary>
  [JsonPropertyName("height")]
  public double? Height { get; init; }

  /// <summary>Greatest height in pixels when the View decides its own height.</summary>
  [JsonPropertyName("maxHeight")]
  public double? MaxHeight { get; init; }

  /// <summary>Fixed width in pixels.</summary>
  [JsonPropertyName("width")]
  public double? Width { get; init; }

  /// <summary>Greatest width in pixels when the View decides its own width.</summary>
  [JsonPropertyName("maxWidth")]
  public double? MaxWidth { get; init; }
}

/// <summary>Input devices of the Host's platform.</summary>
public sealed class UiDeviceCapabilities : WireObject
{
  /// <summary>Touch input.</summary>
  [JsonPropertyName("touch")]
  public bool? Touch { get; init; }

  /// <summary>Hover input.</summary>
  [JsonPropertyName("hover")]
  public bool? Hover { get; init; }
}

/// <summary>Safe area boundaries in pixels.</summary>
public sealed class UiSafeAreaInsets : WireObject
{
  /// <summary>Top inset.</summary>
  [JsonPropertyName("top")]
  public double Top { get; init; }

  /// <summary>Right inset.</summary>
  [JsonPropertyName("right")]
  public double Right { get; init; }

  /// <summary>Bottom inset.</summary>
  [JsonPropertyName("bottom")]
  public double Bottom { get; init; }

  /// <summary>Left inset.</summary>
  [JsonPropertyName("left")]
  public double Left { get; init; }
}

/// <summary>
/// What a Host tells a View about its surroundings. In <see cref="UiMethods.HostContextChanged"/> only the changed members are present;
/// a View merges them into what it has.
/// </summary>
public sealed class UiHostContext : WireObject
{
  /// <summary>The tool call that created the View.</summary>
  [JsonPropertyName("toolInfo")]
  public UiToolInfo? ToolInfo { get; init; }

  /// <summary><c>light</c> or <c>dark</c>.</summary>
  [JsonPropertyName("theme")]
  public string? Theme { get; init; }

  /// <summary>Theming.</summary>
  [JsonPropertyName("styles")]
  public UiHostStyles? Styles { get; init; }

  /// <summary>How the View is shown now (<see cref="UiDisplayModes"/>).</summary>
  [JsonPropertyName("displayMode")]
  public string? DisplayMode { get; init; }

  /// <summary>The display modes the Host supports.</summary>
  [JsonPropertyName("availableDisplayModes")]
  public List<string>? AvailableDisplayModes { get; init; }

  /// <summary>The container's size.</summary>
  [JsonPropertyName("containerDimensions")]
  public UiContainerDimensions? ContainerDimensions { get; init; }

  /// <summary>The user's language and region (BCP 47).</summary>
  [JsonPropertyName("locale")]
  public string? Locale { get; init; }

  /// <summary>The user's time zone (IANA).</summary>
  [JsonPropertyName("timeZone")]
  public string? TimeZone { get; init; }

  /// <summary>The Host application's identifier.</summary>
  [JsonPropertyName("userAgent")]
  public string? UserAgent { get; init; }

  /// <summary><c>web</c>, <c>desktop</c> or <c>mobile</c>.</summary>
  [JsonPropertyName("platform")]
  public string? Platform { get; init; }

  /// <summary>Input devices.</summary>
  [JsonPropertyName("deviceCapabilities")]
  public UiDeviceCapabilities? DeviceCapabilities { get; init; }

  /// <summary>Safe area boundaries.</summary>
  [JsonPropertyName("safeAreaInsets")]
  public UiSafeAreaInsets? SafeAreaInsets { get; init; }
}

/// <summary>Params of <c>ui/open-link</c>.</summary>
public sealed class UiOpenLinkParams : WireObject
{
  /// <summary>The URL to open. Untrusted input from the View: a Host checks the scheme and asks the user before opening.</summary>
  [JsonPropertyName("url")]
  public required string Url { get; init; }
}

/// <summary>Params of <c>ui/message</c>.</summary>
public sealed class UiMessageParams : WireObject
{
  /// <summary>The role of the message; the specification defines <c>user</c>.</summary>
  [JsonPropertyName("role")]
  public required string Role { get; init; }

  /// <summary>The content, a text block.</summary>
  [JsonPropertyName("content")]
  public required ContentBlock Content { get; init; }
}

/// <summary>Params of <c>ui/request-display-mode</c>.</summary>
public sealed class UiRequestDisplayModeParams : WireObject
{
  /// <summary>The mode the View asks for (<see cref="UiDisplayModes"/>).</summary>
  [JsonPropertyName("mode")]
  public required string Mode { get; init; }
}

/// <summary>Result of <c>ui/request-display-mode</c>.</summary>
public sealed class UiRequestDisplayModeResult : WireObject
{
  /// <summary>The mode in force after the request, which may differ from the one asked for.</summary>
  [JsonPropertyName("mode")]
  public required string Mode { get; init; }
}

/// <summary>Params of <c>ui/update-model-context</c>. Each request replaces the previous context.</summary>
public sealed class UiUpdateModelContextParams : WireObject
{
  /// <summary>Content for the model.</summary>
  [JsonPropertyName("content")]
  public List<ContentBlock>? Content { get; init; }

  /// <summary>Structured content for the model.</summary>
  [JsonPropertyName("structuredContent")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public JsonElement StructuredContent { get; init; }
}

/// <summary>Params of <c>ui/notifications/tool-input</c> and <c>tool-input-partial</c>.</summary>
public sealed class UiToolInputParams : WireObject
{
  /// <summary>The tool arguments. In a partial notification: best-effort recovery of incomplete JSON, so a View must not rely on it.</summary>
  [JsonPropertyName("arguments")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public JsonElement Arguments { get; init; }
}

/// <summary>Params of <c>ui/notifications/tool-cancelled</c>.</summary>
public sealed class UiToolCancelledParams : WireObject
{
  /// <summary>Why the tool run was cancelled.</summary>
  [JsonPropertyName("reason")]
  public string? Reason { get; init; }
}

/// <summary>Params of <c>ui/resource-teardown</c>.</summary>
public sealed class UiResourceTeardownParams : WireObject
{
  /// <summary>Why the View is torn down.</summary>
  [JsonPropertyName("reason")]
  public string? Reason { get; init; }
}

/// <summary>Params of <c>ui/notifications/size-changed</c>.</summary>
public sealed class UiSizeChangedParams : WireObject
{
  /// <summary>Viewport width in pixels.</summary>
  [JsonPropertyName("width")]
  public double? Width { get; init; }

  /// <summary>Viewport height in pixels.</summary>
  [JsonPropertyName("height")]
  public double? Height { get; init; }
}

/// <summary>Params of <c>ui/notifications/sandbox-resource-ready</c>.</summary>
public sealed class UiSandboxResourceReadyParams : WireObject
{
  /// <summary>The raw HTML to load in the inner frame.</summary>
  [JsonPropertyName("html")]
  public required string Html { get; init; }

  /// <summary>An override for the inner iframe's <c>sandbox</c> attribute.</summary>
  [JsonPropertyName("sandbox")]
  public string? Sandbox { get; init; }

  /// <summary>The CSP configuration from the resource's metadata.</summary>
  [JsonPropertyName("csp")]
  public UiResourceCsp? Csp { get; init; }

  /// <summary>The permissions from the resource's metadata.</summary>
  [JsonPropertyName("permissions")]
  public UiPermissions? Permissions { get; init; }
}
