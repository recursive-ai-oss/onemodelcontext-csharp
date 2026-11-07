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
   `OMC0004`; `application_type` is set). Credentials are keyed by issuer and never reused for another one. A public pre-registered
   client without an `Issuer` binds to the first issuer it is used with (set `Issuer` anyway when you know it). A rejected stored
   registration (`invalid_client` on refresh) is dropped, so the next sign-in registers again.
3. **Authorization code with PKCE S256.** A server whose metadata does not list `S256` is refused. `resource` is sent on the authorization
   and token requests; `state` is checked first, then `iss` by the RFC 9207 table (compared as a plain string, no normalization: present
   and different is an error; absent is an error when the server advertises `authorization_response_iss_parameter_supported`), then the
   `error` parameter, and only then is the code redeemed.
4. **Scopes** come from the challenge, else `scopes_supported`, else are omitted. `offline_access` is added when the server lists it
   (`RequestOfflineAccess`). A `403 insufficient_scope` asks again for the union of the granted and challenged scopes; the attempt stops
   when the challenge adds nothing, and after `MaxAuthorizationAttempts` (3). A challenge with no `scope` names nothing to ask for, so
   it never repeats within one request and starts a new authorization at most once per `StepUpCooldown` (5 minutes) per handler.
5. **Refresh** uses the stored refresh token, replacing it when the server rotates; a refresh failure falls back to a new sign-in.
   Before spending a refresh token the client looks at the store again, and when the server refuses the token as spent it adopts a
   different one that another process has stored in the meantime instead of erasing it.

**Anything that carries a secret needs a pinned issuer.** A resource's metadata may list any authorization server (RFC 9728 section 7.6
leaves the choice to the client), so a hostile resource could name its own and receive the credential. `ClientCredentials` and
`Enterprise` therefore require `ExpectedIssuer`, and a `PreRegisteredClient` with a `ClientSecret` requires `Issuer`; without it
`AuthorizeAsync` throws `OAuthException` with the error `issuer_required` before any request is made. Flows without a secret
(Client ID Metadata Document, public pre-registered client, dynamic registration) keep choosing the first listed server unless
a pinned issuer is set; pinning is still the recommendation when the application knows its server.

Client Credentials (`ClientCredentials`: a secret sent by HTTP Basic, or by form post when the server lists only that, or a signed
`private_key_jwt` assertion with RS256, PS256 or ES256/384/512) and Enterprise-Managed Authorization (`Enterprise`: token exchange at the
identity provider for an ID-JAG, then the JWT-bearer grant at the server) need no user agent.

## Safety rules

- The token goes only to the origin of the resource it was issued for. A token for resource A is never sent to B.
- No request that carries a credential follows a redirect (`AllowAutoRedirect = false`); a 3xx from any OAuth endpoint is a failure.
- Metadata is fetched over HTTPS. Plain HTTP is accepted only for a loopback endpoint and only when the protected resource itself is on
  loopback, so a remote resource cannot point the client at a service on the user's machine. `RequestTimeout` (30 s) and
  `MaxResponseBytes` (256 KiB) bound each request.
- A caller who supplies `HttpHandler` must hand over a `SocketsHttpHandler` or `HttpClientHandler` (possibly under `DelegatingHandler`s) with
  `AllowAutoRedirect = false`; anything that follows redirects, or cannot be checked, is refused with `ArgumentException`. (A
  followed 307 would replay the token request, secret included, at a host the server chose.)
- Tokens, codes and secrets are never logged, never in a URL and are scrubbed from exception text and `ToString()`.
- Only `Bearer` tokens are accepted; DPoP and MAC tokens are refused.

## Token stores

`ITokenStore` is a small key/value contract. `MemoryTokenStore` is the default. `FileTokenStore` writes one file with mode 0600 in a 0700
directory (the user profile's ACL on Windows), replaces it atomically, and optionally passes the bytes through an `ITokenProtector` the
host supplies; there is no keychain package. An unreadable file reads as empty, so the user signs in again, but it is never overwritten: a file the protector cannot decrypt, one over
4 MiB, or (on Unix) one writable by its group or by others makes `SetAsync` and `RemoveAsync` throw `IOException`, because the key may be
back tomorrow or someone else may have written the file. Delete the file to start over. File ownership is not checked (it needs native
calls); temporary files left by a crash are swept on the next write. Keys are
`token:<resource>`, `client:<issuer>` and `bound:<client id>`.

## Loopback receiver

`LoopbackRedirectReceiver` runs the library's HTTP/1.1 server on `127.0.0.1` (a port the system picks unless given), accepts only `GET` on
its callback path with a loopback `Host` and the `state` of the authorization request in progress (compared in constant time; any other
request, such as one a web page fires at the port, gets a 404 and the wait goes on), and returns the redirect URL to `OAuthClient`. Register `RedirectUri` with the server, or list it in
the metadata document. Waiting is bounded by `Timeout` (5 minutes).

## Errors

`AuthorizationException` (root namespace) is what `ContextClient` throws for a `401` or `403`, at connect or mid-session, with the status and
the parsed `Challenges`. `OAuthException` reports a failed flow (state, `iss`, metadata, registration, token endpoint, timeout) with the
server's error code; its text never holds a secret.

## From the command line

`onemodelcontext login <url>` signs in to an http(s) server and `onemodelcontext logout <url>` forgets the tokens.

- **Registration:** `--client-id https://…` names a Client ID Metadata Document; any other `--client-id` is a pre-registered client. A client with a secret needs `--issuer URL`, the authorization server that issued it, and the secret is given as `--client-secret env:NAME`, `-` (first line of standard input) or `@file`, or in the `ONEMODELCONTEXT_CLIENT_SECRET` variable; a literal on the command line shows in the process list. Without `--client-id` the client tries the server's dynamic registration.
- **Browser:** the authorization address is printed to standard error and opened with the system opener (`xdg-open`, `open`, `explorer.exe`); `--no-browser` only prints it. The redirect returns to a loopback receiver, on a free port unless `--redirect-port` fixes it (a pre-registered client needs a registered redirect URI, so fix the port for it).
- **Tokens:** kept in the per-user `onemodelcontext/tokens.json` (`--token-store FILE` to choose), a `FileTokenStore`: mode 0600 on Unix, in a 0700 directory. Running `login` again replaces them once the new sign-in has succeeded (until then the old tokens stay), which is how a session gains more scope.
- **Other commands** (`inspect`, `call`, `read`, `prompt`, `proxy` with an http(s) upstream) send the stored access token and refresh it when it expires. They never open a browser. When the server still refuses (HTTP 401, or a 403 that asks for more scope), the command exits with code 4 and prints the status, the challenge's error and scope, and a hint to run `login`. `--bearer` overrides stored tokens. Tokens are never printed.
