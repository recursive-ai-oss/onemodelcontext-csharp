using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RecursiveAI.OneModelContext.Extensions.Skills;

/// <summary>One file of a skill: its path inside the skill directory and its bytes.</summary>
[Experimental(ExperimentalIds.Skills)]
public sealed class SkillFile
{
  internal SkillFile(string path, byte[] content)
  {
    Path = path;
    Content = content;
    Digest = SkillsExtension.DigestOf(content);
    MimeType = SkillMimeTypes.Of(path, content, out var isText);
    IsText = isText;
  }

  /// <summary>The path relative to the skill directory, with <c>/</c> as the separator.</summary>
  public string Path { get; }

  /// <summary>The file's raw bytes.</summary>
  public byte[] Content { get; }

  /// <summary>The digest of <see cref="Content"/>, <c>sha256:</c> and 64 lowercase hexadecimal characters.</summary>
  public string Digest { get; }

  /// <summary>The media type the resource is served with.</summary>
  public string MimeType { get; }

  internal bool IsText { get; }
}

/// <summary>
/// A skill to publish: a directory of files whose <c>SKILL.md</c> starts with YAML frontmatter that names the skill. The files are read once
/// when the definition is created, so the digests the server publishes describe exactly the bytes it serves.
/// </summary>
/// <remarks>
/// Skills extension, "Skill Format" and "Resource Mapping": the final segment of the skill path equals the frontmatter <c>name</c>, which
/// follows the Agent Skills naming rules (1 to 64 lowercase letters, digits and single hyphens, none at either end). Paths use the
/// characters <c>A-Z a-z 0-9 . _ ~ - + @ = ,</c> so they appear in a resource URI unchanged.
/// </remarks>
[Experimental(ExperimentalIds.Skills)]
public sealed class SkillDefinition
{
  private SkillDefinition(string path, JsonElement frontmatter, string name, string description, IReadOnlyList<SkillFile> files)
  {
    Path = path;
    Frontmatter = frontmatter;
    Name = name;
    Description = description;
    Files = files;
  }

  /// <summary>The skill path, for example <c>acme/billing/refunds</c>; its last segment is <see cref="Name"/>.</summary>
  public string Path { get; }

  /// <summary>The skill's name from its frontmatter.</summary>
  public string Name { get; }

  /// <summary>The skill's description from its frontmatter.</summary>
  public string Description { get; }

  /// <summary>The frontmatter as JSON, exactly what <c>skills/list</c> carries.</summary>
  public JsonElement Frontmatter { get; }

  /// <summary>Every file of the skill, <c>SKILL.md</c> included, in path order.</summary>
  public IReadOnlyList<SkillFile> Files { get; }

  /// <summary>The URI of the skill's <c>SKILL.md</c>.</summary>
  public string Uri => SkillsExtension.Scheme + Path + "/" + SkillsExtension.SkillFileName;

  /// <summary>
  /// Reads a skill from a directory on disk. Symbolic links are skipped, so a link cannot pull in a file from elsewhere.
  /// </summary>
  /// <param name="directory">The skill directory; it holds <c>SKILL.md</c>.</param>
  /// <param name="pathPrefix">An organizational prefix such as <c>acme/billing</c>, or <c>null</c> for none. The skill path is the prefix followed by the skill's name.</param>
  /// <returns>The definition.</returns>
  /// <exception cref="ArgumentException">The directory has no valid <c>SKILL.md</c>, a name or path is not allowed, or the skill exceeds 512 files or 16 MiB.</exception>
  /// <exception cref="IOException">A file could not be read.</exception>
  public static SkillDefinition FromDirectory(string directory, string? pathPrefix = null)
  {
    ArgumentException.ThrowIfNullOrEmpty(directory);
    var root = new DirectoryInfo(directory);
    if (!root.Exists)
    {
      throw new ArgumentException("The skill directory does not exist.", nameof(directory));
    }

    var found = new List<(string Relative, FileInfo File)>();
    Collect(root, string.Empty, found);
    if (found.Count > SkillsExtension.MaxFilesPerSkill)
    {
      throw new ArgumentException("A skill has at most " + SkillsExtension.MaxFilesPerSkill + " files.", nameof(directory));
    }

    long total = 0;
    foreach (var (_, file) in found)
    {
      total += file.Length;
      if (total > SkillsExtension.MaxBytesPerSkill)
      {
        throw new ArgumentException("A skill is at most " + SkillsExtension.MaxBytesPerSkill + " bytes in total.", nameof(directory));
      }
    }

    var files = new List<KeyValuePair<string, byte[]>>(found.Count);
    foreach (var (relative, file) in found)
    {
      files.Add(new KeyValuePair<string, byte[]>(relative, File.ReadAllBytes(file.FullName)));
    }

    var name = files.Find(static f => f.Key == SkillsExtension.SkillFileName).Value is { } skillMd
      ? NameOf(skillMd)
      : throw new ArgumentException("The directory has no SKILL.md.", nameof(directory));
    return FromFiles(string.IsNullOrEmpty(pathPrefix) ? name : pathPrefix.Trim('/') + "/" + name, files);
  }

  /// <summary>Builds a skill from files held in memory.</summary>
  /// <param name="skillPath">The skill path; its last segment must equal the <c>name</c> in <c>SKILL.md</c>.</param>
  /// <param name="files">Path relative to the skill directory (using <c>/</c>) and content. One of them is <c>SKILL.md</c>.</param>
  /// <returns>The definition.</returns>
  /// <exception cref="ArgumentException">A path is not allowed, <c>SKILL.md</c> is missing or its frontmatter is not valid, the name differs from the last path segment, or the skill exceeds 512 files or 16 MiB.</exception>
  public static SkillDefinition FromFiles(string skillPath, IEnumerable<KeyValuePair<string, byte[]>> files)
  {
    ArgumentNullException.ThrowIfNull(skillPath);
    ArgumentNullException.ThrowIfNull(files);
    var segments = skillPath.Split('/');
    foreach (var segment in segments)
    {
      CheckSegment(segment, "skill path");
    }

    var skillName = segments[^1];
    if (!IsValidName(skillName))
    {
      throw new ArgumentException("'" + skillName + "' is not a valid skill name: use 1 to 64 lowercase letters, digits and single hyphens, none at either end.", nameof(skillPath));
    }

    var list = new SortedDictionary<string, SkillFile>(StringComparer.Ordinal);
    long total = 0;
    foreach (var (path, content) in files)
    {
      ArgumentNullException.ThrowIfNull(content);
      foreach (var segment in path.Split('/'))
      {
        CheckSegment(segment, "file path");
      }

      if (list.ContainsKey(path))
      {
        throw new ArgumentException("The file '" + path + "' is listed twice.", nameof(files));
      }

      total += content.Length;
      if (list.Count >= SkillsExtension.MaxFilesPerSkill || total > SkillsExtension.MaxBytesPerSkill)
      {
        throw new ArgumentException("A skill has at most " + SkillsExtension.MaxFilesPerSkill + " files and " + SkillsExtension.MaxBytesPerSkill + " bytes.", nameof(files));
      }

      list.Add(path, new SkillFile(path, content));
    }

    if (!list.TryGetValue(SkillsExtension.SkillFileName, out var skillFile))
    {
      throw new ArgumentException("A skill has a SKILL.md at its root.", nameof(files));
    }

    JsonElement frontmatter;
    try
    {
      frontmatter = SkillYaml.ParseFrontmatter(skillFile.Content);
    }
    catch (FormatException ex)
    {
      throw new ArgumentException("SKILL.md: " + ex.Message, nameof(files), ex);
    }

    var (name, description) = Validate(frontmatter);
    if (!string.Equals(name, skillName, StringComparison.Ordinal))
    {
      throw new ArgumentException("The last segment of the skill path ('" + skillName + "') must equal the name in SKILL.md ('" + name + "').", nameof(skillPath));
    }

    return new SkillDefinition(skillPath, frontmatter, name, description, [.. list.Values]);
  }

  /// <summary>True when <paramref name="name"/> follows the Agent Skills naming rules.</summary>
  /// <param name="name">A skill name.</param>
  public static bool IsValidName(string name)
  {
    if (name.Length is 0 or > SkillsExtension.MaxNameLength || name[0] == '-' || name[^1] == '-')
    {
      return false;
    }

    var previousHyphen = false;
    foreach (var c in name)
    {
      var hyphen = c == '-';
      if (!(c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || hyphen) || (hyphen && previousHyphen))
      {
        return false;
      }

      previousHyphen = hyphen;
    }

    return true;
  }

  private static (string Name, string Description) Validate(JsonElement frontmatter)
  {
    if (frontmatter.ValueKind != JsonValueKind.Object
      || !frontmatter.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
      || !frontmatter.TryGetProperty("description", out var description) || description.ValueKind != JsonValueKind.String)
    {
      throw new ArgumentException("The frontmatter of SKILL.md needs a 'name' and a 'description', both text.");
    }

    var descriptionText = description.GetString()!;
    if (descriptionText.Length is 0 or > SkillsExtension.MaxDescriptionLength)
    {
      throw new ArgumentException("The 'description' in SKILL.md is 1 to " + SkillsExtension.MaxDescriptionLength + " characters.");
    }

    return (name.GetString()!, descriptionText);
  }

  private static string NameOf(byte[] skillMd)
  {
    try
    {
      var frontmatter = SkillYaml.ParseFrontmatter(skillMd);
      return Validate(frontmatter).Name;
    }
    catch (FormatException ex)
    {
      throw new ArgumentException("SKILL.md: " + ex.Message, ex);
    }
  }

  private static void Collect(DirectoryInfo directory, string prefix, List<(string, FileInfo)> found)
  {
    foreach (var entry in directory.EnumerateFileSystemInfos().OrderBy(static e => e.Name, StringComparer.Ordinal))
    {
      if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
      {
        continue;
      }

      var relative = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
      if (entry is DirectoryInfo child)
      {
        Collect(child, relative, found);
        if (found.Count > SkillsExtension.MaxFilesPerSkill)
        {
          return;
        }
      }
      else if (entry is FileInfo file)
      {
        found.Add((relative, file));
        if (found.Count > SkillsExtension.MaxFilesPerSkill)
        {
          return;
        }
      }
    }
  }

  // Resource Mapping: the first segment sits in the URI authority (a reg-name, RFC 3986), the others are path segments. Keeping every
  // segment to unreserved characters means the URI needs no escaping and compares byte for byte.
  private static void CheckSegment(string segment, string what)
  {
    if (segment.Length == 0 || segment is "." or "..")
    {
      throw new ArgumentException("A " + what + " has no empty, '.' or '..' segments.");
    }

    foreach (var c in segment)
    {
      if (!(c is >= 'a' and <= 'z' || c is >= 'A' and <= 'Z' || c is >= '0' and <= '9' || c is '.' or '_' or '~' or '-' or '+' or '@' or '=' or ','))
      {
        throw new ArgumentException("The " + what + " segment '" + segment + "' uses a character outside A-Z a-z 0-9 . _ ~ - + @ = ,.");
      }
    }
  }
}

internal static class SkillMimeTypes
{
  private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
  {
    [".md"] = "text/markdown",
    [".markdown"] = "text/markdown",
    [".txt"] = "text/plain",
    [".json"] = "application/json",
    [".yaml"] = "application/yaml",
    [".yml"] = "application/yaml",
    [".xml"] = "application/xml",
    [".html"] = "text/html",
    [".css"] = "text/css",
    [".csv"] = "text/csv",
    [".js"] = "text/javascript",
    [".ts"] = "text/plain",
    [".py"] = "text/x-python",
    [".sh"] = "text/x-shellscript",
    [".svg"] = "image/svg+xml",
    [".png"] = "image/png",
    [".jpg"] = "image/jpeg",
    [".jpeg"] = "image/jpeg",
    [".gif"] = "image/gif",
    [".webp"] = "image/webp",
    [".pdf"] = "application/pdf",
    [".zip"] = "application/zip",
  };

  private static readonly UTF8Encoding Strict = new(false, true);

  // The resource carries text when the bytes are valid UTF-8 and the type is a text type, so the digest (over the raw bytes) matches what
  // a client rebuilds from the string. Everything else travels as base64.
  internal static string Of(string path, byte[] content, out bool isText)
  {
    var dot = path.LastIndexOf('.');
    var slash = path.LastIndexOf('/');
    var extension = dot > slash ? path[dot..] : string.Empty;
    var mime = Known.TryGetValue(extension, out var known) ? known : null;
    var textual = mime is null
      ? LooksLikeText(content)
      : mime.StartsWith("text/", StringComparison.Ordinal) || mime is "application/json" or "application/yaml" or "application/xml" or "image/svg+xml";
    isText = textual && IsUtf8(content);
    return mime ?? (isText ? "text/plain" : "application/octet-stream");
  }

  private static bool IsUtf8(byte[] content)
  {
    try
    {
      _ = Strict.GetCharCount(content);
      return true;
    }
    catch (ArgumentException)
    {
      return false;
    }
  }

  private static bool LooksLikeText(byte[] content)
  {
    foreach (var b in content)
    {
      if (b < 0x20 && b is not ((byte)'\t' or (byte)'\n' or (byte)'\r'))
      {
        return false;
      }
    }

    return IsUtf8(content);
  }
}
