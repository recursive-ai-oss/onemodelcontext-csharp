# Authorization server

A .NET application can be the OAuth 2.1 authorization server for its own MCP endpoint, so it can act as a connector for hosted clients
without a third-party identity provider. `AuthorizationServer` implements the protocol; **you** keep the users. Signing a user in and
asking for consent is a callback into your application, and the server never renders a login page itself.

```csharp
var server = new AuthorizationServer(new AuthorizationServerOptions
{
    Issuer = new Uri("https://example.com"),
    Resources = ["https://example.com/mcp"],                    // what tokens may be issued for (RFC 8707)
    SigningKeys = [JsonWebSigningKey.FromECDsa(ecdsa, "2026-11")],
    ScopesSupported = ["files:read", "files:write"],
    Store = new FileAuthorizationStore("/var/lib/app/oauth.json"),
    AuthorizeUser = async (request, ct) =>
        request.Exchange.User is { } user                       // the host's own sign-in
            ? AuthorizationDecision.Approve(user.Identity!.Name!, request.RequestedScopes)   // after your consent screen
            : AuthorizationDecision.Redirect(new Uri("https://example.com/login?return=" + Uri.EscapeDataString(request.ResumeUrl.AbsoluteUri))),
});

// ASP.NET Core:
app.MapAuthorizationServer(server);

// Any other host: hand it the requests whose path it owns.
if (server.IsServerPath(exchange.Request.Path)) await server.HandleAsync(exchange);

// The MCP endpoint in the same process, refusing revoked tokens too:
var resourceServer = new ResourceServer(new ResourceServerOptions(new Uri("https://example.com/mcp"), server.CreateTokenValidator()) { AuthorizationServers = [server.Issuer] });
```

The access tokens are JWTs in the RFC 9068 shape, so a resource server elsewhere validates them with the unchanged `JwtTokenValidator`
over `server.JwksUri`.

## Endpoints

| Path (below the issuer's path) | What |
|---|---|
| `/.well-known/oauth-authorization-server`, `/.well-known/openid-configuration` (and the issuer-path forms) | RFC 8414 metadata: `code_challenge_methods_supported` `["S256"]`, `authorization_response_iss_parameter_supported` `true`, `client_id_metadata_document_supported`, the grant types and client authentication methods, `registration_endpoint` when enabled, `authorization_grant_profiles_supported` when Enterprise-Managed Authorization is on |
| `/authorize` | GET (and POST). PKCE, exact redirect URI, `resource`, then your callback |
| `/token` | `authorization_code`, `refresh_token`, `client_credentials`, and `urn:ietf:params:oauth:grant-type:jwt-bearer` when enabled |
| `/revoke` | RFC 7009 for refresh tokens (revokes the family) and access tokens |
| `/jwks` | The public keys, including retired keys until the last token they signed has expired |
| `/register` | RFC 7591, off by default (`DynamicClientRegistration`, `[Obsolete]` `OMC0004`) |

Paths are options. `server.Paths` lists everything served; `IsServerPath` tests one request path. With an issuer without a path the
metadata is at the origin, which is where clients of the 2025-03-26 revision look; with an issuer that has a path, set
`ServeMetadataAtOrigin` to serve it there too. Those clients send no `resource`: `AllowMissingResource` binds their tokens to the one
configured resource.

## What is enforced

- **PKCE** with S256 is required on every authorization request; `plain` is not offered. The verifier is checked in constant time.
- **Redirect URIs** are compared character for character against the registration. The one exception is an `http` loopback redirect
  (`127.0.0.1`, `[::1]`, `localhost`), whose port may differ (RFC 8252 section 7.3). A request with an unknown client or an unregistered
  redirect URI is answered with a plain 400 and never redirected.
- **`iss`** (RFC 9207) is on every redirect, success and error, next to `state`.
- **`resource`** is required (one value, one of `Resources`) and becomes the token's `aud`. A different resource at the token endpoint
  is `invalid_target`.
- **Codes** live 60 seconds, work once, and a second use revokes the tokens the first produced.
- **Refresh tokens** rotate on every use. Showing a spent one again revokes its whole family: the refresh tokens and the access tokens
  (they carry the family in `sid`; `CreateTokenValidator` checks it). A family also has an absolute lifetime (90 days).
- **Client secrets** are stored as salted PBKDF2-SHA256 hashes (`ClientSecret.Hash`) and compared in constant time. Codes and refresh
  tokens are stored as SHA-256 hashes. Nothing secret appears in a log, a diagnostic event, an exception or an error response.
- **Request limits:** bodies are read to `MaxRequestBodyBytes` (32 KiB) at most, repeated parameters are refused, and a handler fault
  becomes a bare 500.
- **Cache headers:** token, registration and error responses are `no-store`.

## Login and consent

`AuthorizeUser` receives who is asking (`ClientId`, `ClientName`, whether the client is a metadata-document client, `RedirectUri`), what
for (`RequestedScopes`, `Resource`) and the browser's request (`Exchange`, so you can read your session cookie). It returns:

- `AuthorizationDecision.Approve(subject, scopes, claims)` — the user is signed in and consents. `scopes` may only narrow the request.
  `claims` become string claims of the access token (not the registered names).
- `Deny(description)` — the client is sent back with `access_denied`.
- `Interact(respond)` or `Redirect(url)` — the user must sign in first. Your response is sent untouched. When the user has signed in,
  send the browser to `request.ResumeUrl`: it carries the pending request in an HMAC-signed, expiring token (`ResumeLifetime`, 15 minutes),
  so it cannot be edited or reused for another client, redirect URI or scope, and the server keeps no state while the user is away. Your
  callback is called again for it and sees the same context. Set `ResumeKey` (32 bytes or more) when several instances serve the same
  users; otherwise the key is random per process.

The `ClientName` is whatever the client wrote. Show the `ClientId` and the host of the `RedirectUri` beside it. The callback may redirect
to an upstream OpenID Connect provider; the result is still your decision to approve.

## Clients

- **Pre-registered:** `store.SaveClientAsync(new RegisteredClient { ... })`. Public clients use `none`; confidential ones
  `client_secret_basic` or `client_secret_post` (a secret hash from `ClientSecret.Hash`; `ClientSecret.Generate` makes a secret) or
  `private_key_jwt` with `JwksJson` or `JwksUri`. `GrantTypes` lists what the client may use (`client_credentials` and the JWT-bearer
  grant are never implied) and `Scopes` limits what it may be granted.
- **Client ID Metadata Documents:** a `client_id` that is an HTTPS URL is fetched
  (draft-ietf-oauth-client-id-metadata-document). The document must name itself in `client_id`, list valid redirect URIs, use `none` or
  `private_key_jwt`, and may not carry a secret. A pre-registered client of the same name wins. Fetching is guarded (below). Documents
  are cached per `Cache-Control` (default 5 minutes, at most an hour; `no-store` is honoured), failures for 30 seconds, at most 1000
  entries, at most 8 fetches in flight.
- **Dynamic registration** is `[Obsolete]` since the 2026-07-28 revision and off by default. Turn it on only when clients you must
  serve still need it, and rate-limit the endpoint in the host: it accepts anonymous callers and stores what they send.

### The fetch guard

Every outbound fetch for a remote party (metadata documents, `jwks_uri`) goes through `GuardedHttpFetcher`, which you can use for your own
such fetches. It requires HTTPS; resolves the name **once**, in the connect step, checks every returned address against `AddressPolicy`
and connects to one of those addresses, so a name that answers differently a second time cannot redirect the connection; refuses the
whole host when any address is not public; never follows redirects, uses no proxy, cookies or credentials; reads at most `MaxBytes`
(16 KiB) within `Timeout` (5 seconds); and requires status 200 and a JSON content type for documents.

`AddressPolicy.IsPublic` refuses the RFC 6890 special-use ranges (loopback, private, link-local, carrier-grade NAT, documentation,
benchmarking, multicast, reserved), IPv4-mapped IPv6 forms judged as the IPv4 address, 6to4 judged by the embedded address, NAT64,
Teredo, unique-local, link-local and site-local IPv6, and everything outside `2000::/3`. Odd spellings (`2130706433`, `0x7f.1`,
`[::ffff:7f00:1]`) are parsed by the platform before the policy sees them. To fetch a document from a host you run (a test fixture), add it
explicitly: `new AddressPolicy().Allow("127.0.0.1")`, and `AllowHttpToAllowListedAddresses` if it has no certificate.

## Tokens and keys

Access tokens live 10 minutes by default (`AccessTokenLifetime`) and carry `iss`, `sub`, `aud` (the resource), `client_id`, `scope`, `iat`,
`nbf`, `exp`, `jti`, and `sid` (the refresh family) next to the host's extra claims. `typ` is `at+jwt`.

Signing keys come from you: `JsonWebSigningKey.FromRsa` (RS or PS algorithms, at least 2048 bits) or `FromECDsa` (P-256, P-384, P-521). The
first key that is not retired signs. **Rotating:** put the new key first in `SigningKeys` and set `RetiredAt` on the old one (restart or
rebuild the server with the new list). The old key stays in `/jwks` for `AccessTokenLifetime` plus a minute after `RetiredAt`, so every
token it signed stays valid until it expires. Resource servers that cache the key set refetch on an unknown `kid`.

## Enterprise-Managed Authorization and Client Credentials

`EnterpriseAuthorization` accepts an Identity Assertion JWT Authorization Grant (draft-ietf-oauth-identity-assertion-authz-grant-04,
the Enterprise-Managed Authorization extension) through the JWT-bearer grant. The server checks, in this order: the `typ` is
`oauth-id-jag+jwt`; the algorithm is RS, PS or ES; `iss` is one of the trusted identity providers; the signature verifies against that
provider's keys; `aud` is exactly this server's issuer; `client_id` is the authenticated client; `exp`, `iat` and `nbf` are in range and
the lifetime is at most 10 minutes; `sub` and `jti` are present; then `resource` and `scope` are matched against the request and local
policy; and last the `jti` is recorded, so an assertion is accepted once, and a request refused for its scope does not spend it. The
token is audience-restricted to the resolved resource. No refresh token is issued. `ResolveSubject` maps the provider's user to your
subject (or refuses). EdDSA-signed assertions need the OneCrypto adapter and are refused until then.

Client Credentials needs a pre-registered, authenticated client that lists `client_credentials` in its grant types. `client_secret_basic`,
`client_secret_post` and `private_key_jwt` work; an assertion is single-use (`jti`), must name this issuer or the token endpoint as
`aud`, and must expire within ten minutes.

## Stores

`IAuthorizationStore` holds clients, codes, refresh tokens, revocations and used assertion ids. Each method is one atomic step with no
shared object identity, so a database implementation needs a row or a unique key per record. `MemoryAuthorizationStore` (one process,
lost on restart) and `FileAuthorizationStore` (one JSON file, written atomically, owner-only on Unix, hashes only; one process) ship in
the core. Multi-instance hosts need a database store.

## Not provided

Login pages, consent screens, CORS headers for browser clients, DPoP, mTLS, token introspection, rate limiting (put it in front of the
endpoints), and `id_token`s (this is not an OpenID Connect provider, though its metadata is also served at the OpenID discovery path).
