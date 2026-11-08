# Streamable HTTP client

`ModelContext.ConnectAsync("https://host/mcp")` (or `ConnectAsync(new Uri(...))`, or `ContextTransport.Http(uri, client, options)`)
connects over Streamable HTTP. Every message is a POST to the endpoint; the answer is a JSON body or an event stream. The client speaks
both eras and negotiates like it does over stdio: a `server/discover` probe, then an optimistic modern request with a `-32022` retry,
then `initialize` for servers that only speak a legacy revision. The era is remembered per origin. A timeout or transport failure
never records a legacy choice; definitive rejections (including HTTP 400/404/405) can. The default `EraCache` expires legacy choices
after five minutes without renewing them on cache hits, so modern support is probed again.

## What each era does on the wire

- **2026-07-28:** no session. Each request carries `MCP-Protocol-Version`, `Mcp-Method` and `Mcp-Name` headers that match the body, and
  the version, capabilities and client info in `_meta`. A broken response stream means the request is issued again with a new id
  (`MaxReissues`), except for `subscriptions/listen`, which the caller reopens.
- **2024-11-05 to 2025-11-25:** `initialize`, then `Mcp-Session-Id` on every request; a GET stream (`OpenListeningStream`) carries
  server-initiated requests and notifications; a dropped event stream resumes with `Last-Event-ID` and the server's `retry` value
  (`MaxResumeAttempts`, `DefaultRetryDelay`, `MaxRetryDelay`); closing the client sends DELETE. A server that refuses the GET stream with
  405 is tolerated.
- **Deprecated HTTP+SSE (2024-11-05):** if a POST to the URL is refused the way an HTTP+SSE-only server would (400, 404 or 405 without a JSON-RPC error) and
  `AllowSseFallback` is on, the client opens the URL with GET, waits for the `endpoint` event (`SseFallbackTimeout`) and posts to that
  endpoint. The endpoint must be on the same origin. This transport is client-only; the library never serves it.

## Subscriptions

`client.ListenAsync(filter)` opens `subscriptions/listen` on 2026-07-28 and reports list changes and resource updates. On a legacy
server it uses the GET stream and `resources/subscribe`. See `ContextSubscription`.

## Credentials and redirects

- Credentials are never put in the URL: `ContextTransport.Http` refuses a URL with a user name or password.
- `HttpTransportOptions.AuthorizeRequest` runs before every request the transport sends, including the GET stream, resumption and
  DELETE, so a token provider can add `Authorization` (and refresh it) in one place. Tokens are never logged or put in exception text.
- Redirects are not followed: the client the transport creates has `AllowAutoRedirect = false`, and a 3xx answer is a `TransportException`
  that names the status. If you pass your own `HttpClient`, give it a handler that does not follow redirects, so a credential cannot be
  forwarded to another place. A 401 or 403 is surfaced as an authorization error with the server's challenge for the caller (or the
  OAuth client) to act on.

## Limits and defaults

| Option (`HttpTransportOptions`) | Default | Effect |
|---|---|---|
| `AllowSseFallback` | on | Try the HTTP+SSE transport when the URL only speaks that. |
| `OpenListeningStream` | on | Open the legacy GET stream after `initialize`. |
| `MaxEventBytes` | 8 MiB | A larger event ends that stream. |
| `MaxResponseBodyBytes` | 16 MiB | A larger JSON body fails that request. |
| `StreamIdleTimeout` | 5 min | A stream with no event or keep-alive comment for this long is treated as broken. |
| `MaxResumeAttempts` | 5 | Resumptions of one stream before the request fails. |
| `DefaultRetryDelay`, `MaxRetryDelay` | 1 s, 30 s | Wait before resuming; the server's `retry` is capped by the maximum. |
| `MaxReissues` | 2 | Modern re-issues of a request whose stream broke. |
| `SseFallbackTimeout` | 10 s | Wait for the `endpoint` event. |
| `CloseTimeout` | 5 s | The DELETE on close. |
| `MaxQueuedMessages` | 256 | Messages waiting to be read by the client. |
| `TimeProvider` | system | Every timeout and delay above. |

Failures before the first successful exchange end the connection with a `TransportException`; afterwards a failure fails the request
it belongs to and leaves the client usable.
