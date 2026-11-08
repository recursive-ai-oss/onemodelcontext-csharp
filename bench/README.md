# Speed benchmarks

These apps measure OneModelContext's speed rows on one machine, black-box, against a pinned reference SDK. One command per machine:

```
bench/run.sh --rows all --out <dir>          # or --rows r1,r3 ...; --seconds N sets the timed phase (default 4), --seq N the sequential calls (default 2000)
```

`run.sh` publishes the apps, runs the driver, and writes `results.md` and `results.json` in `<dir>`: for every row our median, the reference's median, the ratio (our cost over the reference's, so below 1.0 means cheaper), allocations and CPU time per call, peak memory, the geomean of the gated ratios, the worst ratio, and the machine (CPU, ISA flags, cores, load before, during and after, runtime and reference versions). Results are machine-specific and are not committed. Numbers taken on a loaded or shared host are indicative only; publishable rows are run alone on an otherwise idle machine.

## Pins

| What | Version |
|---|---|
| .NET SDK | 10.0.400 (`global.json`, `latestPatch`) |
| Reference SDK (`ModelContextProtocol`, `ModelContextProtocol.AspNetCore`) | 2.4.0, from NuGet, only under `bench/` (`Directory.Packages.props`) |

The reference is used only through its published API. It is a speed reference, nothing more: no comparative claims are made from these rows.

## Layout

- `Ours/`, `Reference/`: one app per stack with the same two roles. `server stdio|kestrel[|listener]` serves the same tools; `client --server <command or url> --kind call|text|image|list` is a client process. `Reference/` exists only for the speed reference.
- `Driver/`: starts the processes, sends the stack-neutral raw HTTP load, computes the table. It references no protocol stack.
- `Shared/`: the payloads and the measuring loop, compiled into both apps, so both do identical work.

## Equivalent work

Same tools on both servers: `echo` (a short string in, a short string out), `text1mb` (1 MiB of text), `image1mb` (1 MiB of bytes, base64-encoded on every call, returned as an inline `image/png`), `bench_stats` (process allocation, peak memory and CPU time), and with `--filler 1000` a thousand small tools. Same build configuration (Release, workstation non-concurrent GC, tiered PGO, invariant globalization), logging providers cleared, loopback only, one fresh server process per measurement.

What cannot be made equal, and how it is handled:

- Tool registration differs by necessity: ours uses dynamic `ToolDefinition`s with a handler, the reference uses delegates it wraps itself. Both do trivial work in the handler.
- Each client is paired with its own stack's server for rows 1, 4 and 5, as a user of either SDK would run it; row 3 runs both clients against the same fixed server, and row 2 sends the same raw bytes to both servers.
- The reference's HTTP server runs in its stateless mode, which is also how OneModelContext's modern era works. The raw load uses the older session flow (an `initialize` first), which both accept, so the row compares like with like; a second, information-only pass sends self-contained 2026-07-28 requests.
- Allocation per call is `GC.GetTotalAllocatedBytes` across the whole process divided by calls (the server's through the `bench_stats` tool), so it includes background work.

HTTP client pairings use a separate server process for each client, with an equal unmeasured load pass of at least four seconds before the measured pass. This keeps tiered compilation in a shared server from favouring whichever client runs second.

## Rows

| Row | What | Gate |
|---|---|---|
| `r1` | stdio `tools/call`: sequential p50 and throughput with 16 calls in flight | yes |
| `r2` | Streamable HTTP `tools/call` throughput on Kestrel (`MapModelContext` against the reference's `MapMcp`), 32 connections of raw requests; the built-in listener and the 2026-07-28 request form are information | yes (Kestrel legacy flow) |
| `r3` | client calls/s against a fixed server, over stdio and over HTTP; the same clients against the other stack's server are information | yes (our server fixed) |
| `r4` | a 1 MiB text result and a 1 MiB inline image over stdio, p50 of the whole call (server encode, transport, client decode) | yes |
| `r5` | `tools/list` with 1,000 tools over stdio, p50 (nothing is cached: the server sends no `ttlMs`) | yes |
| `r6` | NativeAOT stdio server, process start to the first `initialize` response, median of 15; if the reference cannot be published with NativeAOT the row passes and reports ours alone; the framework-dependent pair is information | yes |

The gate is the geomean of the gated ratios at or below 1.0 with no row above 1.25.

---

Model Context Protocol® is a registered trademark of LF Projects, LLC. OneModelContext is an independent project and is not affiliated with or endorsed by LF Projects, LLC or the Model Context Protocol project.
