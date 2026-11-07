using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Extensions.Apps;

// MCP Apps (extension io.modelcontextprotocol/ui, stable revision 2026-01-26): the metadata a server puts on tools and UI resources.
// Every record keeps members it does not model, so a newer revision's fields survive a decode and re-encode.

/// <summary>The settings a client declares for the Apps extension (<c>capabilities.extensions["io.modelcontextprotocol/ui"]</c>).</summary>
public sealed class AppsSettings : WireObject
{
  /// <summary>The content types the client can render, for example <c>text/html;profile=mcp-app</c>. Required by the extension.</summary>
  [JsonPropertyName("mimeTypes")]
  public List<string>? MimeTypes { get; init; }
}

/// <summary>Who may use a tool (<c>_meta.ui.visibility</c>).</summary>
public static class UiVisibility
{
  /// <summary>The tool is visible to and callable by the model.</summary>
  public const string Model = "model";

  /// <summary>The tool is callable by the app from the same server connection.</summary>
  public const string App = "app";
}

/// <summary>The <c>_meta.ui</c> object of a tool: the UI resource that renders its results and who may call it.</summary>
public sealed class UiToolMeta : WireObject
{
  /// <summary>The <c>ui://</c> resource that renders the tool's results.</summary>
  [JsonPropertyName("resourceUri")]
  public string? ResourceUri { get; init; }

  /// <summary>Who may use the tool: <see cref="UiVisibility.Model"/> and/or <see cref="UiVisibility.App"/>. Absent means both.</summary>
  [JsonPropertyName("visibility")]
  public List<string>? Visibility { get; init; }

  /// <summary>True when the model may see and call the tool (absent visibility means both).</summary>
  [JsonIgnore]
  public bool VisibleToModel => Visibility is null || Visibility.Contains(UiVisibility.Model);

  /// <summary>True when an app may call the tool (absent visibility means both).</summary>
  [JsonIgnore]
  public bool VisibleToApp => Visibility is null || Visibility.Contains(UiVisibility.App);
}

/// <summary>A browser capability a UI asks for. An empty object on the wire; a grant is only a request, and the host decides.</summary>
public sealed class UiPermissionRequest : WireObject;

/// <summary>Sandbox permissions a UI asks for (<c>_meta.ui.permissions</c>), mapped by hosts to Permission Policy features.</summary>
public sealed class UiPermissions : WireObject
{
  /// <summary>Camera access.</summary>
  [JsonPropertyName("camera")]
  public UiPermissionRequest? Camera { get; init; }

  /// <summary>Microphone access.</summary>
  [JsonPropertyName("microphone")]
  public UiPermissionRequest? Microphone { get; init; }

  /// <summary>Geolocation access.</summary>
  [JsonPropertyName("geolocation")]
  public UiPermissionRequest? Geolocation { get; init; }

  /// <summary>Clipboard write access.</summary>
  [JsonPropertyName("clipboardWrite")]
  public UiPermissionRequest? ClipboardWrite { get; init; }
}

/// <summary>The origins a UI needs beyond itself (<c>_meta.ui.csp</c>). Empty or absent means none, which is the secure default.</summary>
public sealed class UiResourceCsp : WireObject
{
  /// <summary>Origins for network requests (CSP <c>connect-src</c>).</summary>
  [JsonPropertyName("connectDomains")]
  public List<string>? ConnectDomains { get; init; }

  /// <summary>Origins for scripts, styles, images, fonts and media (<c>script-src</c>, <c>style-src</c>, <c>img-src</c>, <c>font-src</c>, <c>media-src</c>). <c>https://*.example.com</c> is allowed.</summary>
  [JsonPropertyName("resourceDomains")]
  public List<string>? ResourceDomains { get; init; }

  /// <summary>Origins for nested iframes (<c>frame-src</c>).</summary>
  [JsonPropertyName("frameDomains")]
  public List<string>? FrameDomains { get; init; }

  /// <summary>Allowed base URIs (<c>base-uri</c>).</summary>
  [JsonPropertyName("baseUriDomains")]
  public List<string>? BaseUriDomains { get; init; }
}

/// <summary>The <c>_meta.ui</c> object of a UI resource's contents: security and rendering configuration.</summary>
public sealed class UiResourceMeta : WireObject
{
  /// <summary>Content Security Policy configuration.</summary>
  [JsonPropertyName("csp")]
  public UiResourceCsp? Csp { get; init; }

  /// <summary>Sandbox permissions requested.</summary>
  [JsonPropertyName("permissions")]
  public UiPermissions? Permissions { get; init; }

  /// <summary>A dedicated origin for the view; the format is host-dependent.</summary>
  [JsonPropertyName("domain")]
  public string? Domain { get; init; }

  /// <summary>Whether the host should draw a border and background; absent leaves it to the host.</summary>
  [JsonPropertyName("prefersBorder")]
  public bool? PrefersBorder { get; init; }
}

/// <summary>A UI resource as read from a server: the HTML document and how to sandbox it.</summary>
public sealed class UiResource
{
  /// <summary>The <c>ui://</c> URI.</summary>
  public required string Uri { get; init; }

  /// <summary>The content type, always <see cref="AppsExtension.MimeType"/>.</summary>
  public required string MimeType { get; init; }

  /// <summary>The HTML document (decoded from <c>blob</c> when the server sent it that way). Untrusted: render it only in a sandbox.</summary>
  public required string Html { get; init; }

  /// <summary>The CSP, permissions and display preferences the server declared, or null when it declared none.</summary>
  public UiResourceMeta? Meta { get; init; }
}
