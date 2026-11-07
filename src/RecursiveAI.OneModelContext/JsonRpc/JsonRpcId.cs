using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecursiveAI.OneModelContext.JsonRpc;

/// <summary>
/// A JSON-RPC 2.0 request id: a string or a number, kept exactly as the peer wrote it.
/// </summary>
/// <remarks>
/// JSON-RPC 2.0 §4 lets an id be a string, a number or null; MCP forbids null. The text of a number is kept verbatim, so
/// <c>1</c> and <c>1.0</c> are different ids and an echoed id is byte-for-byte the one received.
/// The default value is "no id" and is not a valid id.
/// </remarks>
[JsonConverter(typeof(JsonRpcIdConverter))]
public readonly struct JsonRpcId : IEquatable<JsonRpcId>
{
  private readonly string? _text;
  private readonly bool _isNumber;

  private JsonRpcId(string text, bool isNumber)
  {
    _text = text;
    _isNumber = isNumber;
  }

  /// <summary>True when this holds an id; false for <c>default</c>.</summary>
  public bool HasValue => _text is not null;

  /// <summary>True when the id is a JSON number.</summary>
  public bool IsNumber => _isNumber;

  /// <summary>The string value, or the verbatim text of the number. Empty for <c>default</c>.</summary>
  public string Text => _text ?? string.Empty;

  /// <summary>Creates a string id.</summary>
  /// <param name="value">The id text.</param>
  /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
  public static JsonRpcId FromString(string value)
  {
    ArgumentNullException.ThrowIfNull(value);
    return new JsonRpcId(value, false);
  }

  /// <summary>Creates a number id.</summary>
  /// <param name="value">The number.</param>
  public static JsonRpcId FromNumber(long value) => new(value.ToString(CultureInfo.InvariantCulture), true);

  /// <summary>Reads an id from a JSON element.</summary>
  /// <param name="element">A string or number element.</param>
  /// <param name="id">The id, when the element is a string or a number.</param>
  /// <returns>False for any other kind of element (null, boolean, object, array).</returns>
  public static bool TryFromElement(JsonElement element, out JsonRpcId id)
  {
    switch (element.ValueKind)
    {
      case JsonValueKind.String:
        id = new JsonRpcId(element.GetString()!, false);
        return true;
      case JsonValueKind.Number:
        id = new JsonRpcId(element.GetRawText(), true);
        return true;
      default:
        id = default;
        return false;
    }
  }

  /// <summary>Writes the id as a JSON string or number.</summary>
  /// <param name="writer">The writer.</param>
  public void WriteTo(Utf8JsonWriter writer)
  {
    ArgumentNullException.ThrowIfNull(writer);
    if (_text is null)
    {
      writer.WriteNullValue();
    }
    else if (_isNumber)
    {
      writer.WriteRawValue(_text);
    }
    else
    {
      writer.WriteStringValue(_text);
    }
  }

  /// <inheritdoc />
  public bool Equals(JsonRpcId other) => _isNumber == other._isNumber && string.Equals(_text, other._text, StringComparison.Ordinal);

  /// <inheritdoc />
  public override bool Equals(object? obj) => obj is JsonRpcId other && Equals(other);

  /// <inheritdoc />
  public override int GetHashCode() => HashCode.Combine(_isNumber, _text);

  /// <inheritdoc />
  public override string ToString() => _text ?? string.Empty;

  /// <summary>Equality.</summary>
  public static bool operator ==(JsonRpcId left, JsonRpcId right) => left.Equals(right);

  /// <summary>Inequality.</summary>
  public static bool operator !=(JsonRpcId left, JsonRpcId right) => !left.Equals(right);
}

/// <summary>Reads and writes a <see cref="JsonRpcId"/> as a JSON string or number.</summary>
public sealed class JsonRpcIdConverter : JsonConverter<JsonRpcId>
{
  /// <inheritdoc />
  public override JsonRpcId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
  {
    using var doc = JsonDocument.ParseValue(ref reader);
    if (!JsonRpcId.TryFromElement(doc.RootElement, out var id))
    {
      throw new JsonException("A request id or progress token must be a string or a number.");
    }

    return id;
  }

  /// <inheritdoc />
  public override void Write(Utf8JsonWriter writer, JsonRpcId value, JsonSerializerOptions options) => value.WriteTo(writer);
}
