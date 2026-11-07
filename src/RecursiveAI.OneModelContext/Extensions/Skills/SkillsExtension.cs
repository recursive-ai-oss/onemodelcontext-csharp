using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Extensions.Skills;

/// <summary>The names the Skills extension defines (extension <c>io.modelcontextprotocol/skills</c>, Beta).</summary>
[Experimental(ExperimentalIds.Skills)]
public static class SkillsExtension
{
  /// <summary>The extension identifier.</summary>
  public const string Id = "io.modelcontextprotocol/skills";

  /// <summary><c>skills/list</c>: the skills a server publishes.</summary>
  public const string ListMethod = "skills/list";

  /// <summary><c>skills/get</c>: the entry of one skill, by the URI of its <c>SKILL.md</c>.</summary>
  public const string GetMethod = "skills/get";

  /// <summary><c>resources/directory/read</c>: the direct children of a directory resource.</summary>
  public const string DirectoryReadMethod = "resources/directory/read";

  /// <summary>The URI scheme skills are published under.</summary>
  public const string Scheme = "skill://";

  /// <summary>The media type of a directory resource.</summary>
  public const string DirectoryMimeType = "inode/directory";

  /// <summary>The file every skill has at its root.</summary>
  public const string SkillFileName = "SKILL.md";

  /// <summary>Most files in one skill (Skills extension, "Limits").</summary>
  public const int MaxFilesPerSkill = 512;

  /// <summary>Most bytes in all the files of one skill (Skills extension, "Limits"): 16 MiB.</summary>
  public const long MaxBytesPerSkill = 16L * 1024 * 1024;

  /// <summary>Longest skill name (Agent Skills specification).</summary>
  public const int MaxNameLength = 64;

  /// <summary>Longest skill description (Agent Skills specification).</summary>
  public const int MaxDescriptionLength = 1024;

  /// <summary>
  /// What a client may add to <see cref="ContextClientOptions.Extensions"/> to say it understands skills. The extension does not need it: a
  /// client finds out from the server's declaration, and servers here answer <c>skills/list</c>, <c>skills/get</c> and
  /// <c>resources/directory/read</c> for every caller on 2026-07-28.
  /// </summary>
  /// <returns>The declaration; the extension has no client settings.</returns>
  public static ClientExtension Client() => new(Id, default, official: true);

  /// <summary>The digest of a file's raw bytes in the form the extension uses: <c>sha256:</c> and 64 lowercase hexadecimal characters.</summary>
  /// <param name="content">The bytes.</param>
  public static string DigestOf(ReadOnlySpan<byte> content) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));
}

/// <summary>Settings of the Skills extension on a server. Every limit is a default you can change.</summary>
[Experimental(ExperimentalIds.Skills)]
public sealed class SkillsServerOptions
{
  /// <summary>
  /// Whether the server answers <c>resources/directory/read</c> and says so in its settings (<c>directoryRead</c>). Default <c>true</c>:
  /// every directory level of the published skills can be listed.
  /// </summary>
  public bool DirectoryRead { get; init; } = true;

  /// <summary>Entries per page of <c>skills/list</c> and children per page of a directory read. Default 100. Must be positive.</summary>
  public int PageSize { get; init; } = 100;

  /// <summary>How long clients may reuse <c>skills/get</c>, <c>skills/list</c> and directory results (<c>ttlMs</c>). Default the server's <see cref="ContextServerOptions.ListCacheTtl"/>.</summary>
  public TimeSpan? CacheTtl { get; init; }

  /// <summary>Who may cache those results. Default the server's <see cref="ContextServerOptions.ListCacheScope"/>. Skills are the same for every caller, so <c>public</c> is usually right.</summary>
  public string? CacheScope { get; init; }
}

/// <summary>Publishing skills from a <see cref="ContextServer"/>.</summary>
[Experimental(ExperimentalIds.Skills)]
public static class SkillsServerExtensions
{
  private static readonly ConditionalWeakTable<ContextServer, SkillsService> Services = new();

  /// <summary>
  /// Opts the server in to the Skills extension: it advertises <see cref="SkillsExtension.Id"/> on 2026-07-28 and answers the three requests
  /// to every caller. Earlier revisions see the skill files as plain resources. The server must publish at least one skill
  /// (<see cref="AddSkill"/>) so that it has the <c>resources</c> capability the extension depends on.
  /// </summary>
  /// <param name="server">The server.</param>
  /// <param name="options">Settings; <c>null</c> for the defaults.</param>
  /// <returns>The server.</returns>
  /// <exception cref="InvalidOperationException">The server already uses the extension.</exception>
  public static ContextServer UseSkills(this ContextServer server, SkillsServerOptions? options = null)
  {
    ArgumentNullException.ThrowIfNull(server);
    options ??= new SkillsServerOptions();
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.PageSize);
    var service = new SkillsService(server, options);
    lock (Services)
    {
      if (Services.TryGetValue(server, out _))
      {
        throw new InvalidOperationException("The server already uses the Skills extension.");
      }

      Services.Add(server, service);
    }

    server.Extensions.Add(service.CreateRegistration());
    return server;
  }

  /// <summary>
  /// Publishes a skill: every file becomes a resource under <c>skill://</c>, <c>skills/list</c> and <c>skills/get</c> describe it, and
  /// directory reads list its folders. The server opts in to the extension first if it has not (<see cref="UseSkills"/>).
  /// </summary>
  /// <param name="server">The server.</param>
  /// <param name="skill">The skill.</param>
  /// <returns>The server.</returns>
  /// <exception cref="InvalidOperationException">A skill with the same URI is already published, or a file path collides with a directory of another skill.</exception>
  public static ContextServer AddSkill(this ContextServer server, SkillDefinition skill)
  {
    ArgumentNullException.ThrowIfNull(server);
    ArgumentNullException.ThrowIfNull(skill);
    if (!Services.TryGetValue(server, out var service))
    {
      server.UseSkills();
      Services.TryGetValue(server, out service);
    }

    service!.Add(skill);
    return server;
  }
}

/// <summary>The skills of one server instance and the answers to the extension's requests.</summary>
internal sealed class SkillsService
{
  private readonly ContextServer _server;
  private readonly SkillsServerOptions _options;
  private readonly object _gate = new();

  // Keyed by the URI of SKILL.md so listing is in a stable order and paging can resume after a key.
  private readonly SortedDictionary<string, SkillDefinition> _skills = new(StringComparer.Ordinal);

  // Every file URI to the digest it was registered with (nested skills may share files), and every directory to its children by name.
  private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
  private readonly Dictionary<string, SortedDictionary<string, Resource>> _directories = new(StringComparer.Ordinal);

  internal SkillsService(ContextServer server, SkillsServerOptions options)
  {
    _server = server;
    _options = options;
  }

  internal ExtensionRegistration CreateRegistration()
  {
    var settings = JsonSerializer.SerializeToElement(
      new Dictionary<string, bool> { ["directoryRead"] = _options.DirectoryRead },
      SkillsJsonContext.Default.DictionaryStringBoolean);

    // Specified against 2026-07-28 only (Skills extension, "Protocol Revision").
    // The client learns of the extension from the server's declaration and sends no declaration of its own (the specification's examples
    // carry empty client capabilities), so the requests are answered for every caller on 2026-07-28.
    var registration = new ExtensionRegistration(SkillsExtension.Id, settings, [ProtocolVersions.V20260728], official: true)
    {
      AnswersUndeclaredPeers = true,
    }
      .Request(SkillsExtension.ListMethod, ListAsync)
      .Request(SkillsExtension.GetMethod, GetAsync);
    if (_options.DirectoryRead)
    {
      registration.Request(SkillsExtension.DirectoryReadMethod, ReadDirectoryAsync);
    }

    return registration;
  }

  internal void Add(SkillDefinition skill)
  {
    lock (_gate)
    {
      if (_skills.ContainsKey(skill.Uri))
      {
        throw new InvalidOperationException("The skill " + skill.Uri + " is already published.");
      }

      // Check every file against what is there before changing anything, so a refusal leaves the server as it was.
      var additions = new List<(SkillFile File, string Uri)>();
      foreach (var file in skill.Files)
      {
        var uri = SkillsExtension.Scheme + skill.Path + "/" + file.Path;
        if (_files.TryGetValue(uri, out var existing))
        {
          if (existing != file.Digest)
          {
            throw new InvalidOperationException("The file " + uri + " is already published with different content.");
          }

          continue;
        }

        if (_directories.ContainsKey(uri))
        {
          throw new InvalidOperationException("The file " + uri + " has the path of a directory of another skill.");
        }

        for (var parent = ParentOf(uri); parent is not null; parent = ParentOf(parent))
        {
          if (_files.ContainsKey(parent))
          {
            throw new InvalidOperationException("The directory " + parent + " has the path of a file of another skill.");
          }
        }

        additions.Add((file, uri));
      }

      foreach (var (file, uri) in additions)
      {
        Register(skill, file, uri);
      }

      _skills.Add(skill.Uri, skill);
    }
  }

  private void Register(SkillDefinition skill, SkillFile file, string uri)
  {
    var isSkillFile = file.Path == SkillsExtension.SkillFileName;
    var definition = new ResourceDefinition(uri, isSkillFile ? skill.Name : LastSegment(file.Path))
    {
      Description = isSkillFile ? skill.Description : null,
      MimeType = file.MimeType,
      Size = file.Content.Length,
    };
    _server.Resources.Add(definition, (requested, _) => ValueTask.FromResult(new ReadResourceResult
    {
      Contents = [file.IsText
        ? new TextResourceContents { Uri = requested, MimeType = file.MimeType, Text = new UTF8Encoding(false, true).GetString(file.Content) }
        : new BlobResourceContents { Uri = requested, MimeType = file.MimeType, Blob = Convert.ToBase64String(file.Content) }],
    }));

    _files[uri] = file.Digest;
    var child = definition.ToResource();
    var directory = ParentOf(uri);
    if (directory is null)
    {
      return;
    }

    DirectoryOf(directory)[LastSegment(uri)] = child;

    // Every level above the file is a directory resource, up to the first path segment (Skills extension, "Directory Resources").
    for (var current = directory; ParentOf(current) is { } above; current = above)
    {
      var name = LastSegment(current);
      var directoryResource = new Resource { Uri = current, Name = name, MimeType = SkillsExtension.DirectoryMimeType };
      var children = DirectoryOf(above);
      if (children.ContainsKey(name))
      {
        break;
      }

      children[name] = directoryResource;
    }
  }

  private SortedDictionary<string, Resource> DirectoryOf(string uri)
  {
    if (!_directories.TryGetValue(uri, out var children))
    {
      _directories[uri] = children = new SortedDictionary<string, Resource>(StringComparer.Ordinal);
    }

    return children;
  }

  // skill://a/b/c -> skill://a/b; skill://a -> null (the authority is the top).
  private static string? ParentOf(string uri)
  {
    var slash = uri.LastIndexOf('/');
    return slash > SkillsExtension.Scheme.Length - 1 && slash >= SkillsExtension.Scheme.Length ? uri[..slash] : null;
  }

  private static string LastSegment(string path) => path[(path.LastIndexOf('/') + 1)..];

  // ---- requests ----

  private ValueTask<JsonElement> ListAsync(JsonElement parameters, RequestContext context)
  {
    var cursor = StringParameter(parameters, "cursor");
    List<SkillDefinition> page;
    string? next = null;
    lock (_gate)
    {
      var all = new List<SkillDefinition>(_skills.Values);
      var start = 0;
      if (cursor is not null)
      {
        var after = DecodeCursor(cursor);
        while (start < all.Count && string.CompareOrdinal(all[start].Uri, after) <= 0)
        {
          start++;
        }
      }

      var count = Math.Min(_options.PageSize, all.Count - start);
      page = all.GetRange(start, count);
      if (start + count < all.Count)
      {
        next = EncodeCursor(page[^1].Uri);
      }
    }

    var buffer = new ArrayBufferWriter<byte>();
    using (var writer = new Utf8JsonWriter(buffer))
    {
      writer.WriteStartObject();
      writer.WriteString("resultType", WireNames.ResultTypeComplete);
      writer.WriteStartArray("skills");
      foreach (var skill in page)
      {
        WriteEntry(writer, skill);
      }

      writer.WriteEndArray();
      if (next is not null)
      {
        writer.WriteString("nextCursor", next);
      }

      WriteCaching(writer);
      writer.WriteEndObject();
    }

    return ValueTask.FromResult(Parse(buffer));
  }

  private ValueTask<JsonElement> GetAsync(JsonElement parameters, RequestContext context)
  {
    var uri = StringParameter(parameters, "uri") ?? throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "Invalid params: skills/get needs the uri of a SKILL.md.");
    SkillDefinition? skill;
    lock (_gate)
    {
      _skills.TryGetValue(uri, out skill);
    }

    // Skills extension, "Getting a Skill": a URI that is not a skill this server serves is -32602, like an unknown resource.
    if (skill is null)
    {
      throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "No skill is served at " + uri);
    }

    var buffer = new ArrayBufferWriter<byte>();
    using (var writer = new Utf8JsonWriter(buffer))
    {
      writer.WriteStartObject();
      writer.WriteString("resultType", WireNames.ResultTypeComplete);
      writer.WritePropertyName("skill");
      WriteEntry(writer, skill);
      WriteCaching(writer);
      writer.WriteEndObject();
    }

    return ValueTask.FromResult(Parse(buffer));
  }

  private ValueTask<JsonElement> ReadDirectoryAsync(JsonElement parameters, RequestContext context)
  {
    var uri = StringParameter(parameters, "uri") ?? throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "Invalid params: resources/directory/read needs the uri of a directory.");
    var cursor = StringParameter(parameters, "cursor");
    List<Resource> page;
    string? next = null;
    lock (_gate)
    {
      if (!_directories.TryGetValue(uri, out var children))
      {
        throw new ProtocolException(
          JsonRpcErrorCodes.InvalidParams,
          _files.ContainsKey(uri) ? uri + " is not a directory resource" : "No directory resource at " + uri);
      }

      var all = new List<KeyValuePair<string, Resource>>(children);
      var start = 0;
      if (cursor is not null)
      {
        var after = DecodeCursor(cursor);
        while (start < all.Count && string.CompareOrdinal(all[start].Key, after) <= 0)
        {
          start++;
        }
      }

      var count = Math.Min(_options.PageSize, all.Count - start);
      page = all.GetRange(start, count).ConvertAll(static pair => pair.Value);
      if (start + count < all.Count)
      {
        next = EncodeCursor(all[start + count - 1].Key);
      }
    }

    var buffer = new ArrayBufferWriter<byte>();
    using (var writer = new Utf8JsonWriter(buffer))
    {
      writer.WriteStartObject();
      writer.WriteString("resultType", WireNames.ResultTypeComplete);
      writer.WriteStartArray("resources");
      foreach (var resource in page)
      {
        JsonSerializer.Serialize(writer, resource, WireJsonContext.Default.Resource);
      }

      writer.WriteEndArray();
      if (next is not null)
      {
        writer.WriteString("nextCursor", next);
      }

      WriteCaching(writer);
      writer.WriteEndObject();
    }

    return ValueTask.FromResult(Parse(buffer));
  }

  private static void WriteEntry(Utf8JsonWriter writer, SkillDefinition skill)
  {
    writer.WriteStartObject();
    writer.WriteString("uri", skill.Uri);
    writer.WritePropertyName("frontmatter");
    skill.Frontmatter.WriteTo(writer);
    writer.WriteStartArray("resources");
    foreach (var file in skill.Files)
    {
      writer.WriteStartObject();
      writer.WriteString("uri", SkillsExtension.Scheme + skill.Path + "/" + file.Path);
      writer.WriteString("digest", file.Digest);
      writer.WriteNumber("size", file.Content.Length);
      writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteEndObject();
  }

  // Skills extension: ttlMs and cacheScope are required on all three results (CacheableResult).
  private void WriteCaching(Utf8JsonWriter writer)
  {
    var ttl = _options.CacheTtl ?? _server.CacheTtl;
    writer.WriteNumber("ttlMs", (long)ttl.TotalMilliseconds);
    writer.WriteString("cacheScope", _options.CacheScope ?? _server.Options.ListCacheScope);
  }

  private static string? StringParameter(JsonElement parameters, string name) =>
    parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
      ? value.GetString()
      : null;

  private static string EncodeCursor(string key) => Convert.ToBase64String(Encoding.UTF8.GetBytes(key)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

  private static string DecodeCursor(string cursor)
  {
    try
    {
      return Encoding.UTF8.GetString(Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/').PadRight((cursor.Length + 3) / 4 * 4, '=')));
    }
    catch (FormatException)
    {
      throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "Invalid params: the cursor is not valid.");
    }
  }

  private static JsonElement Parse(ArrayBufferWriter<byte> buffer)
  {
    using var document = JsonDocument.Parse(buffer.WrittenMemory);
    return document.RootElement.Clone();
  }
}
