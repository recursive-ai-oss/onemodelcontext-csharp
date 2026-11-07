using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Extensions.Apps;

/// <summary>
/// Source-generated serialization for the MCP Apps records and the View-to-Host messages. Hosts that bridge a View to a server use its type
/// infos; nothing here serializes through reflection.
/// </summary>
[JsonSourceGenerationOptions(
  DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  Converters = [typeof(ContentBlockConverter), typeof(ResourceContentsConverter)],
  GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AppsSettings))]
[JsonSerializable(typeof(UiToolMeta))]
[JsonSerializable(typeof(UiResourceMeta))]
[JsonSerializable(typeof(UiResourceCsp))]
[JsonSerializable(typeof(UiPermissions))]
[JsonSerializable(typeof(UiInitializeRequestParams))]
[JsonSerializable(typeof(UiInitializeResult))]
[JsonSerializable(typeof(UiHostContext))]
[JsonSerializable(typeof(UiOpenLinkParams))]
[JsonSerializable(typeof(UiMessageParams))]
[JsonSerializable(typeof(UiRequestDisplayModeParams))]
[JsonSerializable(typeof(UiRequestDisplayModeResult))]
[JsonSerializable(typeof(UiUpdateModelContextParams))]
[JsonSerializable(typeof(UiToolInputParams))]
[JsonSerializable(typeof(UiToolCancelledParams))]
[JsonSerializable(typeof(UiResourceTeardownParams))]
[JsonSerializable(typeof(UiSizeChangedParams))]
[JsonSerializable(typeof(UiSandboxResourceReadyParams))]
[JsonSerializable(typeof(CallToolResult))]
public sealed partial class AppsJsonContext : JsonSerializerContext;

/// <summary>
/// MCP Apps, extension <c>io.modelcontextprotocol/ui</c> (stable, specification revision 2026-01-26): a server declares <c>ui://</c> HTML
/// resources on its tools, and a host that renders them shows the tool's results in a sandboxed view.
/// </summary>
/// <remarks>
/// <para>
/// Server: <see cref="AppsServerExtensions.UseApps"/> opts in; <see cref="AppsServerExtensions.AddUiResource"/> and
/// <see cref="AppsServerExtensions.AddAppTool"/> register the pieces. A caller that did not declare the extension (or does not list the
/// <see cref="MimeType"/> in its <c>mimeTypes</c>) sees the tool as an ordinary text tool: its UI metadata is left out of the listing and
/// tools meant only for the app are hidden. The tool's result carries its text content for every caller.
/// </para>
/// <para>Client: list <see cref="Client"/> in <see cref="ContextClientOptions.Extensions"/>, then use <see cref="AppsClientExtensions"/> to find a tool's UI and read it. The library renders nothing.</para>
/// </remarks>
public static class AppsExtension
{
  /// <summary>The extension identifier.</summary>
  public const string Id = "io.modelcontextprotocol/ui";

  /// <summary>The content type of an HTML view.</summary>
  public const string MimeType = "text/html;profile=mcp-app";

  /// <summary>The URI scheme reserved for UI resources.</summary>
  public const string UriScheme = "ui://";

  /// <summary>The Apps specification revision implemented.</summary>
  public const string SpecificationRevision = "2026-01-26";

  /// <summary>The key of the UI object in a tool's or resource's <c>_meta</c>.</summary>
  public const string MetaKey = "ui";

  // The earlier flat spelling of _meta.ui.resourceUri, which the specification deprecates; read for old servers, never written.
  internal const string DeprecatedResourceUriKey = "ui/resourceUri";

  /// <summary>Creates the server registration. <see cref="AppsServerExtensions.UseApps"/> adds it.</summary>
  public static ExtensionRegistration Server() =>
    new(Id, ContextServer.EmptyObject, null, official: true)
    {
      MetaKeys = [MetaKey, DeprecatedResourceUriKey],
      ToolAdapter = AdaptTool,
    };

  /// <summary>Creates the client declaration for <see cref="ContextClientOptions.Extensions"/>.</summary>
  /// <param name="mimeTypes">The content types the client can render; default just <see cref="MimeType"/>. A host that renders nothing should not declare the extension.</param>
  public static ClientExtension Client(IReadOnlyList<string>? mimeTypes = null)
  {
    var settings = new AppsSettings { MimeTypes = mimeTypes is null ? [MimeType] : [.. mimeTypes] };
    return new ClientExtension(Id, JsonSerializer.SerializeToElement(settings, AppsJsonContext.Default.AppsSettings), official: true);
  }

  /// <summary>Builds the <c>_meta</c> of a tool that a UI resource renders (what <c>[Tool(UiResource = ...)]</c> generates).</summary>
  /// <param name="resourceUri">The <c>ui://</c> resource.</param>
  /// <param name="visibility"><see cref="UiVisibility.Model"/> and/or <see cref="UiVisibility.App"/>; <c>null</c> for both.</param>
  /// <exception cref="ArgumentException"><paramref name="resourceUri"/> is not a <c>ui://</c> URI, or a visibility is not <c>model</c> or <c>app</c>, or the list is empty.</exception>
  public static MetaObject ToolMeta(string resourceUri, IReadOnlyList<string>? visibility = null)
  {
    ArgumentNullException.ThrowIfNull(resourceUri);
    if (!IsUiUri(resourceUri))
    {
      throw new ArgumentException("A UI resource URI starts with 'ui://' and names something after it.", nameof(resourceUri));
    }

    if (visibility is not null)
    {
      if (visibility.Count == 0)
      {
        throw new ArgumentException("A tool visible to nobody cannot be called; omit the visibility for the default.", nameof(visibility));
      }

      foreach (var entry in visibility)
      {
        if (entry is not (UiVisibility.Model or UiVisibility.App))
        {
          throw new ArgumentException("A visibility is 'model' or 'app', not '" + entry + "'.", nameof(visibility));
        }
      }
    }

    var ui = new UiToolMeta { ResourceUri = resourceUri, Visibility = visibility is null ? null : [.. visibility] };
    return AppsMeta.With(null, MetaKey, JsonSerializer.SerializeToElement(ui, AppsJsonContext.Default.UiToolMeta));
  }

  /// <summary>
  /// True when the caller declared Apps and can render <see cref="MimeType"/> (the extension requires <c>mimeTypes</c>; a declaration
  /// without it is treated as no support).
  /// </summary>
  /// <param name="context">The request.</param>
  public static bool SupportsApps(this RequestContext context)
  {
    ArgumentNullException.ThrowIfNull(context);
    if (context.GetExtensionSettings(Id, AppsJsonContext.Default.AppsSettings) is not { MimeTypes: { } types })
    {
      return false;
    }

    foreach (var type in types)
    {
      if (IsAppMimeType(type))
      {
        return true;
      }
    }

    return false;
  }

  /// <summary>True when <paramref name="mimeType"/> is the HTML view type, ignoring case and spaces around the parameter separator.</summary>
  /// <param name="mimeType">A content type.</param>
  public static bool IsAppMimeType(string? mimeType) =>
    mimeType is not null && string.Equals(mimeType.Replace(" ", string.Empty, StringComparison.Ordinal), MimeType, StringComparison.OrdinalIgnoreCase);

  /// <summary>True when <paramref name="uri"/> is a UI resource URI (<c>ui://</c> followed by something).</summary>
  /// <param name="uri">A URI.</param>
  public static bool IsUiUri(string? uri) => uri is { Length: > 5 } && uri.StartsWith(UriScheme, StringComparison.Ordinal);

  // Callers without Apps get an ordinary text tool (specification: "If host does not support MCP Apps, tool behaves as standard tool").
  private static Tool? AdaptTool(Tool tool, RequestContext context)
  {
    var ui = AppsMeta.ReadToolMeta(tool.Meta);
    if (ui is null || context.SupportsApps())
    {
      return tool;
    }

    if (!ui.VisibleToModel)
    {
      return null;
    }

    return new Tool
    {
      Name = tool.Name,
      Title = tool.Title,
      Description = tool.Description,
      InputSchema = tool.InputSchema,
      OutputSchema = tool.OutputSchema,
      Annotations = tool.Annotations,
      Icons = tool.Icons,
      AdditionalProperties = tool.AdditionalProperties,
      Meta = AppsMeta.Without(tool.Meta, MetaKey, DeprecatedResourceUriKey),
    };
  }
}

/// <summary>Reads and writes the <c>_meta.ui</c> object.</summary>
internal static class AppsMeta
{
  internal static UiToolMeta? ReadToolMeta(MetaObject? meta)
  {
    if (meta?.AdditionalProperties is not { } extra)
    {
      return null;
    }

    try
    {
      if (extra.TryGetValue(AppsExtension.MetaKey, out var element) && element.ValueKind == JsonValueKind.Object)
      {
        return element.Deserialize(AppsJsonContext.Default.UiToolMeta);
      }

      if (extra.TryGetValue(AppsExtension.DeprecatedResourceUriKey, out var flat) && flat.ValueKind == JsonValueKind.String)
      {
        return new UiToolMeta { ResourceUri = flat.GetString() };
      }
    }
    catch (JsonException)
    {
      // A server's metadata is input like any other: malformed UI metadata means the tool has no UI.
    }

    return null;
  }

  internal static MetaObject With(MetaObject? meta, string key, JsonElement value)
  {
    var extra = meta?.AdditionalProperties is { } existing ? new Dictionary<string, JsonElement>(existing, StringComparer.Ordinal) : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    extra[key] = value;
    return Copy(meta, extra);
  }

  internal static MetaObject? Without(MetaObject? meta, params string[] keys)
  {
    if (meta?.AdditionalProperties is not { } existing)
    {
      return meta;
    }

    var extra = new Dictionary<string, JsonElement>(existing, StringComparer.Ordinal);
    foreach (var key in keys)
    {
      extra.Remove(key);
    }

    return Copy(meta, extra.Count == 0 ? null : extra);
  }

  private static MetaObject Copy(MetaObject? meta, Dictionary<string, JsonElement>? extra) => new()
  {
    ProgressToken = meta?.ProgressToken,
    ProtocolVersion = meta?.ProtocolVersion,
    ClientInfo = meta?.ClientInfo,
    ClientCapabilities = meta?.ClientCapabilities,
    LogLevel = meta?.LogLevel,
    SubscriptionId = meta?.SubscriptionId,
    ServerInfo = meta?.ServerInfo,
    TraceParent = meta?.TraceParent,
    TraceState = meta?.TraceState,
    Baggage = meta?.Baggage,
    AdditionalProperties = extra,
  };
}
