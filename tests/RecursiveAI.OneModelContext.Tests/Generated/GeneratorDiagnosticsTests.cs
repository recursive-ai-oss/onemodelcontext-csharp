using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RecursiveAI.OneModelContext.Generator;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests.Generated;

/// <summary>What the generator says when a declaration cannot be turned into a registration.</summary>
public class GeneratorDiagnosticsTests
{
  private static readonly Lazy<List<MetadataReference>> References = new(() =>
  {
    var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty).Split(System.IO.Path.PathSeparator);
    var references = paths.Where(p => p.Length > 0).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
    references.Add(MetadataReference.CreateFromFile(typeof(ContextServer).Assembly.Location));
    return references;
  });

  private static (string[] Ids, string[] Sources) Run(string code)
  {
    var tree = CSharpSyntaxTree.ParseText("#nullable enable\nusing System; using System.Collections.Generic; using System.Text.Json.Serialization; using System.Threading; using System.Threading.Tasks; using RecursiveAI.OneModelContext;\n" + code, new CSharpParseOptions(LanguageVersion.Latest));
    var compilation = CSharpCompilation.Create("t", [tree], References.Value, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    var driver = CSharpGeneratorDriver.Create(new ContextGenerator()).RunGenerators(compilation, TestContext.Current.CancellationToken);
    var result = driver.GetRunResult();
    return ([.. result.Diagnostics.Select(d => d.Id)], [.. result.GeneratedTrees.Select(t => t.ToString())]);
  }

  [Theory]
  [InlineData("public class C { [Tool] public string A() => \"\"; }", "OMCG0001")]
  [InlineData("public static partial class C { [Tool] public static string A() => \"\"; }", "OMCG0001")]
  [InlineData("public partial class C { public class In { [Tool] public string A() => \"\"; } }", "OMCG0001")]
  [InlineData("public partial class C<T> { [Tool] public string A() => \"\"; }", "OMCG0010")]
  [InlineData("public partial class C { [Tool] public string A(object o) => \"\"; }", "OMCG0002")]
  [InlineData("public partial class C { [Tool] public string A(int[,] o) => \"\"; }", "OMCG0002")]
  [InlineData("public partial class C { [Tool] public string A(ref int o) => \"\"; }", "OMCG0002")]
  [InlineData("public partial class C { [Tool] public object A() => \"\"; }", "OMCG0003")]
  [InlineData("public partial class C { [Tool] public async void A() { await Task.Yield(); } }", "OMCG0003")]
  [InlineData("public partial class C { [Tool] public string A<T>(T t) => \"\"; }", "OMCG0004")]
  [InlineData("public partial class C { [Tool] public string A(List<Item> items) => \"\"; } public record Item(string N);", "OMCG0005")]
  [InlineData("public partial class C { [Tool] public Item A() => new(\"\"); } public record Item(string N);", "OMCG0005")]
  [InlineData("public partial class C { [Tool] public string A(Node n) => \"\"; } public record Node(string N, List<Node> Kids); [JsonSerializable(typeof(Node))] public partial class J : JsonSerializerContext { }", "OMCG0006")]
  [InlineData("public partial class C { [Tool(\"x\")] public string A() => \"\"; [Tool(Name = \"x\")] public string B() => \"\"; }", "OMCG0007")]
  [InlineData("public partial class C { [Tool(\"has space\")] public string A() => \"\"; }", "OMCG0008")]
  [InlineData("public partial class C { [Tool] public string A(RequestContext a, RequestContext b) => \"\"; }", "OMCG0009")]
  [InlineData("public partial class C { [Tool] public string A(CancellationToken a, CancellationToken b) => \"\"; }", "OMCG0009")]
  [InlineData("public partial class C { [Tool] public string A(Item i) => \"\"; } public record Item(string N); [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)] [JsonSerializable(typeof(Item))] public partial class J : JsonSerializerContext { }", "OMCG0011")]
  [InlineData("public partial class C { [Resource(\"fixed://x\")] public string A(string id) => \"\"; }", "OMCG0002")]
  [InlineData("public partial class C { [Resource(\"t://{id}\")] public string A(Uri id) => \"\"; }", "OMCG0002")]
  [InlineData("public partial class C { [Resource(\"t://{id}\")] public int A(string id) => 1; }", "OMCG0003")]
  [InlineData("public partial class C { [Prompt] public int A() => 1; }", "OMCG0003")]
  public void Misuse_is_reported_with_its_id(string code, string id)
  {
    var (ids, sources) = Run(code);
    Assert.Contains(id, ids);
    Assert.Empty(sources);
  }

  [Fact]
  public void A_valid_class_generates_without_diagnostics()
  {
    var (ids, sources) = Run("public partial class C { [Tool] public string A(string s, int n = 2) => s; }");
    Assert.Empty(ids);
    var source = Assert.Single(sources);
    Assert.Contains("IContextRegistrar", source, StringComparison.Ordinal);
    Assert.DoesNotContain("Reflection", source, StringComparison.Ordinal);
  }

  [Fact]
  public void A_class_in_a_namespace_and_nested_in_another_generates()
  {
    var (ids, sources) = Run("namespace N.M { public partial class Outer { public partial class C { [Tool] public string A() => \"\"; } } }");
    Assert.Empty(ids);
    Assert.Contains("namespace N.M", Assert.Single(sources), StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("GetDocketAsync", "get_docket")]
  [InlineData("HTTPRequest", "http_request")]
  [InlineData("GetHTTPRequest", "get_http_request")]
  [InlineData("Add", "add")]
  [InlineData("Async", "async")]
  [InlineData("Nothing2", "nothing2")]
  public void Method_names_become_snake_case(string method, string expected) =>
    Assert.Equal(expected, ClassEmitter.SnakeCase(method));
}
