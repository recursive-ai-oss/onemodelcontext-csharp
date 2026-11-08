# Stdio and duplex streams

Messages are UTF-8 JSON-RPC, one per line. Standard output carries only protocol messages; diagnostics belong on standard error.
Both protocol eras share the stream. A legacy `initialize` is an admission barrier: its outcome is visible before later requests or
notifications on the connection are dispatched. Ordinary requests still run concurrently and replies may arrive out of order.

## Era negotiation

Auto mode probes modern first. `ContextClientOptions.ProbeTimeout` defaults to 5 seconds; newly spawned processes get an additional
`ProcessStartupAllowance` of 10 seconds on their first probe (15 seconds total), still within the 30-second `ConnectTimeout`.
Silence or a recoverable transport failure can trigger legacy fallback for that connection, but cannot teach the cache that the server
is legacy. Only a definitive rejection or an explicit legacy-only version list can do that. Other protocol errors and malformed
probe responses also remain uncached.

The default in-memory `EraCache` expires a legacy choice five minutes after a definitive probe, even if cached legacy connections keep
succeeding. This lets a dual-era or upgraded server be probed again. Modern choices remain until forgotten or negotiation fails.
Construct `EraCache` with a clock and legacy lifetime to change expiration; custom `IEraCache` implementations own their expiration policy.

## Backpressure

`NewlineChannel` queues at most 1 MiB of output, except that a single larger message is accepted into an empty queue. One additional
block may be in flight to the stream. Writers wait **before copying** when the queue has no room. The connection also stops reading
input while that queue is full, and awaits rejection writes, so a peer that never reads cannot create an unbounded backlog of replies.
Handler concurrency remains subject to `ContextServerOptions.MaxConcurrentRequests` (256 by default).

The cost of that bound: a single-threaded peer that writes more than about 1 MiB of requests before reading any response can block
against the paused input, because the server stops reading until its replies are read. Clients should read and write concurrently, as
the protocol already expects.

## EOF and shutdown

Input EOF is a half-close: admitted handlers may finish and their queued replies are drained before output closes.
`ContextServerOptions.EndOfStreamTimeout` gives handlers a 5-second grace period, followed by cancellation and a bounded 5-second
shutdown wait. Total EOF linger, including output draining, is at most grace plus shutdown: **10 seconds by default**.
The JSON-RPC layer exposes both bounds as `JsonRpcConnectionOptions.EndOfStreamTimeout` and `ShutdownTimeout`.

Output progress is checked after each 16 KiB write. A successful write resets the no-progress deadline (the shutdown timeout,
5 seconds by default); a reader that starts late or reads slowly is not cut off by a fixed one-second drain deadline. The total
shutdown bound still applies. Transport failure or a reader that remains stalled past either deadline closes the output; a failed
write is never followed by another message on that stream. Like any bounded transport shutdown, this cannot guarantee delivery to a
peer that has stopped reading or disconnected halfway through a message.

Explicit cancellation and transport faults skip the handler grace period. Standalone `NewlineChannel` disposal uses the same
5-second idle and 10-second total output bounds with `TimeProvider.System`; connections inject their clock and configured bounds.
