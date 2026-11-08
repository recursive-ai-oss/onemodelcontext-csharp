# Changelog

All notable changes are recorded here, following [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

## [1.0.1]

### Added

- Tool-call argument overloads for ordinary C# objects and source-generated JSON metadata.
- Compile checks for documentation C# samples.

### Fixed

- EdDSA guidance describes the custom-validator workaround rather than an unavailable package.
- CLI inspection counts resource templates separately from concrete resources.
- Analyzer release tracking records the diagnostics shipped in 1.0.0; the diagnostic range includes `OMCG0012`.
- Documentation examples and US English spelling.

## [1.0.0]

### Fixed

- Stdio output shutdown follows write progress within the EOF linger bound; queued writes apply backpressure to input and are safe
  against concurrent disposal. Pipelined legacy requests wait for initialization. Invalid tool-name encoding is invalid params, and
  HTTP response-body I/O failures are no longer mistaken for size limits. The protected-server sample requires its token in the environment.

- Stdio and stream servers finish admitted requests after input EOF before closing their output. `EndOfStreamTimeout` gives handlers a
  5-second grace period before cancellation; shutdown still has a bounded wait for handlers that ignore cancellation.

### Changed

- OAuth client hardening: a client secret, signing key or identity assertion is sent only to an issuer the application pinned (`ExpectedIssuer`, `PreRegisteredClient.Issuer`; error `issuer_required`); the loopback receiver ignores callbacks whose `state` is not the one in progress; a stale process adopts a refresh token another process rotated; plain-HTTP OAuth endpoints are allowed only for loopback resources; `OAuthClientOptions.HttpHandler` replaces `HttpClient` and must not follow redirects; a token file that cannot be read or is group/world-writable is never overwritten; a scope-less `insufficient_scope` no longer starts an authorization per request (`StepUpCooldown`). CLI: `--issuer`, secrets from `env:NAME`, `-` or `@file`, and `login` replaces stored tokens only after the new sign-in succeeds.
- `AuthorizationServer` hardening: the metadata-document cache evicts instead of switching itself off, with a per-host fetch cap and a short
  per-host backoff (`MaxFetchesPerHost`, `HostFailureBackoff`); Client Credentials and dynamically registered clients are refused scopes
  unless a scope allow-list applies; scope strings must be RFC 6749 scope tokens; redirect URIs with control characters, whitespace,
  backslashes or userinfo are refused and a redirect is never sent without a safe `Location`; `RequireConfidentialClient` defaults to
  true; the default ID-JAG subject is `issuer#sub`; a spent authorization code is remembered until its refresh family expires
  (`AuthorizationCodeGrant.FamilyExpiresAt`), in the memory, file and EF stores; the server's own validator requires `typ` `at+jwt`
  (`JwtTokenValidator.RequireAccessTokenType` for others); client secrets use a salted HMAC-SHA-256 hash (PBKDF2 hashes still verify).
  `ApplyAuthorizationStore` takes an optional binary collation for `client_id`.

### Added

- `RecursiveAI.OneModelContext.OneLite`: `OneLiteAuthorizationStore`, `OneLiteTokenStore` and `OneLiteTaskStore` (experimental) in one embedded
  OneLite database file, for a single host with no database server and no Entity Framework. The tables are created and versioned by the
  package itself (a file written by a newer schema is refused), single-use codes, refresh rotation, assertion replay checks and the task
  compare-and-swap hold across threads and processes, and the package is trim and NativeAOT safe. See `docs/stores.md`.
- Listener and endpoint hardening: `MaxConnectionsPerAddress`, `FirstByteTimeout` and TCP keep-alive on the built-in listener; `MaxInFlightBodyBytes` (request bodies are sized to their declared length) and `MaxStreamLifetime` on `StreamableHttpOptions`; POST needs `Content-Type: application/json` (415); `ModelContext.ServeHttpAsync` caps bodies at 1 MiB; the non-loopback warning reaches standard error when nothing subscribes to diagnostics. See `docs/http-limits.md`.
- Experimental extensions (`OMCX0001` Tasks, `OMCX0002` Skills, `OMCX0003` Server Cards), off until the host opts in. Tasks: tools that answer with a task handle, `tasks/get`, `tasks/update` and `tasks/cancel`, client polling, and `ITaskStore` with in-memory and file stores. Skills over MCP: `SkillDefinition`, `UseSkills`/`AddSkill`, `skills/list`, `skills/get` and `resources/directory/read` on the server; listing, loading and reading with digest, size and frontmatter verification on the client. Server Cards: published at `<endpoint>/server-card` by the HTTP core, the listener and `MapModelContext`, fetched through the guarded fetcher with AI Catalog discovery. See `docs/extensions.md`.
- CLI `login` and `logout` for http(s) servers (browser and loopback redirect, tokens in an owner-only file); the other commands use and refresh the stored tokens, and an authorization failure ends with exit code 4 and the challenge summarised, never the token.
- `RecursiveAI.OneModelContext.EntityFrameworkCore`: `EfAuthorizationStore<TContext>` and `EfTokenStore<TContext>` over your own `DbContext`
  (`modelBuilder.ApplyContextStores()`), for authorization servers and clients that run as several instances. Single-use codes, refresh
  rotation and assertion replay checks are conditional updates, so they hold when instances race. `EfTaskStore<TContext>` (experimental)
  shares Tasks between instances with a compare-and-swap on `Version`. See `docs/stores.md`.
- The JSON-RPC 2.0 base layer: message model, a reader that never throws on bad input, a writer, newline-delimited framing over any duplex
  stream with a bounded line length, and a connection that correlates responses, runs incoming requests concurrently and supports
  cancellation.
- System.Text.Json source-generated records for the messages of the 2026-07-28 revision and the legacy revisions back to 2024-11-05.
- `ContextServer`, `ContextClient`, `RequestContext` and `ContextTransport`, with the codec contract that the two protocol eras implement.
- Typed errors, an `ActivitySource` and `Meter` named `RecursiveAI.OneModelContext`, a diagnostics event stream and a per-request audit hook.
- The `RecursiveAI.OneModelContext` and `RecursiveAI.OneModelContext.Cli` packages, with the source generator bundled as an analyzer.
- `[Tool]`, `[Resource]` and `[Prompt]` with a source generator that writes JSON Schema 2020-12 input and output schemas from signatures and doc
  comments, and invokers that bind arguments through source-generated System.Text.Json metadata without reflection (diagnostics `OMCG0001`
  to `OMCG0012`). See `docs/tool-schemas.md`.
- `server.Tool(...)`, `server.Resource(...)` and `server.Prompt(...)`: a reflection quick path that writes the same schemas and is annotated
  as not trim- or AOT-safe.
- `ModelContext.ServeAsync` and `ModelContext.ConnectAsync` for the happy path, and a NativeAOT stdio server sample.
- `AuthorizationServer`: an OAuth 2.1 authorization server for an MCP endpoint with host-owned login and consent. PKCE (S256), exact
  redirect URI matching, `iss` on every authorization response, RFC 8707 `resource` bound into the token audience, single-use codes,
  rotating refresh tokens with reuse detection, revocation, key rotation with a published key set, Client ID Metadata Documents under an
  SSRF guard, pre-registered public and confidential clients (`client_secret_basic`, `client_secret_post`, `private_key_jwt`), Client
  Credentials, the Enterprise-Managed Authorization JWT-bearer grant, and the deprecated registration endpoint (`OMC0004`). See
  `docs/authorization-server.md`.
- `IAuthorizationStore` with in-memory and file implementations; `GuardedHttpFetcher` and `AddressPolicy` for fetching remote-supplied
  URLs safely; `JsonWebSigningKey` for signing JWTs with BCL keys; `MapAuthorizationServer` for ASP.NET Core.
- The Streamable HTTP client (`ContextTransport.Http`, `ModelContext.ConnectAsync(Uri)`), speaking both eras with sessions, the GET stream
  and `Last-Event-ID` resumption, a client-only fallback to the deprecated HTTP+SSE transport, an SSE reader with bounded events, a hook
  for adding credentials to every request, and `subscriptions/listen` on the client. See `docs/http-client.md`.
- `ModelContext.ConnectAsync("https://...")` connects a URL over Streamable HTTP, with the same callbacks as a command line.
- The OAuth 2.1 client (`OAuthClient`, `OAuthHttpHandler`, `LoopbackRedirectReceiver`, `ITokenStore` with memory and file stores): discovery, Client ID Metadata Documents, pre-registered and dynamic registration, PKCE, `resource`, RFC 9207 `iss` checks, step-up, refresh, Client Credentials and Enterprise-Managed Authorization; `AuthorizationException` for 401 and 403 on the HTTP client; `IHttpRequest.Query`. See `docs/oauth-client.md`.
- `RecursiveAI.OneModelContext.Hosting`: `services.AddContextServer<T>()` for the generic host, with tool classes created by the container,
  a DI scope per request (`context.GetRequiredService<T>()`), hosted stdio, stream and built-in listener lifetimes that stop with the host,
  an `ILogger` bridge for the diagnostics stream that redacts credentials, and `AddContextClient` for apps that consume a server.
- `RecursiveAI.OneModelContext.Extensions.AI`: a client's tools as `AIFunction`s for any `IChatClient`, `AIFunction`s served as tools
  (`server.AddAIFunction`), and Sampling answered by any `IChatClient` (`ChatClientSampling.CreateHandler`). See `docs/adapters.md`.
