using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RecursiveAI.OneModelContext.Binding;

/// <summary>
/// Builds the input and output schemas of the reflection quick path by the same signature table the source generator follows, so a tool
/// registered either way lists the same schema. It cannot see doc comments: only <see cref="DescriptionAttribute"/> counts.
/// </summary>
[RequiresUnreferencedCode("Reads types and attributes by reflection. Use the [Tool] source generator for trimmed or NativeAOT apps.")]
[RequiresDynamicCode("Reads types and attributes by reflection. Use the [Tool] source generator for trimmed or NativeAOT apps.")]
internal static class ReflectionSchema
{
  /// <summary>The options the quick path reads arguments with: camelCase members, enum members by name.</summary>
  internal static JsonSerializerOptions Options { get; } = CreateOptions();

  private static JsonSerializerOptions CreateOptions()
  {
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };
    options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
    return options;
  }

  /// <summary>True for the parameters the runtime fills in.</summary>
  internal static bool IsInjected(Type type) => type == typeof(RequestContext) || type == typeof(System.Threading.CancellationToken);

  /// <summary>The schema of the argument object for the given parameters.</summary>
  internal static SchemaNode ForParameters(IEnumerable<ParameterInfo> parameters)
  {
    var root = new SchemaNode { Properties = [], ClosedObject = true };
    root.Types.Add("object");
    var context = new NullabilityInfoContext();
    foreach (var parameter in parameters)
    {
      if (IsInjected(parameter.ParameterType))
      {
        continue;
      }

      var nullable = IsNullable(parameter.ParameterType, context.Create(parameter));
      var node = ForType(parameter.ParameterType, nullable, [], parameter.GetCustomAttributes());
      var description = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description;
      if (description is not null)
      {
        node = node.Clone();
        node.Description = description;
      }

      if (parameter.HasDefaultValue)
      {
        node = node.Clone();
        node.Default = DefaultLiteral(parameter.DefaultValue, parameter.ParameterType);
      }
      else
      {
        root.Required.Add(parameter.Name!);
      }

      root.Properties.Add(new KeyValuePair<string, SchemaNode>(parameter.Name!, node));
    }

    return root;
  }

  /// <summary>True when the type is a value or reference type that serializes as an object (a structured result).</summary>
  internal static bool IsObjectShaped(Type type)
  {
    type = Nullable.GetUnderlyingType(type) ?? type;
    return TryDictionary(type, out _) || (!IsScalar(type) && !IsSequence(type, out _) && type != typeof(object) && type != typeof(JsonElement) && !typeof(JsonNode).IsAssignableFrom(type) && !typeof(Delegate).IsAssignableFrom(type));
  }

  private static bool IsNullable(Type type, NullabilityInfo info) =>
    Nullable.GetUnderlyingType(type) is not null || (!type.IsValueType && info.ReadState == NullabilityState.Nullable);

  internal static SchemaNode ForType(Type type, bool nullable, HashSet<Type> path, IEnumerable<Attribute>? annotations = null)
  {
    var underlying = Nullable.GetUnderlyingType(type);
    if (underlying is not null)
    {
      type = underlying;
      nullable = true;
    }

    var node = Build(type, path);
    if (annotations is not null)
    {
      node = Annotate(node, annotations);
    }

    if (nullable)
    {
      node = node.Clone();
      node.MakeNullable();
    }

    return node;
  }

  private static SchemaNode Build(Type type, HashSet<Type> path)
  {
    SchemaNode Simple(string json, string? format = null, Action<SchemaNode>? tweak = null)
    {
      var n = new SchemaNode { Format = format };
      n.Types.Add(json);
      tweak?.Invoke(n);
      return n;
    }

    SchemaNode Integer(string min, string max) => Simple("integer", null, n =>
    {
      n.Minimum = min;
      n.Maximum = max;
    });

    if (type == typeof(bool))
    {
      return Simple("boolean");
    }

    if (type == typeof(sbyte))
    {
      return Integer("-128", "127");
    }

    if (type == typeof(byte))
    {
      return Integer("0", "255");
    }

    if (type == typeof(short))
    {
      return Integer("-32768", "32767");
    }

    if (type == typeof(ushort))
    {
      return Integer("0", "65535");
    }

    if (type == typeof(int))
    {
      return Integer("-2147483648", "2147483647");
    }

    if (type == typeof(uint))
    {
      return Integer("0", "4294967295");
    }

    if (type == typeof(long))
    {
      return Integer("-9223372036854775808", "9223372036854775807");
    }

    if (type == typeof(ulong))
    {
      return Integer("0", "18446744073709551615");
    }

    if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
    {
      return Simple("number");
    }

    if (type == typeof(string))
    {
      return Simple("string");
    }

    if (type == typeof(char))
    {
      return Simple("string", null, n =>
      {
        n.MinLength = 1;
        n.MaxLength = 1;
      });
    }

    if (type == typeof(Guid))
    {
      return Simple("string", "uuid");
    }

    if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
    {
      return Simple("string", "date-time");
    }

    if (type == typeof(DateOnly))
    {
      return Simple("string", "date");
    }

    if (type == typeof(TimeOnly))
    {
      return Simple("string", "time");
    }

    if (type == typeof(TimeSpan))
    {
      return Simple("string", null, n => n.Pattern = @"^-?(\d+\.)?\d{2}:\d{2}:\d{2}(\.\d{1,7})?$");
    }

    if (type == typeof(Uri))
    {
      return Simple("string", "uri");
    }

    if (type == typeof(byte[]))
    {
      return Simple("string", null, n => n.ContentEncoding = "base64");
    }

    if (type.IsEnum)
    {
      return Simple("string", null, n => n.EnumNames = [.. Enum.GetNames(type)]);
    }

    if (type == typeof(JsonElement) || type == typeof(JsonNode))
    {
      return new SchemaNode();
    }

    if (type == typeof(JsonObject))
    {
      return Simple("object");
    }

    if (type == typeof(JsonArray))
    {
      return Simple("array");
    }

    if (TryDictionary(type, out var valueType))
    {
      var dictionary = Simple("object");
      dictionary.AdditionalProperties = ForType(valueType!, false, path);
      return dictionary;
    }

    if (IsSequence(type, out var elementType))
    {
      var array = Simple("array");
      array.Items = ForType(elementType!, false, path);
      array.UniqueItems = type.IsGenericType && type.GetGenericTypeDefinition() is var d && (d == typeof(HashSet<>) || d == typeof(ISet<>) || d == typeof(IReadOnlySet<>));
      return array;
    }

    if (type.IsInterface || type.IsAbstract || type == typeof(object) || typeof(Delegate).IsAssignableFrom(type) || type.IsGenericTypeDefinition || type.IsArray || type.IsPointer || type.IsByRef)
    {
      throw new NotSupportedException("The type " + type + " has no JSON Schema in the tool signature mapping.");
    }

    return BuildObject(type, path);
  }

  private static SchemaNode BuildObject(Type type, HashSet<Type> path)
  {
    if (!path.Add(type))
    {
      throw new NotSupportedException("The type " + type + " refers to itself, which the tool signature mapping does not support.");
    }

    try
    {
      var obj = new SchemaNode { Properties = [], ClosedObject = true };
      obj.Types.Add("object");
      var chain = new List<Type>();
      for (var t = type; t is not null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
      {
        chain.Insert(0, t);
      }

      var constructors = type.GetConstructors();
      var parametersWithoutDefault = constructors.Length == 1
        ? new HashSet<string>(constructors[0].GetParameters().Where(p => !p.HasDefaultValue).Select(p => p.Name!), StringComparer.OrdinalIgnoreCase)
        : [];
      var context = new NullabilityInfoContext();
      foreach (var declaring in chain)
      {
        foreach (var property in declaring.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
          if (property.GetMethod is null || !property.GetMethod.IsPublic || property.GetIndexParameters().Length > 0 || property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always })
          {
            continue;
          }

          var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);
          var node = ForType(property.PropertyType, IsNullable(property.PropertyType, context.Create(property)), path, property.GetCustomAttributes());
          var description = property.GetCustomAttribute<DescriptionAttribute>()?.Description;
          if (description is not null)
          {
            node = node.Clone();
            node.Description = description;
          }

          obj.Properties!.Add(new KeyValuePair<string, SchemaNode>(name, node));
          if (property.GetCustomAttribute<RequiredMemberAttribute>() is not null
            || property.GetCustomAttribute<JsonRequiredAttribute>() is not null
            || parametersWithoutDefault.Contains(property.Name))
          {
            obj.Required.Add(name);
          }
        }
      }

      return obj;
    }
    finally
    {
      path.Remove(type);
    }
  }

  private static SchemaNode Annotate(SchemaNode node, IEnumerable<Attribute> attributes)
  {
    SchemaNode? copy = null;
    SchemaNode Edit() => copy ??= node.Clone();
    foreach (var attribute in attributes)
    {
      switch (attribute)
      {
        case RangeAttribute range when range.Minimum is IConvertible && range.Maximum is IConvertible:
          Edit().Minimum = Convert.ToString(range.Minimum, CultureInfo.InvariantCulture);
          Edit().Maximum = Convert.ToString(range.Maximum, CultureInfo.InvariantCulture);
          break;
        case StringLengthAttribute length:
          Edit().MaxLength = length.MaximumLength;
          if (length.MinimumLength > 0)
          {
            Edit().MinLength = length.MinimumLength;
          }

          break;
        case MinLengthAttribute min:
          if (node.Types.Contains("array"))
          {
            Edit().MinItems = min.Length;
          }
          else
          {
            Edit().MinLength = min.Length;
          }

          break;
        case MaxLengthAttribute max:
          if (node.Types.Contains("array"))
          {
            Edit().MaxItems = max.Length;
          }
          else
          {
            Edit().MaxLength = max.Length;
          }

          break;
        case LengthAttribute both:
          if (node.Types.Contains("array"))
          {
            Edit().MinItems = both.MinimumLength;
            Edit().MaxItems = both.MaximumLength;
          }
          else
          {
            Edit().MinLength = both.MinimumLength;
            Edit().MaxLength = both.MaximumLength;
          }

          break;
        case RegularExpressionAttribute pattern:
          Edit().Pattern = pattern.Pattern;
          break;
        case EmailAddressAttribute:
          Edit().Format = "email";
          break;
        case UrlAttribute:
          Edit().Format = "uri";
          break;
      }
    }

    return copy ?? node;
  }

  private static string? DefaultLiteral(object? value, Type type)
  {
    type = Nullable.GetUnderlyingType(type) ?? type;
    switch (value)
    {
      case null:
        return null;
      case string s:
        return SchemaNode.QuoteToString(s);
      case char c:
        return SchemaNode.QuoteToString(c.ToString());
      case bool b:
        return b ? "true" : "false";
      case Enum e:
        return SchemaNode.QuoteToString(e.ToString());
      case IFormattable f when type.IsPrimitive || type == typeof(decimal):
        return f.ToString(type == typeof(float) || type == typeof(double) ? "R" : null, CultureInfo.InvariantCulture);
      default:
        return null;
    }
  }

  private static bool IsScalar(Type type) =>
    type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset)
    || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(TimeSpan) || type == typeof(Uri) || type == typeof(byte[]);

  private static bool TryDictionary(Type type, out Type? valueType)
  {
    valueType = null;
    if (!type.IsGenericType)
    {
      return false;
    }

    var definition = type.GetGenericTypeDefinition();
    if ((definition == typeof(Dictionary<,>) || definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>)) && type.GetGenericArguments()[0] == typeof(string))
    {
      valueType = type.GetGenericArguments()[1];
      return true;
    }

    return false;
  }

  private static bool IsSequence(Type type, out Type? elementType)
  {
    elementType = null;
    if (type == typeof(string) || type == typeof(byte[]))
    {
      return false;
    }

    if (type.IsArray)
    {
      if (type.GetArrayRank() != 1)
      {
        return false;
      }

      elementType = type.GetElementType();
      return true;
    }

    if (type.IsGenericType)
    {
      var definition = type.GetGenericTypeDefinition();
      if (definition == typeof(List<>) || definition == typeof(IList<>) || definition == typeof(ICollection<>) || definition == typeof(IEnumerable<>)
        || definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyCollection<>) || definition == typeof(HashSet<>) || definition == typeof(ISet<>) || definition == typeof(IReadOnlySet<>))
      {
        elementType = type.GetGenericArguments()[0];
        return true;
      }
    }

    return false;
  }

  /// <summary>True for a type the runtime can wrap as a sequence of content blocks.</summary>
  internal static bool IsContentSequence(Type type) => type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type) && type.GetInterfaces().Append(type).Any(i => i == typeof(IEnumerable<Protocol.ContentBlock>));
}
