# Streamable HTTP server: limits and defaults

`StreamableHttpEndpoint` serves both protocol eras on one path over the small `IHttpExchange` abstraction. Every limit below is a
property of `StreamableHttpOptions`.

| Option | Default | Effect |
|---|---|---|
| `ValidateHost` | on | The `Host` header must be a loopback name or listed in `AllowedHosts`. |
| `AllowedOrigins`, `AllowLoopbackOrigins` | none, on | A request with an `Origin` header outside the list is refused with 403 (DNS-rebinding protection). A request without `Origin` is accepted. `*` disables the check and is unsafe. |
| `MaxRequestBodyBytes` | 8 MiB (`ModelContext.ServeHttpAsync`: 1 MiB) | A larger declared body gets 413 without being read; a chunked body is cut off at the limit. A body with a declared length is held in a buffer of exactly that size. |
| `MaxInFlightBodyBytes` | 64 MiB | Request bodies held in memory at once, over all requests of the endpoint. A body is charged its declared length (chunked: its size so far) until its request ends; a request that would exceed the budget gets 503 with `Retry-After: 1`. |
| `MaxStreamLifetime` | 1 hour | A legacy GET event stream is ended with a `retry` field after about this long (checked every `KeepAliveInterval`); a live client resumes with `Last-Event-ID`, a reader that never reads is dropped. Zero keeps streams until the client leaves. |
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
- A POST without `Content-Type: application/json` (parameters such as `charset` are fine) is 415. Browsers can then only send it after a CORS preflight, which the `Origin` check answers.
- A POST that finds the in-flight body budget used up is 503.
- A method the modern endpoint does not implement is 404 with `-32601`, including `initialize` sent with the 2026-07-28 version header.
- Everything a handler says (unknown tool, bad arguments, resource not found) is a 200 with the error in the body.
- A legacy request other than `initialize` without `Mcp-Session-Id` is 400; with an unknown or ended session it is 404.

## Era handling

- Modern requests carry no session; a stray `Mcp-Session-Id` is ignored and never echoed. Closing the response stream cancels the request.
- Legacy requests are pinned to the session. A dropped connection does not cancel a running handler, so the client can resume the
  stream with `Last-Event-ID`; an unknown id is 400.
- `server/discover` without `_meta` is answered `-32602` with status 400: the discovery page defines its request as the standard
  `_meta` and nothing else, and every 2026-07-28 request must carry the version and client capabilities there.

# The built-in HTTP listener: limits and defaults

`HttpServer` is a small HTTP/1.1 server on `Socket` (with `SslStream` when a certificate is set) for local tools and command-line
programs. `ModelContext.ServeHttpAsync` starts one on the loopback address. For a server on the internet, host the endpoint in a web
framework. The listener checks nothing about `Origin` or `Host`; `StreamableHttpEndpoint` does, as above. Every limit below is a
property of `HttpServerOptions`.

| Option | Default | Effect |
|---|---|---|
| `Address`, `Port` | loopback, 0 | Any other address is an explicit choice and warns unless `Certificate` or `TrustedProxies` is set; `TrustedProxies` on an any-address bind warns too, because direct clients reach the port as well. The warning is a `DiagnosticKind.Warning` event when a diagnostics subscriber exists, otherwise one line on standard error. Port 0 picks a free port (`LocalEndPoint`). |
| `Certificate`, `SslProtocols` | none, OS default | With a certificate every connection is TLS and only `http/1.1` is offered through ALPN. |
| `TrustedProxies` | none | Only these peers' `X-Forwarded-For`, `-Proto` and `-Host` are believed (the client address is the first address from the right that is not itself a trusted proxy; the last proto and host value). For every other peer all `X-Forwarded-*` and `Forwarded` headers are removed before the handler sees the request. |
| `MaxConnections` | 256 | Further connections queue in the operating system's accept backlog. |
| `MaxConnectionsPerAddress` | 64 | Connections one client IP address may hold at once (IPv4-mapped IPv6 folded in). A surplus connection is closed at once. Behind a reverse proxy all clients share the proxy's address: raise it there. |
| `MaxRequestLineBytes` | 8 KiB | 414. |
| `MaxHeaderBytes`, `MaxHeaderCount` | 32 KiB, 100 | 431. The head is read into one pooled buffer of this size. |
| `MaxRequestBodyBytes` | 8 MiB | A larger `Content-Length` gets 413 before anything is read; a chunked body is cut off at the limit with 413. |
| `FirstByteTimeout` | 10 s | A new connection that sends no byte is closed silently (the shorter of this and `IdleTimeout`). |
| `IdleTimeout` | 60 s | A keep-alive connection that sends no byte between requests is closed silently. |
| `HeaderReadTimeout` | 10 s | From the first byte of a request to the end of its head (and for the TLS handshake). Slower gets 408. A sender that trickles bytes still hits it. |
| `RequestTimeout` | 30 s | From the end of the head to the end of the body. Slower gets 408. |
| `WriteTimeout` | 30 s | One write to the peer. A client that stops reading is dropped. |
| `ShutdownGracePeriod` | 5 s | How long `DisposeAsync` waits for handlers after cancelling them. |

## What is refused

The parser is strict (RFC 9112), because a disagreement with a proxy about where a request ends is what request smuggling uses.
Each of these gets 400 (unless stated), is answered with `Connection: close`, and nothing after it on the connection is parsed:

- a bare CR or LF anywhere in the head; obsolete line folding; whitespace before a header colon; control characters in values;
- `Content-Length` together with `Transfer-Encoding`; a repeated, signed, spaced or list-valued `Content-Length`;
- a transfer coding other than a lone `chunked` (501); `Transfer-Encoding` on HTTP/1.0;
- a missing or repeated `Host` on HTTP/1.1; request targets other than origin-form; lower-case method names; versions other than
  1.0 and 1.1 (505); bad percent escapes and encoded NULs in the path;
- malformed chunk sizes (non-hex, signed, spaced, empty, more than 15 digits), a chunk not followed by CRLF, bare LF in chunk framing,
  obsolete folding in a trailer; `Expect` other than `100-continue` (417).

Chunk extensions are read and ignored (RFC 9112 section 7.1.1) and trailers are read and dropped, both bounded.

## Connection behaviour

- Keep-alive on HTTP/1.1; HTTP/1.0 connections close after the response. Pipelined requests are answered in order, one at a time.
- A handler that does not read the whole request body leaves the connection open only if the rest is at most 64 KiB; otherwise it closes.
- `IHttpExchange.Aborted` is cancelled when the peer closes the connection, including closing only its sending side, or the server
  stops. While a handler runs with a pipelined request already waiting, a hang-up is not watched for.
- Every accepted socket has TCP keep-alive probes on (first after 60 s, then every 15 s, 4 misses), so a peer that vanished is evicted; the TLS close notification is bounded to 2 s.
- Error responses the listener writes itself carry `X-Content-Type-Options: nosniff`.
- After an error response the server finishes its side, then reads and discards up to 64 KiB for up to 2 s so the peer sees the
  response instead of a connection reset.
- A response that is never flushed is sent with `Content-Length`; a flushed one (every event stream) is chunked. A handler may set
  `Content-Length` itself, and then writing more or fewer bytes closes the connection. `Transfer-Encoding` cannot be set, and header
  names and values with control characters are refused when set.
