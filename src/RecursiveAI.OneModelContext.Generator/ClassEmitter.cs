using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RecursiveAI.OneModelContext.Binding;

namespace RecursiveAI.OneModelContext.Generator;

/// <summary>Writes the partial class that registers one class's tools, resources and prompts.</summary>
internal sealed class ClassEmitter
{
  private const string Core = "global::RecursiveAI.OneModelContext";
  private const string Json = "global::System.Text.Json";
  private const string TypeInfo = "global::System.Text.Json.Serialization.Metadata.JsonTypeInfo";

  private readonly INamedTypeSymbol _type;
  private readonly ClassDeclarationSyntax _declaration;
  private readonly TypeMapper _mapper;
  private readonly List<Diagnostic> _diagnostics = new();
  private readonly StringBuilder _fields = new();
  private readonly StringBuilder _registrations = new();
  private readonly StringBuilder _methods = new();
  private readonly Dictionary<string, string> _typeInfos = new(StringComparer.Ordinal);
  private readonly HashSet<string> _toolNames = new(StringComparer.Ordinal);
  private readonly HashSet<string> _resourceUris = new(StringComparer.Ordinal);
  private readonly HashSet<string> _promptNames = new(StringComparer.Ordinal);
  private int _counter;

  public ClassEmitter(INamedTypeSymbol type, ClassDeclarationSyntax declaration, TypeMapper mapper)
  {
    _type = type;
    _declaration = declaration;
    _mapper = mapper;
  }

  public GenerationResult Emit(System.Threading.CancellationToken ct)
  {
    var hint = _type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty).Replace('<', '_').Replace('>', '_').Replace(',', '_').Replace(' ', '_') + ".OneModelContext.g.cs";
    if (!CheckContainers())
    {
      return new GenerationResult(hint, null, _diagnostics);
    }

    foreach (var method in _type.GetMembers().OfType<IMethodSymbol>())
    {
      ct.ThrowIfCancellationRequested();
      foreach (var attribute in method.GetAttributes())
      {
        var name = attribute.AttributeClass?.ToDisplayString();
        try
        {
          switch (name)
          {
            case "RecursiveAI.OneModelContext.ToolAttribute":
              EmitTool(method, attribute);
              break;
            case "RecursiveAI.OneModelContext.ResourceAttribute":
              EmitResource(method, attribute);
              break;
            case "RecursiveAI.OneModelContext.PromptAttribute":
              EmitPrompt(method, attribute);
              break;
          }
        }
        catch (MappingException ex)
        {
          _diagnostics.Add(Diagnostic.Create(Diagnostics.ById(ex.Id), ex.Location ?? MethodLocation(method), ex.Message));
        }
      }
    }

    if (_diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
    {
      return new GenerationResult(hint, null, _diagnostics);
    }

    return new GenerationResult(hint, Compose(), _diagnostics);
  }

  private bool CheckContainers()
  {
    var ok = true;
    for (INamedTypeSymbol? t = _type; t is not null; t = t.ContainingType)
    {
      var partial = t.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<TypeDeclarationSyntax>().Any(d => d.Modifiers.Any(SyntaxKind.PartialKeyword));
      if (t.TypeKind != TypeKind.Class || t.IsStatic || !partial)
      {
        var declaration = t.DeclaringSyntaxReferences.First().GetSyntax();
        _diagnostics.Add(Diagnostic.Create(Diagnostics.NotPartial, (declaration as TypeDeclarationSyntax)?.Identifier.GetLocation() ?? declaration.GetLocation(), "The type '" + t.Name + "' declares tools, resources or prompts, so it and every type around it must be a non-static partial class."));
        ok = false;
      }
      else if (t.IsGenericType)
      {
        _diagnostics.Add(Diagnostic.Create(Diagnostics.GenericClass, _declaration.Identifier.GetLocation(), "The type '" + t.Name + "' is generic; tool classes cannot be."));
        ok = false;
      }
    }

    return ok;
  }

  private static Location MethodLocation(IMethodSymbol method) => method.Locations.FirstOrDefault() ?? Location.None;

  // ---- tools ----

  private void EmitTool(IMethodSymbol method, AttributeData attribute)
  {
    var location = MethodLocation(method);
    CheckMethod(method, location);
    var docs = DocComment.Read(MethodSyntax(method));
    var name = NamedString(attribute, "Name") ?? (attribute.ConstructorArguments.Length == 1 ? attribute.ConstructorArguments[0].Value as string : null) ?? SnakeCase(method.Name);
    if (name.Length is < 1 or > 128 || !name.All(c => (c < 128 && char.IsLetterOrDigit(c)) || c is '_' or '-' or '.'))
    {
      throw new MappingException("OMCG0008", "The tool name '" + name + "' must be 1 to 128 letters, digits, '_', '-' or '.'.", location);
    }

    if (!_toolNames.Add(name))
    {
      throw new MappingException("OMCG0007", "Two tools in '" + _type.Name + "' are named '" + name + "'.", location);
    }

    var description = NamedString(attribute, "Description") ?? TypeMapper.DescriptionOf(method.GetAttributes()) ?? docs.Summary;
    var title = NamedString(attribute, "Title");
    var id = _counter++;

    var root = new SchemaNode { Properties = new List<KeyValuePair<string, SchemaNode>>(), ClosedObject = true };
    root.Types.Add("object");
    var known = new List<string>();
    var statements = new StringBuilder();
    var arguments = new List<string>();
    var injected = 0;
    var tokens = 0;
    var contextParameters = 0;
    var index = 0;
    foreach (var parameter in method.Parameters)
    {
      var parameterLocation = parameter.Locations.FirstOrDefault() ?? location;
      if (parameter.RefKind != RefKind.None)
      {
        throw new MappingException("OMCG0002", "The parameter '" + parameter.Name + "' is ref, out or in; tool parameters cannot be.", parameterLocation);
      }

      var typeName = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
      if (typeName == Core + ".RequestContext")
      {
        contextParameters++;
        arguments.Add("context");
        continue;
      }

      if (typeName == "global::System.Threading.CancellationToken")
      {
        tokens++;
        arguments.Add("context.CancellationToken");
        continue;
      }

      injected = contextParameters + tokens;
      var variable = "p" + index++;
      var node = MapParameterSchema(parameter, docs, parameterLocation, out var camel);
      if (parameter.HasExplicitDefaultValue)
      {
        var defaultJson = DefaultJson(parameter);
        if (defaultJson is not null)
        {
          node = node.Clone();
          node.Default = defaultJson;
        }
      }
      else
      {
        root.Required.Add(parameter.Name);
      }

      root.Properties.Add(new KeyValuePair<string, SchemaNode>(parameter.Name, node));
      known.Add(parameter.Name);
      statements.Append("    var ").Append(variable).Append(" = ").Append(BindExpression(parameter, parameterLocation)).AppendLine(";");
      arguments.Add(variable);
    }

    if (contextParameters > 1 || tokens > 1)
    {
      throw new MappingException("OMCG0009", "A tool method takes at most one RequestContext and one CancellationToken.", location);
    }

    _ = injected;
    var knownField = "__omc_known" + id;
    _fields.Append("  private static readonly string[] ").Append(knownField).Append(" = { ").Append(string.Join(", ", known.Select(Lit))).AppendLine(" };");
    var prologue = "    var arguments_ = new " + Core + ".Binding.ToolArguments(arguments, " + knownField + ");\n";
    var body = prologue + statements.ToString();

    var returned = AnalyzeReturn(method, location, out var outputSchema);
    var call = method.Name + "(" + string.Join(", ", arguments) + ")";
    var signature = returned.Await
      ? "  private async global::System.Threading.Tasks.ValueTask<" + Core + ".Protocol.CallToolResult> __omc_Tool" + id + "(" + Json + ".JsonElement arguments, " + Core + ".RequestContext context)"
      : "  private global::System.Threading.Tasks.ValueTask<" + Core + ".Protocol.CallToolResult> __omc_Tool" + id + "(" + Json + ".JsonElement arguments, " + Core + ".RequestContext context)";
    _methods.AppendLine(signature);
    _methods.AppendLine("  {");
    _methods.Append(body);
    if (returned.Void)
    {
      _methods.Append("    ").Append(returned.Await ? "await " : string.Empty).Append(call).Append(returned.Await ? ".ConfigureAwait(false)" : string.Empty).AppendLine(";");
    }
    else
    {
      _methods.Append("    var result = ").Append(returned.Await ? "await " : string.Empty).Append(call).Append(returned.Await ? ".ConfigureAwait(false)" : string.Empty).AppendLine(";");
    }

    var mapped = returned.Map("result");
    _methods.Append("    ").Append(returned.Await ? "return " + mapped : "return new global::System.Threading.Tasks.ValueTask<" + Core + ".Protocol.CallToolResult>(" + mapped + ")").AppendLine(";");
    _methods.AppendLine("  }");
    _methods.AppendLine();

    // Annotations only for the hints the attribute names, so a listing never claims more than the author said.
    var hints = new List<string>();
    foreach (var (property, hint) in new[] { ("ReadOnly", "ReadOnlyHint"), ("Destructive", "DestructiveHint"), ("Idempotent", "IdempotentHint"), ("OpenWorld", "OpenWorldHint") })
    {
      foreach (var named in attribute.NamedArguments)
      {
        if (named.Key == property && named.Value.Value is bool flag)
        {
          hints.Add(hint + " = " + (flag ? "true" : "false"));
        }
      }
    }

    if (title is not null)
    {
      hints.Add("Title = " + Lit(title));
    }

    _registrations.Append("    server.Tools.Add(new ").Append(Core).Append(".ToolDefinition(").Append(Lit(name)).Append(", ").Append(description is null ? "null" : Lit(description))
      .Append(", ").Append(Core).Append(".Binding.ToolResults.Schema(").Append(Lit(root.ToJson())).Append("))");
    var initializers = new List<string>();
    if (title is not null)
    {
      initializers.Add("Title = " + Lit(title));
    }

    if (hints.Count > 0)
    {
      initializers.Add("Annotations = new " + Core + ".Protocol.ToolAnnotations { " + string.Join(", ", hints) + " }");
    }

    if (outputSchema is not null)
    {
      initializers.Add("OutputSchema = " + Core + ".Binding.ToolResults.Schema(" + Lit(outputSchema.ToJson()) + ")");
    }

    if (initializers.Count > 0)
    {
      _registrations.Append(" { ").Append(string.Join(", ", initializers)).Append(" }");
    }

    _registrations.Append(", __omc_Tool").Append(id).AppendLine(");");
  }

  private SchemaNode MapParameterSchema(IParameterSymbol parameter, DocComment docs, Location location, out bool camel)
  {
    var type = parameter.Type;
    camel = true;
    var plain = TypeMapper.Unwrap(type);
    var shape = TypeMapper.Shape(plain);
    // A type with no schema is reported as such before the missing serializer metadata is.
    _ = _mapper.Map(type, true, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), location);
    if (shape != TypeShape.Enum)
    {
      var (_, policy) = _mapper.FindMetadata(type, location);
      camel = policy != NamingPolicy.None;
    }

    SchemaNode node;
    try
    {
      node = _mapper.Map(type, camel, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), location);
    }
    catch (MappingException ex) when (ex.Location is null)
    {
      throw new MappingException(ex.Id, ex.Message, location);
    }

    node = TypeMapper.Annotate(node, parameter.GetAttributes());
    var description = TypeMapper.DescriptionOf(parameter.GetAttributes()) ?? (docs.Parameters.TryGetValue(parameter.Name, out var text) && text.Length > 0 ? text : null);
    if (description is not null)
    {
      node = node.Clone();
      node.Description = description;
    }

    return node;
  }

  private string BindExpression(IParameterSymbol parameter, Location location)
  {
    var type = parameter.Type;
    var plain = TypeMapper.Unwrap(type);
    var nullable = TypeMapper.IsNullable(type);
    var typeText = Display(type);
    string bind;
    if (TypeMapper.Shape(plain) == TypeShape.Enum)
    {
      var members = plain.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).ToList();
      var names = "new[] { " + string.Join(", ", members.Select(m => Lit(m.Name))) + " }";
      var values = "new " + Display(plain) + "[] { " + string.Join(", ", members.Select(m => Display(plain) + "." + m.Name)) + " }";
      bind = "arguments_.BindEnum<" + Display(plain) + ">(" + Lit(parameter.Name) + ", " + names + ", " + values + ", " + (nullable ? "true" : "false") + ")" + (nullable ? string.Empty : ".GetValueOrDefault()");
    }
    else
    {
      var info = TypeInfoField(type, location);
      bind = "arguments_.Bind<" + typeText + ">(" + Lit(parameter.Name) + ", " + info + ", " + Lit(TypeMapper.Expected(type)) + ", " + (nullable ? "true" : "false") + ")";
    }

    if (parameter.HasExplicitDefaultValue)
    {
      return "arguments_.Has(" + Lit(parameter.Name) + ") ? " + bind + " : " + DefaultExpression(parameter);
    }

    return bind;
  }

  private static string Display(ITypeSymbol type) => type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

  private string TypeInfoField(ITypeSymbol type, Location location)
  {
    var (context, _) = _mapper.FindMetadata(type, location);
    var key = (context ?? "builtin") + "|" + TypeMapper.KeyOf(type);
    if (_typeInfos.TryGetValue(key, out var existing))
    {
      return existing;
    }

    var name = "__omc_info" + _typeInfos.Count;
    var typeText = Display(type);
    var source = context ?? Core + ".Binding.BindingJson";
    _fields.Append("  private static readonly ").Append(TypeInfo).Append('<').Append(typeText).Append("> ").Append(name).Append(" = (").Append(TypeInfo).Append('<').Append(typeText)
      .Append(">)").Append(source).Append(".Default.Options.GetTypeInfo(typeof(").Append(typeText).AppendLine("));");
    _typeInfos[key] = name;
    return name;
  }

  private static string DefaultExpression(IParameterSymbol parameter)
  {
    var type = parameter.Type.WithNullableAnnotation(NullableAnnotation.None);
    var value = parameter.ExplicitDefaultValue;
    var typeText = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    if (value is null)
    {
      return "default(" + typeText + ")";
    }

    if (value is string or bool)
    {
      return SymbolDisplay.FormatPrimitive(value, quoteStrings: true, useHexadecimalNumbers: false);
    }

    return "unchecked((" + typeText + ")" + SymbolDisplay.FormatPrimitive(value, quoteStrings: true, useHexadecimalNumbers: false) + ")";
  }

  private static string? DefaultJson(IParameterSymbol parameter)
  {
    var value = parameter.ExplicitDefaultValue;
    var plain = TypeMapper.Unwrap(parameter.Type);
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
    }

    if (plain.TypeKind == TypeKind.Enum)
    {
      var member = plain.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, value));
      return member is null ? null : SchemaNode.QuoteToString(member.Name);
    }

    return value switch
    {
      double d => d.ToString("R", CultureInfo.InvariantCulture),
      float f => f.ToString("R", CultureInfo.InvariantCulture),
      IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
      _ => null,
    };
  }

  // ---- return values ----

  private sealed class ReturnPlan
  {
    public bool Void { get; set; }

    public bool Await { get; set; }

    public Func<string, string> Map { get; set; } = null!;
  }

  private ReturnPlan AnalyzeReturn(IMethodSymbol method, Location location, out SchemaNode? outputSchema)
  {
    outputSchema = null;
    var type = method.ReturnType;
    var plan = new ReturnPlan();
    if (method.IsAsync && method.ReturnsVoid)
    {
      throw new MappingException("OMCG0003", "An async void method cannot be a tool; return Task.", location);
    }

    if (type.SpecialType == SpecialType.System_Void)
    {
      plan.Void = true;
      plan.Map = _ => Core + ".Binding.ToolResults.Empty()";
      return plan;
    }

    var constructed = (type as INamedTypeSymbol)?.ConstructedFrom.ToDisplayString();
    if (constructed is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask" || type.ToDisplayString() is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask")
    {
      plan.Void = true;
      plan.Await = true;
      plan.Map = _ => Core + ".Binding.ToolResults.Empty()";
      return plan;
    }

    if (constructed is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>")
    {
      plan.Await = true;
      type = ((INamedTypeSymbol)type).TypeArguments[0];
    }

    var plain = TypeMapper.Unwrap(type);
    var typeName = plain.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    if (plain.SpecialType == SpecialType.System_String)
    {
      plan.Map = v => Core + ".Binding.ToolResults.Text(" + v + ")";
    }
    else if (typeName == Core + ".Protocol.CallToolResult")
    {
      plan.Map = v => v;
    }
    else if (DerivesFrom(plain, Core + ".Protocol.ContentBlock"))
    {
      plan.Map = v => Core + ".Binding.ToolResults.Part(" + v + ")";
    }
    else if (ImplementsEnumerableOf(plain, Core + ".Protocol.ContentBlock"))
    {
      plan.Map = v => Core + ".Binding.ToolResults.Parts(" + v + ")";
    }
    else if (typeName is "global::System.Text.Json.Nodes.JsonNode" or "global::System.Text.Json.Nodes.JsonObject" or "global::System.Text.Json.Nodes.JsonArray" || DerivesFrom(plain, "global::System.Text.Json.Nodes.JsonNode"))
    {
      plan.Map = v => Core + ".Binding.ToolResults.Json(" + v + ")";
    }
    else if (typeName == "global::System.Text.Json.JsonElement")
    {
      plan.Map = v => Core + ".Binding.ToolResults.Json(" + v + ")";
    }
    else if (plain.TypeKind == TypeKind.Enum)
    {
      var nullable = TypeMapper.IsNullable(type);
      plan.Map = v => Core + ".Binding.ToolResults.EnumName(" + v + (nullable ? "?" : string.Empty) + ".ToString())";
    }
    else
    {
      var shape = TypeMapper.Shape(plain);
      try
      {
        _ = _mapper.Map(type, true, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), location);
      }
      catch (MappingException ex) when (ex.Id == "OMCG0002")
      {
        throw new MappingException("OMCG0003", "The return type " + type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + " is not supported.", location);
      }

      if (shape is TypeShape.Primitive or TypeShape.Sequence or TypeShape.Dictionary or TypeShape.Object)
      {
        var info = TypeInfoField(type, location);
        var typeText = Display(type);
        if (shape is TypeShape.Dictionary or TypeShape.Object)
        {
          var (_, policy) = _mapper.FindMetadata(type, location);
          // A nullable result still has an object schema: outputSchema.type must be "object" (2025-06-18 and later); null goes out as content only.
          outputSchema = _mapper.Map(plain.WithNullableAnnotation(NullableAnnotation.NotAnnotated), policy != NamingPolicy.None, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), location);
          plan.Map = v => Core + ".Binding.ToolResults.Structured<" + typeText + ">(" + v + ", " + info + ")";
        }
        else
        {
          plan.Map = v => Core + ".Binding.ToolResults.Value<" + typeText + ">(" + v + ", " + info + ")";
        }
      }
      else
      {
        throw new MappingException("OMCG0003", "The return type " + type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + " is not supported.", location);
      }
    }

    return plan;
  }

  private static bool DerivesFrom(ITypeSymbol type, string fullName)
  {
    for (var t = type; t is not null; t = t.BaseType)
    {
      if (t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == fullName)
      {
        return true;
      }
    }

    return false;
  }

  private static bool ImplementsEnumerableOf(ITypeSymbol type, string elementBase)
  {
    if (type.SpecialType == SpecialType.System_String)
    {
      return false;
    }

    var candidates = type.AllInterfaces.Concat(new[] { type }.OfType<INamedTypeSymbol>());
    if (type is IArrayTypeSymbol array)
    {
      return DerivesFrom(array.ElementType, elementBase);
    }

    foreach (var candidate in candidates)
    {
      if (candidate.ConstructedFrom.ToDisplayString() == "System.Collections.Generic.IEnumerable<T>" && DerivesFrom(candidate.TypeArguments[0], elementBase))
      {
        return true;
      }
    }

    return false;
  }

  // ---- resources and prompts ----

  private void EmitResource(IMethodSymbol method, AttributeData attribute)
  {
    var location = MethodLocation(method);
    CheckMethod(method, location);
    var docs = DocComment.Read(MethodSyntax(method));
    var uri = attribute.ConstructorArguments.Length == 1 ? attribute.ConstructorArguments[0].Value as string : null;
    if (string.IsNullOrEmpty(uri))
    {
      throw new MappingException("OMCG0008", "A resource needs a URI or URI template.", location);
    }

    if (!_resourceUris.Add(uri!))
    {
      throw new MappingException("OMCG0007", "Two resources in '" + _type.Name + "' use the URI '" + uri + "'.", location);
    }

    var name = NamedString(attribute, "Name") ?? SnakeCase(method.Name);
    var description = NamedString(attribute, "Description") ?? TypeMapper.DescriptionOf(method.GetAttributes()) ?? docs.Summary;
    var title = NamedString(attribute, "Title");
    var mime = NamedString(attribute, "MimeType");
    var isTemplate = uri!.IndexOf('{') >= 0;
    var id = _counter++;

    if (!isTemplate && method.Parameters.Any(p => p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) is not (Core + ".RequestContext" or "global::System.Threading.CancellationToken")))
    {
      throw new MappingException("OMCG0002", "A resource with a fixed URI takes no arguments; give it a URI template such as " + uri + "/{id}.", location);
    }

    var (statements, arguments) = StringParameters(method, "variables", docs, location, out _);
    var returned = ResourceReturn(method, location, uri, mime);
    AppendStringMethod("__omc_Resource" + id, "global::System.Threading.Tasks.ValueTask<" + Core + ".Protocol.ReadResourceResult>", "string uri, " + Core + ".RequestContext context",
      (isTemplate ? "    var variables = " + Core + ".Binding.StringArguments.Match(" + Lit(uri) + ", uri);\n" : string.Empty) + statements,
      method, arguments, returned, "uri");

    var initializers = new List<string>();
    if (title is not null)
    {
      initializers.Add("Title = " + Lit(title));
    }

    if (description is not null)
    {
      initializers.Add("Description = " + Lit(description));
    }

    if (mime is not null)
    {
      initializers.Add("MimeType = " + Lit(mime));
    }

    var definition = isTemplate
      ? "server.ResourceTemplates.Add(new " + Core + ".ResourceTemplateDefinition(" + Lit(uri) + ", " + Lit(name) + ")"
      : "server.Resources.Add(new " + Core + ".ResourceDefinition(" + Lit(uri) + ", " + Lit(name) + ")";
    _registrations.Append("    ").Append(definition).Append(initializers.Count > 0 ? " { " + string.Join(", ", initializers) + " }" : string.Empty).Append(", __omc_Resource").Append(id).AppendLine(");");
  }

  private void EmitPrompt(IMethodSymbol method, AttributeData attribute)
  {
    var location = MethodLocation(method);
    CheckMethod(method, location);
    var docs = DocComment.Read(MethodSyntax(method));
    var name = NamedString(attribute, "Name") ?? (attribute.ConstructorArguments.Length == 1 ? attribute.ConstructorArguments[0].Value as string : null) ?? SnakeCase(method.Name);
    if (name.Length == 0)
    {
      throw new MappingException("OMCG0008", "A prompt needs a name.", location);
    }

    if (!_promptNames.Add(name))
    {
      throw new MappingException("OMCG0007", "Two prompts in '" + _type.Name + "' are named '" + name + "'.", location);
    }

    var description = NamedString(attribute, "Description") ?? TypeMapper.DescriptionOf(method.GetAttributes()) ?? docs.Summary;
    var title = NamedString(attribute, "Title");
    var id = _counter++;

    var (statements, arguments) = StringParameters(method, "values", docs, location, out var promptArguments);
    var returned = PromptReturn(method, location, description);
    AppendStringMethod("__omc_Prompt" + id, "global::System.Threading.Tasks.ValueTask<" + Core + ".Protocol.GetPromptResult>", "global::System.Collections.Generic.IReadOnlyDictionary<string, string> values, " + Core + ".RequestContext context",
      statements, method, arguments, returned, null);

    var initializers = new List<string>();
    if (title is not null)
    {
      initializers.Add("Title = " + Lit(title));
    }

    if (promptArguments.Count > 0)
    {
      initializers.Add("Arguments = new " + Core + ".Protocol.PromptArgument[] { " + string.Join(", ", promptArguments) + " }");
    }

    _registrations.Append("    server.Prompts.Add(new ").Append(Core).Append(".PromptDefinition(").Append(Lit(name)).Append(", ").Append(description is null ? "null" : Lit(description)).Append(')')
      .Append(initializers.Count > 0 ? " { " + string.Join(", ", initializers) + " }" : string.Empty).Append(", __omc_Prompt").Append(id).AppendLine(");");
  }

  private static (string Statements, List<string> Arguments) StringParameters(IMethodSymbol method, string? source, DocComment docs, Location location, out List<string> promptArguments)
  {
    promptArguments = new List<string>();
    var statements = new StringBuilder();
    var arguments = new List<string>();
    var contextCount = 0;
    var tokenCount = 0;
    var index = 0;
    foreach (var parameter in method.Parameters)
    {
      var parameterLocation = parameter.Locations.FirstOrDefault() ?? location;
      var typeName = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
      if (typeName == Core + ".RequestContext")
      {
        contextCount++;
        arguments.Add("context");
        continue;
      }

      if (typeName == "global::System.Threading.CancellationToken")
      {
        tokenCount++;
        arguments.Add("context.CancellationToken");
        continue;
      }

      var plain = TypeMapper.Unwrap(parameter.Type);
      var parsable = plain.SpecialType == SpecialType.System_String
        || (TypeMapper.Shape(plain) == TypeShape.Primitive && plain.SpecialType != SpecialType.System_Char && plain is not IArrayTypeSymbol && plain.ToDisplayString() != "System.Uri");
      if (parameter.RefKind != RefKind.None || !parsable)
      {
        throw new MappingException("OMCG0002", "The parameter '" + parameter.Name + "' must be a string or a type that parses from a string (numbers, bool, Guid, dates); template variables and prompt arguments are text.", parameterLocation);
      }

      var variable = "p" + index++;
      var required = !parameter.HasExplicitDefaultValue && !TypeMapper.IsNullable(parameter.Type);
      var fallback = parameter.HasExplicitDefaultValue ? DefaultExpression(parameter) : "default(" + Display(parameter.Type) + ")";
      statements.Append("    var ").Append(variable).Append(" = ").Append(Core).Append(".Binding.StringArguments.TryGet<").Append(Display(plain)).Append(">(").Append(source ?? "variables").Append(", ")
        .Append(Lit(parameter.Name)).Append(", ").Append(required ? "true" : "false").Append(", out var ").Append(variable).Append("_value) ? (").Append(Display(parameter.Type)).Append(')').Append(variable).Append("_value : ").Append(fallback).AppendLine(";");
      arguments.Add(variable);
      var description = TypeMapper.DescriptionOf(parameter.GetAttributes()) ?? (docs.Parameters.TryGetValue(parameter.Name, out var text) && text.Length > 0 ? text : null);
      promptArguments.Add("new " + Core + ".Protocol.PromptArgument { Name = " + Lit(parameter.Name) + (description is null ? string.Empty : ", Description = " + Lit(description)) + (required ? ", Required = true" : string.Empty) + " }");
    }

    if (contextCount > 1 || tokenCount > 1)
    {
      throw new MappingException("OMCG0009", "A method takes at most one RequestContext and one CancellationToken.", location);
    }

    return (statements.ToString(), arguments);
  }

  private void AppendStringMethod(string methodName, string returnType, string parameters, string statements, IMethodSymbol method, List<string> arguments, ReturnPlan returned, string? uriVariable)
  {
    _ = uriVariable;
    var call = method.Name + "(" + string.Join(", ", arguments) + ")";
    _methods.Append("  private ").Append(returned.Await ? "async " : string.Empty).Append(returnType).Append(' ').Append(methodName).Append('(').Append(parameters).AppendLine(")");
    _methods.AppendLine("  {");
    _methods.Append(statements);
    _methods.Append("    var result = ").Append(returned.Await ? "await " : string.Empty).Append(call).AppendLine(returned.Await ? ".ConfigureAwait(false);" : ";");
    var mapped = returned.Map("result");
    _methods.Append("    return ").Append(returned.Await ? mapped : "new " + returnType + "(" + mapped + ")").AppendLine(";");
    _methods.AppendLine("  }");
    _methods.AppendLine();
  }

  private static ReturnPlan ResourceReturn(IMethodSymbol method, Location location, string uriTemplate, string? mime)
  {
    _ = uriTemplate;
    var plan = UnwrapAsync(method, location, out var type);
    var typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    var mimeText = mime is null ? "null" : Lit(mime);
    if (type.SpecialType == SpecialType.System_String)
    {
      plan.Map = v => Core + ".Binding.StringArguments.TextResource(uri, " + mimeText + ", " + v + " ?? string.Empty)";
    }
    else if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte, Rank: 1 })
    {
      plan.Map = v => Core + ".Binding.StringArguments.BlobResource(uri, " + mimeText + ", " + v + ")";
    }
    else if (typeName == Core + ".Protocol.ReadResourceResult")
    {
      plan.Map = v => v;
    }
    else if (DerivesFrom(type, Core + ".Protocol.ResourceContents"))
    {
      plan.Map = v => Core + ".Binding.StringArguments.Resource(new " + Core + ".Protocol.ResourceContents[] { " + v + " })";
    }
    else if (ImplementsEnumerableOf(type, Core + ".Protocol.ResourceContents"))
    {
      plan.Map = v => Core + ".Binding.StringArguments.Resource(" + v + ")";
    }
    else
    {
      throw new MappingException("OMCG0003", "A resource method returns a string, a byte array, ReadResourceResult or resource contents (or a Task of one); '" + type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + "' is not supported.", location);
    }

    return plan;
  }

  private static ReturnPlan PromptReturn(IMethodSymbol method, Location location, string? description)
  {
    var plan = UnwrapAsync(method, location, out var type);
    var typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    var descriptionText = description is null ? "null" : Lit(description);
    if (type.SpecialType == SpecialType.System_String)
    {
      plan.Map = v => Core + ".Binding.StringArguments.UserPrompt(" + descriptionText + ", " + v + " ?? string.Empty)";
    }
    else if (typeName == Core + ".Protocol.GetPromptResult")
    {
      plan.Map = v => v;
    }
    else if (typeName == Core + ".Protocol.PromptMessage")
    {
      plan.Map = v => Core + ".Binding.StringArguments.Prompt(" + descriptionText + ", new " + Core + ".Protocol.PromptMessage[] { " + v + " })";
    }
    else if (ImplementsEnumerableOf(type, Core + ".Protocol.PromptMessage"))
    {
      plan.Map = v => Core + ".Binding.StringArguments.Prompt(" + descriptionText + ", " + v + ")";
    }
    else
    {
      throw new MappingException("OMCG0003", "A prompt method returns a string, PromptMessage(s) or GetPromptResult (or a Task of one); '" + type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + "' is not supported.", location);
    }

    return plan;
  }

  private static ReturnPlan UnwrapAsync(IMethodSymbol method, Location location, out ITypeSymbol type)
  {
    var plan = new ReturnPlan();
    type = method.ReturnType;
    var constructed = (type as INamedTypeSymbol)?.ConstructedFrom.ToDisplayString();
    if (constructed is "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>")
    {
      plan.Await = true;
      type = ((INamedTypeSymbol)type).TypeArguments[0];
    }
    else if (type.SpecialType == SpecialType.System_Void || type.ToDisplayString() is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask")
    {
      throw new MappingException("OMCG0003", "This method must return content.", location);
    }

    return plan;
  }

  // ---- shared ----

  private static void CheckMethod(IMethodSymbol method, Location location)
  {
    if (method.IsGenericMethod)
    {
      throw new MappingException("OMCG0004", "The method '" + method.Name + "' is generic; tools, resources and prompts cannot be.", location);
    }
  }

  private static SyntaxNode? MethodSyntax(IMethodSymbol method) => method.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).FirstOrDefault();

  private static string? NamedString(AttributeData attribute, string name)
  {
    foreach (var named in attribute.NamedArguments)
    {
      if (named.Key == name && named.Value.Value is string value)
      {
        return value;
      }
    }

    return null;
  }

  private static string Lit(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

  /// <summary><c>GetDocketAsync</c> becomes <c>get_docket</c>; <c>HTTPRequest</c> becomes <c>http_request</c>.</summary>
  internal static string SnakeCase(string name)
  {
    if (name.EndsWith("Async", StringComparison.Ordinal) && name.Length > "Async".Length)
    {
      name = name.Substring(0, name.Length - "Async".Length);
    }

    var sb = new StringBuilder();
    for (var i = 0; i < name.Length; i++)
    {
      var c = name[i];
      if (char.IsUpper(c) && i > 0)
      {
        var previous = name[i - 1];
        var nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
        if (char.IsLower(previous) || char.IsDigit(previous) || (char.IsUpper(previous) && nextIsLower))
        {
          sb.Append('_');
        }
      }

      sb.Append(char.ToLowerInvariant(c));
    }

    return sb.ToString();
  }

  private string Compose()
  {
    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated>Written by the OneModelContext source generator. Changes are lost.</auto-generated>");
    sb.AppendLine("#nullable disable");
    sb.AppendLine("#pragma warning disable");
    sb.AppendLine();

    var chain = new List<INamedTypeSymbol>();
    for (INamedTypeSymbol? t = _type; t is not null; t = t.ContainingType)
    {
      chain.Insert(0, t);
    }

    var indent = string.Empty;
    var ns = _type.ContainingNamespace;
    var hasNamespace = ns is { IsGlobalNamespace: false };
    if (hasNamespace)
    {
      sb.Append("namespace ").Append(ns.ToDisplayString()).AppendLine();
      sb.AppendLine("{");
      indent = "  ";
    }

    for (var i = 0; i < chain.Count; i++)
    {
      var t = chain[i];
      var access = t.DeclaredAccessibility switch
      {
        Accessibility.Public => "public ",
        Accessibility.Internal => "internal ",
        Accessibility.Private => "private ",
        Accessibility.Protected => "protected ",
        Accessibility.ProtectedOrInternal => "protected internal ",
        Accessibility.ProtectedAndInternal => "private protected ",
        _ => string.Empty,
      };
      sb.Append(indent).Append(access).Append("partial class ").Append(t.Name);
      if (i == chain.Count - 1)
      {
        sb.Append(" : ").Append(Core).Append(".IContextRegistrar");
      }

      sb.AppendLine();
      sb.Append(indent).AppendLine("{");
      indent += "  ";
    }

    var body = new StringBuilder();
    body.Append(_fields);
    if (_fields.Length > 0)
    {
      body.AppendLine();
    }

    body.AppendLine("  /// <inheritdoc />");
    body.AppendLine("  public void AddTo(" + Core + ".ContextServer server)");
    body.AppendLine("  {");
    body.AppendLine("    if (server is null) throw new global::System.ArgumentNullException(nameof(server));");
    body.Append(_registrations);
    body.AppendLine("  }");
    body.AppendLine();
    body.Append(_methods);

    foreach (var line in body.ToString().TrimEnd().Split('\n'))
    {
      var trimmed = line.TrimEnd('\r');
      sb.AppendLine(trimmed.Length == 0 ? string.Empty : indent.Substring(0, indent.Length - 2) + trimmed);
    }

    for (var i = chain.Count - 1; i >= 0; i--)
    {
      indent = indent.Substring(0, indent.Length - 2);
      sb.Append(indent).AppendLine("}");
    }

    if (hasNamespace)
    {
      sb.AppendLine("}");
    }

    return sb.ToString();
  }
}
