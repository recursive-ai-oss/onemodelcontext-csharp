using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Extensions.Apps;

/// <summary>Registering MCP Apps pieces on a <see cref="ContextServer"/>.</summary>
public static class AppsServerExtensions
{
  /// <summary>Opts the server in to MCP Apps: it advertises <see cref="AppsExtension.Id"/> and shapes tool listings per caller.</summary>
  /// <param name="server">The server.</param>
  /// <returns>The server.</returns>
  public static ContextServer UseApps(this ContextServer server)
  {
    ArgumentNullException.ThrowIfNull(server);
    if (!server.Extensions.TryGet(AppsExtension.Id, out _))
    {
      server.Extensions.Add(AppsExtension.Server());
    }

    return server;
  }

  /// <summary>
  /// Registers a UI resource: an HTML document read with <c>resources/read</c>, returned with its Content Security Policy and permission
  /// metadata under <c>_meta.ui</c>.
  /// </summary>
  /// <param name="server">The server.</param>
  /// <param name="uri">The <c>ui://</c> URI.</param>
  /// <param name="name">The programmatic name.</param>
  /// <param name="html">The HTML5 document.</param>
  /// <param name="meta">CSP, permissions and display preferences; <c>null</c> declares none, which hosts read as the most restrictive policy.</param>
  /// <param name="description">What the view shows.</param>
  /// <returns>The server.</returns>
  /// <exception cref="ArgumentException"><paramref name="uri"/> does not start with <c>ui://</c>.</exception>
  public static ContextServer AddUiResource(this ContextServer server, string uri, string name, string html, UiResourceMeta? meta = null, string? description = null)
  {
    ArgumentNullException.ThrowIfNull(server);
    ArgumentNullException.ThrowIfNull(html);
    CheckUiUri(uri);
    ArgumentException.ThrowIfNullOrEmpty(name);
    var contentsMeta = meta is null
      ? null
      : AppsMeta.With(null, AppsExtension.MetaKey, JsonSerializer.SerializeToElement(meta, AppsJsonContext.Default.UiResourceMeta));
    server.Resources.Add(
      new ResourceDefinition(uri, name) { Description = description, MimeType = AppsExtension.MimeType },
      (requested, _) => ValueTask.FromResult(new ReadResourceResult
      {
        Contents = [new TextResourceContents { Uri = requested, MimeType = AppsExtension.MimeType, Text = html, Meta = contentsMeta }],
      }));
    return server;
  }

  /// <summary>
  /// Registers a tool whose results a host can render in a UI resource. The server also opts in to Apps (<see cref="UseApps"/>).
  /// A caller without Apps sees an ordinary tool; the handler should return meaningful text content for it (and for everyone, as the
  /// specification asks).
  /// </summary>
  /// <param name="server">The server.</param>
  /// <param name="definition">The tool.</param>
  /// <param name="resourceUri">The <c>ui://</c> resource that renders it; register it with <see cref="AddUiResource"/>.</param>
  /// <param name="handler">Runs the tool. <see cref="AppsExtension.SupportsApps"/> on its context says whether the caller renders views.</param>
  /// <param name="visibility">Who may use the tool: <see cref="UiVisibility.Model"/>, <see cref="UiVisibility.App"/> or both (default). A tool visible only to <c>app</c> is hidden from callers without Apps and cannot be called by them.</param>
  /// <param name="fallbackText">Text content used when the handler returns none, so every caller gets a meaningful answer.</param>
  /// <returns>The server.</returns>
  /// <exception cref="ArgumentException"><paramref name="resourceUri"/> does not start with <c>ui://</c>, or a visibility is not <c>model</c> or <c>app</c>.</exception>
  public static ContextServer AddAppTool(this ContextServer server, ToolDefinition definition, string resourceUri, ToolHandler handler, IReadOnlyList<string>? visibility = null, string? fallbackText = null)
  {
    ArgumentNullException.ThrowIfNull(server);
    ArgumentNullException.ThrowIfNull(definition);
    ArgumentNullException.ThrowIfNull(handler);
    var toolMeta = AppsExtension.ToolMeta(resourceUri, visibility);
    server.UseApps();
    var withUi = new ToolDefinition(definition.Name, definition.Description, definition.InputSchema)
    {
      Title = definition.Title,
      OutputSchema = definition.OutputSchema,
      Annotations = definition.Annotations,
      Icons = definition.Icons,
      RequiredScopes = definition.RequiredScopes,
      Meta = AppsMeta.With(definition.Meta, AppsExtension.MetaKey, toolMeta.AdditionalProperties![AppsExtension.MetaKey]),
    };

    server.Tools.Add(withUi, fallbackText is null ? handler : async (arguments, context) =>
    {
      var result = await handler(arguments, context).ConfigureAwait(false);
      return result.Content.Count > 0 || result.IsError == true
        ? result
        : new CallToolResult
        {
          Content = [TextContent.Of(fallbackText)],
          StructuredContent = result.StructuredContent,
          IsError = result.IsError,
          Meta = result.Meta,
          ResultType = result.ResultType,
          AdditionalProperties = result.AdditionalProperties,
        };
    });
    return server;
  }

  private static void CheckUiUri(string uri)
  {
    ArgumentNullException.ThrowIfNull(uri);
    if (!AppsExtension.IsUiUri(uri))
    {
      throw new ArgumentException("A UI resource URI starts with 'ui://' and names something after it.", nameof(uri));
    }
  }
}

/// <summary>What a client or host does with MCP Apps: find a tool's UI, read it, and bridge it.</summary>
public static class AppsClientExtensions
{
  /// <summary>
  /// Reads the UI metadata of a listed tool. Returns <c>null</c> when the tool has none, the metadata is malformed, or it names a UI
  /// resource that is not a <c>ui://</c> URI (the extension reserves the scheme, and a host must not be steered to fetch anything else).
  /// The earlier flat <c>ui/resourceUri</c> key is read too.
  /// </summary>
  /// <param name="tool">A tool from <c>tools/list</c>.</param>
  public static UiToolMeta? GetUi(this Tool tool)
  {
    ArgumentNullException.ThrowIfNull(tool);
    var ui = AppsMeta.ReadToolMeta(tool.Meta);
    return ui is not null && (ui.ResourceUri is null || AppsExtension.IsUiUri(ui.ResourceUri)) ? ui : null;
  }

  /// <summary>
  /// Reads a UI resource with <c>resources/read</c> and checks it is what the extension defines: a <c>ui://</c> URI and content type
  /// <see cref="AppsExtension.MimeType"/> with text or base64 content.
  /// </summary>
  /// <param name="client">The client.</param>
  /// <param name="uri">The <c>ui://</c> URI, usually from <see cref="GetUi"/>.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  /// <returns>The document and its metadata. Nothing is rendered or fetched.</returns>
  /// <exception cref="ArgumentException"><paramref name="uri"/> is not a <c>ui://</c> URI.</exception>
  /// <exception cref="ContextException">The server returned something that is not a UI resource.</exception>
  public static async ValueTask<UiResource> ReadUiResourceAsync(this ContextClient client, string uri, System.Threading.CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(client);
    ArgumentNullException.ThrowIfNull(uri);
    if (!AppsExtension.IsUiUri(uri))
    {
      throw new ArgumentException("A UI resource URI starts with 'ui://'.", nameof(uri));
    }

    var read = await client.ReadResourceAsync(uri, cancellationToken).ConfigureAwait(false);
    foreach (var contents in read.Contents)
    {
      if (contents.Uri != uri)
      {
        continue;
      }

      if (!AppsExtension.IsAppMimeType(contents.MimeType))
      {
        throw new ContextException("The resource '" + uri + "' has content type '" + contents.MimeType + "', not '" + AppsExtension.MimeType + "'.");
      }

      string html;
      switch (contents)
      {
        case TextResourceContents text:
          html = text.Text;
          break;
        case BlobResourceContents blob:
          try
          {
            html = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(blob.Blob));
          }
          catch (FormatException ex)
          {
            throw new ContextException("The UI resource '" + uri + "' holds content that is not valid base64.", ex);
          }

          break;
        default:
          throw new ContextException("The UI resource '" + uri + "' has no text or blob content.");
      }

      UiResourceMeta? meta = null;
      if (contents.Meta?.AdditionalProperties is { } extra && extra.TryGetValue(AppsExtension.MetaKey, out var ui) && ui.ValueKind == JsonValueKind.Object)
      {
        try
        {
          meta = ui.Deserialize(AppsJsonContext.Default.UiResourceMeta);
        }
        catch (JsonException)
        {
          // Malformed metadata means no declared origins or permissions, which is the restrictive reading.
        }
      }

      return new UiResource { Uri = uri, MimeType = AppsExtension.MimeType, Html = html, Meta = meta };
    }

    throw new ContextException("The server did not return the UI resource '" + uri + "'.");
  }
}
