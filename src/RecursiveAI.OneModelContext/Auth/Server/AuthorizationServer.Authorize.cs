using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Http;

namespace RecursiveAI.OneModelContext.Auth.Server;

public sealed partial class AuthorizationServer
{
  private const int MaxStateLength = 1024;

  /// <summary>An authorization request after its client and redirect URI were verified. From here on errors go back to the client.</summary>
  private sealed record PendingRequest(
    RegisteredClient Client,
    string RedirectUri,
    bool RedirectUriSent,
    string? State,
    string CodeChallenge,
    string Scope,
    string Resource);

  private async Task AuthorizeAsync(IHttpExchange exchange, CancellationToken cancellationToken)
  {
    var request = exchange.Request;
    Parameters parameters;
    if (request.Method == "GET")
    {
      parameters = Parameters.Parse(request.Query);
    }
    else if (request.Method == "POST")
    {
      // RFC 6749 section 3.1: the endpoint may also take the parameters as a form.
      parameters = await ReadFormAsync(exchange, cancellationToken).ConfigureAwait(false) ?? new Parameters();
    }
    else
    {
      exchange.Response.StatusCode = 405;
      exchange.Response.SetHeader("Allow", "GET, POST");
      return;
    }

    if (parameters.Has("resume"))
    {
      await ResumeAsync(exchange, parameters, cancellationToken).ConfigureAwait(false);
      return;
    }

    // RFC 6749 section 4.1.2.1: with no trustworthy client or redirect URI the user is told, and nothing is redirected.
    var clientId = parameters.Get("client_id");
    if (string.IsNullOrEmpty(clientId) || parameters.IsDuplicate("client_id") || clientId.Length > 2048)
    {
      await WriteTextErrorAsync(exchange, 400, "invalid_request: the client_id is missing or repeated.", cancellationToken).ConfigureAwait(false);
      return;
    }

    var client = await FindClientAsync(clientId, cancellationToken).ConfigureAwait(false);
    if (client is null)
    {
      await WriteTextErrorAsync(exchange, 400, "invalid_request: the client is not known.", cancellationToken).ConfigureAwait(false);
      return;
    }

    string? redirectUri = parameters.Get("redirect_uri");
    var sent = redirectUri is not null;
    if (parameters.IsDuplicate("redirect_uri"))
    {
      await WriteTextErrorAsync(exchange, 400, "invalid_request: the redirect_uri is repeated.", cancellationToken).ConfigureAwait(false);
      return;
    }

    if (redirectUri is not null && (redirectUri.Length > 2048 || !RedirectUriRules.IsPlainUri(redirectUri)))
    {
      await WriteTextErrorAsync(exchange, 400, "invalid_request: the redirect_uri is not valid.", cancellationToken).ConfigureAwait(false);
      return;
    }

    if (redirectUri is null)
    {
      // RFC 6749 section 3.1.2.3: only a client with exactly one registered URI may omit it.
      if (client.RedirectUris.Count != 1)
      {
        await WriteTextErrorAsync(exchange, 400, "invalid_request: the redirect_uri is required.", cancellationToken).ConfigureAwait(false);
        return;
      }

      redirectUri = client.RedirectUris[0];
    }
    else if (!client.RedirectUris.Any(registered => RedirectUriRules.Matches(registered, redirectUri)))
    {
      await WriteTextErrorAsync(exchange, 400, "invalid_request: the redirect_uri is not registered for this client.", cancellationToken).ConfigureAwait(false);
      return;
    }

    var state = parameters.Get("state");
    async Task ErrorAsync(string error, string description) =>
      await RedirectAsync(exchange, redirectUri, state, [("error", error), ("error_description", description)], cancellationToken).ConfigureAwait(false);

    if (state is { Length: > MaxStateLength })
    {
      state = null;
      await ErrorAsync("invalid_request", "The state is too long.").ConfigureAwait(false);
      return;
    }

    if (parameters.HasDuplicates)
    {
      await ErrorAsync("invalid_request", "A parameter was sent more than once.").ConfigureAwait(false);
      return;
    }

    if (parameters.Get("response_type") != "code")
    {
      await ErrorAsync("unsupported_response_type", "Only response_type=code is supported.").ConfigureAwait(false);
      return;
    }

    // OAuth 2.1 section 4.1.2.1 and RFC 7636: PKCE is required; plain is not offered.
    var challenge = parameters.Get("code_challenge");
    if (challenge is null || !IsValidPkceValue(challenge) || challenge.Length != 43)
    {
      await ErrorAsync("invalid_request", "A code_challenge (S256) is required.").ConfigureAwait(false);
      return;
    }

    if (parameters.Get("code_challenge_method") != "S256")
    {
      await ErrorAsync("invalid_request", "code_challenge_method must be S256.").ConfigureAwait(false);
      return;
    }

    var asked = SplitScopes(parameters.Get("scope"));
    var scopes = AllowedScopes(asked.Count > 0 ? asked : _options.DefaultScopes, client);
    if (!AreScopeTokens(asked) || (asked.Count > 0 && scopes.Count == 0))
    {
      await ErrorAsync("invalid_scope", "None of the requested scopes is available.").ConfigureAwait(false);
      return;
    }

    var resource = ChooseResource(parameters.Get("resource"), false, out var resourceError);
    if (resource is null)
    {
      await ErrorAsync("invalid_target", resourceError).ConfigureAwait(false);
      return;
    }

    var pending = new PendingRequest(client, redirectUri, sent, state, challenge, string.Join(' ', scopes), resource);
    await AskHostAsync(exchange, pending, cancellationToken).ConfigureAwait(false);
  }

  // RFC 7636 section 4.1/4.2: unreserved characters only; a verifier is 43 to 128 of them, an S256 challenge exactly 43.
  private static bool IsValidPkceValue(string value)
  {
    if (value.Length is < 43 or > 128)
    {
      return false;
    }

    foreach (var c in value)
    {
      if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~'))
      {
        return false;
      }
    }

    return true;
  }

  private async Task AskHostAsync(IHttpExchange exchange, PendingRequest pending, CancellationToken cancellationToken)
  {
    var now = _time.GetUtcNow();
    var context = new AuthorizationRequestContext
    {
      Exchange = exchange,
      ClientId = pending.Client.ClientId,
      ClientName = pending.Client.Name,
      IsMetadataDocumentClient = pending.Client.IsMetadataDocument,
      RedirectUri = new Uri(pending.RedirectUri),
      RequestedScopes = SplitScopes(pending.Scope),
      Resource = pending.Resource,
      ResumeUrl = new Uri(_issuer + _options.AuthorizePath + "?resume=" + Uri.EscapeDataString(MakeResumeToken(pending, now))),
    };

    var decision = await _options.AuthorizeUser(context, cancellationToken).ConfigureAwait(false);
    if (decision.Interaction is { } interact)
    {
      await interact(exchange.Response, cancellationToken).ConfigureAwait(false);
      return;
    }

    if (decision.IsDenied)
    {
      await RedirectAsync(exchange, pending.RedirectUri, pending.State, [("error", "access_denied"), ("error_description", decision.DenyDescription ?? "The user did not authorize the request.")], cancellationToken).ConfigureAwait(false);
      return;
    }

    if (!decision.IsApproved || string.IsNullOrEmpty(decision.Subject))
    {
      await RedirectAsync(exchange, pending.RedirectUri, pending.State, [("error", "server_error"), ("error_description", "The user could not be authenticated.")], cancellationToken).ConfigureAwait(false);
      return;
    }

    // The host can narrow what was asked for but never widen it.
    var requested = SplitScopes(pending.Scope);
    var granted = decision.GrantedScopes is null ? requested : requested.Where(s => decision.GrantedScopes.Contains(s, StringComparer.Ordinal)).ToList();

    var code = RandomToken();
    await _store.SaveCodeAsync(new AuthorizationCodeGrant
    {
      CodeHash = Hash(code),
      ClientId = pending.Client.ClientId,
      RedirectUri = pending.RedirectUri,
      RedirectUriSent = pending.RedirectUriSent,
      Subject = decision.Subject,
      Scope = string.Join(' ', granted),
      Resource = pending.Resource,
      CodeChallenge = pending.CodeChallenge,
      FamilyId = RandomToken(),
      Claims = decision.Claims,
      ExpiresAt = now + _options.AuthorizationCodeLifetime,
      FamilyExpiresAt = now + _options.MaxRefreshFamilyLifetime,
    }, cancellationToken).ConfigureAwait(false);

    await RedirectAsync(exchange, pending.RedirectUri, pending.State, [("code", code)], cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Sends the browser back to the client with the parameters in the query, always with <c>iss</c> (RFC 9207 section 2).</summary>
  private async Task RedirectAsync(IHttpExchange exchange, string redirectUri, string? state, (string Name, string Value)[] parameters, CancellationToken cancellationToken)
  {
    // A redirect that cannot be built safely is never sent as a bare 302; the user is told instead (RFC 6749 section 4.1.2.1).
    if (!RedirectUriRules.IsPlainUri(redirectUri) || !RedirectUriRules.IsPlainUri(_issuer))
    {
      await WriteTextErrorAsync(exchange, 400, "invalid_request: the redirect_uri is not valid.", cancellationToken).ConfigureAwait(false);
      return;
    }

    var sb = new StringBuilder(redirectUri);
    sb.Append(redirectUri.Contains('?', StringComparison.Ordinal) ? '&' : '?');
    var first = true;
    void Add(string name, string value)
    {
      if (!first)
      {
        sb.Append('&');
      }

      first = false;
      sb.Append(name).Append('=').Append(Uri.EscapeDataString(value));
    }

    foreach (var (name, value) in parameters)
    {
      Add(name, value);
    }

    if (state is not null)
    {
      Add("state", state);
    }

    Add("iss", _issuer);
    var response = exchange.Response;
    response.StatusCode = 302;
    response.SetHeader("Location", sb.ToString());
    response.SetHeader("Cache-Control", "no-store");
    response.SetHeader("Referrer-Policy", "no-referrer");
    await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
  }

  private async Task<RegisteredClient?> FindClientAsync(string clientId, CancellationToken cancellationToken)
  {
    var client = await _store.FindClientAsync(clientId, cancellationToken).ConfigureAwait(false);
    if (client is not null)
    {
      return client;
    }

    // A client_id that is an HTTPS URL names its own metadata document (draft CIMD). Pre-registered clients win over the URL.
    return _clients.LooksLikeUrl(clientId) ? await _clients.ResolveAsync(clientId, cancellationToken).ConfigureAwait(false) : null;
  }

  // ------------------------------------------------------------------------------------------------ resuming after the host's login

  // The pending request travels through the host's login page inside a token signed with the server's key. The host cannot change it,
  // another client cannot reuse it, and the server keeps no state while the user signs in.
  private string MakeResumeToken(PendingRequest p, DateTimeOffset now)
  {
    var buffer = new ArrayBufferWriter<byte>();
    using (var w = new Utf8JsonWriter(buffer))
    {
      w.WriteStartObject();
      w.WriteString("c", p.Client.ClientId);
      w.WriteString("r", p.RedirectUri);
      w.WriteBoolean("rs", p.RedirectUriSent);
      if (p.State is not null)
      {
        w.WriteString("st", p.State);
      }

      w.WriteString("cc", p.CodeChallenge);
      w.WriteString("s", p.Scope);
      w.WriteString("res", p.Resource);
      w.WriteNumber("e", (now + _options.ResumeLifetime).ToUnixTimeSeconds());
      w.WriteEndObject();
    }

    var payload = System.Buffers.Text.Base64Url.EncodeToString(buffer.WrittenSpan);
    return payload + "." + System.Buffers.Text.Base64Url.EncodeToString(Sign("resume", payload));
  }

  private byte[] Sign(string purpose, string payload) => HMACSHA256.HashData(_resumeKey, Encoding.UTF8.GetBytes(purpose + "\0" + payload));

  private async Task ResumeAsync(IHttpExchange exchange, Parameters parameters, CancellationToken cancellationToken)
  {
    var token = parameters.Get("resume") ?? string.Empty;
    var dot = token.IndexOf('.', StringComparison.Ordinal);
    PendingRequest? pending = null;
    if (dot > 0 && token.Length < 8192)
    {
      try
      {
        var payload = token[..dot];
        var mac = System.Buffers.Text.Base64Url.DecodeFromChars(token.AsSpan(dot + 1));
        if (CryptographicOperations.FixedTimeEquals(mac, Sign("resume", payload)))
        {
          pending = await ReadResumeAsync(System.Buffers.Text.Base64Url.DecodeFromChars(payload), cancellationToken).ConfigureAwait(false);
        }
      }
      catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
      {
        pending = null;
      }
    }

    if (pending is null)
    {
      await WriteTextErrorAsync(exchange, 400, "invalid_request: the sign-in link is not valid or has expired. Start again from the application.", cancellationToken).ConfigureAwait(false);
      return;
    }

    await AskHostAsync(exchange, pending, cancellationToken).ConfigureAwait(false);
  }

  private async Task<PendingRequest?> ReadResumeAsync(byte[] payload, CancellationToken cancellationToken)
  {
    using var doc = JsonDocument.Parse(payload);
    var r = doc.RootElement;
    if (r.GetProperty("e").GetInt64() <= _time.GetUtcNow().ToUnixTimeSeconds())
    {
      return null;
    }

    // The client may have been removed or changed since: look it up again and require the redirect URI to still be registered.
    var client = await FindClientAsync(r.GetProperty("c").GetString()!, cancellationToken).ConfigureAwait(false);
    var redirect = r.GetProperty("r").GetString()!;
    if (client is null || !client.RedirectUris.Any(u => RedirectUriRules.Matches(u, redirect)))
    {
      return null;
    }

    return new PendingRequest(
      client,
      redirect,
      r.GetProperty("rs").GetBoolean(),
      r.TryGetProperty("st", out var st) ? st.GetString() : null,
      r.GetProperty("cc").GetString()!,
      r.GetProperty("s").GetString()!,
      r.GetProperty("res").GetString()!);
  }
}
