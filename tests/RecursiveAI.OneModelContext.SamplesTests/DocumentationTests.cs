using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RecursiveAI.OneModelContext.Generator;
using Xunit;

namespace RecursiveAI.OneModelContext.SamplesTests;

public sealed partial class DocumentationTests
{
  [GeneratedRegex("```csharp\\r?\\n(.*?)```", RegexOptions.Singleline)]
  private static partial Regex CSharpBlocks();

  [Fact]
  public void Documentation_samples_compile()
  {
    // Compile against the same runtime and project references as the consumer samples. Host-owned objects and generated
    // serializer metadata is a fixture; registration is generated here and serializer generation is exercised by AOT smoke.
    var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
      .Concat(Directory.GetFiles(AppContext.BaseDirectory, "*.dll")).Distinct(StringComparer.Ordinal);
    var references = paths.Select(p => MetadataReference.CreateFromFile(p)).ToArray();
    var compiled = 0;
    var skipped = 0;
    foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Documentation"), "*.md").Order())
    {
      var text = File.ReadAllText(file);
      foreach (Match match in CSharpBlocks().Matches(text))
      {
        var before = text[..match.Index].TrimEnd();
        if (before.EndsWith("-->", StringComparison.Ordinal) && before[(before.LastIndexOf("<!--", StringComparison.Ordinal) + 4)..].StartsWith(" compile-skip:", StringComparison.Ordinal))
        {
          skipped++;
          continue;
        }

        var name = Path.GetFileName(file);
        var block = match.Groups[1].Value;
        var prefix = name switch
        {
          "README.md" when block.Contains("argumentsJsonTypeInfo", StringComparison.Ordinal) => "System.Text.Json.Serialization.Metadata.JsonTypeInfo<ReadFileArguments> argumentsJsonTypeInfo = null!;\n",
          "README.md" when block.Contains("new IndexTools", StringComparison.Ordinal) => "var index = new SearchIndex();\n",
          "adapters.md" => "var builder = WebApplication.CreateBuilder();\n",
          "authorization-server.md" => "using var ecdsa = ECDsa.Create(); var app = WebApplication.Create(); IHttpExchange exchange = null!;\n",
          "oauth-client.md" => "string path = \"tokens.json\"; ValueTask OpenBrowser(Uri url) => ValueTask.CompletedTask;\n",
          "resource-server.md" => "var builder = WebApplication.CreateBuilder(); var app = builder.Build(); var server = new ContextServer(\"demo\", \"1\");\n",
          "extensions.md" when block.Contains("var server =", StringComparison.Ordinal) => "",
          "extensions.md" when block.Contains("// Client", StringComparison.Ordinal) => "ContextClient client = null!;\n",
          "extensions.md" => "var server = new ContextServer(\"demo\", \"1\"); string html = \"\"; var definition = new ToolDefinition(\"demo\", \"\", JsonDocument.Parse(\"{\\\"type\\\":\\\"object\\\"}\").RootElement); ToolHandler handler = null!; JsonElement schema = default;\n",
          _ => "",
        };
        // Usings have to precede the fixture statements, just as they would in a complete application.
        var tree = CSharpSyntaxTree.ParseText(block, cancellationToken: TestContext.Current.CancellationToken);
        var root = (Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)tree.GetRoot(TestContext.Current.CancellationToken);
        var usings = string.Join("\n", root.Usings.Select(u => u.ToString()));
        var body = root.RemoveNodes(root.Usings, SyntaxRemoveOptions.KeepNoTrivia)!.ToFullString();
        var fixtures = Fixtures;
        if (name == "README.md" && block.Contains("new IndexTools", StringComparison.Ordinal))
        {
          fixtures = fixtures[..fixtures.IndexOf("public partial class IndexTools", StringComparison.Ordinal)];
        }

        var source = Imports + usings + "\n" + prefix + body + fixtures;
        var compilation = CSharpCompilation.Create("DocumentationSample", [CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)], references,
          new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        CSharpGeneratorDriver.Create(new ContextGenerator()).RunGeneratorsAndUpdateCompilation(compilation, out var generated, out var generatorDiagnostics, TestContext.Current.CancellationToken);
        var errors = generated.GetDiagnostics(TestContext.Current.CancellationToken).Concat(generatorDiagnostics).Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, name + ":" + (text[..match.Index].Count(c => c == '\n') + 1) + "\n" + string.Join("\n", errors.Select(d => d.ToString())));
        compiled++;
      }
    }

    Assert.Equal(13, compiled);
    Assert.Equal(3, skipped); // Two abbreviated store setups and the external SDK namespace-alias illustration.
  }

  private const string Imports = """
    #pragma warning disable OMCX0001, OMCX0002, OMCX0003
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Security.Cryptography;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.Extensions.DependencyInjection;
    using RecursiveAI.OneModelContext;
    using RecursiveAI.OneModelContext.Protocol;
    using RecursiveAI.OneModelContext.Wire;
    using RecursiveAI.OneModelContext.Hosting;
    using RecursiveAI.OneModelContext.AspNetCore;
    using RecursiveAI.OneModelContext.Auth;
    using RecursiveAI.OneModelContext.Auth.Server;
    using RecursiveAI.OneModelContext.Http;
    using RecursiveAI.OneModelContext.Jose;
    using RecursiveAI.OneModelContext.OAuth;
    using RecursiveAI.OneModelContext.Extensions;
    using RecursiveAI.OneModelContext.Extensions.Apps;
    using RecursiveAI.OneModelContext.Extensions.Tasks;
    using RecursiveAI.OneModelContext.Extensions.Skills;
    using RecursiveAI.OneModelContext.Extensions.ServerCards;

    """;

  private const string Fixtures = """

    public static class ExistingCallShapes
    {
      public static async Task CompileAsync(ContextClient client, JsonElement arguments, CancellationToken ct)
      {
        await client.CallToolAsync("x");
        await client.CallToolAsync("x", default);
        await client.CallToolAsync("x", arguments);
        await client.CallToolAsync("x", arguments, default(CancellationToken));
        await client.CallToolAsync("x", default, ct);
        await client.CallToolAsync("x", arguments, progress: null, cancellationToken: ct);
      }
    }
    public sealed record ReadFileArguments(string path);
    public sealed class SearchIndex
    {
      public Task<string[]> SearchAsync(string query, int limit) => Task.FromResult(new[] { query });
    }
    public partial class IndexTools : IContextRegistrar
    {
      public void AddTo(ContextServer server) { }
    }
    """;
}
