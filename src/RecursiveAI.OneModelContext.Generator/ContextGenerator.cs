using Microsoft.CodeAnalysis;

namespace RecursiveAI.OneModelContext.Generator;

/// <summary>
/// Emits tool, resource and prompt registrations for classes that declare them with attributes.
/// The pipeline is empty for now: it is wired into the package so the shape of the bundle is settled.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ContextGenerator : IIncrementalGenerator
{
  /// <inheritdoc />
  public void Initialize(IncrementalGeneratorInitializationContext context)
  {
    // Nothing to emit yet.
  }
}
