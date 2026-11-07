using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.OAuth;

/// <summary>
/// An HTTP message handler that makes requests to a protected resource carry its access token and answers its challenges. Put it under an
/// <see cref="HttpClient"/> and the client signs in on the first <c>401</c>, widens the scope on <c>403 insufficient_scope</c>, replays the
/// request with the new token and gives up after <see cref="OAuthClientOptions.MaxAuthorizationAttempts"/> authorizations.
/// </summary>
/// <remarks>
/// The <c>Authorization</c> header is added only to requests for the origin of <see cref="OAuthClientOptions.Resource"/>; a request to
/// any other origin goes out untouched, so a token never reaches a host it was not issued for. Redirects are not followed: the inner
/// handler this class creates has them off, and a handler you pass in must too (checked for <see cref="SocketsHttpHandler"/> and
/// <see cref="HttpClientHandler"/>). A 3xx answer is returned to the caller as it is. Request bodies are buffered so they can be replayed.
/// </remarks>
public sealed class OAuthHttpHandler : DelegatingHandler
{
  private readonly OAuthClient _client;

  /// <summary>Creates the handler.</summary>
  /// <param name="client">The OAuth client that holds the tokens. It is not disposed with the handler.</param>
  /// <param name="innerHandler">The handler that sends requests; null for a <see cref="SocketsHttpHandler"/> that does not follow redirects.</param>
  /// <exception cref="ArgumentException"><paramref name="innerHandler"/> follows redirects.</exception>
  public OAuthHttpHandler(OAuthClient client, HttpMessageHandler? innerHandler = null)
    : base(innerHandler ?? new SocketsHttpHandler { AllowAutoRedirect = false })
  {
    ArgumentNullException.ThrowIfNull(client);
    // A redirect would carry the bearer token to wherever the server points, so it is refused outright.
    if (InnerHandler is SocketsHttpHandler { AllowAutoRedirect: true } or HttpClientHandler { AllowAutoRedirect: true })
    {
      throw new ArgumentException("The inner handler follows redirects, which would carry the access token to other hosts. Set AllowAutoRedirect to false.", nameof(innerHandler));
    }

    _client = client;
  }

  /// <inheritdoc />
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(request);
    if (request.RequestUri is null || !OAuthUri.SameOrigin(request.RequestUri, _client.Options.Resource))
    {
      return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    if (request.Content is not null)
    {
      await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
    }

    var token = await _client.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
    var authorizations = 0;
    while (true)
    {
      var response = await base.SendAsync(Clone(request, token), cancellationToken).ConfigureAwait(false);
      if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
      {
        return response;
      }

      var challenge = Pick(BearerChallenge.Parse(GetHeaderValues(response)));
      var stepUp = response.StatusCode == HttpStatusCode.Forbidden;
      if (stepUp && !string.Equals(challenge?.Error, "insufficient_scope", StringComparison.Ordinal))
      {
        // RFC 6750 section 3.1: a 403 that is not about scope is a refusal, not a request for a new token.
        return response;
      }

      if (authorizations >= _client.Options.MaxAuthorizationAttempts || (stepUp && await AlreadyAskedForAsync(challenge, cancellationToken).ConfigureAwait(false)))
      {
        // Retry limit (2026-07-28 authorization, Step-Up Authorization Flow): a repeated refusal is permanent.
        return response;
      }

      response.Dispose();
      authorizations++;
      token = await _client.AuthorizeAsync(challenge, token, stepUp, cancellationToken).ConfigureAwait(false);
    }
  }

  // A step-up that asks for nothing new cannot succeed by asking again.
  private async Task<bool> AlreadyAskedForAsync(BearerChallenge? challenge, CancellationToken cancellationToken)
  {
    if (challenge?.Scope is not { } wanted)
    {
      return false;
    }

    var asked = (await _client.GetRequestedScopeAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
    foreach (var scope in wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
      if (Array.IndexOf(asked, scope) < 0)
      {
        return false;
      }
    }

    return true;
  }

  private static BearerChallenge? Pick(IReadOnlyList<BearerChallenge> challenges) => challenges.Count == 0 ? null : challenges[0];

  private static IEnumerable<string> GetHeaderValues(HttpResponseMessage response) =>
    response.Headers.TryGetValues("WWW-Authenticate", out var values) ? values : [];

  private static HttpRequestMessage Clone(HttpRequestMessage original, string? token)
  {
    var clone = new HttpRequestMessage(original.Method, original.RequestUri)
    {
      Version = original.Version,
      VersionPolicy = original.VersionPolicy,
      Content = original.Content,
    };
    foreach (var header in original.Headers)
    {
      if (!string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
      {
        clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
      }
    }

    foreach (var option in original.Options)
    {
      clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
    }

    if (token is not null)
    {
      clone.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
    }

    return clone;
  }
}
