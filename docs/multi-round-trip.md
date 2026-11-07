# Asking the client for input: the replay rule

A tool, prompt or resource handler can ask the client for input in the middle of a request:
`ctx.ConfirmAsync`, `ctx.ElicitAsync<T>`, `ctx.SampleAsync`, `ctx.ListRootsAsync`, `ctx.ElicitUrlAsync`.
The handler code is the same on every protocol revision. What happens underneath is not.

## 2026-07-28: the handler runs again

The revision has no server-to-client requests. When a handler asks for input and the answer is not in the request:

1. The ask ends the current attempt. The server answers `resultType: "input_required"` with the `inputRequests` and a `requestState`.
2. The client answers each input request and retries the same request with `inputResponses` and the `requestState` it was given.
3. The server runs the handler **again from the start**. This time the same `ctx.*` call returns the answer.

So **code before an ask runs once per attempt and must be safe to repeat**. Read-only work is fine; do not charge a card,
send a message or append to a file before the last ask. Anything that must not be recomputed goes in `ctx.State`
(string values), which travels in `requestState` and is restored on the retry.

Asks made in parallel (`Task.WhenAll`) share one round. Answers from earlier rounds ride inside `requestState`, so a flow of
several rounds sees every answer it was given. The client gives up after `ContextClientOptions.MaxInputRounds` rounds (default 8).

## `requestState` is not trusted input

The client holds it and sends it back, so the server protects it:

- **HMAC-SHA256** by default; with `ContextServerOptions.EncryptRequestState` it is sealed with **AES-GCM**, so the client cannot read it either.
  Keys are derived with HKDF from `ContextServerOptions.RequestStateKey`; without one the server uses an ephemeral per-process key,
  so state from another process or an earlier run is refused. Set a key when several instances serve one client.
- It expires after `RequestStateLifetime` and is bound to the method, the target name, the caller's principal and a digest of the request's
  parameters (leaving out `_meta`, `inputResponses` and `requestState`). State replayed on another request, by another user, or edited, is refused.
- Comparison is constant time. A refused state is `-32602` ("the requestState is not valid"); it never reaches the handler.
- `MaxRequestStateBytes` bounds its size.

A capability the client did not declare (for example elicitation) is `-32021` with the missing capability, not an `input_required` the client cannot answer.

## 2025-11-25 and earlier: a real await

Legacy revisions have server-initiated requests, so the same `ctx.*` call sends `elicitation/create`, `sampling/createMessage` or
`roots/list` on the held connection and awaits the reply. The handler runs once; nothing is replayed.
Code written to the rule above is correct on both.

Sampling and roots are deprecated by 2026-07-28 but still answered on every revision that has them.
