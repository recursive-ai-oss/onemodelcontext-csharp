using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace RecursiveAI.OneModelContext.OAuth;

/// <summary>
/// What the authorization server sent back to the redirect URI, checked before anything in it is acted on: <c>state</c> first, then the
/// issuer (RFC 9207), then the outcome. Build one with <see cref="Validate"/>.
/// </summary>
public sealed class AuthorizationResponse
{
  private AuthorizationResponse(string code)
  {
    Code = code;
  }

  /// <summary>The authorization code. Never put it in a log or a message.</summary>
  public string Code { get; }

  /// <summary>
  /// Validates a redirect back from the authorization endpoint and returns the code to redeem.
  /// </summary>
  /// <param name="callback">The full redirect URI the user agent was sent to, including its query.</param>
  /// <param name="expectedState">The <c>state</c> sent in the request.</param>
  /// <param name="expectedIssuer">The issuer recorded from the validated authorization server metadata before the user agent was sent away, or null when the server was found without metadata (no issuer to compare).</param>
  /// <param name="issParameterSupported"><c>authorization_response_iss_parameter_supported</c> from that metadata.</param>
  /// <returns>The response, holding the code.</returns>
  /// <exception cref="OAuthException">The state is missing or different, the issuer check fails (the server's own <c>error</c> text is then neither shown nor acted on), the server reported an error, or no code came back.</exception>
  public static AuthorizationResponse Validate(Uri callback, string expectedState, string? expectedIssuer, bool issParameterSupported)
  {
    ArgumentNullException.ThrowIfNull(callback);
    ArgumentNullException.ThrowIfNull(expectedState);
    var query = ParseQuery(callback.Query, out var repeated);

    // OAuth 2.1 section 7.12 / RFC 6749 section 10.12: a redirect that does not carry our state is not an answer to our request.
    if (repeated.Contains("state") || !query.TryGetValue("state", out var state)
      || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState)))
    {
      throw new OAuthException("The authorization response did not carry the state of this request, so it was refused.", "state_mismatch");
    }

    // 2026-07-28 authorization, Authorization Response Validation, before the code goes to any token endpoint. The table is applied to
    // success and error responses alike, with plain string comparison (RFC 3986 section 6.2.1) and no normalization.
    var hasIss = query.TryGetValue("iss", out var iss);
    if (repeated.Contains("iss"))
    {
      // RFC 9207 section 2.4: more than one iss parameter is invalid.
      throw new OAuthException("The authorization response named its issuer more than once, so it was refused.", "issuer_mismatch");
    }

    if (issParameterSupported && !hasIss)
    {
      throw new OAuthException("The authorization server says it identifies itself in authorization responses, but this one did not, so it was refused.", "issuer_missing");
    }

    if (hasIss && expectedIssuer is not null && !string.Equals(iss, expectedIssuer, StringComparison.Ordinal))
    {
      throw new OAuthException("The authorization response came from a different issuer than the one this request was sent to, so it was refused.", "issuer_mismatch");
    }

    if (query.TryGetValue("error", out var error))
    {
      query.TryGetValue("error_description", out var description);
      throw new OAuthException(
        "The authorization server refused the request: " + Sanitize(error) + (description is null ? string.Empty : " (" + Sanitize(description) + ")"),
        Sanitize(error));
    }

    if (repeated.Contains("code") || !query.TryGetValue("code", out var code) || code.Length == 0)
    {
      throw new OAuthException("The authorization response carried no authorization code.", "invalid_response");
    }

    return new AuthorizationResponse(code);
  }

  // The server's text goes into an exception message: keep it short and free of control characters.
  internal static string Sanitize(string text)
  {
    var sb = new StringBuilder(Math.Min(text.Length, 200));
    foreach (var c in text)
    {
      if (sb.Length >= 200)
      {
        break;
      }

      sb.Append(char.IsControl(c) ? ' ' : c);
    }

    return sb.ToString();
  }

  // application/x-www-form-urlencoded decoding (RFC 9207 section 2.4): '+' is a space, then percent-decoding, and nothing else.
  internal static Dictionary<string, string> ParseQuery(string query, out HashSet<string> repeated)
  {
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    repeated = new HashSet<string>(StringComparer.Ordinal);
    if (query.StartsWith('?'))
    {
      query = query[1..];
    }

    var parts = 0;
    foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
      if (++parts > 64)
      {
        break;
      }

      var eq = pair.IndexOf('=', StringComparison.Ordinal);
      var name = Decode(eq < 0 ? pair : pair[..eq]);
      var value = eq < 0 ? string.Empty : Decode(pair[(eq + 1)..]);
      if (!result.TryAdd(name, value))
      {
        repeated.Add(name);
      }
    }

    return result;
  }

  private static string Decode(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));
}
