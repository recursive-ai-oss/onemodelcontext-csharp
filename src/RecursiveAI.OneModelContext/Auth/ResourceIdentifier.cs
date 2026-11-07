using System;
using System.Collections.Generic;
using System.Text.Json;

namespace RecursiveAI.OneModelContext.Auth;

/// <summary>Resource identifiers (RFC 8707 section 2) and the audience comparison every validator uses.</summary>
public static class ResourceIdentifier
{
  /// <summary>
  /// Puts a resource identifier in canonical form: lower-case scheme and host, no default port, no fragment, and no trailing slash when
  /// the path is empty. The path is otherwise compared exactly, because its trailing slash can be significant.
  /// </summary>
  /// <param name="value">The identifier.</param>
  /// <returns>The canonical form, or null when it is not an absolute <c>http</c>/<c>https</c> URI without a fragment or user info.</returns>
  public static string? Canonicalize(string? value)
  {
    if (string.IsNullOrEmpty(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
      || uri.Scheme is not ("http" or "https") || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0 || value.Contains('#', StringComparison.Ordinal))
    {
      return null;
    }

    var path = uri.AbsolutePath == "/" ? string.Empty : uri.AbsolutePath;
    return uri.Scheme + "://" + uri.Host + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)) + path + uri.Query;
  }

  /// <summary>
  /// Whether an <c>aud</c> claim (a string or an array of strings, RFC 7519 section 4.1.3) names <paramref name="resource"/>. Both sides
  /// are canonicalised first, so <c>HTTPS://Example.com</c> matches <c>https://example.com</c>; nothing looser matches.
  /// </summary>
  /// <param name="audience">The claim value.</param>
  /// <param name="resource">This server's canonical resource URI.</param>
  public static bool AudienceMatches(JsonElement audience, string resource)
  {
    var expected = Canonicalize(resource);
    if (expected is null)
    {
      return false;
    }

    switch (audience.ValueKind)
    {
      case JsonValueKind.String:
        return Canonicalize(audience.GetString()) == expected;
      case JsonValueKind.Array:
        var seen = 0;
        foreach (var item in audience.EnumerateArray())
        {
          if (++seen > 32)
          {
            return false;
          }

          if (item.ValueKind == JsonValueKind.String && Canonicalize(item.GetString()) == expected)
          {
            return true;
          }
        }

        return false;
      default:
        return false;
    }
  }
}

/// <summary>
/// Scope hierarchies: a broader scope that implies narrower ones. The 2026-07-28 authorization page says servers must account for them when
/// deciding whether a token is sufficient.
/// </summary>
public sealed class ScopeHierarchy
{
  private readonly Dictionary<string, List<string>> _implies = new(StringComparer.Ordinal);

  /// <summary>Declares that holding <paramref name="broader"/> also grants each of <paramref name="narrower"/>. Implication is transitive.</summary>
  /// <param name="broader">The wider scope, such as <c>files:write</c>.</param>
  /// <param name="narrower">The scopes it includes, such as <c>files:read</c>.</param>
  public ScopeHierarchy Imply(string broader, params string[] narrower)
  {
    ArgumentException.ThrowIfNullOrEmpty(broader);
    ArgumentNullException.ThrowIfNull(narrower);
    if (!_implies.TryGetValue(broader, out var list))
    {
      _implies[broader] = list = [];
    }

    list.AddRange(narrower);
    return this;
  }

  /// <summary>Whether <paramref name="granted"/> includes <paramref name="required"/>, directly or through the hierarchy.</summary>
  /// <param name="granted">The scopes the token carries.</param>
  /// <param name="required">The scope an operation needs.</param>
  public bool Satisfies(IReadOnlyCollection<string> granted, string required)
  {
    ArgumentNullException.ThrowIfNull(granted);
    var pending = new Stack<string>(granted);
    var visited = new HashSet<string>(StringComparer.Ordinal);
    while (pending.Count > 0)
    {
      var scope = pending.Pop();
      if (string.Equals(scope, required, StringComparison.Ordinal))
      {
        return true;
      }

      // The visited set also ends cycles such as a implies b implies a.
      if (visited.Add(scope) && _implies.TryGetValue(scope, out var narrower))
      {
        foreach (var next in narrower)
        {
          pending.Push(next);
        }
      }
    }

    return false;
  }
}
