// This file is compiled into the library and into the source generator, so a schema written at build time and one written by
// the reflection path come out byte for byte the same. Keep it free of anything newer than netstandard2.0.
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RecursiveAI.OneModelContext.Binding;

/// <summary>
/// One JSON Schema 2020-12 fragment in the shape the tool-signature mapping uses, and the writer that prints it. Only the keywords the
/// mapping uses exist, and they are always written in one fixed order (models read schemas from the top).
/// </summary>
internal sealed class SchemaNode
{
  /// <summary>The <c>type</c> keywords; empty for "any value".</summary>
  public List<string> Types { get; private set; } = new List<string>();

  /// <summary>The <c>format</c>.</summary>
  public string? Format { get; set; }

  /// <summary>The <c>description</c>.</summary>
  public string? Description { get; set; }

  /// <summary>The <c>enum</c> member names, or null.</summary>
  public List<string>? EnumNames { get; set; }

  /// <summary>True when <c>null</c> is appended to the <c>enum</c> list.</summary>
  public bool EnumAllowsNull { get; set; }

  /// <summary>Printed JSON number for <c>minimum</c>.</summary>
  public string? Minimum { get; set; }

  /// <summary>Printed JSON number for <c>maximum</c>.</summary>
  public string? Maximum { get; set; }

  /// <summary>The <c>minLength</c>.</summary>
  public int? MinLength { get; set; }

  /// <summary>The <c>maxLength</c>.</summary>
  public int? MaxLength { get; set; }

  /// <summary>The <c>pattern</c>.</summary>
  public string? Pattern { get; set; }

  /// <summary>The <c>contentEncoding</c>.</summary>
  public string? ContentEncoding { get; set; }

  /// <summary>The <c>items</c> schema.</summary>
  public SchemaNode? Items { get; set; }

  /// <summary>The <c>minItems</c>.</summary>
  public int? MinItems { get; set; }

  /// <summary>The <c>maxItems</c>.</summary>
  public int? MaxItems { get; set; }

  /// <summary>True for <c>uniqueItems</c>.</summary>
  public bool UniqueItems { get; set; }

  /// <summary>The <c>properties</c> in order; null when the keyword is absent.</summary>
  public List<KeyValuePair<string, SchemaNode>>? Properties { get; set; }

  /// <summary>The <c>required</c> names; the keyword is omitted when empty.</summary>
  public List<string> Required { get; private set; } = new List<string>();

  /// <summary><c>additionalProperties: false</c>.</summary>
  public bool ClosedObject { get; set; }

  /// <summary>The <c>additionalProperties</c> schema of a dictionary.</summary>
  public SchemaNode? AdditionalProperties { get; set; }

  /// <summary>The printed JSON literal of <c>default</c>.</summary>
  public string? Default { get; set; }

  /// <summary>Adds <c>null</c> to the type (and to an enum list).</summary>
  public void MakeNullable()
  {
    if (Types.Count == 0)
    {
      return; // Any value already accepts null.
    }

    if (!Types.Contains("null"))
    {
      Types.Add("null");
    }

    if (EnumNames != null)
    {
      EnumAllowsNull = true;
    }
  }

  /// <summary>A shallow copy with its own type and required lists, so a caller can add a description or nullability.</summary>
  public SchemaNode Clone()
  {
    var copy = (SchemaNode)MemberwiseClone();
    copy.Types = new List<string>(Types);
    copy.Required = new List<string>(Required);
    return copy;
  }

  /// <summary>Prints the schema as compact JSON.</summary>
  public string ToJson()
  {
    var sb = new StringBuilder();
    Write(sb);
    return sb.ToString();
  }

  /// <summary>Appends the schema as compact JSON.</summary>
  public void Write(StringBuilder sb)
  {
    sb.Append('{');
    var first = true;
    void Key(string name)
    {
      if (!first)
      {
        sb.Append(',');
      }

      first = false;
      sb.Append('"').Append(name).Append("\":");
    }

    var types = Types;
    if (types.Count == 1)
    {
      Key("type");
      Quote(sb, types[0]);
    }
    else if (types.Count > 1)
    {
      Key("type");
      sb.Append('[');
      for (var i = 0; i < types.Count; i++)
      {
        if (i > 0)
        {
          sb.Append(',');
        }

        Quote(sb, types[i]);
      }

      sb.Append(']');
    }

    if (Format != null)
    {
      Key("format");
      Quote(sb, Format);
    }

    if (Description != null)
    {
      Key("description");
      Quote(sb, Description);
    }

    if (EnumNames != null)
    {
      Key("enum");
      sb.Append('[');
      for (var i = 0; i < EnumNames.Count; i++)
      {
        if (i > 0)
        {
          sb.Append(',');
        }

        Quote(sb, EnumNames[i]);
      }

      if (EnumAllowsNull)
      {
        sb.Append(",null");
      }

      sb.Append(']');
    }

    if (Minimum != null)
    {
      Key("minimum");
      sb.Append(Minimum);
    }

    if (Maximum != null)
    {
      Key("maximum");
      sb.Append(Maximum);
    }

    if (MinLength != null)
    {
      Key("minLength");
      sb.Append(MinLength.Value.ToString(CultureInfo.InvariantCulture));
    }

    if (MaxLength != null)
    {
      Key("maxLength");
      sb.Append(MaxLength.Value.ToString(CultureInfo.InvariantCulture));
    }

    if (Pattern != null)
    {
      Key("pattern");
      Quote(sb, Pattern);
    }

    if (ContentEncoding != null)
    {
      Key("contentEncoding");
      Quote(sb, ContentEncoding);
    }

    if (Items != null)
    {
      Key("items");
      Items.Write(sb);
    }

    if (MinItems != null)
    {
      Key("minItems");
      sb.Append(MinItems.Value.ToString(CultureInfo.InvariantCulture));
    }

    if (MaxItems != null)
    {
      Key("maxItems");
      sb.Append(MaxItems.Value.ToString(CultureInfo.InvariantCulture));
    }

    if (UniqueItems)
    {
      Key("uniqueItems");
      sb.Append("true");
    }

    if (Properties != null)
    {
      Key("properties");
      sb.Append('{');
      for (var i = 0; i < Properties.Count; i++)
      {
        if (i > 0)
        {
          sb.Append(',');
        }

        Quote(sb, Properties[i].Key);
        sb.Append(':');
        Properties[i].Value.Write(sb);
      }

      sb.Append('}');
    }

    if (Required.Count > 0)
    {
      Key("required");
      sb.Append('[');
      for (var i = 0; i < Required.Count; i++)
      {
        if (i > 0)
        {
          sb.Append(',');
        }

        Quote(sb, Required[i]);
      }

      sb.Append(']');
    }

    if (ClosedObject)
    {
      Key("additionalProperties");
      sb.Append("false");
    }
    else if (AdditionalProperties != null)
    {
      Key("additionalProperties");
      AdditionalProperties.Write(sb);
    }

    if (Default != null)
    {
      Key("default");
      sb.Append(Default);
    }

    sb.Append('}');
  }

  /// <summary>Appends <paramref name="value"/> as a JSON string literal.</summary>
  public static void Quote(StringBuilder sb, string value)
  {
    sb.Append('"');
    foreach (var c in value)
    {
      switch (c)
      {
        case '"':
          sb.Append("\\\"");
          break;
        case '\\':
          sb.Append("\\\\");
          break;
        case '\n':
          sb.Append("\\n");
          break;
        case '\r':
          sb.Append("\\r");
          break;
        case '\t':
          sb.Append("\\t");
          break;
        default:
          if (c < 0x20)
          {
            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
          }
          else
          {
            sb.Append(c);
          }

          break;
      }
    }

    sb.Append('"');
  }

  /// <summary>A JSON string literal for <paramref name="value"/>.</summary>
  public static string QuoteToString(string value)
  {
    var sb = new StringBuilder();
    Quote(sb, value);
    return sb.ToString();
  }
}
