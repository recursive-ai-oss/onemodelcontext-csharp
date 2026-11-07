using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using RecursiveAI.OneModelContext;

namespace QuickPath;

/// <summary>
/// The "just let me try it" server: tools are lambdas and the library works the schema out from their signatures when the server
/// starts. It is the fastest way to a working server and the one path in this library that uses reflection, so it cannot be
/// published with NativeAOT or trimmed. When the tools settle, move them into a partial class with <c>[Tool]</c> methods: the
/// generator writes the same schemas at build time and the server then needs no reflection.
/// </summary>
public static class QuickServer
{
  /// <summary>Builds the server.</summary>
  [RequiresUnreferencedCode("Tools registered from lambdas are described by reflection.")]
  [RequiresDynamicCode("Tools registered from lambdas are invoked by reflection.")]
  public static ContextServer Create()
  {
    var server = new ContextServer("quick-path", "1.0.0");

    // Parameter names become argument names; parameters with a default are optional; a string result is the tool's text.
    server.Tool("add", (int a, int b) => a + b, "Adds two whole numbers.");

    // [Description] on a parameter becomes that argument's description in the schema.
    server.Tool("shout", ([Description("What to shout.")] string text, int times = 1) => string.Join(' ', Enumerable.Repeat(text.ToUpperInvariant() + "!", times)), "Shouts some text.");

    // A RequestContext or CancellationToken parameter is filled in by the library and is not part of the schema.
    server.Tool("countdown", async (int from, RequestContext context) =>
    {
      for (var n = from; n > 0; n--)
      {
        await context.ReportProgressAsync(from - n + 1, from, $"{n}...");
      }

      return "Liftoff.";
    }, "Counts down, reporting progress.");

    server.Resource("quick://about", "about", () => "A server made of lambdas.", "text/plain");
    server.Prompt("explain", ([Description("The topic.")] string topic) => $"Explain {topic} in one paragraph.");
    return server;
  }
}
