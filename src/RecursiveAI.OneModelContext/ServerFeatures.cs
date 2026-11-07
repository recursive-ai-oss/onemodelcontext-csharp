using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.Extensions;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext;

/// <summary>
/// The handlers that are the same in both eras: tools, resources, prompts and completion. They produce results in the modern shape
/// (cache fields, <c>resultType</c> complete); a legacy codec removes what its revision does not have.
/// </summary>
internal sealed class ServerFeatures(ContextServer server)
{
  /// <summary>The tool, prompt or resource the request addresses, read without failing on odd input.</summary>
  internal static string? NameOf(string method, JsonElement parameters)
  {
    var member = method switch
    {
      Methods.ToolsCall or Methods.PromptsGet => "name",
      Methods.ResourcesRead => "uri",
      _ => null,
    };

    return member is not null
      && parameters.ValueKind == JsonValueKind.Object
      && parameters.TryGetProperty(member, out var value)
      && value.ValueKind == JsonValueKind.String
      ? value.GetString()
      : null;
  }

  internal async ValueTask<Result> HandleAsync(string method, JsonElement parameters, RequestContext context)
  {
    switch (method)
    {
      case Methods.ToolsList:
        return ListTools(Read(parameters, WireJsonContext.Default.PaginatedRequestParams), context);
      case Methods.ToolsCall:
        return await CallToolAsync(ReadToolCall(parameters, context), context).ConfigureAwait(false);
      case Methods.ResourcesList:
        return ListResources(Read(parameters, WireJsonContext.Default.PaginatedRequestParams));
      case Methods.ResourcesTemplatesList:
        return ListResourceTemplates(Read(parameters, WireJsonContext.Default.PaginatedRequestParams));
      case Methods.ResourcesRead:
        return await ReadResourceAsync(Read(parameters, WireJsonContext.Default.ReadResourceRequestParams), context).ConfigureAwait(false);
      case Methods.PromptsList:
        return ListPrompts(Read(parameters, WireJsonContext.Default.PaginatedRequestParams));
      case Methods.PromptsGet:
        return await GetPromptAsync(Read(parameters, WireJsonContext.Default.GetPromptRequestParams), context).ConfigureAwait(false);
      case Methods.SubscriptionsListen when context.Era == ProtocolEra.Modern:
        return await ListenAsync(Read(parameters, WireJsonContext.Default.SubscriptionsListenRequestParams), context).ConfigureAwait(false);
      case Methods.CompletionComplete when server.Completions is { } complete:
        return await complete(Read(parameters, WireJsonContext.Default.CompleteRequestParams), context).ConfigureAwait(false);
      default:
        if (server.Extensions.Count > 0 && server.Extensions.TryGetRequest(method, out var registration, out var extensionHandler))
        {
          return await RunExtensionAsync(method, parameters, context, registration, extensionHandler).ConfigureAwait(false);
        }

        throw new ProtocolException(JsonRpcErrorCodes.MethodNotFound, "Method not found: " + method + ".");
    }
  }

  // Extensions overview, "Graceful Degradation": a peer that did not declare the extension gets core behaviour, which has no such method.
  // (A mandatory extension was already enforced before dispatch.)
  private static async ValueTask<Result> RunExtensionAsync(string method, JsonElement parameters, RequestContext context, ExtensionRegistration registration, ExtensionRequestHandler handler)
  {
    if (!context.SupportsExtension(registration.Id) && !(registration.AnswersUndeclaredPeers && registration.SupportsRevision(context.ProtocolVersion)))
    {
      // An extension that insists on being declared answers -32021 on the revisions it works on; elsewhere the method does not exist.
      if (registration.UndeclaredRequestsFail && registration.SupportsRevision(context.ProtocolVersion))
      {
        throw new MissingCapabilityException(
          "The extension '" + registration.Id + "' is required for " + method + ".",
          new ClientCapabilities { Extensions = new Dictionary<string, JsonElement> { [registration.Id] = ContextServer.EmptyObject } });
      }

      throw new ProtocolException(JsonRpcErrorCodes.MethodNotFound, "Method not found: " + method + ".");
    }

    var answer = await handler(parameters.ValueKind == JsonValueKind.Object ? parameters : ContextServer.EmptyObject, context).ConfigureAwait(false);
    if (answer.ValueKind != JsonValueKind.Object)
    {
      throw new ProtocolException(JsonRpcErrorCodes.InternalError, "Internal error.");
    }

    try
    {
      return answer.Deserialize(WireJsonContext.Default.ExtensionResult) ?? throw new ProtocolException(JsonRpcErrorCodes.InternalError, "Internal error.");
    }
    catch (JsonException)
    {
      throw new ProtocolException(JsonRpcErrorCodes.InternalError, "Internal error.");
    }
  }

  // 2026-07-28 basic/patterns/subscriptions: a long-lived request that acknowledges the filter it will honour, then streams the
  // notifications the client opted in to, each tagged with the id of this request, until the client cancels it or the server ends it.
  private async ValueTask<SubscriptionsListenResult> ListenAsync(SubscriptionsListenRequestParams request, RequestContext context)
  {
    var id = context.RequestId ?? throw new ProtocolException(JsonRpcErrorCodes.InternalError, "Internal error.");
    var capabilities = server.Capabilities;
    var wanted = request.Notifications;
    var honored = new SubscriptionFilter
    {
      ToolsListChanged = wanted.ToolsListChanged == true && capabilities.Tools is not null ? true : null,
      PromptsListChanged = wanted.PromptsListChanged == true && capabilities.Prompts is not null ? true : null,
      ResourcesListChanged = wanted.ResourcesListChanged == true && capabilities.Resources is not null ? true : null,
      ResourceSubscriptions = wanted.ResourceSubscriptions is { Count: > 0 } uris && capabilities.Resources is not null ? [.. uris] : null,
    };

    // Changes queue here and are sent in order after the acknowledgment. A list change carries no payload, so when a slow client lets the
    // queue fill, the oldest entries are dropped: the client still learns that something changed.
    var queue = Channel.CreateBounded<JsonRpcNotification>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    var meta = new MetaObject { SubscriptionId = id };
    using var registration = server.Events.Subscribe(serverEvent =>
    {
      switch (serverEvent.Kind)
      {
        case ServerEventKind.ToolsListChanged when honored.ToolsListChanged == true:
          queue.Writer.TryWrite(ListChanged(Methods.NotificationsToolsListChanged, meta));
          break;
        case ServerEventKind.PromptsListChanged when honored.PromptsListChanged == true:
          queue.Writer.TryWrite(ListChanged(Methods.NotificationsPromptsListChanged, meta));
          break;
        case ServerEventKind.ResourcesListChanged when honored.ResourcesListChanged == true:
          queue.Writer.TryWrite(ListChanged(Methods.NotificationsResourcesListChanged, meta));
          break;
        case ServerEventKind.ResourceUpdated when serverEvent.Uri is { } uri && honored.ResourceSubscriptions?.Contains(uri) == true:
          var updated = new ResourceUpdatedNotificationParams { Uri = uri, Meta = meta };
          queue.Writer.TryWrite(new JsonRpcNotification(Methods.NotificationsResourcesUpdated, JsonSerializer.SerializeToElement(updated, WireJsonContext.Default.ResourceUpdatedNotificationParams)));
          break;
        case ServerEventKind.Shutdown:
          queue.Writer.TryComplete();
          break;
      }
    });

    var ack = new SubscriptionsAcknowledgedNotificationParams { Notifications = honored, Meta = meta };
    await context.SendNotificationAsync(
      new JsonRpcNotification(Methods.NotificationsSubscriptionsAcknowledged, JsonSerializer.SerializeToElement(ack, WireJsonContext.Default.SubscriptionsAcknowledgedNotificationParams)),
      context.CancellationToken).ConfigureAwait(false);

    // Cancelling the request (notifications/cancelled, or the stream closing) ends the loop with OperationCanceledException and no response.
    await foreach (var notification in queue.Reader.ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
    {
      await context.SendNotificationAsync(notification, context.CancellationToken).ConfigureAwait(false);
    }

    // Graceful end: a normal result that names the subscription it closes.
    return new SubscriptionsListenResult { ResultType = WireNames.ResultTypeComplete, Meta = meta };
  }

  private static JsonRpcNotification ListChanged(string method, MetaObject meta) =>
    new(method, JsonSerializer.SerializeToElement(new RequestParams { Meta = meta }, WireJsonContext.Default.RequestParams));

  // Bad params never throw out of a handler: they become -32602.
  private static T Read<T>(JsonElement parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    where T : class
  {
    try
    {
      if (parameters.ValueKind == JsonValueKind.Undefined)
      {
        // Methods without required params may omit them; ones with required members fail below.
        parameters = ContextServer.EmptyObject;
      }

      return parameters.Deserialize(info) ?? throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "Invalid params.");
    }
    catch (JsonException)
    {
      throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "Invalid params.");
    }
  }

  private static CallToolRequestParams ReadToolCall(JsonElement parameters, RequestContext context)
  {
    // The modern codec has already validated _meta. Avoid decoding that identity a second time for the common call shape.
    // Other shapes use the record reader, preserving its validation of duplicate members and future fields.
    if (context.Era == ProtocolEra.Modern && context.Peer.Meta is not null && parameters.ValueKind == JsonValueKind.Object)
    {
      string? name = null;
      JsonElement arguments = default;
      var seen = 0;
      foreach (var property in parameters.EnumerateObject())
      {
        int bit;
        if (property.NameEquals("name") && property.Value.ValueKind == JsonValueKind.String)
        {
          bit = 1;
          name = property.Value.GetString();
        }
        else if (property.NameEquals("arguments"))
        {
          bit = 2;
          arguments = property.Value;
        }
        else if (property.NameEquals("_meta"))
        {
          bit = 4;
        }
        else
        {
          return Read(parameters, WireJsonContext.Default.CallToolRequestParams);
        }

        if ((seen & bit) != 0)
        {
          return Read(parameters, WireJsonContext.Default.CallToolRequestParams);
        }

        seen |= bit;
      }

      if (name is not null)
      {
        return new CallToolRequestParams { Name = name, Arguments = arguments, Meta = context.Peer.Meta };
      }
    }

    return Read(parameters, WireJsonContext.Default.CallToolRequestParams);
  }

  private ListToolsResult ListTools(PaginatedRequestParams request, RequestContext context)
  {
    var page = Page(server.Tools.Snapshot(), static e => e.Definition.Name, request.Cursor, out var next);
    var tools = new List<Tool>(page.Length);
    var adapt = server.Extensions.HasToolAdapters;
    foreach (var entry in page)
    {
      var tool = entry.Definition.ToTool();
      if (adapt)
      {
        tool = server.Extensions.AdaptTool(tool, context);
      }

      if (tool is not null)
      {
        tools.Add(tool);
      }
    }

    return new ListToolsResult { Tools = tools, NextCursor = next, ResultType = WireNames.ResultTypeComplete, TtlMs = TtlMs, CacheScope = server.CacheScope };
  }

  private ListResourcesResult ListResources(PaginatedRequestParams request)
  {
    var page = Page(server.Resources.Snapshot(), static e => e.Definition.Uri, request.Cursor, out var next);
    var resources = new List<Resource>(page.Length);
    foreach (var entry in page)
    {
      resources.Add(entry.Definition.ToResource());
    }

    return new ListResourcesResult { Resources = resources, NextCursor = next, ResultType = WireNames.ResultTypeComplete, TtlMs = TtlMs, CacheScope = server.Options.ListCacheScope };
  }

  private ListResourceTemplatesResult ListResourceTemplates(PaginatedRequestParams request)
  {
    var page = Page(server.ResourceTemplates.Snapshot(), static e => e.Definition.UriTemplate, request.Cursor, out var next);
    var templates = new List<ResourceTemplate>(page.Length);
    foreach (var entry in page)
    {
      templates.Add(entry.Definition.ToResourceTemplate());
    }

    return new ListResourceTemplatesResult { ResourceTemplates = templates, NextCursor = next, ResultType = WireNames.ResultTypeComplete, TtlMs = TtlMs, CacheScope = server.Options.ListCacheScope };
  }

  private ListPromptsResult ListPrompts(PaginatedRequestParams request)
  {
    var page = Page(server.Prompts.Snapshot(), static e => e.Definition.Name, request.Cursor, out var next);
    var prompts = new List<Prompt>(page.Length);
    foreach (var entry in page)
    {
      prompts.Add(entry.Definition.ToPrompt());
    }

    return new ListPromptsResult { Prompts = prompts, NextCursor = next, ResultType = WireNames.ResultTypeComplete, TtlMs = TtlMs, CacheScope = server.Options.ListCacheScope };
  }

  private long TtlMs => (long)server.Options.ListCacheTtl.TotalMilliseconds;

  // The cursor is the last key of the previous page (base64url), so a list that changes between pages neither repeats nor skips
  // items that stay. Lists are ordered by key, ordinal.
  private T[] Page<T>(IReadOnlyList<T> all, Func<T, string> keyOf, string? cursor, out string? next)
  {
    var start = 0;
    if (cursor is not null)
    {
      string after;
      try
      {
        after = Encoding.UTF8.GetString(Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/').PadRight((cursor.Length + 3) / 4 * 4, '=')));
      }
      catch (FormatException)
      {
        throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "Invalid params: the cursor is not valid.");
      }

      while (start < all.Count && string.CompareOrdinal(keyOf(all[start]), after) <= 0)
      {
        start++;
      }
    }

    var size = Math.Max(1, server.Options.MaxPageSize);
    var count = Math.Min(size, all.Count - start);
    var page = new T[count];
    for (var i = 0; i < count; i++)
    {
      page[i] = all[start + i];
    }

    next = start + count < all.Count
      ? Convert.ToBase64String(Encoding.UTF8.GetBytes(keyOf(page[^1]))).TrimEnd('=').Replace('+', '-').Replace('/', '_')
      : null;
    return page;
  }

  private async ValueTask<Result> CallToolAsync(CallToolRequestParams request, RequestContext context)
  {
    if (!server.Tools.TryGet(request.Name, out var definition, out var handler)
      || (server.Extensions.HasToolAdapters && server.Extensions.AdaptTool(definition.ToTool(), context) is null))
    {
      // The seed treats an unknown tool as invalid params, as the spec's error handling for tools/call does. A tool an extension hides
      // from this caller (an app-only tool, for a caller without Apps) is as unknown to it as it is in the list.
      throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "Unknown tool: " + request.Name + ".");
    }

    if (request.Arguments.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
    {
      throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "Invalid params: arguments must be an object.");
    }

    var arguments = request.Arguments.ValueKind == JsonValueKind.Object ? request.Arguments : ContextServer.EmptyObject;
    CallToolResult result;
    try
    {
      if (server.Extensions.HasToolCallInterceptors
        && await server.Extensions.InterceptToolCallAsync(request.Name, arguments, handler, context).ConfigureAwait(false) is { } taken)
      {
        return taken;
      }

      result = await handler(arguments, context).ConfigureAwait(false);
    }
    catch (ToolException failure)
    {
      result = CallToolResult.FromError(failure.Message);
    }
    catch (Exception ex) when (ex is not (OperationCanceledException or ProtocolException or InputRequiredException))
    {
      // An unexpected fault in a tool is reported as a tool failure so the model can react; its text is not sent.
      ContextDiagnostics.Publish(DiagnosticKind.HandlerFault, "Tool '" + request.Name + "' threw " + ex.GetType().Name + ".", Methods.ToolsCall, ex);
      result = CallToolResult.FromError("The tool failed.");
    }

    return InlineContentLimit.Apply(result, server.Options.MaxInlineContentBytes);
  }

  private async ValueTask<ReadResourceResult> ReadResourceAsync(ReadResourceRequestParams request, RequestContext context)
  {
    ResourceHandler? handler = null;
    if (server.Resources.TryGet(request.Uri, out _, out var exact))
    {
      handler = exact;
    }
    else
    {
      foreach (var (definition, templateHandler) in server.ResourceTemplates.Snapshot())
      {
        if (UriTemplateMatcher.TryMatch(definition.UriTemplate, request.Uri, out _))
        {
          handler = templateHandler;
          break;
        }
      }
    }

    if (handler is null)
    {
      var data = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["uri"] = request.Uri }, WireJsonContext.Default.DictionaryStringString);
      throw new ProtocolException(ProtocolErrorCodes.ResourceNotFound(context.Era), "Resource not found.", data);
    }

    var result = await handler(request.Uri, context).ConfigureAwait(false);
    return result;
  }

  private async ValueTask<GetPromptResult> GetPromptAsync(GetPromptRequestParams request, RequestContext context)
  {
    if (!server.Prompts.TryGet(request.Name, out var definition, out var handler))
    {
      throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "Unknown prompt: " + request.Name + ".");
    }

    var arguments = request.Arguments ?? [];
    if (definition.Arguments is { } declared)
    {
      foreach (var argument in declared)
      {
        if (argument.Required == true && !arguments.ContainsKey(argument.Name))
        {
          throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "Missing required argument: " + argument.Name + ".");
        }
      }
    }

    return await handler(arguments, context).ConfigureAwait(false);
  }
}

/// <summary>Caps inline binary content in a tool result.</summary>
internal static class InlineContentLimit
{
  /// <summary>Replaces image, audio and embedded-blob blocks larger than the cap with a text note; other blocks pass through.</summary>
  internal static CallToolResult Apply(CallToolResult result, int maxBytes)
  {
    List<ContentBlock>? replaced = null;
    for (var i = 0; i < result.Content.Count; i++)
    {
      var block = result.Content[i];
      var length = block switch
      {
        ImageContent image => DecodedLength(image.Data),
        AudioContent audio => DecodedLength(audio.Data),
        EmbeddedResource { Resource: BlobResourceContents blob } => DecodedLength(blob.Blob),
        _ => 0L,
      };

      if (length <= maxBytes)
      {
        continue;
      }

      replaced ??= [.. result.Content];
      replaced[i] = TextContent.Of($"[{block.Type} content omitted: {length} bytes exceeds the inline limit of {maxBytes} bytes]");
    }

    return replaced is null
      ? result
      : new CallToolResult
      {
        Content = replaced,
        StructuredContent = result.StructuredContent,
        IsError = result.IsError,
        Meta = result.Meta,
        ResultType = result.ResultType,
        AdditionalProperties = result.AdditionalProperties,
      };
  }

  // Base64 length to bytes without decoding.
  private static long DecodedLength(string base64)
  {
    var padding = base64.EndsWith("==", StringComparison.Ordinal) ? 2 : base64.EndsWith('=') ? 1 : 0;
    return (long)base64.Length / 4 * 3 - padding;
  }
}
