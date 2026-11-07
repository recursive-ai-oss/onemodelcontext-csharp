using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Binding;

/// <summary>
/// Serialization metadata the generated invokers use for parameter and return types that need no user-declared context: the primitives
/// of the signature mapping, their nullable forms and a few common collections. Not for direct use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(sbyte))]
[JsonSerializable(typeof(byte))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(ushort))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(char))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(DateOnly))]
[JsonSerializable(typeof(TimeOnly))]
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(Uri))]
[JsonSerializable(typeof(byte[]))]
[JsonSerializable(typeof(bool?))]
[JsonSerializable(typeof(sbyte?))]
[JsonSerializable(typeof(byte?))]
[JsonSerializable(typeof(short?))]
[JsonSerializable(typeof(ushort?))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(uint?))]
[JsonSerializable(typeof(long?))]
[JsonSerializable(typeof(ulong?))]
[JsonSerializable(typeof(float?))]
[JsonSerializable(typeof(double?))]
[JsonSerializable(typeof(decimal?))]
[JsonSerializable(typeof(char?))]
[JsonSerializable(typeof(Guid?))]
[JsonSerializable(typeof(DateTime?))]
[JsonSerializable(typeof(DateTimeOffset?))]
[JsonSerializable(typeof(DateOnly?))]
[JsonSerializable(typeof(TimeOnly?))]
[JsonSerializable(typeof(TimeSpan?))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IEnumerable<string>))]
[JsonSerializable(typeof(int[]))]
[JsonSerializable(typeof(List<int>))]
[JsonSerializable(typeof(long[]))]
[JsonSerializable(typeof(double[]))]
[JsonSerializable(typeof(List<double>))]
[JsonSerializable(typeof(bool[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
public sealed partial class BindingJson : JsonSerializerContext
{
}

/// <summary>
/// Reads the <c>arguments</c> object of a <c>tools/call</c> for a generated invoker. Every failure is a <see cref="ToolException"/>, which
/// the server returns as an error result (<c>isError</c>) whose text names the argument, so a model can correct the call (2025-11-25 and
/// 2026-07-28 tools: input validation errors are tool execution errors). Not for direct use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ToolArguments
{
  private readonly JsonElement _arguments;

  /// <summary>Checks that the arguments are an object that names only <paramref name="known"/> properties.</summary>
  /// <param name="arguments">The <c>arguments</c> value, or <c>default</c> when the call had none.</param>
  /// <param name="known">The names the tool declares.</param>
  /// <exception cref="ToolException">The arguments are not an object, or name an argument the tool does not declare.</exception>
  public ToolArguments(JsonElement arguments, string[] known)
  {
    ArgumentNullException.ThrowIfNull(known);
    if (arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
    {
      _arguments = default;
      return;
    }

    if (arguments.ValueKind != JsonValueKind.Object)
    {
      throw new ToolException("The arguments must be a JSON object.");
    }

    foreach (var property in arguments.EnumerateObject())
    {
      if (Array.IndexOf(known, property.Name) < 0)
      {
        throw new ToolException("Unknown argument '" + Clip(property.Name) + "'.");
      }
    }

    _arguments = arguments;
  }

  /// <summary>True when the call names the argument (even with a null value).</summary>
  /// <param name="name">The argument name.</param>
  public bool Has(string name) => _arguments.ValueKind == JsonValueKind.Object && _arguments.TryGetProperty(name, out _);

  /// <summary>Reads an argument through source-generated metadata.</summary>
  /// <typeparam name="T">The parameter type.</typeparam>
  /// <param name="name">The argument name.</param>
  /// <param name="typeInfo">Metadata for <typeparamref name="T"/>.</param>
  /// <param name="expected">What the value must be, for the message ("an integer").</param>
  /// <param name="allowNull">True when JSON <c>null</c> is a valid value.</param>
  /// <exception cref="ToolException">The argument is missing, null where that is not allowed, or of the wrong type.</exception>
  public T Bind<T>(string name, JsonTypeInfo<T> typeInfo, string expected, bool allowNull)
  {
    var element = Find(name);
    if (element.ValueKind == JsonValueKind.Null)
    {
      return allowNull ? default! : throw WrongType(name, expected);
    }

    try
    {
      var value = element.Deserialize(typeInfo);
      return value is null && !allowNull ? throw WrongType(name, expected) : value!;
    }
    catch (JsonException ex)
    {
      throw Invalid(name, expected, ex);
    }
    catch (FormatException)
    {
      throw WrongType(name, expected);
    }
  }

  /// <summary>Reads an enum argument by member name (exact match, case sensitive).</summary>
  /// <typeparam name="T">The enum type.</typeparam>
  /// <param name="name">The argument name.</param>
  /// <param name="names">The member names in declaration order.</param>
  /// <param name="values">The members, parallel to <paramref name="names"/>.</param>
  /// <param name="allowNull">True when JSON <c>null</c> is a valid value; the result is then <c>null</c>.</param>
  /// <exception cref="ToolException">The argument is missing or not one of the names.</exception>
  public T? BindEnum<T>(string name, string[] names, T[] values, bool allowNull)
    where T : struct, Enum
  {
    var element = Find(name);
    var expected = "one of " + string.Join(", ", names);
    if (element.ValueKind == JsonValueKind.Null)
    {
      return allowNull ? null : throw WrongType(name, expected);
    }

    if (element.ValueKind == JsonValueKind.String)
    {
      var text = element.GetString();
      var index = Array.IndexOf(names, text);
      if (index >= 0)
      {
        return values[index];
      }
    }

    throw WrongType(name, expected);
  }

  // The name comes from the caller; keep an absurdly long one out of the message.
  private static string Clip(string name) => name.Length <= 64 ? name : name[..64] + "...";

  private JsonElement Find(string name)
  {
    if (_arguments.ValueKind == JsonValueKind.Object && _arguments.TryGetProperty(name, out var element))
    {
      return element;
    }

    throw new ToolException("Argument '" + name + "' is required.");
  }

  private static ToolException WrongType(string name, string expected) => new("Argument '" + name + "' must be " + expected + ".");

  private static ToolException Invalid(string name, string expected, JsonException ex)
  {
    // The path inside the argument ("$.items[2].qty") makes the message point at the member that failed.
    var path = ex.Path;
    if (string.IsNullOrEmpty(path) || path == "$")
    {
      return WrongType(name, expected);
    }

    var inner = path.StartsWith("$.", StringComparison.Ordinal) ? "." + path[2..] : path.StartsWith('$') ? path[1..] : "." + path;
    return new ToolException("Argument '" + name + inner + "' is not valid.");
  }
}

/// <summary>Turns the value a generated tool method returned into a <see cref="CallToolResult"/>. Not for direct use.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ToolResults
{
  /// <summary>A successful result with no content.</summary>
  public static CallToolResult Empty() => new() { Content = [] };

  /// <summary>One text block; <c>null</c> becomes the text <c>null</c>.</summary>
  /// <param name="text">The text.</param>
  public static CallToolResult Text(string? text) => CallToolResult.FromText(text ?? "null");

  /// <summary>Content blocks as returned.</summary>
  /// <param name="blocks">The blocks; null for none.</param>
  public static CallToolResult Parts(IEnumerable<ContentBlock>? blocks) => new() { Content = blocks is null ? [] : [.. blocks] };

  /// <summary>One block.</summary>
  /// <param name="block">The block.</param>
  public static CallToolResult Part(ContentBlock block) => new() { Content = [block] };

  /// <summary>One text block holding the JSON of a node.</summary>
  /// <param name="node">The node; null becomes <c>null</c>.</param>
  public static CallToolResult Json(JsonNode? node) => CallToolResult.FromText(node is null ? "null" : node.ToJsonString());

  /// <summary>One text block holding a JSON value.</summary>
  /// <param name="element">The value.</param>
  public static CallToolResult Json(JsonElement element) => CallToolResult.FromText(element.ValueKind == JsonValueKind.Undefined ? "null" : element.GetRawText());

  /// <summary>One text block holding <paramref name="value"/> as JSON.</summary>
  /// <typeparam name="T">The value type.</typeparam>
  /// <param name="value">The value.</param>
  /// <param name="typeInfo">Metadata for <typeparamref name="T"/>.</param>
  public static CallToolResult Value<T>(T value, JsonTypeInfo<T> typeInfo) => CallToolResult.FromText(JsonSerializer.Serialize(value, typeInfo));

  /// <summary>An enum member's name as one text block.</summary>
  /// <param name="value">The member name.</param>
  public static CallToolResult EnumName(string? value) => CallToolResult.FromText(value ?? "null");

  /// <summary>
  /// Structured content plus the same JSON as a text block, which is what the tools specification asks of a tool that returns structured
  /// data (clients without structured support still see it).
  /// </summary>
  /// <typeparam name="T">The value type.</typeparam>
  /// <param name="value">The value.</param>
  /// <param name="typeInfo">Metadata for <typeparamref name="T"/>.</param>
  public static CallToolResult Structured<T>(T value, JsonTypeInfo<T> typeInfo)
  {
    var element = JsonSerializer.SerializeToElement(value, typeInfo);
    return new CallToolResult
    {
      Content = [TextContent.Of(element.GetRawText())],
      StructuredContent = element.ValueKind == JsonValueKind.Object ? element : default,
    };
  }

  /// <summary>Parses a schema literal the generator wrote. The document lives as long as the process.</summary>
  /// <param name="json">A JSON object.</param>
  public static JsonElement Schema(string json)
  {
    using var document = JsonDocument.Parse(json);
    return document.RootElement.Clone();
  }
}

/// <summary>Reads template variables and prompt arguments for generated resource and prompt methods. Not for direct use.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class StringArguments
{
  /// <summary>Matches a URI against its template and returns the variables.</summary>
  /// <param name="template">The URI template.</param>
  /// <param name="uri">The URI being read.</param>
  /// <exception cref="ProtocolException">The URI does not match (-32602).</exception>
  public static IReadOnlyDictionary<string, string> Match(string template, string uri) =>
    UriTemplateMatcher.TryMatch(template, uri, out var variables)
      ? variables
      : throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "The URI does not match the template " + template + ".");

  /// <summary>Reads a value by name and parses it.</summary>
  /// <typeparam name="T">The parameter type.</typeparam>
  /// <param name="values">Template variables or prompt arguments.</param>
  /// <param name="name">The name.</param>
  /// <param name="required">True when a missing value is an error.</param>
  /// <param name="result">The parsed value, or <c>default</c> when absent and optional.</param>
  /// <returns>True when a value was present.</returns>
  /// <exception cref="ProtocolException">A required value is missing or does not parse (-32602).</exception>
  public static bool TryGet<T>(IReadOnlyDictionary<string, string> values, string name, bool required, out T? result)
    where T : IParsable<T>
  {
    if (!values.TryGetValue(name, out var text))
    {
      if (required)
      {
        throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "Missing argument '" + name + "'.");
      }

      result = default;
      return false;
    }

    if (!T.TryParse(text, CultureInfo.InvariantCulture, out result))
    {
      throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "Argument '" + name + "' is not valid.");
    }

    return true;
  }

  /// <summary>Text contents for a resource.</summary>
  /// <param name="uri">The URI.</param>
  /// <param name="mimeType">The media type.</param>
  /// <param name="text">The text.</param>
  public static ReadResourceResult TextResource(string uri, string? mimeType, string text) =>
    new() { Contents = [new TextResourceContents { Uri = uri, MimeType = mimeType ?? "text/plain", Text = text }] };

  /// <summary>Binary contents for a resource.</summary>
  /// <param name="uri">The URI.</param>
  /// <param name="mimeType">The media type.</param>
  /// <param name="data">The bytes.</param>
  public static ReadResourceResult BlobResource(string uri, string? mimeType, byte[] data) =>
    new() { Contents = [new BlobResourceContents { Uri = uri, MimeType = mimeType ?? "application/octet-stream", Blob = Convert.ToBase64String(data) }] };

  /// <summary>Contents as returned by the method.</summary>
  /// <param name="contents">The contents.</param>
  public static ReadResourceResult Resource(IEnumerable<ResourceContents> contents) => new() { Contents = [.. contents] };

  /// <summary>A prompt with one user message.</summary>
  /// <param name="description">The prompt description, or null.</param>
  /// <param name="text">The message text.</param>
  public static GetPromptResult UserPrompt(string? description, string text) =>
    new() { Description = description, Messages = [new PromptMessage { Role = "user", Content = TextContent.Of(text) }] };

  /// <summary>A prompt from messages.</summary>
  /// <param name="description">The prompt description, or null.</param>
  /// <param name="messages">The messages.</param>
  public static GetPromptResult Prompt(string? description, IEnumerable<PromptMessage> messages) =>
    new() { Description = description, Messages = [.. messages] };
}
