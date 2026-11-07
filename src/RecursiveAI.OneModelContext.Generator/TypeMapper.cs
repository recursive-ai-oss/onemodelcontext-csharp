using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RecursiveAI.OneModelContext.Binding;

namespace RecursiveAI.OneModelContext.Generator;

/// <summary>A diagnostic raised while mapping a signature; the method is skipped and the diagnostic reported.</summary>
internal sealed class MappingException : Exception
{
  public MappingException(string id, string message, Location? location = null)
    : base(message)
  {
    Id = id;
    Location = location;
  }

  public string Id { get; }

  public Location? Location { get; }
}

/// <summary>A <c>JsonSerializerContext</c> in the compilation and the types it lists.</summary>
internal sealed class SerializerContextInfo
{
  public SerializerContextInfo(string fullName, HashSet<string> types, NamingPolicy policy)
  {
    FullName = fullName;
    Types = types;
    Policy = policy;
  }

  public string FullName { get; }

  public HashSet<string> Types { get; }

  public NamingPolicy Policy { get; }

  public bool Equals(SerializerContextInfo other) => FullName == other.FullName && Policy == other.Policy && Types.SetEquals(other.Types);
}

internal enum NamingPolicy
{
  None,
  CamelCase,
  Unsupported,
}

/// <summary>What a type is, as far as the signature table cares.</summary>
internal enum TypeShape
{
  Primitive,
  Enum,
  JsonAny,
  Sequence,
  Dictionary,
  Object,
}

/// <summary>Maps C# types to JSON Schema fragments by the shared signature table, and finds the serializer metadata that reads them.</summary>
internal sealed class TypeMapper
{
  internal static readonly SymbolDisplayFormat KeyFormat = SymbolDisplayFormat.FullyQualifiedFormat
    .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.ExpandNullable);

  private readonly IReadOnlyList<SerializerContextInfo> _contexts;

  public TypeMapper(IReadOnlyList<SerializerContextInfo> contexts)
  {
    _contexts = contexts;
  }

  /// <summary>The metadata source for reading or writing <paramref name="type"/>.</summary>
  /// <param name="type">The type, possibly nullable.</param>
  /// <param name="location">Where to report a failure.</param>
  /// <returns>The context class's full name (<c>global::...</c>), or null when the library's built-in metadata covers it, plus the naming policy.</returns>
  public (string? Context, NamingPolicy Policy) FindMetadata(ITypeSymbol type, Location? location)
  {
    var key = KeyOf(type);
    foreach (var context in _contexts)
    {
      if (context.Types.Contains(key))
      {
        if (context.Policy == NamingPolicy.Unsupported)
        {
          throw new MappingException("OMCG0011", "The serializer context " + context.FullName + " uses a property naming policy other than CamelCase; tool schemas support CamelCase or none.", location);
        }

        return (context.FullName, context.Policy);
      }
    }

    if (IsBuiltin(type))
    {
      return (null, NamingPolicy.CamelCase);
    }

    throw new MappingException(
      "OMCG0005",
      "No JsonSerializerContext lists " + type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + ". Add [JsonSerializable(typeof(" + type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + "))] to a source-generated context so the tool can read and write it without reflection.",
      location);
  }

  public static string KeyOf(ITypeSymbol type) => type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString(KeyFormat);

  public static bool IsNullable(ITypeSymbol type) =>
    type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } || (!type.IsValueType && type.NullableAnnotation == NullableAnnotation.Annotated);

  public static ITypeSymbol Unwrap(ITypeSymbol type) =>
    type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable ? nullable.TypeArguments[0] : type;

  public static TypeShape Shape(ITypeSymbol type)
  {
    type = Unwrap(type);
    if (type.TypeKind == TypeKind.Enum)
    {
      return TypeShape.Enum;
    }

    if (IsPrimitive(type))
    {
      return TypeShape.Primitive;
    }

    var name = FullName(type);
    if (name is "System.Text.Json.JsonElement" or "System.Text.Json.Nodes.JsonNode" or "System.Text.Json.Nodes.JsonObject" or "System.Text.Json.Nodes.JsonArray")
    {
      return TypeShape.JsonAny;
    }

    if (TryDictionaryValue(type, out _))
    {
      return TypeShape.Dictionary;
    }

    if (TrySequenceElement(type, out _, out _))
    {
      return TypeShape.Sequence;
    }

    return TypeShape.Object;
  }

  /// <summary>The "expected" phrase for an argument error message.</summary>
  public static string Expected(ITypeSymbol type)
  {
    type = Unwrap(type);
    switch (Shape(type))
    {
      case TypeShape.Sequence:
        return "an array";
      case TypeShape.Dictionary:
      case TypeShape.Object:
        return "an object";
      case TypeShape.JsonAny:
        return "a JSON value";
    }

    switch (type.SpecialType)
    {
      case SpecialType.System_Boolean:
        return "a boolean";
      case SpecialType.System_String:
        return "a string";
      case SpecialType.System_Char:
        return "a single character";
      case SpecialType.System_Single:
      case SpecialType.System_Double:
      case SpecialType.System_Decimal:
        return "a number";
      case SpecialType.System_DateTime:
        return "an RFC 3339 date-time string";
    }

    return FullName(type) switch
    {
      "System.Guid" => "a UUID string",
      "System.DateTimeOffset" => "an RFC 3339 date-time string",
      "System.DateOnly" => "a date string (yyyy-MM-dd)",
      "System.TimeOnly" => "a time string (HH:mm:ss)",
      "System.TimeSpan" => "a duration string ([-][d.]hh:mm:ss)",
      "System.Uri" => "an absolute URI string",
      _ => type.SpecialType is >= SpecialType.System_SByte and <= SpecialType.System_UInt64 ? "an integer" : "a base64 string",
    };
  }

  /// <summary>Builds the schema fragment for a type.</summary>
  public SchemaNode Map(ITypeSymbol type, bool camel, HashSet<ITypeSymbol> path, Location? location)
  {
    var nullable = IsNullable(type);
    type = Unwrap(type);
    var node = Build(type, camel, path, location);
    if (nullable)
    {
      node = node.Clone();
      node.MakeNullable();
    }

    return node;
  }

  private SchemaNode Build(ITypeSymbol type, bool camel, HashSet<ITypeSymbol> path, Location? location)
  {
    static SchemaNode Simple(string json, string? format = null)
    {
      var n = new SchemaNode { Format = format };
      n.Types.Add(json);
      return n;
    }

    static SchemaNode Integer(string min, string max)
    {
      var n = Simple("integer");
      n.Minimum = min;
      n.Maximum = max;
      return n;
    }

    switch (type.SpecialType)
    {
      case SpecialType.System_Boolean:
        return Simple("boolean");
      case SpecialType.System_SByte:
        return Integer("-128", "127");
      case SpecialType.System_Byte:
        return Integer("0", "255");
      case SpecialType.System_Int16:
        return Integer("-32768", "32767");
      case SpecialType.System_UInt16:
        return Integer("0", "65535");
      case SpecialType.System_Int32:
        return Integer("-2147483648", "2147483647");
      case SpecialType.System_UInt32:
        return Integer("0", "4294967295");
      case SpecialType.System_Int64:
        return Integer("-9223372036854775808", "9223372036854775807");
      case SpecialType.System_UInt64:
        return Integer("0", "18446744073709551615");
      case SpecialType.System_Single:
      case SpecialType.System_Double:
      case SpecialType.System_Decimal:
        return Simple("number");
      case SpecialType.System_String:
        return Simple("string");
      case SpecialType.System_Char:
        {
          var n = Simple("string");
          n.MinLength = 1;
          n.MaxLength = 1;
          return n;
        }

      case SpecialType.System_DateTime:
        return Simple("string", "date-time");
    }

    if (type.TypeKind == TypeKind.Enum)
    {
      var n = Simple("string");
      n.EnumNames = type.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).Select(f => f.Name).ToList();
      return n;
    }

    var name = FullName(type);
    switch (name)
    {
      case "System.Guid":
        return Simple("string", "uuid");
      case "System.DateTimeOffset":
        return Simple("string", "date-time");
      case "System.DateOnly":
        return Simple("string", "date");
      case "System.TimeOnly":
        return Simple("string", "time");
      case "System.TimeSpan":
        {
          var n = Simple("string");
          n.Pattern = @"^-?(\d+\.)?\d{2}:\d{2}:\d{2}(\.\d{1,7})?$";
          return n;
        }

      case "System.Uri":
        return Simple("string", "uri");
      case "System.Text.Json.JsonElement":
      case "System.Text.Json.Nodes.JsonNode":
        return new SchemaNode();
      case "System.Text.Json.Nodes.JsonObject":
        return Simple("object");
      case "System.Text.Json.Nodes.JsonArray":
        return Simple("array");
    }

    if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte, Rank: 1 })
    {
      var n = Simple("string");
      n.ContentEncoding = "base64";
      return n;
    }

    if (TryDictionaryValue(type, out var valueType))
    {
      var n = Simple("object");
      n.AdditionalProperties = Map(valueType!, camel, path, location);
      return n;
    }

    if (TrySequenceElement(type, out var element, out var unique))
    {
      var n = Simple("array");
      n.Items = Map(element!, camel, path, location);
      n.UniqueItems = unique;
      return n;
    }

    if (type.TypeKind is TypeKind.Interface or TypeKind.Delegate or TypeKind.Array or TypeKind.Pointer or TypeKind.TypeParameter or TypeKind.FunctionPointer
      || type.SpecialType == SpecialType.System_Object || type.IsAbstract || type is INamedTypeSymbol { IsGenericType: true, IsUnboundGenericType: true })
    {
      throw new MappingException("OMCG0002", "The type " + type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + " has no JSON Schema in the tool signature mapping.", location);
    }

    return BuildObject(type, camel, path, location);
  }

  private SchemaNode BuildObject(ITypeSymbol type, bool camel, HashSet<ITypeSymbol> path, Location? location)
  {
    if (!path.Add(type))
    {
      throw new MappingException("OMCG0006", "The type " + type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + " refers to itself, which the tool signature mapping does not support.", location);
    }

    try
    {
      var obj = new SchemaNode { Properties = new List<KeyValuePair<string, SchemaNode>>(), ClosedObject = true };
      obj.Types.Add("object");
      var chain = new List<INamedTypeSymbol>();
      for (var t = type as INamedTypeSymbol; t is not null && t.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType); t = t.BaseType)
      {
        chain.Insert(0, t);
      }

      var publicConstructors = ((INamedTypeSymbol)type).InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).ToList();
      var constructorRequired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      if (publicConstructors.Count == 1)
      {
        foreach (var parameter in publicConstructors[0].Parameters.Where(p => !p.HasExplicitDefaultValue))
        {
          constructorRequired.Add(parameter.Name);
        }
      }

      foreach (var declaring in chain)
      {
        var recordParameterDocs = DocComment.Read(declaring.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<TypeDeclarationSyntax>().FirstOrDefault());
        foreach (var property in declaring.GetMembers().OfType<IPropertySymbol>())
        {
          if (property.IsStatic || property.IsIndexer || property.GetMethod is not { DeclaredAccessibility: Accessibility.Public } || property.DeclaredAccessibility != Accessibility.Public
            || property.IsImplicitlyDeclared && property.Name == "EqualityContract")
          {
            continue;
          }

          var attributes = property.GetAttributes();
          if (attributes.Any(a => AttributeName(a) == "System.Text.Json.Serialization.JsonIgnoreAttribute" && IsAlwaysIgnore(a)))
          {
            continue;
          }

          var propertyName = attributes.Where(a => AttributeName(a) == "System.Text.Json.Serialization.JsonPropertyNameAttribute")
            .Select(a => a.ConstructorArguments.FirstOrDefault().Value as string).FirstOrDefault(n => n is not null)
            ?? (camel ? CamelCase(property.Name) : property.Name);
          var node = Map(property.Type, camel, path, location);
          var propertySyntax = property.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).FirstOrDefault();
          var description = DescriptionOf(attributes)
            ?? DocComment.Read(propertySyntax is ParameterSyntax ? null : propertySyntax).Summary
            ?? (recordParameterDocs.Parameters.TryGetValue(property.Name, out var recordDoc) && recordDoc.Length > 0 ? recordDoc : null);
          node = Annotate(node, attributes);
          if (description is not null)
          {
            node = node.Clone();
            node.Description = description;
          }

          obj.Properties.Add(new KeyValuePair<string, SchemaNode>(propertyName, node));
          if (property.IsRequired || attributes.Any(a => AttributeName(a) == "System.Text.Json.Serialization.JsonRequiredAttribute") || constructorRequired.Contains(property.Name))
          {
            obj.Required.Add(propertyName);
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

  /// <summary>Applies validation attributes to a fragment.</summary>
  public static SchemaNode Annotate(SchemaNode node, IEnumerable<AttributeData> attributes)
  {
    SchemaNode? copy = null;
    SchemaNode Edit() => copy ??= node.Clone();
    foreach (var attribute in attributes)
    {
      var arguments = attribute.ConstructorArguments;
      switch (AttributeName(attribute))
      {
        case "System.ComponentModel.DataAnnotations.RangeAttribute" when arguments.Length >= 2 && arguments[0].Value is IConvertible low && arguments[1].Value is IConvertible high && arguments[0].Type?.SpecialType != SpecialType.System_Object:
          Edit().Minimum = Number(low);
          Edit().Maximum = Number(high);
          break;
        case "System.ComponentModel.DataAnnotations.StringLengthAttribute" when arguments.Length == 1 && arguments[0].Value is int max:
          Edit().MaxLength = max;
          foreach (var named in attribute.NamedArguments)
          {
            if (named.Key == "MinimumLength" && named.Value.Value is int min && min > 0)
            {
              Edit().MinLength = min;
            }
          }

          break;
        case "System.ComponentModel.DataAnnotations.MinLengthAttribute" when arguments.Length == 1 && arguments[0].Value is int minLength:
          if (node.Types.Contains("array"))
          {
            Edit().MinItems = minLength;
          }
          else
          {
            Edit().MinLength = minLength;
          }

          break;
        case "System.ComponentModel.DataAnnotations.MaxLengthAttribute" when arguments.Length == 1 && arguments[0].Value is int maxLength:
          if (node.Types.Contains("array"))
          {
            Edit().MaxItems = maxLength;
          }
          else
          {
            Edit().MaxLength = maxLength;
          }

          break;
        case "System.ComponentModel.DataAnnotations.LengthAttribute" when arguments.Length == 2 && arguments[0].Value is int lo && arguments[1].Value is int hi:
          if (node.Types.Contains("array"))
          {
            Edit().MinItems = lo;
            Edit().MaxItems = hi;
          }
          else
          {
            Edit().MinLength = lo;
            Edit().MaxLength = hi;
          }

          break;
        case "System.ComponentModel.DataAnnotations.RegularExpressionAttribute" when arguments.Length == 1 && arguments[0].Value is string pattern:
          Edit().Pattern = pattern;
          break;
        case "System.ComponentModel.DataAnnotations.EmailAddressAttribute":
          Edit().Format = "email";
          break;
        case "System.ComponentModel.DataAnnotations.UrlAttribute":
          Edit().Format = "uri";
          break;
      }
    }

    return copy ?? node;
  }

  private static string Number(IConvertible value) =>
    value is double d ? d.ToString("R", CultureInfo.InvariantCulture) : value is float f ? f.ToString("R", CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);

  public static string? DescriptionOf(IEnumerable<AttributeData> attributes) =>
    attributes.Where(a => AttributeName(a) == "System.ComponentModel.DescriptionAttribute").Select(a => a.ConstructorArguments.FirstOrDefault().Value as string).FirstOrDefault(d => d is not null);

  public static string AttributeName(AttributeData attribute) => attribute.AttributeClass?.ToDisplayString() ?? string.Empty;

  private static bool IsAlwaysIgnore(AttributeData attribute)
  {
    // JsonIgnoreCondition.Always (1) is the default when no condition is named.
    foreach (var named in attribute.NamedArguments)
    {
      if (named.Key == "Condition")
      {
        return named.Value.Value is int condition && condition == 1;
      }
    }

    return true;
  }

  /// <summary>The camelCase form of a member name, as <c>JsonNamingPolicy.CamelCase</c> writes it.</summary>
  public static string CamelCase(string name)
  {
    if (string.IsNullOrEmpty(name) || !char.IsUpper(name[0]))
    {
      return name;
    }

    var chars = name.ToCharArray();
    for (var i = 0; i < chars.Length; i++)
    {
      if (i == 1 && !char.IsUpper(chars[i]))
      {
        break;
      }

      var hasNext = i + 1 < chars.Length;
      if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
      {
        if (chars[i + 1] == ' ')
        {
          chars[i] = char.ToLowerInvariant(chars[i]);
        }

        break;
      }

      chars[i] = char.ToLowerInvariant(chars[i]);
    }

    return new string(chars);
  }

  private static string FullName(ITypeSymbol type) => type.ContainingNamespace is { IsGlobalNamespace: false } ns && type.ContainingType is null ? ns.ToDisplayString() + "." + type.Name : type.Name;

  private static bool IsPrimitive(ITypeSymbol type)
  {
    if (type.SpecialType is SpecialType.System_Boolean or SpecialType.System_Char or SpecialType.System_String or SpecialType.System_DateTime
      or (>= SpecialType.System_SByte and <= SpecialType.System_Double) or SpecialType.System_Decimal)
    {
      return true;
    }

    if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte, Rank: 1 })
    {
      return true;
    }

    return FullName(type) is "System.Guid" or "System.DateTimeOffset" or "System.DateOnly" or "System.TimeOnly" or "System.TimeSpan" or "System.Uri";
  }

  /// <summary>True when the library's built-in metadata (<c>BindingJson</c>) can read and write the type.</summary>
  private static bool IsBuiltin(ITypeSymbol type)
  {
    var plain = Unwrap(type);
    if (IsPrimitive(plain) || Shape(plain) == TypeShape.JsonAny)
    {
      return true;
    }

    if (TypeMapper.IsNullable(type) && type.IsReferenceType)
    {
      return IsBuiltin(type.WithNullableAnnotation(NullableAnnotation.None));
    }

    static bool CommonElement(ITypeSymbol element) => element.SpecialType is SpecialType.System_String or SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Double or SpecialType.System_Boolean;
    if (type is IArrayTypeSymbol { Rank: 1 } array)
    {
      return CommonElement(array.ElementType);
    }

    if (type is INamedTypeSymbol { IsGenericType: true } named)
    {
      var definition = named.ConstructedFrom.ToDisplayString();
      var args = named.TypeArguments;
      return (definition, args[0].SpecialType) switch
      {
        ("System.Collections.Generic.List<T>", SpecialType.System_String or SpecialType.System_Int32 or SpecialType.System_Double) => true,
        ("System.Collections.Generic.IReadOnlyList<T>" or "System.Collections.Generic.IEnumerable<T>", SpecialType.System_String) => true,
        ("System.Collections.Generic.Dictionary<TKey, TValue>" or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>", SpecialType.System_String) => args[1].SpecialType == SpecialType.System_String,
        _ => false,
      };
    }

    return false;
  }

  private static bool TryDictionaryValue(ITypeSymbol type, out ITypeSymbol? valueType)
  {
    valueType = null;
    if (type is INamedTypeSymbol { IsGenericType: true } named && named.TypeArguments.Length == 2 && named.TypeArguments[0].SpecialType == SpecialType.System_String)
    {
      var definition = named.ConstructedFrom.ToDisplayString();
      if (definition is "System.Collections.Generic.Dictionary<TKey, TValue>" or "System.Collections.Generic.IDictionary<TKey, TValue>" or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>")
      {
        valueType = named.TypeArguments[1];
        return true;
      }
    }

    return false;
  }

  private static bool TrySequenceElement(ITypeSymbol type, out ITypeSymbol? element, out bool unique)
  {
    element = null;
    unique = false;
    if (type is IArrayTypeSymbol array)
    {
      if (array.Rank != 1 || array.ElementType.SpecialType == SpecialType.System_Byte)
      {
        return false;
      }

      element = array.ElementType;
      return true;
    }

    if (type is INamedTypeSymbol { IsGenericType: true } named && named.TypeArguments.Length == 1)
    {
      switch (named.ConstructedFrom.ToDisplayString())
      {
        case "System.Collections.Generic.List<T>":
        case "System.Collections.Generic.IList<T>":
        case "System.Collections.Generic.ICollection<T>":
        case "System.Collections.Generic.IEnumerable<T>":
        case "System.Collections.Generic.IReadOnlyList<T>":
        case "System.Collections.Generic.IReadOnlyCollection<T>":
          element = named.TypeArguments[0];
          return true;
        case "System.Collections.Generic.HashSet<T>":
        case "System.Collections.Generic.ISet<T>":
        case "System.Collections.Generic.IReadOnlySet<T>":
          element = named.TypeArguments[0];
          unique = true;
          return true;
      }
    }

    return false;
  }
}
