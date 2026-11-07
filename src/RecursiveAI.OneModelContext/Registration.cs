using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext;

/// <summary>Runs a tool. Throw <see cref="ToolException"/> to return a tool failure; throw <see cref="ProtocolException"/> for a bad call.</summary>
/// <param name="arguments">The <c>arguments</c> object, or an empty object when the call had none.</param>
/// <param name="context">The request.</param>
public delegate ValueTask<CallToolResult> ToolHandler(JsonElement arguments, RequestContext context);

/// <summary>Reads a resource.</summary>
/// <param name="uri">The URI being read, which matched a resource or a template.</param>
/// <param name="context">The request.</param>
public delegate ValueTask<ReadResourceResult> ResourceHandler(string uri, RequestContext context);

/// <summary>Builds a prompt.</summary>
/// <param name="arguments">The argument values; empty when the call had none.</param>
/// <param name="context">The request.</param>
public delegate ValueTask<GetPromptResult> PromptHandler(IReadOnlyDictionary<string, string> arguments, RequestContext context);

/// <summary>Completes a prompt or template argument.</summary>
/// <param name="request">What is being completed.</param>
/// <param name="context">The request.</param>
public delegate ValueTask<CompleteResult> CompletionHandler(CompleteRequestParams request, RequestContext context);

/// <summary>A tool: its listing and schema. The handler is registered beside it.</summary>
public sealed class ToolDefinition
{
  /// <summary>Creates a tool definition.</summary>
  /// <param name="name">The programmatic name; unique on the server.</param>
  /// <param name="description">What the tool does, written for a model.</param>
  /// <param name="inputSchema">A JSON Schema object schema for the arguments (<c>"type": "object"</c>). The element is used as is.</param>
  /// <exception cref="ArgumentException">The name is empty or the schema is not a JSON object.</exception>
  public ToolDefinition(string name, string? description, JsonElement inputSchema)
  {
    ArgumentException.ThrowIfNullOrEmpty(name);
    if (inputSchema.ValueKind != JsonValueKind.Object)
    {
      throw new ArgumentException("A tool's input schema must be a JSON object.", nameof(inputSchema));
    }

    Name = name;
    Description = description;
    InputSchema = inputSchema;
  }

  /// <summary>Creates a tool definition from a parsed schema document. The document stays owned by the caller and must outlive the tool.</summary>
  /// <param name="name">The programmatic name; unique on the server.</param>
  /// <param name="description">What the tool does, written for a model.</param>
  /// <param name="inputSchema">The document holding the schema.</param>
  public ToolDefinition(string name, string? description, JsonDocument inputSchema)
    : this(name, description, (inputSchema ?? throw new ArgumentNullException(nameof(inputSchema))).RootElement)
  {
  }

  /// <summary>The programmatic name.</summary>
  public string Name { get; }

  /// <summary>What the tool does.</summary>
  public string? Description { get; }

  /// <summary>The JSON Schema for the arguments.</summary>
  public JsonElement InputSchema { get; }

  /// <summary>A display name.</summary>
  public string? Title { get; init; }

  /// <summary>A JSON Schema for <c>structuredContent</c>; <c>default</c> for none.</summary>
  public JsonElement OutputSchema { get; init; }

  /// <summary>Behaviour hints.</summary>
  public ToolAnnotations? Annotations { get; init; }

  /// <summary>Icons.</summary>
  public IReadOnlyList<Icon>? Icons { get; init; }

  /// <summary>The <c>_meta</c> of the listing.</summary>
  public MetaObject? Meta { get; init; }

  /// <summary>The listing entry for this tool.</summary>
  public Tool ToTool() => new()
  {
    Name = Name,
    Title = Title,
    Description = Description,
    InputSchema = InputSchema,
    OutputSchema = OutputSchema,
    Annotations = Annotations,
    Icons = Icons is null ? null : [.. Icons],
    Meta = Meta,
  };
}

/// <summary>A resource: its listing. The handler is registered beside it.</summary>
/// <param name="Uri">The URI.</param>
/// <param name="Name">The programmatic name.</param>
public sealed record ResourceDefinition(string Uri, string Name)
{
  /// <summary>A display name.</summary>
  public string? Title { get; init; }

  /// <summary>What it is.</summary>
  public string? Description { get; init; }

  /// <summary>The media type.</summary>
  public string? MimeType { get; init; }

  /// <summary>Size in bytes.</summary>
  public long? Size { get; init; }

  /// <summary>Hints.</summary>
  public Annotations? Annotations { get; init; }

  /// <summary>The listing entry.</summary>
  public Resource ToResource() => new() { Uri = Uri, Name = Name, Title = Title, Description = Description, MimeType = MimeType, Size = Size, Annotations = Annotations };
}

/// <summary>A resource template: its listing. Matching supports <c>{name}</c> and <c>{+name}</c> expressions (RFC 6570 levels 1 and 2 for a single variable).</summary>
/// <param name="UriTemplate">The URI template.</param>
/// <param name="Name">The programmatic name.</param>
public sealed record ResourceTemplateDefinition(string UriTemplate, string Name)
{
  /// <summary>A display name.</summary>
  public string? Title { get; init; }

  /// <summary>What it is.</summary>
  public string? Description { get; init; }

  /// <summary>The media type.</summary>
  public string? MimeType { get; init; }

  /// <summary>Hints.</summary>
  public Annotations? Annotations { get; init; }

  /// <summary>The listing entry.</summary>
  public ResourceTemplate ToResourceTemplate() => new() { UriTemplate = UriTemplate, Name = Name, Title = Title, Description = Description, MimeType = MimeType, Annotations = Annotations };
}

/// <summary>A prompt: its listing. The handler is registered beside it.</summary>
/// <param name="Name">The programmatic name.</param>
/// <param name="Description">What it does.</param>
public sealed record PromptDefinition(string Name, string? Description = null)
{
  /// <summary>A display name.</summary>
  public string? Title { get; init; }

  /// <summary>The arguments it takes.</summary>
  public IReadOnlyList<PromptArgument>? Arguments { get; init; }

  /// <summary>The listing entry.</summary>
  public Prompt ToPrompt() => new() { Name = Name, Title = Title, Description = Description, Arguments = Arguments is null ? null : [.. Arguments] };
}

/// <summary>
/// A set of definitions with their handlers that can change while the server runs. Reads see a consistent snapshot and never block
/// writers for long; the snapshot is ordered by key (ordinal), so listings are deterministic.
/// </summary>
/// <typeparam name="TDefinition">The definition type.</typeparam>
/// <typeparam name="THandler">The handler delegate type.</typeparam>
public abstract class DefinitionRegistry<TDefinition, THandler>
  where TDefinition : class
  where THandler : Delegate
{
  private readonly object _gate = new();
  private Entry[] _entries = [];

  private protected DefinitionRegistry()
  {
  }

  /// <summary>Raised after an add or remove, on the calling thread, outside any lock.</summary>
  public event EventHandler? Changed;

  /// <summary>How many definitions are registered.</summary>
  public int Count => _entries.Length;

  /// <summary>A snapshot of the definitions in key order.</summary>
  public IReadOnlyList<TDefinition> Definitions
  {
    get
    {
      var entries = _entries;
      var list = new TDefinition[entries.Length];
      for (var i = 0; i < entries.Length; i++)
      {
        list[i] = entries[i].Definition;
      }

      return list;
    }
  }

  /// <summary>Registers a definition, replacing any with the same key.</summary>
  /// <param name="definition">The definition.</param>
  /// <param name="handler">What runs when it is used.</param>
  public void Add(TDefinition definition, THandler handler)
  {
    ArgumentNullException.ThrowIfNull(definition);
    ArgumentNullException.ThrowIfNull(handler);
    var key = KeyOf(definition);
    lock (_gate)
    {
      var list = new List<Entry>(_entries);
      list.RemoveAll(e => string.Equals(e.Key, key, StringComparison.Ordinal));
      list.Add(new Entry(key, definition, handler));
      list.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
      _entries = [.. list];
    }

    Changed?.Invoke(this, EventArgs.Empty);
  }

  /// <summary>Removes a definition.</summary>
  /// <param name="key">The name (tools, prompts) or URI (resources, templates).</param>
  /// <returns>True when it was registered.</returns>
  public bool Remove(string key)
  {
    ArgumentNullException.ThrowIfNull(key);
    lock (_gate)
    {
      var list = new List<Entry>(_entries);
      if (list.RemoveAll(e => string.Equals(e.Key, key, StringComparison.Ordinal)) == 0)
      {
        return false;
      }

      _entries = [.. list];
    }

    Changed?.Invoke(this, EventArgs.Empty);
    return true;
  }

  /// <summary>Finds a registration.</summary>
  /// <param name="key">The name or URI.</param>
  /// <param name="definition">The definition.</param>
  /// <param name="handler">The handler.</param>
  public bool TryGet(string key, out TDefinition definition, out THandler handler)
  {
    foreach (var entry in _entries)
    {
      if (string.Equals(entry.Key, key, StringComparison.Ordinal))
      {
        definition = entry.Definition;
        handler = entry.Handler;
        return true;
      }
    }

    definition = null!;
    handler = null!;
    return false;
  }

  internal IReadOnlyList<(TDefinition Definition, THandler Handler)> Snapshot()
  {
    var entries = _entries;
    var list = new (TDefinition, THandler)[entries.Length];
    for (var i = 0; i < entries.Length; i++)
    {
      list[i] = (entries[i].Definition, entries[i].Handler);
    }

    return list;
  }

  private protected abstract string KeyOf(TDefinition definition);

  private sealed record Entry(string Key, TDefinition Definition, THandler Handler);
}

/// <summary>The tools of a server, keyed by name.</summary>
public sealed class ToolRegistry : DefinitionRegistry<ToolDefinition, ToolHandler>
{
  internal ToolRegistry()
  {
  }

  private protected override string KeyOf(ToolDefinition definition) => definition.Name;
}

/// <summary>The resources of a server, keyed by URI.</summary>
public sealed class ResourceRegistry : DefinitionRegistry<ResourceDefinition, ResourceHandler>
{
  internal ResourceRegistry()
  {
  }

  private protected override string KeyOf(ResourceDefinition definition) => definition.Uri;
}

/// <summary>The resource templates of a server, keyed by URI template.</summary>
public sealed class ResourceTemplateRegistry : DefinitionRegistry<ResourceTemplateDefinition, ResourceHandler>
{
  internal ResourceTemplateRegistry()
  {
  }

  private protected override string KeyOf(ResourceTemplateDefinition definition) => definition.UriTemplate;
}

/// <summary>The prompts of a server, keyed by name.</summary>
public sealed class PromptRegistry : DefinitionRegistry<PromptDefinition, PromptHandler>
{
  internal PromptRegistry()
  {
  }

  private protected override string KeyOf(PromptDefinition definition) => definition.Name;
}

/// <summary>Matches a URI against a URI template made of literals and <c>{name}</c> / <c>{+name}</c> expressions.</summary>
public static class UriTemplateMatcher
{
  private static readonly System.Buffers.SearchValues<char> OperatorCharacters = System.Buffers.SearchValues.Create("#./;?&=,!@|");

  /// <summary>Matches <paramref name="uri"/> against <paramref name="template"/>.</summary>
  /// <param name="template">The template, e.g. <c>file:///{path}</c>. Operators other than <c>+</c> never match.</param>
  /// <param name="uri">The URI.</param>
  /// <param name="variables">The variable values, percent-decoded, when it matched.</param>
  /// <returns>True when the whole URI matched. Each <c>{name}</c> captures at least one character and no <c>/</c>, <c>?</c> or <c>#</c>; <c>{+name}</c> captures any.</returns>
  public static bool TryMatch(string template, string uri, out Dictionary<string, string> variables)
  {
    ArgumentNullException.ThrowIfNull(template);
    ArgumentNullException.ThrowIfNull(uri);
    variables = new Dictionary<string, string>(StringComparer.Ordinal);
    var parts = Parse(template);
    if (parts is null)
    {
      return false;
    }

    return Match(parts, 0, uri, 0, variables);
  }

  private static List<(string Text, bool IsVariable, bool Reserved)>? Parse(string template)
  {
    var parts = new List<(string, bool, bool)>();
    var i = 0;
    while (i < template.Length)
    {
      var open = template.IndexOf('{', i);
      if (open < 0)
      {
        parts.Add((template[i..], false, false));
        break;
      }

      if (open > i)
      {
        parts.Add((template[i..open], false, false));
      }

      var close = template.IndexOf('}', open);
      if (close < 0)
      {
        return null;
      }

      var name = template[(open + 1)..close];
      var reserved = name.StartsWith('+');
      if (reserved)
      {
        name = name[1..];
      }

      if (name.Length == 0 || name.AsSpan().IndexOfAny(OperatorCharacters) >= 0)
      {
        return null;
      }

      parts.Add((name, true, reserved));
      i = close + 1;
    }

    return parts;
  }

  // Backtracking over where each variable ends; templates are short and the URI is bounded by the request limit.
  private static bool Match(List<(string Text, bool IsVariable, bool Reserved)> parts, int index, string uri, int position, Dictionary<string, string> variables)
  {
    if (index == parts.Count)
    {
      return position == uri.Length;
    }

    var (text, isVariable, reserved) = parts[index];
    if (!isVariable)
    {
      return uri.AsSpan(position).StartsWith(text, StringComparison.Ordinal) && Match(parts, index + 1, uri, position + text.Length, variables);
    }

    for (var end = position + 1; end <= uri.Length; end++)
    {
      var last = uri[end - 1];
      if (!reserved && last is '/' or '?' or '#')
      {
        return false;
      }

      if (Match(parts, index + 1, uri, end, variables))
      {
        variables[text] = Uri.UnescapeDataString(uri[position..end]);
        return true;
      }
    }

    return false;
  }
}
