using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RecursiveAI.OneModelContext.Protocol;
using AIText = Microsoft.Extensions.AI.TextContent;
using McpText = RecursiveAI.OneModelContext.Protocol.TextContent;

namespace RecursiveAI.OneModelContext.Extensions.AI;

/// <summary>The mapping between MCP content blocks and <see cref="AIContent"/>, in both directions.</summary>
internal static class ContextAIContent
{
  // Tool result -> what a function returns (see ContextClientAIExtensions.ToAIFunctionsAsync).
  public static object? ToFunctionResult(CallToolResult result)
  {
    if (result.IsError == true)
    {
      var text = result.Text;
      return text.Length == 0 ? "Error: the tool failed." : "Error: " + text;
    }

    if (result.StructuredContent.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
    {
      return result.StructuredContent.Clone();
    }

    var allText = true;
    foreach (var block in result.Content)
    {
      if (block is not McpText)
      {
        allText = false;
        break;
      }
    }

    return allText ? result.Text : Map(result.Content);
  }

  public static IList<AIContent> Map(IReadOnlyList<ContentBlock> blocks)
  {
    var contents = new List<AIContent>(blocks.Count);
    foreach (var block in blocks)
    {
      switch (block)
      {
        case McpText text:
          contents.Add(new AIText(text.Text));
          break;
        case ImageContent image:
          contents.Add(Data(image.Data, image.MimeType));
          break;
        case AudioContent audio:
          contents.Add(Data(audio.Data, audio.MimeType));
          break;
        case ResourceLink link:
          contents.Add(Uri.TryCreate(link.Uri, UriKind.Absolute, out var uri)
            ? new UriContent(uri, link.MimeType ?? "application/octet-stream")
            : new AIText(link.Uri));
          break;
        case EmbeddedResource { Resource: TextResourceContents text }:
          contents.Add(new AIText(text.Text));
          break;
        case EmbeddedResource { Resource: BlobResourceContents blob }:
          contents.Add(Data(blob.Blob, blob.MimeType ?? "application/octet-stream"));
          break;
      }
    }

    return contents;
  }

  // A block whose base64 is malformed is dropped to a note rather than failing the whole result.
  private static AIContent Data(string base64, string mediaType)
  {
    try
    {
      return new DataContent(Convert.FromBase64String(base64), mediaType);
    }
    catch (FormatException)
    {
      return new AIText("[" + mediaType + " content that could not be decoded]");
    }
  }

  // A function's return value -> tool result content (see ContextServerAIExtensions.AddAIFunction).
  public static CallToolResult ToToolResult(object? value, AIFunction function)
  {
    var options = function.JsonSerializerOptions;
    switch (value)
    {
      case null:
        return new CallToolResult { Content = [] };
      case string text:
        return CallToolResult.FromText(text);
      case AIContent single:
        return new CallToolResult { Content = ToBlocks([single]) };
      case IEnumerable<AIContent> many:
        return new CallToolResult { Content = ToBlocks(many) };
      case JsonElement { ValueKind: JsonValueKind.String } json:
        return CallToolResult.FromText(json.GetString() ?? string.Empty);
    }

    var element = value is JsonElement e ? e.Clone() : JsonSerializer.SerializeToElement(value, options.GetTypeInfo(value.GetType()));
    var asText = CallToolResult.FromText(element.GetRawText());

    // structuredContent must be an object that conforms to the outputSchema (2026-07-28 server/tools), so only functions that declare one send it.
    return function.ReturnJsonSchema is { ValueKind: JsonValueKind.Object } && element.ValueKind == JsonValueKind.Object
      ? new CallToolResult { Content = asText.Content, StructuredContent = element }
      : asText;
  }

  private static List<ContentBlock> ToBlocks(IEnumerable<AIContent> contents)
  {
    var blocks = new List<ContentBlock>();
    foreach (var content in contents)
    {
      switch (content)
      {
        case AIText text:
          blocks.Add(McpText.Of(text.Text));
          break;
        case DataContent data when data.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
          blocks.Add(new ImageContent { Data = Convert.ToBase64String(data.Data.Span), MimeType = data.MediaType });
          break;
        case DataContent data when data.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase):
          blocks.Add(new AudioContent { Data = Convert.ToBase64String(data.Data.Span), MimeType = data.MediaType });
          break;
        case DataContent data:
          blocks.Add(new EmbeddedResource
          {
            Resource = new BlobResourceContents { Uri = "data:" + data.MediaType, MimeType = data.MediaType, Blob = Convert.ToBase64String(data.Data.Span) },
          });
          break;
        case UriContent uri:
          blocks.Add(new ResourceLink { Uri = uri.Uri.AbsoluteUri, Name = uri.Uri.Segments.Length > 0 ? uri.Uri.Segments[^1].Trim('/') : uri.Uri.AbsoluteUri, MimeType = uri.MediaType });
          break;
      }
    }

    return blocks;
  }
}
