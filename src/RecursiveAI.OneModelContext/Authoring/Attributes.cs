using System;

namespace RecursiveAI.OneModelContext;

/// <summary>
/// Marks a method of a <c>partial</c> class as a tool. The source generator writes the input schema from the signature and the doc
/// comments, an invoker that needs no reflection, and an <see cref="IContextRegistrar.AddTo"/> method that registers the tool.
/// </summary>
/// <remarks>
/// A parameter of type <see cref="RequestContext"/> or <see cref="System.Threading.CancellationToken"/> is filled in by the runtime and
/// is not part of the schema. Every other parameter is a property of the argument object, required unless it has a default value.
/// An argument that is missing, unknown or of the wrong type makes the call return an error result that names it.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ToolAttribute : Attribute
{
  /// <summary>Creates the attribute with the default name: the method name in snake_case without a trailing <c>Async</c>.</summary>
  public ToolAttribute()
  {
  }

  /// <summary>Creates the attribute with an explicit name.</summary>
  /// <param name="name">1 to 128 letters, digits, <c>_</c>, <c>-</c> or <c>.</c>.</param>
  public ToolAttribute(string name)
  {
    Name = name;
  }

  /// <summary>The tool name; null derives it from the method name.</summary>
  public string? Name { get; set; }

  /// <summary>What the tool does, written for a model. Defaults to the <c>[Description]</c> of the method, then its <c>&lt;summary&gt;</c>.</summary>
  public string? Description { get; set; }

  /// <summary>A display name.</summary>
  public string? Title { get; set; }

  /// <summary>Sets <c>readOnlyHint</c>: the tool does not change its environment. Left out of the listing when never assigned.</summary>
  public bool ReadOnly { get; set; }

  /// <summary>Sets <c>destructiveHint</c>: the tool may destroy data. Left out of the listing when never assigned.</summary>
  public bool Destructive { get; set; }

  /// <summary>Sets <c>idempotentHint</c>: repeating a call has no further effect. Left out of the listing when never assigned.</summary>
  public bool Idempotent { get; set; }

  /// <summary>Sets <c>openWorldHint</c>: the tool reaches outside a closed world. Left out of the listing when never assigned.</summary>
  public bool OpenWorld { get; set; }
}

/// <summary>
/// Marks a method of a <c>partial</c> class as a resource, or as a resource template when the URI holds <c>{variable}</c> expressions.
/// Template variables are passed to parameters of the same name (strings or any type that parses from a string).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ResourceAttribute : Attribute
{
  /// <summary>Creates the attribute.</summary>
  /// <param name="uri">The resource URI or a URI template such as <c>notes://{id}</c> (<c>{name}</c> and <c>{+name}</c> expressions).</param>
  public ResourceAttribute(string uri)
  {
    ArgumentException.ThrowIfNullOrEmpty(uri);
    Uri = uri;
  }

  /// <summary>The URI or URI template.</summary>
  public string Uri { get; }

  /// <summary>The programmatic name; null derives it from the method name.</summary>
  public string? Name { get; set; }

  /// <summary>A display name.</summary>
  public string? Title { get; set; }

  /// <summary>What it is. Defaults to the method's <c>[Description]</c>, then its <c>&lt;summary&gt;</c>.</summary>
  public string? Description { get; set; }

  /// <summary>The media type of the contents, for example <c>text/markdown</c>. Defaults to <c>text/plain</c> for text and <c>application/octet-stream</c> for bytes.</summary>
  public string? MimeType { get; set; }
}

/// <summary>
/// Marks a method of a <c>partial</c> class as a prompt. Parameters become the prompt's arguments: string-like values, required unless
/// they have a default value.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class PromptAttribute : Attribute
{
  /// <summary>Creates the attribute with the default name: the method name in snake_case without a trailing <c>Async</c>.</summary>
  public PromptAttribute()
  {
  }

  /// <summary>Creates the attribute with an explicit name.</summary>
  /// <param name="name">The prompt name.</param>
  public PromptAttribute(string name)
  {
    Name = name;
  }

  /// <summary>The prompt name; null derives it from the method name.</summary>
  public string? Name { get; set; }

  /// <summary>A display name.</summary>
  public string? Title { get; set; }

  /// <summary>What the prompt is for. Defaults to the method's <c>[Description]</c>, then its <c>&lt;summary&gt;</c>.</summary>
  public string? Description { get; set; }
}

/// <summary>
/// Implemented by the source generator on every partial class that declares <see cref="ToolAttribute"/>, <see cref="ResourceAttribute"/>
/// or <see cref="PromptAttribute"/> methods. It is how <see cref="ModelContext.ServeAsync(IContextRegistrar, string, string, System.Threading.CancellationToken)"/> takes tool classes without reflection.
/// </summary>
public interface IContextRegistrar
{
  /// <summary>Registers the class's tools, resources and prompts. Names that are already registered are replaced.</summary>
  /// <param name="server">The server to add them to.</param>
  void AddTo(ContextServer server);
}
