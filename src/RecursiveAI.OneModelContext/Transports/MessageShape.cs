using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Transports;

internal enum MessageKind
{
  Invalid,
  Request,
  Notification,
  Response,
  Batch,
}

/// <summary>What a transport needs to know about a JSON-RPC message without decoding it: its kind, id and method.</summary>
internal readonly record struct MessageShape(MessageKind Kind, JsonRpcId Id, string? Method)
{
  /// <summary>Scans the top-level members of a message. Never throws: odd input is <see cref="MessageKind.Invalid"/>.</summary>
  internal static MessageShape Inspect(ReadOnlySpan<byte> utf8)
  {
    try
    {
      var reader = new Utf8JsonReader(utf8, isFinalBlock: true, new JsonReaderState(new JsonReaderOptions { MaxDepth = 64 }));
      if (!reader.Read())
      {
        return default;
      }

      if (reader.TokenType == JsonTokenType.StartArray)
      {
        return new MessageShape(MessageKind.Batch, default, null);
      }

      if (reader.TokenType != JsonTokenType.StartObject)
      {
        return default;
      }

      JsonRpcId id = default;
      string? method = null;
      var hasOutcome = false;
      while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
      {
        var isId = reader.ValueTextEquals("id"u8);
        var isMethod = reader.ValueTextEquals("method"u8);
        var isOutcome = reader.ValueTextEquals("result"u8) || reader.ValueTextEquals("error"u8);
        reader.Read();
        if (isId)
        {
          if (reader.TokenType == JsonTokenType.String)
          {
            id = JsonRpcId.FromString(reader.GetString()!);
          }
          else if (reader.TokenType == JsonTokenType.Number)
          {
            // The verbatim number text, as JsonRpcId keeps it.
            id = JsonRpcId.FromNumberText(Encoding.UTF8.GetString(reader.ValueSpan));
          }
        }
        else if (isMethod && reader.TokenType == JsonTokenType.String)
        {
          method = reader.GetString();
        }
        else if (isOutcome)
        {
          hasOutcome = true;
        }

        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
          reader.Skip();
        }
      }

      if (method is not null)
      {
        return new MessageShape(id.HasValue ? MessageKind.Request : MessageKind.Notification, id, method);
      }

      return hasOutcome ? new MessageShape(MessageKind.Response, id, null) : default;
    }
    catch (JsonException)
    {
      return default;
    }
  }

  /// <summary>Copies a message with its <c>id</c> replaced.</summary>
  internal static byte[] WithId(ReadOnlySpan<byte> utf8, JsonRpcId id)
  {
    using var document = JsonDocument.Parse(utf8.ToArray());
    var buffer = new ArrayBufferWriter<byte>(utf8.Length + 16);
    using (var writer = new Utf8JsonWriter(buffer))
    {
      writer.WriteStartObject();
      foreach (var property in document.RootElement.EnumerateObject())
      {
        if (property.NameEquals("id"))
        {
          writer.WritePropertyName("id");
          id.WriteTo(writer);
        }
        else
        {
          property.WriteTo(writer);
        }
      }

      writer.WriteEndObject();
    }

    return buffer.WrittenSpan.ToArray();
  }
}

/// <summary>One tool parameter mirrored into a header: <c>Mcp-Param-{Name}</c> carries the value at <c>Path</c> of the arguments.</summary>
internal sealed record HeaderParameter(string Name, string[] Path);

/// <summary>
/// 2026-07-28 Streamable HTTP, "Custom Headers from Tool Parameters" and "Value Encoding": which parameters of a tool go into headers and
/// how a value is written. Applies to the HTTP transport only.
/// </summary>
internal static class HeaderMirroring
{
  private const string Prefix = "=?base64?";
  private const string Suffix = "?=";
  private const int MaxSchemaDepth = 32;
  private const int MaxSchemaNodes = 20_000;
  private const long MaxSafeInteger = 9_007_199_254_740_991;

  /// <summary>
  /// Reads the <c>x-mcp-header</c> annotations of a tool's input schema.
  /// </summary>
  /// <returns>False, with the reason, when an annotation breaks a rule: the tool is then left out of the client's tool list.</returns>
  internal static bool TryRead(Tool tool, out HeaderParameter[] parameters, out string? reason)
  {
    var found = new List<HeaderParameter>();
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var nodes = 0;
    string? failure = null;
    Walk(tool.InputSchema, onChain: true, [], 0);
    parameters = failure is null ? [.. found] : [];
    reason = failure;
    return failure is null;

    void Walk(JsonElement node, bool onChain, List<string> path, int depth)
    {
      if (failure is not null)
      {
        return;
      }

      if (++nodes > MaxSchemaNodes || depth > MaxSchemaDepth)
      {
        failure = "the input schema is too large or too deeply nested to check";
        return;
      }

      if (node.ValueKind == JsonValueKind.Array)
      {
        foreach (var item in node.EnumerateArray())
        {
          Walk(item, false, path, depth + 1);
        }

        return;
      }

      if (node.ValueKind != JsonValueKind.Object)
      {
        return;
      }

      foreach (var member in node.EnumerateObject())
      {
        if (member.NameEquals("x-mcp-header"))
        {
          Annotation(node, member.Value, onChain, path);
        }
        else if (member.NameEquals("properties") && member.Value.ValueKind == JsonValueKind.Object)
        {
          foreach (var child in member.Value.EnumerateObject())
          {
            path.Add(child.Name);
            Walk(child.Value, onChain, path, depth + 1);
            path.RemoveAt(path.Count - 1);
          }
        }
        else
        {
          Walk(member.Value, false, path, depth + 1);
        }
      }
    }

    void Annotation(JsonElement owner, JsonElement value, bool onChain, List<string> path)
    {
      if (failure is not null)
      {
        return;
      }

      if (!onChain || path.Count == 0)
      {
        failure = "x-mcp-header is not on a property reachable through properties keys alone";
        return;
      }

      if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } name)
      {
        failure = "x-mcp-header must be a non-empty string";
        return;
      }

      foreach (var c in name)
      {
        if (!IsTokenChar(c))
        {
          failure = "x-mcp-header '" + Printable(name) + "' is not a valid HTTP field name";
          return;
        }
      }

      if (!names.Add(name))
      {
        failure = "x-mcp-header '" + name + "' is used more than once (names compare without case)";
        return;
      }

      if (!owner.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() is not ("string" or "integer" or "boolean"))
      {
        failure = "x-mcp-header '" + name + "' is on a parameter that is not a string, integer or boolean";
        return;
      }

      found.Add(new HeaderParameter(name, [.. path]));
    }
  }

  /// <summary>The header value for the argument at a parameter's path, or null when the arguments carry no value there.</summary>
  internal static string? ValueOf(JsonElement arguments, HeaderParameter parameter)
  {
    var current = arguments;
    foreach (var segment in parameter.Path)
    {
      if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
      {
        return null;
      }
    }

    switch (current.ValueKind)
    {
      case JsonValueKind.String:
        return Encode(current.GetString()!);
      case JsonValueKind.True:
        return "true";
      case JsonValueKind.False:
        return "false";
      case JsonValueKind.Number:
        if (current.TryGetInt64(out var integer))
        {
          // Outside the range every JavaScript peer reads exactly, an integer is not mirrored.
          return Math.Abs(integer) > MaxSafeInteger ? null : integer.ToString(CultureInfo.InvariantCulture);
        }

        return null;
      default:
        return null;
    }
  }

  /// <summary>
  /// Writes a value for a header: as it is when it is plain visible ASCII (spaces inside are fine), otherwise as <c>=?base64?...?=</c> of
  /// its UTF-8 bytes. A plain value that looks like the sentinel is encoded too, so it cannot be mistaken for one.
  /// </summary>
  internal static string Encode(string value)
  {
    var plain = value.Length == 0 || (value[0] != ' ' && value[^1] != ' ');
    if (plain)
    {
      foreach (var c in value)
      {
        if (!(c == ' ' || (c >= 0x21 && c <= 0x7E)))
        {
          plain = false;
          break;
        }
      }
    }

    if (plain && value.StartsWith(Prefix, StringComparison.Ordinal) && value.EndsWith(Suffix, StringComparison.Ordinal))
    {
      plain = false;
    }

    return plain ? value : Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + Suffix;
  }

  private static bool IsTokenChar(char c) =>
    c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
      or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

  private static string Printable(string text)
  {
    var builder = new StringBuilder(Math.Min(text.Length, 40));
    foreach (var c in text.Length > 40 ? text[..40] : text)
    {
      builder.Append(c is >= ' ' and <= '~' ? c : '?');
    }

    return builder.ToString();
  }
}
