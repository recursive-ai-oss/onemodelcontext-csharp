using System;

namespace RecursiveAI.OneModelContext.OAuth;

/// <summary>
/// An OAuth step failed: discovery, registration, the authorization response, a token request, or a refusal to proceed on safety grounds.
/// The message is safe to show and to log: it never contains an access token, a refresh token, a client secret, an authorization code or a
/// code verifier.
/// </summary>
public sealed class OAuthException : Exception
{
  /// <summary>Creates the exception.</summary>
  /// <param name="message">What failed.</param>
  /// <param name="error">The OAuth error code (RFC 6749 section 5.2) the authorization server sent, or a short code of this library's own, such as <c>issuer_mismatch</c>.</param>
  /// <param name="inner">The cause, if any. Callers that handle credentials do not pass exceptions from the HTTP stack, whose messages are not trusted to be free of secrets.</param>
  public OAuthException(string message, string? error = null, Exception? inner = null)
    : base(message, inner)
  {
    Error = error;
  }

  /// <summary>The OAuth error code, when there is one.</summary>
  public string? Error { get; }
}
