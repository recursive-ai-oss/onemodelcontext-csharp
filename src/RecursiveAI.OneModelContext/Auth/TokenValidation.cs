using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.Auth;

/// <summary>Claim types used on the principals this library builds.</summary>
public static class AuthClaimTypes
{
  /// <summary>One claim per granted OAuth scope.</summary>
  public const string Scope = "scope";

  /// <summary>The subject (<c>sub</c>) of the token.</summary>
  public const string Subject = "sub";

  /// <summary>The issuer (<c>iss</c>) of the token.</summary>
  public const string Issuer = "iss";

  /// <summary>The OAuth client the token was issued to (<c>client_id</c>, or <c>azp</c> when that is absent).</summary>
  public const string ClientId = "client_id";

  /// <summary>Reads the scopes a principal holds: every <see cref="Scope"/> claim, with space-separated values split (RFC 6749 section 3.3).</summary>
  /// <param name="principal">The caller.</param>
  public static IReadOnlyList<string> GetScopes(ClaimsPrincipal principal)
  {
    ArgumentNullException.ThrowIfNull(principal);
    var scopes = new List<string>();
    foreach (var claim in principal.FindAll(Scope))
    {
      foreach (var scope in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
      {
        scopes.Add(scope);
      }
    }

    return scopes;
  }
}

/// <summary>What a validator is asked about a token.</summary>
/// <param name="Resource">The canonical URI of this server (RFC 8707). A token is valid only if it was issued for it.</param>
public readonly record struct TokenValidationRequest(string Resource);

/// <summary>The outcome of validating an access token. Build one with <see cref="Success"/>, <see cref="Failure"/> or <see cref="TemporarilyUnavailable"/>.</summary>
public sealed class TokenValidationResult
{
  private TokenValidationResult()
  {
  }

  /// <summary>Whether the token is valid for this server.</summary>
  public bool IsValid { get; private init; }

  /// <summary>The authenticated caller; set when <see cref="IsValid"/>. It carries one <see cref="AuthClaimTypes.Scope"/> claim per scope.</summary>
  public ClaimsPrincipal? Principal { get; private init; }

  /// <summary>The scopes the token grants.</summary>
  public IReadOnlyList<string> Scopes { get; private init; } = [];

  /// <summary>When the token stops being valid, if it says.</summary>
  public DateTimeOffset? ExpiresAt { get; private init; }

  /// <summary>
  /// Why the token was refused, safe to send to the caller in <c>error_description</c>. It never contains the token.
  /// </summary>
  public string? Description { get; private init; }

  /// <summary>True when the check could not be made (the issuer or introspection endpoint is down), as opposed to the token being bad. The server answers 503 rather than 401.</summary>
  public bool Unavailable { get; private init; }

  /// <summary>A valid token.</summary>
  /// <param name="principal">The caller. Scope claims are added to it when it has none.</param>
  /// <param name="scopes">The granted scopes.</param>
  /// <param name="expiresAt">When the token expires, if known.</param>
  public static TokenValidationResult Success(ClaimsPrincipal principal, IEnumerable<string>? scopes = null, DateTimeOffset? expiresAt = null)
  {
    ArgumentNullException.ThrowIfNull(principal);
    var list = new List<string>(scopes ?? []);
    if (!principal.HasClaim(static c => c.Type == AuthClaimTypes.Scope) && list.Count > 0)
    {
      var identity = new ClaimsIdentity("Bearer");
      foreach (var scope in list)
      {
        identity.AddClaim(new Claim(AuthClaimTypes.Scope, scope));
      }

      principal.AddIdentity(identity);
    }

    return new TokenValidationResult { IsValid = true, Principal = principal, Scopes = list, ExpiresAt = expiresAt };
  }

  /// <summary>A token that is not valid for this server: unknown, expired, wrong audience or issuer, bad signature.</summary>
  /// <param name="description">Why, in words that are safe to show the caller. Never include the token.</param>
  public static TokenValidationResult Failure(string description) => new() { Description = description };

  /// <summary>The token could not be checked now.</summary>
  /// <param name="description">Why, safe to show the caller.</param>
  public static TokenValidationResult TemporarilyUnavailable(string description) => new() { Description = description, Unavailable = true };
}

/// <summary>
/// Decides whether a bearer access token is valid for this server. Implementations must check that the token was issued for the
/// resource in the request (RFC 8707 section 2) and must never accept a token meant for another resource. They must not log the token or
/// put it in an exception message.
/// </summary>
public interface ITokenValidator
{
  /// <summary>Validates a token.</summary>
  /// <param name="accessToken">The token, as sent after <c>Bearer </c>.</param>
  /// <param name="request">The resource it must have been issued for.</param>
  /// <param name="cancellationToken">Cancels the check.</param>
  /// <returns>The outcome. Bad input is a failed result, never an exception.</returns>
  ValueTask<TokenValidationResult> ValidateAsync(string accessToken, TokenValidationRequest request, CancellationToken cancellationToken);
}

/// <summary>An <see cref="ITokenValidator"/> that calls a delegate, for hosts with their own token format or lookup.</summary>
/// <param name="validate">The check. It receives the token and the resource it must be valid for.</param>
public sealed class DelegateTokenValidator(Func<string, TokenValidationRequest, CancellationToken, ValueTask<TokenValidationResult>> validate) : ITokenValidator
{
  private readonly Func<string, TokenValidationRequest, CancellationToken, ValueTask<TokenValidationResult>> _validate = validate ?? throw new ArgumentNullException(nameof(validate));

  /// <inheritdoc />
  public ValueTask<TokenValidationResult> ValidateAsync(string accessToken, TokenValidationRequest request, CancellationToken cancellationToken) =>
    _validate(accessToken, request, cancellationToken);
}
