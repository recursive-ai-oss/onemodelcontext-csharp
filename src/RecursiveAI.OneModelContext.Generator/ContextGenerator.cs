using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RecursiveAI.OneModelContext.Binding;

namespace RecursiveAI.OneModelContext.Generator;

/// <summary>
/// Emits tool, resource and prompt registrations for partial classes that declare them with <c>[Tool]</c>, <c>[Resource]</c> and
/// <c>[Prompt]</c>. The emitted code binds arguments through source-generated System.Text.Json metadata and calls the methods directly,
/// so nothing in it needs reflection.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ContextGenerator : IIncrementalGenerator
{
  private const string ToolAttributeName = "RecursiveAI.OneModelContext.ToolAttribute";
  private const string ResourceAttributeName = "RecursiveAI.OneModelContext.ResourceAttribute";
  private const string PromptAttributeName = "RecursiveAI.OneModelContext.PromptAttribute";

  /// <inheritdoc />
  public void Initialize(IncrementalGeneratorInitializationContext context)
  {
    var contexts = context.SyntaxProvider.ForAttributeWithMetadataName(
        "System.Text.Json.Serialization.JsonSerializableAttribute",
        static (node, _) => node is ClassDeclarationSyntax,
        static (ctx, _) => ReadContext(ctx))
      .Collect();

    var candidates = context.SyntaxProvider.CreateSyntaxProvider(
      static (node, _) => node is ClassDeclarationSyntax c && c.Members.Any(m => m is MethodDeclarationSyntax { AttributeLists.Count: > 0 }),
      static (ctx, ct) =>
      {
        var declaration = (ClassDeclarationSyntax)ctx.Node;
        var symbol = ctx.SemanticModel.GetDeclaredSymbol(declaration, ct);
        return symbol is not null && symbol.GetMembers().OfType<IMethodSymbol>().Any(m => m.GetAttributes().Any(a => IsOurs(a))) ? (symbol, declaration) : default;
      })
      .Where(static c => c.symbol is not null);

    var results = candidates.Combine(contexts).Select(static (pair, ct) => Generate(pair.Left.symbol, pair.Left.declaration, pair.Right, ct));
    context.RegisterSourceOutput(results, static (spc, result) =>
    {
      foreach (var diagnostic in result.Diagnostics)
      {
        spc.ReportDiagnostic(diagnostic);
      }

      if (result.Source is not null)
      {
        spc.AddSource(result.HintName, result.Source);
      }
    });
  }

  private static bool IsOurs(AttributeData attribute)
  {
    var name = attribute.AttributeClass?.ToDisplayString();
    return name is ToolAttributeName or ResourceAttributeName or PromptAttributeName;
  }

  private static SerializerContextInfo ReadContext(GeneratorAttributeSyntaxContext ctx)
  {
    var types = new HashSet<string>(StringComparer.Ordinal);
    foreach (var attribute in ctx.Attributes)
    {
      if (attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is ITypeSymbol listed)
      {
        types.Add(TypeMapper.KeyOf(listed));
      }
    }

    var policy = NamingPolicy.None;
    var explicitPolicy = -1;
    var defaults = 0;
    foreach (var attribute in ctx.TargetSymbol.GetAttributes())
    {
      if (attribute.AttributeClass?.ToDisplayString() != "System.Text.Json.Serialization.JsonSourceGenerationOptionsAttribute")
      {
        continue;
      }

      if (attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is int d)
      {
        defaults = d;
      }

      foreach (var named in attribute.NamedArguments)
      {
        if (named.Key == "PropertyNamingPolicy" && named.Value.Value is int p)
        {
          explicitPolicy = p;
        }
      }
    }

    // JsonKnownNamingPolicy: Unspecified 0, CamelCase 1, others are not supported by the signature table. JsonSerializerDefaults.Web is 1.
    if (explicitPolicy > 1)
    {
      policy = NamingPolicy.Unsupported;
    }
    else if (explicitPolicy == 1 || (explicitPolicy <= 0 && defaults == 1))
    {
      policy = NamingPolicy.CamelCase;
    }

    return new SerializerContextInfo(((INamedTypeSymbol)ctx.TargetSymbol).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), types, policy);
  }

  private static GenerationResult Generate(INamedTypeSymbol type, ClassDeclarationSyntax declaration, ImmutableArray<SerializerContextInfo> contexts, System.Threading.CancellationToken ct)
  {
    var emitter = new ClassEmitter(type, declaration, new TypeMapper(contexts));
    return emitter.Emit(ct);
  }
}

internal sealed class GenerationResult
{
  public GenerationResult(string hintName, string? source, List<Diagnostic> diagnostics)
  {
    HintName = hintName;
    Source = source;
    Diagnostics = diagnostics;
  }

  public string HintName { get; }

  public string? Source { get; }

  public List<Diagnostic> Diagnostics { get; }
}

internal static class Diagnostics
{
  private const string Category = "OneModelContext.Generator";

  private static DiagnosticDescriptor Make(string id, string title) => new(id, title, "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

  public static readonly DiagnosticDescriptor NotPartial = Make("OMCG0001", "The containing type must be a partial class");
  public static readonly DiagnosticDescriptor NoSchema = Make("OMCG0002", "A parameter type has no schema");
  public static readonly DiagnosticDescriptor BadReturn = Make("OMCG0003", "The return type is not supported");
  public static readonly DiagnosticDescriptor GenericMethod = Make("OMCG0004", "The method is generic");
  public static readonly DiagnosticDescriptor NoContext = Make("OMCG0005", "A serializer context must list the type");
  public static readonly DiagnosticDescriptor SelfReference = Make("OMCG0006", "A type refers to itself");
  public static readonly DiagnosticDescriptor DuplicateName = Make("OMCG0007", "Two members of one class have the same name");
  public static readonly DiagnosticDescriptor BadName = Make("OMCG0008", "The name is not valid");
  public static readonly DiagnosticDescriptor TooManyInjected = Make("OMCG0009", "More than one RequestContext or CancellationToken parameter");
  public static readonly DiagnosticDescriptor GenericClass = Make("OMCG0010", "The containing class is generic");
  public static readonly DiagnosticDescriptor NamingPolicy = Make("OMCG0011", "Unsupported property naming policy");

  public static DiagnosticDescriptor ById(string id) => id switch
  {
    "OMCG0001" => NotPartial,
    "OMCG0002" => NoSchema,
    "OMCG0003" => BadReturn,
    "OMCG0004" => GenericMethod,
    "OMCG0005" => NoContext,
    "OMCG0006" => SelfReference,
    "OMCG0007" => DuplicateName,
    "OMCG0008" => BadName,
    "OMCG0009" => TooManyInjected,
    "OMCG0010" => GenericClass,
    _ => NamingPolicy,
  };
}
