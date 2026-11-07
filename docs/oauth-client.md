# OAuth client

`OAuthClient` signs a program in to an OAuth-protected server and keeps its tokens fresh. It lives in the `RecursiveAI.OneModelContext.OAuth`
namespace, which holds no protocol types, and follows the 2026-07-28 authorization pages (with the 2025-03-26, 2025-06-18 and 2025-11-25
differences), OAuth 2.1, RFC 9728, RFC 8414, OpenID Connect Discovery, RFC 7636, RFC 8707 and RFC 9207.

```csharp
using var receiver = new LoopbackRedirectReceiver((url, ct) => OpenBrowser(url));   // host supplies OpenBrowser
using var oauth = new OAuthClient(new OAuthClientOptions(new Uri("https://example.com/mcp"))
{
    UserAgent = receiver,
    ClientIdMetadataUrl = "https://app.example/client.json",   // or PreRegistered = new("id", "secret")
    Store = new FileTokenStore(path),
});
await using var client = await ModelContext.ConnectAsync(ContextTransport.Http(new Uri("https://example.com/mcp"), oauth.CreateHttpClient()));
```

`CreateHttpClient()` is the integration: its handler adds the token, and answers a `401` or `403` by running the flow and replaying the
request. `HttpTransportOptions.AuthorizeRequest = oauth.AuthorizeRequestAsync` only decorates requests and cannot answer a challenge. A host
that wants to handle `AuthorizationException` itself calls `oauth.AuthorizeAsync(ex.Challenge, null, ex.InsufficientScope)` and retries.

## The flow

1. **Discovery.** The `resource_metadata` URL of the `WWW-Authenticate` challenge, else the well-known protected resource document for the
   endpoint path, then the origin. The document's `resource` must be the endpoint or a parent of it, and is the `resource` parameter from
   then on. Authorization server metadata comes from RFC 8414, then OpenID Connect Discovery; its `issuer` must equal the URL it was fetched
   for exactly. A server with no protected resource document is treated as 2025-03-26 does: metadata on the MCP origin, else default
   endpoints.
2. **Registration**, in this order: `PreRegistered` credentials, the Client ID Metadata Document URL (when the server advertises support),
   credentials stored for this issuer, then Dynamic Client Registration (`AllowDynamicRegistration`, default on, `[Obsolete]` with
   `OMC0004`; `application_type` is set). Credentials are keyed by issuer and never reused for another one. Pre-registered credentials
   without an `Issuer` bind to the first issuer they are used with.
3. **Authorization code with PKCE S256.** A server whose metadata does not list `S256` is refused. `resource` is sent on the authorization
   and token requests; `state` is checked first, then `iss` by the RFC 9207 table (compared as a plain string, no normalization: present
   and different is an error; absent is an error when the server advertises `authorization_response_iss_parameter_supported`), then the
   `error` parameter, and only then is the code redeemed.
4. **Scopes** come from the challenge, else `scopes_supported`, else are omitted. `offline_access` is added when the server lists it
   (`RequestOfflineAccess`). A `403 insufficient_scope` asks again for the union of the granted and challenged scopes; the attempt stops
   when the challenge adds nothing, and after `MaxAuthorizationAttempts` (3).
5. **Refresh** uses the stored refresh token, replacing it when the server rotates; a refresh failure falls back to a new sign-in.

Client Credentials (`ClientCredentials`: a secret sent by HTTP Basic, or by form post when the server lists only that, or a signed
`private_key_jwt` assertion with RS256, PS256 or ES256/384/512) and Enterprise-Managed Authorization (`Enterprise`: token exchange at the
identity provider for an ID-JAG, then the JWT-bearer grant at the server) need no user agent.

## Safety rules

- The token goes only to the origin of the resource it was issued for. A token for resource A is never sent to B.
- No request that carries a credential follows a redirect (`AllowAutoRedirect = false`); a 3xx from any OAuth endpoint is a failure.
- Metadata is fetched over HTTPS (HTTP only for loopback hosts), with a `RequestTimeout` (30 s) and a `MaxResponseBytes` limit (256 KiB).
- Tokens, codes and secrets are never logged, never in a URL and are scrubbed from exception text and `ToString()`.
- Only `Bearer` tokens are accepted; DPoP and MAC tokens are refused.

## Token stores

`ITokenStore` is a small key/value contract. `MemoryTokenStore` is the default. `FileTokenStore` writes one file with mode 0600 in a 0700
directory (the user profile's ACL on Windows), replaces it atomically, and optionally passes the bytes through an `ITokenProtector` the
host supplies; there is no keychain package. An unreadable file reads as empty, so the user signs in again. Keys are
`token:<resource>`, `client:<issuer>` and `bound:<client id>`.

## Loopback receiver

`LoopbackRedirectReceiver` runs the library's HTTP/1.1 server on `127.0.0.1` (a port the system picks unless given), accepts only `GET` on
its callback path with a loopback `Host`, and returns the redirect URL to `OAuthClient`. Register `RedirectUri` with the server, or list it in
the metadata document. Waiting is bounded by `Timeout` (5 minutes).

## Errors

`AuthorizationException` (root namespace) is what `ContextClient` throws for a `401` or `403`, at connect or mid-session, with the status and
the parsed `Challenges`. `OAuthException` reports a failed flow (state, `iss`, metadata, registration, token endpoint, timeout) with the
server's error code; its text never holds a secret.
