using System;
using System.Collections.Generic;
using System.Text;

namespace RecursiveAI.OneModelContext.OAuth;

/// <summary>
/// One <c>Bearer</c> challenge from a <c>WWW-Authenticate</c> header (RFC 6750 section 3, RFC 9110 section 11.6.1), with the parameters
/// OAuth clients act on. Other authentication schemes are skipped.
/// </summary>
public sealed class BearerChallenge
{
  private BearerChallenge(IReadOnlyDictionary<string, string> parameters)
  {
    Parameters = parameters;
  }

  /// <summary>Every parameter of the challenge; names are lower-case.</summary>
  public IReadOnlyDictionary<string, string> Parameters { get; }

  /// <summary>The <c>resource_metadata</c> URL (RFC 9728 section 5.1), or null.</summary>
  public string? ResourceMetadata => Get("resource_metadata");

  /// <summary>The <c>scope</c> parameter, a space-separated list, or null.</summary>
  public string? Scope => Get("scope");

  /// <summary>The <c>error</c> parameter, such as <c>invalid_token</c> or <c>insufficient_scope</c>, or null.</summary>
  public string? Error => Get("error");

  /// <summary>The <c>error_description</c> parameter, or null. It comes from the server: show it, never act on it.</summary>
  public string? ErrorDescription => Get("error_description");

  private string? Get(string name) => Parameters.TryGetValue(name, out var value) ? value : null;

  /// <summary>
  /// Parses every <c>WWW-Authenticate</c> header value of a response and returns the <c>Bearer</c> challenges in order. Malformed input
  /// yields what could be read before the fault; it never throws.
  /// </summary>
  /// <param name="headerValues">The header values (one string per header line; each may hold several comma-separated challenges).</param>
  public static IReadOnlyList<BearerChallenge> Parse(IEnumerable<string> headerValues)
  {
    ArgumentNullException.ThrowIfNull(headerValues);
    var result = new List<BearerChallenge>();
    foreach (var value in headerValues)
    {
      ParseOne(value, result);
    }

    return result;
  }

  private static void ParseOne(string text, List<BearerChallenge> result)
  {
    // Each header is bounded so a hostile server cannot make this loop long.
    const int MaxLength = 16 * 1024;
    if (text.Length > MaxLength)
    {
      text = text[..MaxLength];
    }

    var i = 0;
    string? scheme = null;
    Dictionary<string, string>? current = null;

    void Flush()
    {
      if (current is not null && string.Equals(scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
      {
        result.Add(new BearerChallenge(current));
      }

      current = null;
    }

    while (i < text.Length)
    {
      while (i < text.Length && (text[i] is ' ' or '\t' or ','))
      {
        i++;
      }

      var start = i;
      while (i < text.Length && IsTokenChar(text[i]))
      {
        i++;
      }

      if (i == start)
      {
        // Not a token: skip the byte so the loop always makes progress.
        i++;
        continue;
      }

      var token = text[start..i];
      var j = i;
      while (j < text.Length && text[j] is ' ' or '\t')
      {
        j++;
      }

      if (j < text.Length && text[j] == '=' && current is not null)
      {
        // name=value inside the current challenge.
        i = j + 1;
        while (i < text.Length && text[i] is ' ' or '\t')
        {
          i++;
        }

        string value;
        if (i < text.Length && text[i] == '"')
        {
          i++;
          var sb = new StringBuilder();
          while (i < text.Length && text[i] != '"')
          {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
              i++;
            }

            sb.Append(text[i]);
            i++;
          }

          i++;
          value = sb.ToString();
        }
        else
        {
          var vs = i;
          while (i < text.Length && text[i] is not (',' or ' ' or '\t'))
          {
            i++;
          }

          value = text[vs..i];
        }

        // The first occurrence of a parameter wins; a repeated one is ignored rather than merged.
        current.TryAdd(token.ToLowerInvariant(), value);
      }
      else
      {
        // A new challenge starts with its scheme.
        Flush();
        scheme = token;
        current = new Dictionary<string, string>(StringComparer.Ordinal);
        i = j;
      }
    }

    Flush();
  }

  private static bool IsTokenChar(char c) =>
    char.IsAsciiLetterOrDigit(c) || c is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
}
