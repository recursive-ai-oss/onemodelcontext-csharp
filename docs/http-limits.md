# Streamable HTTP server: limits and defaults

`StreamableHttpEndpoint` serves both protocol eras on one path over the small `IHttpExchange` abstraction. Every limit below is a
property of `StreamableHttpOptions`.

| Option | Default | Effect |
|---|---|---|
| `ValidateHost` | on | The `Host` header must be a loopback name or listed in `AllowedHosts`. |
| `AllowedOrigins`, `AllowLoopbackOrigins` | none, on | A request with an `Origin` header outside the list is refused with 403 (DNS-rebinding protection). A request without `Origin` is accepted. `*` disables the check and is unsafe. |
| `MaxRequestBodyBytes` | 8 MiB | A larger declared body gets 413 without being read; a chunked body is cut off at the limit. |
| `BodyReadTimeout` | 30 s | A body that trickles in more slowly gets 408. |
| `SseWriteTimeout` | 30 s | A client that stops reading an event stream is dropped. |
| `KeepAliveInterval` | 15 s | Comment lines keep idle streams open. |
| `MaxSessions`, `SessionIdleTimeout` | 1000, 30 min | Legacy sessions only; more sessions get 503, idle ones are removed. |
| `MaxReplayEvents`, `MaxReplayBytes` | 256, 4 MiB | Events kept per session for `Last-Event-ID` resumption. |
| `MaxStreamsPerSession` | 16 | Concurrent GET streams of one legacy session. |

## Status codes

- Envelope errors are 400: parse errors, invalid requests, a missing or mismatched `MCP-Protocol-Version`, `Mcp-Method`, `Mcp-Name`
  or `Mcp-Param-*` header (`-32020`), an unsupported version (`-32022`), a missing capability (`-32021`), and a modern request whose
  `_meta` lacks the protocol version or the client capabilities (2026-07-28 Streamable HTTP, "Protocol Version Header"; SEP-2575).
- A method the modern endpoint does not implement is 404 with `-32601`, including `initialize` sent with the 2026-07-28 version header.
- Everything a handler says (unknown tool, bad arguments, resource not found) is a 200 with the error in the body.
- A legacy request other than `initialize` without `Mcp-Session-Id` is 400; with an unknown or ended session it is 404.

## Era handling

- Modern requests carry no session; a stray `Mcp-Session-Id` is ignored and never echoed. Closing the response stream cancels the request.
- Legacy requests are pinned to the session. A dropped connection does not cancel a running handler, so the client can resume the
  stream with `Last-Event-ID`; an unknown id is 400.
- `server/discover` without `_meta` is answered `-32602` with status 400: the discovery page defines its request as the standard
  `_meta` and nothing else, and every 2026-07-28 request must carry the version and client capabilities there.
