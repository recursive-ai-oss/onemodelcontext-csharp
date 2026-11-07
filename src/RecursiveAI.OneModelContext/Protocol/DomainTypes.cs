using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecursiveAI.OneModelContext.Protocol;

/// <summary>
/// A block of content in a tool result, prompt message or sampling message. The concrete type follows the <c>type</c> member.
/// A block with a <c>type</c> this library does not know is kept as <see cref="UnknownContent"/> and written back unchanged.
/// </summary>
public abstract class ContentBlock : WireObject
{
  /// <summary>The <c>type</c> discriminator.</summary>
  [JsonIgnore]
  public abstract string Type { get; }

  /// <summary>Hints about the audience and importance.</summary>
  [JsonPropertyName("annotations")]
  public Annotations? Annotations { get; init; }

  /// <summary>The <c>_meta</c> object.</summary>
  [JsonPropertyName("_meta")]
  public MetaObject? Meta { get; init; }
}

/// <summary>Text.</summary>
public sealed class TextContent : ContentBlock
{
  /// <inheritdoc />
  [JsonIgnore]
  public override string Type => "text";

  /// <summary>The text.</summary>
  [JsonPropertyName("text")]
  public required string Text { get; init; }

  /// <summary>Creates a text block.</summary>
  /// <param name="text">The text.</param>
  public static TextContent Of(string text) => new() { Text = text };
}

/// <summary>An image, inline as base64.</summary>
public sealed class ImageContent : ContentBlock
{
  /// <inheritdoc />
  [JsonIgnore]
  public override string Type => "image";

  /// <summary>Base64 image data.</summary>
  [JsonPropertyName("data")]
  public required string Data { get; init; }

  /// <summary>The media type, such as <c>image/png</c>.</summary>
  [JsonPropertyName("mimeType")]
  public required string MimeType { get; init; }
}

/// <summary>Audio, inline as base64 (2025-03-26 and later).</summary>
public sealed class AudioContent : ContentBlock
{
  /// <inheritdoc />
  [JsonIgnore]
  public override string Type => "audio";

  /// <summary>Base64 audio data.</summary>
  [JsonPropertyName("data")]
  public required string Data { get; init; }

  /// <summary>The media type, such as <c>audio/wav</c>.</summary>
  [JsonPropertyName("mimeType")]
  public required string MimeType { get; init; }
}

/// <summary>A link to a resource the client may read (2025-06-18 and later). It has the members of <see cref="Resource"/>.</summary>
public sealed class ResourceLink : ContentBlock
{
  /// <inheritdoc />
  [JsonIgnore]
  public override string Type => "resource_link";

  /// <summary>The resource URI.</summary>
  [JsonPropertyName("uri")]
  public required string Uri { get; init; }

  /// <summary>The programmatic name.</summary>
  [JsonPropertyName("name")]
  public required string Name { get; init; }

  /// <summary>A display name.</summary>
  [JsonPropertyName("title")]
  public string? Title { get; init; }

  /// <summary>What the resource is.</summary>
  [JsonPropertyName("description")]
  public string? Description { get; init; }

  /// <summary>The media type.</summary>
  [JsonPropertyName("mimeType")]
  public string? MimeType { get; init; }

  /// <summary>Size in bytes.</summary>
  [JsonPropertyName("size")]
  public long? Size { get; init; }

  /// <summary>Icons.</summary>
  [JsonPropertyName("icons")]
  public List<Icon>? Icons { get; init; }
}

/// <summary>A resource's contents embedded in the message.</summary>
public sealed class EmbeddedResource : ContentBlock
{
  /// <inheritdoc />
  [JsonIgnore]
  public override string Type => "resource";

  /// <summary>The contents.</summary>
  [JsonPropertyName("resource")]
  public required ResourceContents Resource { get; init; }
}

/// <summary>A model's request to run a tool, inside a sampling message (2025-11-25 and later).</summary>
public sealed class ToolUseContent : ContentBlock
{
  /// <inheritdoc />
  [JsonIgnore]
  public override string Type => "tool_use";

  /// <summary>The id the result must quote.</summary>
  [JsonPropertyName("id")]
  public required string Id { get; init; }

  /// <summary>The tool name.</summary>
  [JsonPropertyName("name")]
  public required string Name { get; init; }

  /// <summary>The arguments object.</summary>
  [JsonPropertyName("input")]
  public required JsonElement Input { get; init; }
}

/// <summary>The result of a tool the model asked for, inside a sampling message (2025-11-25 and later).</summary>
public sealed class ToolResultContent : ContentBlock
{
  /// <inheritdoc />
  [JsonIgnore]
  public override string Type => "tool_result";

  /// <summary>The <see cref="ToolUseContent.Id"/> this answers.</summary>
  [JsonPropertyName("toolUseId")]
  public required string ToolUseId { get; init; }

  /// <summary>The result content.</summary>
  [JsonPropertyName("content")]
  public required List<ContentBlock> Content { get; init; }

  /// <summary>Structured result data.</summary>
  [JsonPropertyName("structuredContent")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public JsonElement StructuredContent { get; init; }

  /// <summary>True when the tool failed.</summary>
  [JsonPropertyName("isError")]
  public bool? IsError { get; init; }
}

/// <summary>A block whose <c>type</c> is not known to this version. It is kept whole so it survives a round trip.</summary>
public sealed class UnknownContent : ContentBlock
{
  /// <inheritdoc />
  [JsonIgnore]
  public override string Type => RawType;

  /// <summary>The <c>type</c> the peer sent.</summary>
  [JsonIgnore]
  public string RawType { get; init; } = string.Empty;

  /// <summary>The whole block as received.</summary>
  [JsonIgnore]
  public JsonElement Raw { get; init; }
}

/// <summary>The contents of a resource: <see cref="TextResourceContents"/> or <see cref="BlobResourceContents"/>.</summary>
public abstract class ResourceContents : WireObject
{
  /// <summary>The resource URI.</summary>
  [JsonPropertyName("uri")]
  public required string Uri { get; init; }

  /// <summary>The media type.</summary>
  [JsonPropertyName("mimeType")]
  public string? MimeType { get; init; }

  /// <summary>The <c>_meta</c> object.</summary>
  [JsonPropertyName("_meta")]
  public MetaObject? Meta { get; init; }
}

/// <summary>Text contents.</summary>
public sealed class TextResourceContents : ResourceContents
{
  /// <summary>The text.</summary>
  [JsonPropertyName("text")]
  public required string Text { get; init; }
}

/// <summary>Binary contents.</summary>
public sealed class BlobResourceContents : ResourceContents
{
  /// <summary>Base64 data.</summary>
  [JsonPropertyName("blob")]
  public required string Blob { get; init; }
}

/// <summary>Hints a client can show about a tool without trusting them (they come from the server).</summary>
public sealed class ToolAnnotations : WireObject
{
  /// <summary>A display title.</summary>
  [JsonPropertyName("title")]
  public string? Title { get; init; }

  /// <summary>The tool does not change its environment.</summary>
  [JsonPropertyName("readOnlyHint")]
  public bool? ReadOnlyHint { get; init; }

  /// <summary>The tool may destroy data (meaningful when not read-only).</summary>
  [JsonPropertyName("destructiveHint")]
  public bool? DestructiveHint { get; init; }

  /// <summary>Calling again with the same arguments has no further effect.</summary>
  [JsonPropertyName("idempotentHint")]
  public bool? IdempotentHint { get; init; }

  /// <summary>The tool reaches outside a closed world (the web, other systems).</summary>
  [JsonPropertyName("openWorldHint")]
  public bool? OpenWorldHint { get; init; }
}

/// <summary>A tool as listed.</summary>
public sealed class Tool : WireObject
{
  /// <summary>The programmatic name.</summary>
  [JsonPropertyName("name")]
  public required string Name { get; init; }

  /// <summary>A display name.</summary>
  [JsonPropertyName("title")]
  public string? Title { get; init; }

  /// <summary>What the tool does.</summary>
  [JsonPropertyName("description")]
  public string? Description { get; init; }

  /// <summary>JSON Schema (2020-12 by default on 2026-07-28) for the arguments; an object schema.</summary>
  [JsonPropertyName("inputSchema")]
  public required JsonElement InputSchema { get; init; }

  /// <summary>JSON Schema for <c>structuredContent</c> (2025-06-18 and later).</summary>
  [JsonPropertyName("outputSchema")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public JsonElement OutputSchema { get; init; }

  /// <summary>Behaviour hints.</summary>
  [JsonPropertyName("annotations")]
  public ToolAnnotations? Annotations { get; init; }

  /// <summary>Icons.</summary>
  [JsonPropertyName("icons")]
  public List<Icon>? Icons { get; init; }

  /// <summary>The <c>_meta</c> object.</summary>
  [JsonPropertyName("_meta")]
  public MetaObject? Meta { get; init; }
}

/// <summary>A resource as listed.</summary>
public sealed class Resource : WireObject
{
  /// <summary>The URI.</summary>
  [JsonPropertyName("uri")]
  public required string Uri { get; init; }

  /// <summary>The programmatic name.</summary>
  [JsonPropertyName("name")]
  public required string Name { get; init; }

  /// <summary>A display name.</summary>
  [JsonPropertyName("title")]
  public string? Title { get; init; }

  /// <summary>What it is.</summary>
  [JsonPropertyName("description")]
  public string? Description { get; init; }

  /// <summary>The media type.</summary>
  [JsonPropertyName("mimeType")]
  public string? MimeType { get; init; }

  /// <summary>Hints.</summary>
  [JsonPropertyName("annotations")]
  public Annotations? Annotations { get; init; }

  /// <summary>Size in bytes.</summary>
  [JsonPropertyName("size")]
  public long? Size { get; init; }

  /// <summary>Icons.</summary>
  [JsonPropertyName("icons")]
  public List<Icon>? Icons { get; init; }

  /// <summary>The <c>_meta</c> object.</summary>
  [JsonPropertyName("_meta")]
  public MetaObject? Meta { get; init; }
}

/// <summary>A resource template as listed (RFC 6570 URI template).</summary>
public sealed class ResourceTemplate : WireObject
{
  /// <summary>The URI template.</summary>
  [JsonPropertyName("uriTemplate")]
  public required string UriTemplate { get; init; }

  /// <summary>The programmatic name.</summary>
  [JsonPropertyName("name")]
  public required string Name { get; init; }

  /// <summary>A display name.</summary>
  [JsonPropertyName("title")]
  public string? Title { get; init; }

  /// <summary>What it is.</summary>
  [JsonPropertyName("description")]
  public string? Description { get; init; }

  /// <summary>The media type.</summary>
  [JsonPropertyName("mimeType")]
  public string? MimeType { get; init; }

  /// <summary>Hints.</summary>
  [JsonPropertyName("annotations")]
  public Annotations? Annotations { get; init; }

  /// <summary>Icons.</summary>
  [JsonPropertyName("icons")]
  public List<Icon>? Icons { get; init; }

  /// <summary>The <c>_meta</c> object.</summary>
  [JsonPropertyName("_meta")]
  public MetaObject? Meta { get; init; }
}

/// <summary>A prompt as listed.</summary>
public sealed class Prompt : WireObject
{
  /// <summary>The programmatic name.</summary>
  [JsonPropertyName("name")]
  public required string Name { get; init; }

  /// <summary>A display name.</summary>
  [JsonPropertyName("title")]
  public string? Title { get; init; }

  /// <summary>What it does.</summary>
  [JsonPropertyName("description")]
  public string? Description { get; init; }

  /// <summary>The arguments it takes.</summary>
  [JsonPropertyName("arguments")]
  public List<PromptArgument>? Arguments { get; init; }

  /// <summary>Icons.</summary>
  [JsonPropertyName("icons")]
  public List<Icon>? Icons { get; init; }

  /// <summary>The <c>_meta</c> object.</summary>
  [JsonPropertyName("_meta")]
  public MetaObject? Meta { get; init; }
}

/// <summary>An argument a prompt takes.</summary>
public sealed class PromptArgument : WireObject
{
  /// <summary>The name.</summary>
  [JsonPropertyName("name")]
  public required string Name { get; init; }

  /// <summary>A display name.</summary>
  [JsonPropertyName("title")]
  public string? Title { get; init; }

  /// <summary>What it is for.</summary>
  [JsonPropertyName("description")]
  public string? Description { get; init; }

  /// <summary>True when the prompt cannot be built without it.</summary>
  [JsonPropertyName("required")]
  public bool? Required { get; init; }
}

/// <summary>A message in a prompt.</summary>
public sealed class PromptMessage : WireObject
{
  /// <summary><c>user</c> or <c>assistant</c>.</summary>
  [JsonPropertyName("role")]
  public required string Role { get; init; }

  /// <summary>The content.</summary>
  [JsonPropertyName("content")]
  public required ContentBlock Content { get; init; }
}

/// <summary>A root: a place in the client's world the server may work in (deprecated in 2026-07-28).</summary>
[Obsolete(DeprecatedFeatures.Roots, DiagnosticId = DeprecatedFeatures.RootsId)]
public sealed class Root : WireObject
{
  /// <summary>The URI; <c>file:</c> in practice.</summary>
  [JsonPropertyName("uri")]
  public required string Uri { get; init; }

  /// <summary>A display name.</summary>
  [JsonPropertyName("name")]
  public string? Name { get; init; }

  /// <summary>The <c>_meta</c> object.</summary>
  [JsonPropertyName("_meta")]
  public MetaObject? Meta { get; init; }
}

/// <summary>A message in a sampling request or result. <c>content</c> is a single block or an array on the wire.</summary>
[Obsolete(DeprecatedFeatures.Sampling, DiagnosticId = DeprecatedFeatures.SamplingId)]
public sealed class SamplingMessage : WireObject
{
  /// <summary><c>user</c> or <c>assistant</c>.</summary>
  [JsonPropertyName("role")]
  public required string Role { get; init; }

  /// <summary>
  /// The blocks. A single block is written as an object, several as an array, which is what every revision accepts
  /// (arrays arrive in 2025-11-25).
  /// </summary>
  [JsonPropertyName("content")]
  [JsonConverter(typeof(ContentBlockListConverter))]
  public required List<ContentBlock> Content { get; init; }

  /// <summary>The <c>_meta</c> object.</summary>
  [JsonPropertyName("_meta")]
  public MetaObject? Meta { get; init; }
}

/// <summary>How a server weighs model choice in a sampling request.</summary>
[Obsolete(DeprecatedFeatures.Sampling, DiagnosticId = DeprecatedFeatures.SamplingId)]
public sealed class ModelPreferences : WireObject
{
  /// <summary>Model name hints, tried in order.</summary>
  [JsonPropertyName("hints")]
  public List<ModelHint>? Hints { get; init; }

  /// <summary>0 to 1.</summary>
  [JsonPropertyName("costPriority")]
  public double? CostPriority { get; init; }

  /// <summary>0 to 1.</summary>
  [JsonPropertyName("speedPriority")]
  public double? SpeedPriority { get; init; }

  /// <summary>0 to 1.</summary>
  [JsonPropertyName("intelligencePriority")]
  public double? IntelligencePriority { get; init; }
}

/// <summary>A model name hint.</summary>
[Obsolete(DeprecatedFeatures.Sampling, DiagnosticId = DeprecatedFeatures.SamplingId)]
public sealed class ModelHint : WireObject
{
  /// <summary>A model name or a substring of one.</summary>
  [JsonPropertyName("name")]
  public string? Name { get; init; }
}

/// <summary>How a model may use the tools of a sampling request.</summary>
public sealed class ToolChoice : WireObject
{
  /// <summary><c>auto</c>, <c>required</c> or <c>none</c>.</summary>
  [JsonPropertyName("mode")]
  public string? Mode { get; init; }
}

/// <summary>A reference in a completion request: <c>ref/prompt</c> (with <see cref="Name"/>) or <c>ref/resource</c> (with <see cref="Uri"/>).</summary>
public sealed class CompletionReference : WireObject
{
  /// <summary><c>ref/prompt</c> or <c>ref/resource</c>.</summary>
  [JsonPropertyName("type")]
  public required string Type { get; init; }

  /// <summary>The prompt name, for <c>ref/prompt</c>.</summary>
  [JsonPropertyName("name")]
  public string? Name { get; init; }

  /// <summary>A display name, for <c>ref/prompt</c>.</summary>
  [JsonPropertyName("title")]
  public string? Title { get; init; }

  /// <summary>The template URI, for <c>ref/resource</c>.</summary>
  [JsonPropertyName("uri")]
  public string? Uri { get; init; }
}

/// <summary>The argument being completed.</summary>
public sealed class CompleteArgument : WireObject
{
  /// <summary>The argument name.</summary>
  [JsonPropertyName("name")]
  public required string Name { get; init; }

  /// <summary>The text typed so far.</summary>
  [JsonPropertyName("value")]
  public required string Value { get; init; }
}

/// <summary>Already-resolved arguments that can narrow a completion.</summary>
public sealed class CompleteContext : WireObject
{
  /// <summary>Argument values by name.</summary>
  [JsonPropertyName("arguments")]
  public Dictionary<string, string>? Arguments { get; init; }
}

/// <summary>Completion suggestions.</summary>
public sealed class CompletionValues : WireObject
{
  /// <summary>Up to 100 suggestions, best first.</summary>
  [JsonPropertyName("values")]
  public required List<string> Values { get; init; }

  /// <summary>How many exist.</summary>
  [JsonPropertyName("total")]
  public int? Total { get; init; }

  /// <summary>More exist beyond <see cref="Values"/>.</summary>
  [JsonPropertyName("hasMore")]
  public bool? HasMore { get; init; }
}

/// <summary>Which notifications a <c>subscriptions/listen</c> request opts into (2026-07-28).</summary>
public sealed class SubscriptionFilter : WireObject
{
  /// <summary>Send tool list changes.</summary>
  [JsonPropertyName("toolsListChanged")]
  public bool? ToolsListChanged { get; init; }

  /// <summary>Send prompt list changes.</summary>
  [JsonPropertyName("promptsListChanged")]
  public bool? PromptsListChanged { get; init; }

  /// <summary>Send resource list changes.</summary>
  [JsonPropertyName("resourcesListChanged")]
  public bool? ResourcesListChanged { get; init; }

  /// <summary>Send updates for these resource URIs.</summary>
  [JsonPropertyName("resourceSubscriptions")]
  public List<string>? ResourceSubscriptions { get; init; }
}

/// <summary>A request the server needs the client to answer before it can finish (2026-07-28 multi round-trip requests).</summary>
public sealed class InputRequest : WireObject
{
  /// <summary><c>sampling/createMessage</c>, <c>roots/list</c> or <c>elicitation/create</c>.</summary>
  [JsonPropertyName("method")]
  public required string Method { get; init; }

  /// <summary>The parameters of that request.</summary>
  [JsonPropertyName("params")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public JsonElement Params { get; init; }
}

/// <summary>Reads and writes a block list that is a single object or an array on the wire.</summary>
public sealed class ContentBlockListConverter : JsonConverter<List<ContentBlock>>
{
  /// <inheritdoc />
  public override List<ContentBlock>? Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
  {
    if (reader.TokenType == JsonTokenType.StartArray)
    {
      var list = new List<ContentBlock>();
      while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
      {
        list.Add(ContentBlockConverter.ReadBlock(ref reader));
      }

      return list;
    }

    return [ContentBlockConverter.ReadBlock(ref reader)];
  }

  /// <inheritdoc />
  public override void Write(Utf8JsonWriter writer, List<ContentBlock> value, JsonSerializerOptions options)
  {
    if (value.Count == 1)
    {
      ContentBlockConverter.WriteBlock(writer, value[0]);
      return;
    }

    writer.WriteStartArray();
    foreach (var block in value)
    {
      ContentBlockConverter.WriteBlock(writer, block);
    }

    writer.WriteEndArray();
  }
}

/// <summary>Chooses the concrete <see cref="ContentBlock"/> from the <c>type</c> member.</summary>
public sealed class ContentBlockConverter : JsonConverter<ContentBlock>
{
  /// <inheritdoc />
  public override bool CanConvert(System.Type typeToConvert) => typeToConvert == typeof(ContentBlock);

  /// <inheritdoc />
  public override ContentBlock? Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options) => ReadBlock(ref reader);

  /// <inheritdoc />
  public override void Write(Utf8JsonWriter writer, ContentBlock value, JsonSerializerOptions options) => WriteBlock(writer, value);

  internal static ContentBlock ReadBlock(ref Utf8JsonReader reader)
  {
    using var doc = JsonDocument.ParseValue(ref reader);
    var element = doc.RootElement;
    var type = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
    var context = WireJsonContext.Default;
    ContentBlock? block = type switch
    {
      "text" => element.Deserialize(context.TextContent),
      "image" => element.Deserialize(context.ImageContent),
      "audio" => element.Deserialize(context.AudioContent),
      "resource_link" => element.Deserialize(context.ResourceLink),
      "resource" => element.Deserialize(context.EmbeddedResource),
      "tool_use" => element.Deserialize(context.ToolUseContent),
      "tool_result" => element.Deserialize(context.ToolResultContent),
      _ => null,
    };

    return block ?? new UnknownContent { RawType = type ?? string.Empty, Raw = element.Clone() };
  }

  internal static void WriteBlock(Utf8JsonWriter writer, ContentBlock block)
  {
    var context = WireJsonContext.Default;
    switch (block)
    {
      case TextContent text:
        WriteTyped(writer, text.Type, text, context.TextContent);
        break;
      case ImageContent image:
        WriteTyped(writer, image.Type, image, context.ImageContent);
        break;
      case AudioContent audio:
        WriteTyped(writer, audio.Type, audio, context.AudioContent);
        break;
      case ResourceLink link:
        WriteTyped(writer, link.Type, link, context.ResourceLink);
        break;
      case EmbeddedResource embedded:
        WriteTyped(writer, embedded.Type, embedded, context.EmbeddedResource);
        break;
      case ToolUseContent use:
        WriteTyped(writer, use.Type, use, context.ToolUseContent);
        break;
      case ToolResultContent result:
        WriteTyped(writer, result.Type, result, context.ToolResultContent);
        break;
      case UnknownContent unknown:
        unknown.Raw.WriteTo(writer);
        break;
      default:
        throw new JsonException("Unknown content block type.");
    }
  }

  // Serializes the record, then puts "type" first by writing it ahead of the record's members.
  private static void WriteTyped<T>(Utf8JsonWriter writer, string type, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
  {
    using var element = JsonSerializer.SerializeToDocument(value, info);
    writer.WriteStartObject();
    writer.WriteString("type", type);
    foreach (var property in element.RootElement.EnumerateObject())
    {
      // "type" reads back into the extension data (the property is get-only); it was written above.
      if (property.Name != "type")
      {
        property.WriteTo(writer);
      }
    }

    writer.WriteEndObject();
  }
}

/// <summary>Chooses <see cref="TextResourceContents"/> or <see cref="BlobResourceContents"/> by which of <c>text</c> and <c>blob</c> is present.</summary>
public sealed class ResourceContentsConverter : JsonConverter<ResourceContents>
{
  /// <inheritdoc />
  public override bool CanConvert(System.Type typeToConvert) => typeToConvert == typeof(ResourceContents);

  /// <inheritdoc />
  public override ResourceContents? Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
  {
    using var doc = JsonDocument.ParseValue(ref reader);
    var element = doc.RootElement;
    if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("blob", out _))
    {
      return element.Deserialize(WireJsonContext.Default.BlobResourceContents);
    }

    return element.Deserialize(WireJsonContext.Default.TextResourceContents);
  }

  /// <inheritdoc />
  public override void Write(Utf8JsonWriter writer, ResourceContents value, JsonSerializerOptions options)
  {
    switch (value)
    {
      case TextResourceContents text:
        JsonSerializer.Serialize(writer, text, WireJsonContext.Default.TextResourceContents);
        break;
      case BlobResourceContents blob:
        JsonSerializer.Serialize(writer, blob, WireJsonContext.Default.BlobResourceContents);
        break;
      default:
        throw new JsonException("Unknown resource contents type.");
    }
  }
}
