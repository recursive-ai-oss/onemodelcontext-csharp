using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Cli;

/// <summary>Builds a tool's arguments object from <c>--arg k=v</c> pairs or JSON.</summary>
internal static class ToolArguments
{
  public static JsonElement Empty { get; } = JsonDocument.Parse("{}").RootElement.Clone();

  /// <summary>Reads a JSON object from literal text or, after <c>@</c>, from a file (or standard input for <c>@-</c>, which is not supported).</summary>
  public static JsonElement FromJson(string textOrFile)
  {
    string text;
    if (textOrFile.StartsWith('@'))
    {
      var path = textOrFile[1..];
      try
      {
        text = File.ReadAllText(path);
      }
      catch (IOException ex)
      {
        throw new ArgumentException("cannot read '" + path + "': " + ex.Message, ex);
      }
      catch (UnauthorizedAccessException ex)
      {
        throw new ArgumentException("cannot read '" + path + "': " + ex.Message, ex);
      }
    }
    else
    {
      text = textOrFile;
    }

    try
    {
      using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
      if (doc.RootElement.ValueKind != JsonValueKind.Object)
      {
        throw new ArgumentException("tool arguments must be a JSON object.");
      }

      return doc.RootElement.Clone();
    }
    catch (JsonException ex)
    {
      throw new ArgumentException("tool arguments are not valid JSON: " + ex.Message, ex);
    }
  }

  /// <summary>
  /// Makes the object from key=value pairs. A property the schema types as integer, number, boolean, array or object is parsed as that JSON;
  /// a string stays a string. With no schema type, a value that is a JSON number, boolean, null, array or object is taken as one.
  /// </summary>
  public static JsonElement FromPairs(IReadOnlyList<KeyValuePair<string, string>> pairs, JsonElement inputSchema)
  {
    using var stream = new MemoryStream();
    using (var w = new Utf8JsonWriter(stream))
    {
      w.WriteStartObject();
      foreach (var (key, value) in pairs)
      {
        w.WritePropertyName(key);
        var type = TypeOf(inputSchema, key);
        if (type is "string")
        {
          w.WriteStringValue(value);
          continue;
        }

        var parsed = TryLiteral(value);
        if (type is null)
        {
          if (parsed is { } any && (any.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Array or JsonValueKind.Object))
          {
            any.WriteTo(w);
          }
          else
          {
            w.WriteStringValue(value);
          }

          continue;
        }

        if (parsed is { } literal && Matches(type, literal))
        {
          literal.WriteTo(w);
        }
        else
        {
          throw new ArgumentException("argument '" + key + "' should be " + type + ", not '" + value + "'.");
        }
      }

      w.WriteEndObject();
    }

    using var result = JsonDocument.Parse(stream.ToArray());
    return result.RootElement.Clone();
  }

  private static JsonElement? TryLiteral(string value)
  {
    try
    {
      using var doc = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 64 });
      return doc.RootElement.Clone();
    }
    catch (JsonException)
    {
      return null;
    }
  }

  private static bool Matches(string type, JsonElement value) => type switch
  {
    "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
    "number" => value.ValueKind == JsonValueKind.Number,
    "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
    "array" => value.ValueKind == JsonValueKind.Array,
    "object" => value.ValueKind == JsonValueKind.Object,
    "null" => value.ValueKind == JsonValueKind.Null,
    _ => true,
  };

  // The first non-null type of a property, from "type": "x" or "type": ["x", "null"]. Null when the schema does not say.
  private static string? TypeOf(JsonElement schema, string property)
  {
    if (schema.ValueKind != JsonValueKind.Object
        || !schema.TryGetProperty("properties", out var props)
        || props.ValueKind != JsonValueKind.Object
        || !props.TryGetProperty(property, out var p)
        || p.ValueKind != JsonValueKind.Object
        || !p.TryGetProperty("type", out var type))
    {
      return null;
    }

    if (type.ValueKind == JsonValueKind.String)
    {
      return type.GetString();
    }

    if (type.ValueKind == JsonValueKind.Array)
    {
      foreach (var t in type.EnumerateArray())
      {
        if (t.ValueKind == JsonValueKind.String && t.GetString() != "null")
        {
          return t.GetString();
        }
      }
    }

    return null;
  }
}

/// <summary>Text and JSON forms of what the commands print.</summary>
internal sealed class Render(bool colour)
{
  public string Heading(string name) => colour ? "\u001b[1m" + name + "\u001b[0m" : name;

  /// <summary>" - first line of description", or nothing.</summary>
  public static string Summary(string? description)
  {
    if (string.IsNullOrWhiteSpace(description))
    {
      return "";
    }

    var line = description.Trim();
    var nl = line.IndexOf('\n', StringComparison.Ordinal);
    return " - " + (nl >= 0 ? line[..nl].TrimEnd() : line);
  }

  public static string CapabilityNames(ServerCapabilities caps)
  {
    var names = new List<string>();
    if (caps.Tools is not null)
    {
      names.Add("tools" + (caps.Tools.ListChanged == true ? " (listChanged)" : ""));
    }

    if (caps.Resources is not null)
    {
      var flags = new List<string>();
      if (caps.Resources.Subscribe == true)
      {
        flags.Add("subscribe");
      }

      if (caps.Resources.ListChanged == true)
      {
        flags.Add("listChanged");
      }

      names.Add("resources" + (flags.Count > 0 ? " (" + string.Join(", ", flags) + ")" : ""));
    }

    if (caps.Prompts is not null)
    {
      names.Add("prompts" + (caps.Prompts.ListChanged == true ? " (listChanged)" : ""));
    }

    if (caps.Logging.ValueKind == JsonValueKind.Object)
    {
      names.Add("logging");
    }

    if (caps.Completions.ValueKind == JsonValueKind.Object)
    {
      names.Add("completions");
    }

    return names.Count == 0 ? "none" : string.Join(", ", names);
  }

  /// <summary>The text of a tool result; other content is described in brackets. Structured content is printed when there is no text.</summary>
  public static string ToolResultText(CallToolResult result)
  {
    var text = new StringBuilder();
    foreach (var block in result.Content)
    {
      switch (block)
      {
        case TextContent t:
          text.AppendLine(t.Text);
          break;
        case ImageContent i:
          text.AppendLine("[image " + i.MimeType + ", " + Base64Size(i.Data) + " bytes]");
          break;
        case AudioContent a:
          text.AppendLine("[audio " + a.MimeType + ", " + Base64Size(a.Data) + " bytes]");
          break;
        case ResourceLink l:
          text.AppendLine("[resource " + l.Uri + "]");
          break;
        case EmbeddedResource e:
          text.AppendLine(ResourceBlock(e.Resource));
          break;
        default:
          text.AppendLine("[" + block.Type + "]");
          break;
      }
    }

    if (text.Length == 0 && result.StructuredContent.ValueKind != JsonValueKind.Undefined)
    {
      text.AppendLine(result.StructuredContent.GetRawText());
    }

    return text.ToString();
  }

  public static string ResourceText(ReadResourceResult result)
  {
    var text = new StringBuilder();
    foreach (var c in result.Contents)
    {
      text.AppendLine(ResourceBlock(c));
    }

    return text.ToString();
  }

  private static string ResourceBlock(ResourceContents c) => c switch
  {
    TextResourceContents t => t.Text,
    BlobResourceContents b => "[blob " + b.Uri + (b.MimeType is null ? "" : " " + b.MimeType) + ", " + Base64Size(b.Blob) + " bytes]",
    _ => "[resource " + c.Uri + "]",
  };

  public static string PromptText(GetPromptResult result)
  {
    var text = new StringBuilder();
    if (!string.IsNullOrEmpty(result.Description))
    {
      text.AppendLine(result.Description);
    }

    foreach (var m in result.Messages)
    {
      text.Append(m.Role).Append(": ").AppendLine(m.Content switch
      {
        TextContent t => t.Text,
        ImageContent i => "[image " + i.MimeType + "]",
        AudioContent a => "[audio " + a.MimeType + "]",
        EmbeddedResource e => ResourceBlock(e.Resource),
        ResourceLink l => "[resource " + l.Uri + "]",
        var other => "[" + other.Type + "]",
      });
    }

    return text.ToString();
  }

  private static string Base64Size(string data) =>
    (data.Length / 4 * 3 - (data.EndsWith("==", StringComparison.Ordinal) ? 2 : data.EndsWith('=') ? 1 : 0)).ToString(CultureInfo.InvariantCulture);

  public static string InspectJson(
    ContextClient client,
    string eraReason,
    ListedItems<Tool>? tools,
    ListedItems<Resource>? resources,
    ListedItems<ResourceTemplate>? templates,
    ListedItems<Prompt>? prompts)
  {
    using var stream = new MemoryStream();
    using (var w = new Utf8JsonWriter(stream))
    {
      w.WriteStartObject();
      w.WritePropertyName("serverInfo");
      JsonSerializer.SerializeToElement(client.ServerInfo, WireJsonContext.Default.Implementation).WriteTo(w);
      w.WriteString("protocolVersion", client.ProtocolVersion);
      w.WriteString("era", client.Era == ProtocolEra.Modern ? "modern" : "legacy");
      w.WriteString("eraReason", eraReason);
      if (client.Instructions is not null)
      {
        w.WriteString("instructions", client.Instructions);
      }

      w.WritePropertyName("capabilities");
      JsonSerializer.SerializeToElement(client.ServerCapabilities, WireJsonContext.Default.ServerCapabilities).WriteTo(w);
      if (tools is not null)
      {
        w.WriteStartArray("tools");
        foreach (var t in tools)
        {
          JsonSerializer.SerializeToElement(t, WireJsonContext.Default.Tool).WriteTo(w);
        }

        w.WriteEndArray();
      }

      if (resources is not null)
      {
        w.WriteStartArray("resources");
        foreach (var r in resources)
        {
          JsonSerializer.SerializeToElement(r, WireJsonContext.Default.Resource).WriteTo(w);
        }

        w.WriteEndArray();
        w.WriteStartArray("resourceTemplates");
        foreach (var r in templates!)
        {
          JsonSerializer.SerializeToElement(r, WireJsonContext.Default.ResourceTemplate).WriteTo(w);
        }

        w.WriteEndArray();
      }

      if (prompts is not null)
      {
        w.WriteStartArray("prompts");
        foreach (var p in prompts)
        {
          JsonSerializer.SerializeToElement(p, WireJsonContext.Default.Prompt).WriteTo(w);
        }

        w.WriteEndArray();
      }

      w.WriteEndObject();
    }

    return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
  }
}
