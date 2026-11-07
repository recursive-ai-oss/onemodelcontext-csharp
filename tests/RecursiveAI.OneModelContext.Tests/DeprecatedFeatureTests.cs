using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>What a project that uses Sampling, Roots or Logging sees when it compiles against the library.</summary>
public class DeprecatedFeatureTests
{
  private static readonly Lazy<List<MetadataReference>> References = new(() =>
  {
    var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty).Split(System.IO.Path.PathSeparator);
    var references = paths.Where(p => p.Length > 0).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
    references.Add(MetadataReference.CreateFromFile(typeof(ContextServer).Assembly.Location));
    return references;
  });

  private static Diagnostic[] Compile(string body)
  {
    var code = "using System; using System.Threading.Tasks; using RecursiveAI.OneModelContext; using RecursiveAI.OneModelContext.Protocol;\n"
      + "public static class T { public static async Task Run(RequestContext ctx, ContextClient client) { " + body + " } }";
    var tree = CSharpSyntaxTree.ParseText(code);
    var compilation = CSharpCompilation.Create("t", [tree], References.Value, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    return [.. compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity >= DiagnosticSeverity.Warning)];
  }

  [Theory]
  [InlineData("await ctx.ListRootsAsync();", "OMC0001", "tool parameters, resource URIs or server configuration")]
  [InlineData("await client.NotifyRootsListChangedAsync();", "OMC0001", "tool parameters, resource URIs or server configuration")]
  [InlineData("await ctx.SampleAsync(null!);", "OMC0002", "LLM provider APIs")]
  [InlineData("var c = new SamplingCapability();", "OMC0002", "LLM provider APIs")]
  [InlineData("await using var c = await ModelContext.ConnectAsync(\"x\", onSample: (r, t) => default);", "OMC0002", "LLM provider APIs")]
  [InlineData("await using var c = await ModelContext.ConnectAsync(\"x\", onListRoots: t => default);", "OMC0001", "tool parameters")]
  [InlineData("var o = new ContextClientOptions { OnSample = (r, t) => default };", "OMC0002", "LLM provider APIs")]
  [InlineData("var o = new ContextClientOptions { OnListRoots = t => default };", "OMC0001", "tool parameters")]
  [InlineData("await ctx.LogAsync(\"info\", \"x\");", "OMC0003", "standard error")]
  [InlineData("await client.SetLoggingLevelAsync(\"info\");", "OMC0003", "OpenTelemetry")]
  public void Using_a_deprecated_feature_warns_with_its_migration(string body, string id, string migration)
  {
    var diagnostic = Assert.Single(Compile(body), d => d.Id == id);

    Assert.Contains(migration, diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
  }

  [Fact]
  public void The_active_utilities_do_not_warn()
  {
    Assert.Empty(Compile("await ctx.ReportProgressAsync(1); await ctx.ConfirmAsync(\"ok?\"); await client.ListToolsAsync(); await using var c = await ModelContext.ConnectAsync(\"x\", onConfirm: (m, t) => ValueTask.FromResult(true));"));
  }

  [Theory]
  [InlineData("var s = new RecursiveAI.OneModelContext.Extensions.Tasks.MemoryTaskStore();", "OMCX0001")]
  [InlineData("var d = RecursiveAI.OneModelContext.Extensions.Skills.SkillDefinition.IsValidName(\"x\");", "OMCX0002")]
  [InlineData("var c = new RecursiveAI.OneModelContext.Extensions.ServerCards.ServerCardClient();", "OMCX0003")]
  public void Using_an_experimental_extension_is_an_error_until_the_project_opts_in(string body, string id)
  {
    var diagnostic = Assert.Single(Compile(body), d => d.Id == id);

    Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    Assert.Contains("evaluation purposes", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
  }
}
