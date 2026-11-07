using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Extensions.Skills;

/// <summary>Why a skill's content was refused (Skills extension, "Integrity and Verification").</summary>
[Experimental(ExperimentalIds.Skills)]
public enum SkillVerificationFailure
{
  /// <summary>The entry is not one a host may load: <c>resources</c> is missing or neither an array nor <c>"dynamic"</c>, or a field is malformed.</summary>
  InvalidEntry,

  /// <summary>The skill is over 512 files or 16 MiB, so no conforming host is required to load it.</summary>
  LimitExceeded,

  /// <summary>The SHA-256 of the bytes read differs from the entry's digest.</summary>
  DigestMismatch,

  /// <summary>The number of bytes read differs from the entry's <c>size</c>.</summary>
  SizeMismatch,

  /// <summary>The frontmatter of the fetched <c>SKILL.md</c> differs from the entry's <c>frontmatter</c>, or cannot be read.</summary>
  FrontmatterMismatch,

  /// <summary>The file is not one the entry lists.</summary>
  UnlistedFile,

  /// <summary>The file is more than the 16 MiB a skill may hold, read from a skill that publishes no sizes.</summary>
  TooLarge,
}

/// <summary>
/// The content of a skill is not what its entry described. The content is not returned. To recover, fetch the entry again
/// (<see cref="SkillsClientExtensions.GetSkillAsync"/>); a changed file set means any approval the host stored for the skill is void.
/// </summary>
[Experimental(ExperimentalIds.Skills)]
public sealed class SkillVerificationException : ContextException
{
  internal SkillVerificationException(SkillVerificationFailure failure, string uri, string message)
    : base(message)
  {
    Failure = failure;
    Uri = uri;
  }

  /// <summary>What did not match.</summary>
  public SkillVerificationFailure Failure { get; }

  /// <summary>The URI of the file or skill concerned.</summary>
  public string Uri { get; }
}

/// <summary>A file of a skill as its entry lists it.</summary>
/// <param name="Uri">The resource URI.</param>
/// <param name="Digest">The SHA-256 of the file's bytes as <c>sha256:</c> and lowercase hexadecimal.</param>
/// <param name="Size">The length of the file in bytes.</param>
[Experimental(ExperimentalIds.Skills)]
public sealed record SkillResourceEntry(string Uri, string Digest, long Size);

/// <summary>
/// A skill as a server describes it (a <c>Skill</c> entry of <c>skills/list</c> or <c>skills/get</c>). A host keeps the entry it
/// approved and loaded from; reads of the skill's files are checked against it.
/// </summary>
[Experimental(ExperimentalIds.Skills)]
public sealed class SkillEntry
{
  internal SkillEntry(string uri, JsonElement frontmatter, IReadOnlyList<SkillResourceEntry>? resources)
  {
    Uri = uri;
    Frontmatter = frontmatter;
    Resources = resources;
  }

  /// <summary>The resource URI of the skill's <c>SKILL.md</c>.</summary>
  public string Uri { get; }

  /// <summary>The skill's frontmatter exactly as the server sent it. <c>name</c> and <c>description</c> are always there.</summary>
  public JsonElement Frontmatter { get; }

  /// <summary>The skill's name; the last segment of the URI's skill path.</summary>
  public string Name => Frontmatter.GetProperty("name").GetString()!;

  /// <summary>The skill's description.</summary>
  public string Description => Frontmatter.GetProperty("description").GetString()!;

  /// <summary>
  /// Every file of the skill with its digest and size, or <c>null</c> when the server marked the skill <c>"dynamic"</c>: its content is
  /// generated, offers no integrity check, and a stored approval cannot be tied to it.
  /// </summary>
  public IReadOnlyList<SkillResourceEntry>? Resources { get; }

  /// <summary>True when the server publishes no digests for this skill.</summary>
  public bool IsDynamic => Resources is null;

  /// <summary>
  /// The skill's root, the URI of its <c>SKILL.md</c> without <c>/SKILL.md</c>. Relative references inside the skill resolve against it, and
  /// it is the directory resource to read when the server supports directory reads.
  /// </summary>
  public string RootUri => Uri[..^(SkillsExtension.SkillFileName.Length + 1)];
}

/// <summary>A skill whose <c>SKILL.md</c> has been fetched and verified against its entry.</summary>
[Experimental(ExperimentalIds.Skills)]
public sealed class LoadedSkill
{
  internal LoadedSkill(SkillEntry entry, byte[] skillMd)
  {
    Entry = entry;
    SkillMd = skillMd;
  }

  /// <summary>The entry the skill was loaded from; later file reads are checked against it.</summary>
  public SkillEntry Entry { get; }

  /// <summary>The bytes of <c>SKILL.md</c>, verified.</summary>
  public byte[] SkillMd { get; }

  /// <summary>The text of <c>SKILL.md</c>: frontmatter and instructions.</summary>
  public string Text => new UTF8Encoding(false, true).GetString(SkillMd);
}

/// <summary>Reading skills from a server that offers the Skills extension.</summary>
/// <remarks>
/// Nothing is fetched ahead of need: listing returns entries only, <see cref="LoadSkillAsync"/> reads one <c>SKILL.md</c>, and a supporting
/// file is read when asked for. Content is checked against the entry (size, digest, and for <c>SKILL.md</c> the frontmatter) before it is
/// returned. Digests come from the same server as the content, so a match shows consistency, not trustworthiness.
/// </remarks>
[Experimental(ExperimentalIds.Skills)]
public static class SkillsClientExtensions
{
  // A listing is paged; this bound stops a server that never ends it.
  private const int MaxPages = 1000;

  /// <summary>Lists the skills the server publishes. A listing may be partial or empty; <see cref="GetSkillAsync"/> works for a skill that is not in it.</summary>
  /// <param name="client">The client; it declared <see cref="SkillsExtension.Client"/>.</param>
  /// <param name="cancellationToken">Cancels the calls.</param>
  /// <returns>The entries a host may load. An entry the extension calls invalid (no <c>resources</c>) is left out.</returns>
  /// <exception cref="InvalidOperationException">The server does not offer the extension.</exception>
  public static async ValueTask<IReadOnlyList<SkillEntry>> ListSkillsAsync(this ContextClient client, CancellationToken cancellationToken = default)
  {
    RequireServerSupport(client);
    var entries = new List<SkillEntry>();
    string? cursor = null;
    for (var page = 0; page < MaxPages; page++)
    {
      var parameters = cursor is null ? default : Parameters(("cursor", cursor));
      var answer = await client.CallExtensionAsync(SkillsExtension.ListMethod, parameters, cancellationToken).ConfigureAwait(false);
      if (answer.TryGetProperty("skills", out var skills) && skills.ValueKind == JsonValueKind.Array)
      {
        foreach (var element in skills.EnumerateArray())
        {
          if (TryParseEntry(element, out var entry, out _))
          {
            entries.Add(entry);
          }
        }
      }

      cursor = answer.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
      if (string.IsNullOrEmpty(cursor))
      {
        return entries;
      }
    }

    throw new ContextException("The server's skill listing has more than " + MaxPages + " pages.");
  }

  /// <summary>Fetches the entry of one skill by the URI of its <c>SKILL.md</c>, whether or not the server lists it.</summary>
  /// <param name="client">The client.</param>
  /// <param name="uri">The URI of the skill's <c>SKILL.md</c>.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  /// <returns>The entry as of now.</returns>
  /// <exception cref="InvalidOperationException">The server does not offer the extension.</exception>
  /// <exception cref="ProtocolException">The server does not serve a skill at the URI (-32602).</exception>
  /// <exception cref="SkillVerificationException">The server's entry is not valid.</exception>
  public static async ValueTask<SkillEntry> GetSkillAsync(this ContextClient client, string uri, CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrEmpty(uri);
    RequireServerSupport(client);
    var answer = await client.CallExtensionAsync(SkillsExtension.GetMethod, Parameters(("uri", uri)), cancellationToken).ConfigureAwait(false);
    string? problem = null;
    SkillEntry? entry = null;
    if (!answer.TryGetProperty("skill", out var element) || !TryParseEntry(element, out entry, out problem))
    {
      throw new SkillVerificationException(SkillVerificationFailure.InvalidEntry, uri, problem ?? "The server's answer has no skill entry.");
    }

    return entry;
  }

  /// <summary>Reads a skill's <c>SKILL.md</c> and verifies it against the entry: size, digest and frontmatter.</summary>
  /// <param name="client">The client.</param>
  /// <param name="entry">The entry to load; the file set it lists binds later reads.</param>
  /// <param name="cancellationToken">Cancels the read.</param>
  /// <returns>The verified skill.</returns>
  /// <exception cref="SkillVerificationException">The skill is over the limits, or the content does not match the entry.</exception>
  public static async ValueTask<LoadedSkill> LoadSkillAsync(this ContextClient client, SkillEntry entry, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(entry);
    CheckLimits(entry);
    var bytes = await ReadVerifiedAsync(client, entry, entry.Uri, cancellationToken).ConfigureAwait(false);

    // Skills extension, "Frontmatter Verification": compare the fetched file's frontmatter with the entry's, field by field.
    JsonElement fetched;
    try
    {
      fetched = SkillYaml.ParseFrontmatter(bytes);
    }
    catch (FormatException ex)
    {
      throw new SkillVerificationException(SkillVerificationFailure.FrontmatterMismatch, entry.Uri, "The frontmatter of the fetched SKILL.md cannot be read: " + ex.Message);
    }

    if (!JsonEquals(fetched, entry.Frontmatter))
    {
      throw new SkillVerificationException(SkillVerificationFailure.FrontmatterMismatch, entry.Uri, "The frontmatter of the fetched SKILL.md differs from the entry's frontmatter.");
    }

    return new LoadedSkill(entry, bytes);
  }

  /// <summary>Reads one file of a skill and verifies it against the entry.</summary>
  /// <param name="client">The client.</param>
  /// <param name="entry">The entry the host holds for the skill.</param>
  /// <param name="file">The file's URI, or its path relative to the skill root (<c>references/GUIDE.md</c>).</param>
  /// <param name="cancellationToken">Cancels the read.</param>
  /// <returns>The file's bytes, verified.</returns>
  /// <exception cref="SkillVerificationException">The file is not listed in the entry, or its size or digest differs from the entry's.</exception>
  public static ValueTask<byte[]> ReadSkillFileAsync(this ContextClient client, SkillEntry entry, string file, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentException.ThrowIfNullOrEmpty(file);
    return ReadVerifiedAsync(client, entry, ResolveReference(entry, file), cancellationToken);
  }

  /// <summary>Lists the direct children of a directory resource (<c>resources/directory/read</c>), following pages.</summary>
  /// <param name="client">The client.</param>
  /// <param name="directoryUri">The directory's URI without a trailing slash, for example a skill's <see cref="SkillEntry.RootUri"/>.</param>
  /// <param name="cancellationToken">Cancels the calls.</param>
  /// <returns>The children. A directory read is a live view; it does not extend the entry's file list, and files the entry does not list are not readable under it.</returns>
  /// <exception cref="InvalidOperationException">The server did not declare <c>directoryRead</c>.</exception>
  /// <exception cref="ProtocolException">The URI is not a directory resource (-32602).</exception>
  public static async ValueTask<IReadOnlyList<Resource>> ReadSkillDirectoryAsync(this ContextClient client, string directoryUri, CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrEmpty(directoryUri);
    RequireServerSupport(client);

    // Skills extension, "Capability Negotiation": clients MUST NOT call resources/directory/read unless directoryRead is true.
    if (!client.ServerExtensions[SkillsExtension.Id].TryGetProperty("directoryRead", out var flag) || flag.ValueKind != JsonValueKind.True)
    {
      throw new InvalidOperationException("The server did not declare directoryRead for the Skills extension.");
    }

    var children = new List<Resource>();
    string? cursor = null;
    for (var page = 0; page < MaxPages; page++)
    {
      var parameters = cursor is null ? Parameters(("uri", directoryUri)) : Parameters(("uri", directoryUri), ("cursor", cursor));
      var answer = await client.CallExtensionAsync(SkillsExtension.DirectoryReadMethod, parameters, cancellationToken).ConfigureAwait(false);
      if (answer.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array)
      {
        foreach (var element in resources.EnumerateArray())
        {
          if (element.Deserialize(WireJsonContext.Default.Resource) is { } resource)
          {
            children.Add(resource);
          }
        }
      }

      cursor = answer.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
      if (string.IsNullOrEmpty(cursor))
      {
        return children;
      }
    }

    throw new ContextException("The server's directory listing has more than " + MaxPages + " pages.");
  }

  // ---- internals ----

  private static void RequireServerSupport(ContextClient client)
  {
    ArgumentNullException.ThrowIfNull(client);
    if (!client.ServerSupportsExtension(SkillsExtension.Id))
    {
      throw new InvalidOperationException("The server does not offer the Skills extension (" + SkillsExtension.Id + "), or the client did not declare it.");
    }
  }

  private static JsonElement Parameters(params (string Name, string Value)[] values)
  {
    var map = new Dictionary<string, string>(values.Length, StringComparer.Ordinal);
    foreach (var (name, value) in values)
    {
      map[name] = value;
    }

    return JsonSerializer.SerializeToElement(map, WireJsonContext.Default.DictionaryStringString);
  }

  private static void CheckLimits(SkillEntry entry)
  {
    if (entry.Resources is not { } resources)
    {
      return;
    }

    long total = 0;
    foreach (var resource in resources)
    {
      total += resource.Size;
    }

    if (resources.Count > SkillsExtension.MaxFilesPerSkill || total > SkillsExtension.MaxBytesPerSkill)
    {
      throw new SkillVerificationException(
        SkillVerificationFailure.LimitExceeded,
        entry.Uri,
        "The skill lists " + resources.Count + " files and " + total + " bytes; the limits are " + SkillsExtension.MaxFilesPerSkill + " files and " + SkillsExtension.MaxBytesPerSkill + " bytes.");
    }
  }

  // Skills extension, "Relative References": resolve against the skill's root like a filesystem path; nothing may climb out of it.
  private static string ResolveReference(SkillEntry entry, string file)
  {
    if (file.Contains("://", StringComparison.Ordinal))
    {
      return file;
    }

    foreach (var segment in file.Split('/'))
    {
      if (segment is "" or "." or "..")
      {
        throw new SkillVerificationException(SkillVerificationFailure.UnlistedFile, file, "'" + file + "' is not a path inside the skill.");
      }
    }

    return entry.RootUri + "/" + file;
  }

  private static async ValueTask<byte[]> ReadVerifiedAsync(ContextClient client, SkillEntry entry, string uri, CancellationToken cancellationToken)
  {
    SkillResourceEntry? listed = null;
    if (entry.Resources is { } resources)
    {
      // "The Acting Window and the Held Entry": a file the held entry does not list is a verification failure, and no request is made.
      foreach (var resource in resources)
      {
        if (string.Equals(resource.Uri, uri, StringComparison.Ordinal))
        {
          listed = resource;
          break;
        }
      }

      if (listed is null)
      {
        throw new SkillVerificationException(SkillVerificationFailure.UnlistedFile, uri, "The skill's entry does not list " + uri + ".");
      }
    }

    var result = await client.ReadResourceAsync(uri, cancellationToken).ConfigureAwait(false);
    if (result.Contents.Count != 1)
    {
      throw new SkillVerificationException(SkillVerificationFailure.SizeMismatch, uri, "The server returned " + result.Contents.Count + " contents for " + uri + "; a skill file has one.");
    }

    byte[] bytes;
    switch (result.Contents[0])
    {
      case TextResourceContents text:
        bytes = new UTF8Encoding(false).GetBytes(text.Text);
        break;
      case BlobResourceContents blob:
        try
        {
          bytes = Convert.FromBase64String(blob.Blob);
        }
        catch (FormatException)
        {
          throw new SkillVerificationException(SkillVerificationFailure.DigestMismatch, uri, "The blob of " + uri + " is not valid base64.");
        }

        break;
      default:
        throw new SkillVerificationException(SkillVerificationFailure.DigestMismatch, uri, "The contents of " + uri + " are neither text nor a blob.");
    }

    if (listed is null)
    {
      // Dynamic skill: nothing to compare with, so only the total-size limit applies to what is actually retrieved.
      if (bytes.Length > SkillsExtension.MaxBytesPerSkill)
      {
        throw new SkillVerificationException(SkillVerificationFailure.TooLarge, uri, "The file is larger than the 16 MiB a skill may hold.");
      }

      return bytes;
    }

    if (bytes.Length != listed.Size)
    {
      throw new SkillVerificationException(
        SkillVerificationFailure.SizeMismatch,
        uri,
        "The file has " + bytes.Length.ToString(CultureInfo.InvariantCulture) + " bytes; the entry says " + listed.Size.ToString(CultureInfo.InvariantCulture) + ".");
    }

    if (!string.Equals(SkillsExtension.DigestOf(bytes), listed.Digest, StringComparison.Ordinal))
    {
      throw new SkillVerificationException(SkillVerificationFailure.DigestMismatch, uri, "The digest of the file differs from the entry's.");
    }

    return bytes;
  }

  private static bool TryParseEntry(JsonElement element, [NotNullWhen(true)] out SkillEntry? entry, out string? problem)
  {
    entry = null;
    problem = null;
    if (element.ValueKind != JsonValueKind.Object
      || !element.TryGetProperty("uri", out var uri) || uri.ValueKind != JsonValueKind.String
      || uri.GetString() is not { } uriText
      || !uriText.EndsWith("/" + SkillsExtension.SkillFileName, StringComparison.Ordinal))
    {
      problem = "A skill entry needs the uri of a SKILL.md.";
      return false;
    }

    if (!element.TryGetProperty("frontmatter", out var frontmatter) || frontmatter.ValueKind != JsonValueKind.Object
      || !frontmatter.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
      || !frontmatter.TryGetProperty("description", out var description) || description.ValueKind != JsonValueKind.String)
    {
      problem = "The entry for " + uriText + " has no frontmatter with a name and a description.";
      return false;
    }

    // "Resource Mapping": the last segment of the skill path is the skill name.
    var path = uriText[..^(SkillsExtension.SkillFileName.Length + 1)];
    if (!path.EndsWith("/" + name.GetString(), StringComparison.Ordinal))
    {
      problem = "The name in the frontmatter of the entry for " + uriText + " is not the last segment of its path.";
      return false;
    }

    if (!element.TryGetProperty("resources", out var resources))
    {
      problem = "The entry for " + uriText + " has no resources.";
      return false;
    }

    IReadOnlyList<SkillResourceEntry>? files;
    if (resources.ValueKind == JsonValueKind.String && resources.GetString() == "dynamic")
    {
      files = null;
    }
    else if (resources.ValueKind == JsonValueKind.Array)
    {
      var list = new List<SkillResourceEntry>();
      foreach (var item in resources.EnumerateArray())
      {
        if (item.ValueKind != JsonValueKind.Object
          || !item.TryGetProperty("uri", out var fileUri) || fileUri.ValueKind != JsonValueKind.String
          || !item.TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.String
          || !item.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var sizeValue) || sizeValue < 0)
        {
          problem = "A file of the entry for " + uriText + " needs a uri, a digest and a size.";
          return false;
        }

        list.Add(new SkillResourceEntry(fileUri.GetString()!, digest.GetString()!, sizeValue));
      }

      files = list;
    }
    else
    {
      problem = "The resources of the entry for " + uriText + " are neither an array nor \"dynamic\".";
      return false;
    }

    entry = new SkillEntry(uriText, frontmatter.Clone(), files);
    return true;
  }

  // Field-by-field comparison: objects ignore member order, numbers compare by value, everything else by kind and value.
  private static bool JsonEquals(JsonElement a, JsonElement b)
  {
    if (a.ValueKind != b.ValueKind)
    {
      return false;
    }

    switch (a.ValueKind)
    {
      case JsonValueKind.Object:
        var count = 0;
        foreach (var property in a.EnumerateObject())
        {
          count++;
          if (!b.TryGetProperty(property.Name, out var other) || !JsonEquals(property.Value, other))
          {
            return false;
          }
        }

        var otherCount = 0;
        foreach (var unused in b.EnumerateObject())
        {
          otherCount++;
        }

        return count == otherCount;
      case JsonValueKind.Array:
        var left = a.EnumerateArray();
        var right = b.EnumerateArray();
        while (true)
        {
          var hasLeft = left.MoveNext();
          var hasRight = right.MoveNext();
          if (hasLeft != hasRight)
          {
            return false;
          }

          if (!hasLeft)
          {
            return true;
          }

          if (!JsonEquals(left.Current, right.Current))
          {
            return false;
          }
        }

      case JsonValueKind.String:
        return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
      case JsonValueKind.Number:
        return a.GetDouble() == b.GetDouble();
      default:
        return true;
    }
  }
}
