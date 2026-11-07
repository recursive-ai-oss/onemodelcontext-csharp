using System;
using System.Collections.Generic;
using RecursiveAI.OneModelContext.OAuth;

namespace RecursiveAI.OneModelContext;

/// <summary>
/// The server refused a request for lack of authorization: HTTP 401 (a token is missing or not valid) or 403 (the token does not carry
/// what the operation needs). It carries the <c>Bearer</c> challenge the server sent, which names the protected resource metadata
/// (<c>resource_metadata</c>), the scopes needed (<c>scope</c>) and the reason (<c>error</c>), so a host can start authorization and
/// try again; an <see cref="OAuthClient"/> given to the HTTP transport does that by itself. The message and the challenge never contain
/// the token that was sent.
/// </summary>
public sealed class AuthorizationException : ContextException
{
  /// <summary>Creates the exception.</summary>
  /// <param name="message">What happened.</param>
  /// <param name="statusCode">The HTTP status: 401 or 403.</param>
  /// <param name="challenges">The <c>Bearer</c> challenges of the response; empty when it sent none.</param>
  public AuthorizationException(string message, int statusCode, IReadOnlyList<BearerChallenge> challenges)
    : base(message)
  {
    StatusCode = statusCode;
    Challenges = challenges ?? [];
  }

  /// <summary>The HTTP status code, 401 or 403.</summary>
  public int StatusCode { get; }

  /// <summary>Every <c>Bearer</c> challenge of the response.</summary>
  public IReadOnlyList<BearerChallenge> Challenges { get; }

  /// <summary>The first challenge, or null when the server sent none.</summary>
  public BearerChallenge? Challenge => Challenges.Count > 0 ? Challenges[0] : null;

  /// <summary>True for a 403 whose challenge says <c>insufficient_scope</c>: authorizing again with the union of the old and the challenged scopes can succeed.</summary>
  public bool InsufficientScope => StatusCode == 403 && string.Equals(Challenge?.Error, "insufficient_scope", StringComparison.Ordinal);
}
