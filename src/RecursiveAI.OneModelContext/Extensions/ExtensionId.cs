using System;

namespace RecursiveAI.OneModelContext.Extensions;

/// <summary>
/// The identifier of an extension: <c>{vendor-prefix}/{extension-name}</c>, for example <c>com.example/my-extension</c>. The rules are the
/// ones for <c>_meta</c> keys with the prefix made mandatory (extensions overview, "MCP Extensions"). Official extensions use the vendor
/// prefix <c>io.modelcontextprotocol</c>; a third party uses a reversed domain name it owns.
/// </summary>
public static class ExtensionId
{
  /// <summary>Checks an identifier.</summary>
  /// <param name="id">The identifier.</param>
  /// <param name="reason">Why it is not valid, when it is not.</param>
  /// <returns>True when <paramref name="id"/> is a well-formed extension identifier.</returns>
  public static bool IsValid(string? id, out string reason)
  {
    if (string.IsNullOrEmpty(id))
    {
      reason = "The identifier is empty.";
      return false;
    }

    var slash = id.IndexOf('/', StringComparison.Ordinal);
    if (slash <= 0 || slash == id.Length - 1 || id.IndexOf('/', slash + 1) >= 0)
    {
      reason = "An extension identifier is {vendor-prefix}/{extension-name} with exactly one slash and both parts present.";
      return false;
    }

    var prefix = id.AsSpan(0, slash);
    var name = id.AsSpan(slash + 1);
    var labelStart = 0;
    for (var i = 0; i <= prefix.Length; i++)
    {
      if (i < prefix.Length && prefix[i] != '.')
      {
        continue;
      }

      if (!IsLabel(prefix[labelStart..i]))
      {
        reason = "Each label of the vendor prefix starts with a letter, ends with a letter or digit and holds only letters, digits and hyphens.";
        return false;
      }

      labelStart = i + 1;
    }

    if (!IsName(name))
    {
      reason = "The extension name starts and ends with a letter or digit and holds only letters, digits, hyphens, underscores and dots.";
      return false;
    }

    reason = string.Empty;
    return true;
  }

  /// <summary>
  /// True when the vendor prefix is one the protocol reserves for itself: <c>_meta</c> keys whose second label is <c>modelcontextprotocol</c>
  /// or <c>mcp</c> (basic, "_meta"). Only the library's own official extensions may use such an identifier.
  /// </summary>
  /// <param name="id">A valid identifier.</param>
  public static bool IsReserved(string id)
  {
    ArgumentNullException.ThrowIfNull(id);
    var slash = id.IndexOf('/', StringComparison.Ordinal);
    if (slash <= 0)
    {
      return false;
    }

    var labels = id[..slash].Split('.');
    return labels.Length >= 2 && (labels[1] == "modelcontextprotocol" || labels[1] == "mcp");
  }

  private static bool IsLabel(ReadOnlySpan<char> label)
  {
    if (label.IsEmpty || !char.IsAsciiLetter(label[0]) || !char.IsAsciiLetterOrDigit(label[^1]))
    {
      return false;
    }

    foreach (var c in label)
    {
      if (!char.IsAsciiLetterOrDigit(c) && c != '-')
      {
        return false;
      }
    }

    return true;
  }

  private static bool IsName(ReadOnlySpan<char> name)
  {
    if (name.IsEmpty || !char.IsAsciiLetterOrDigit(name[0]) || !char.IsAsciiLetterOrDigit(name[^1]))
    {
      return false;
    }

    foreach (var c in name)
    {
      if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
      {
        return false;
      }
    }

    return true;
  }
}
