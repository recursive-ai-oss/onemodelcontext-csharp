# Interop

OneModelContext is checked against other implementations of the protocol, run as black boxes: installed from their published releases at pinned versions and driven through their command lines, their wire behaviour and small programs in this directory written from each SDK's published documentation. Their files are never opened or copied.

## What a run covers

| Direction | Oracles | Transports | Eras |
|---|---|---|---|
| Their client → our server | TypeScript SDK client, Python SDK client, official C# SDK client, Inspector CLI | stdio, Streamable HTTP (ASP.NET Core host; the built-in listener in the full matrix) | each era the oracle can speak: Python `auto`/`legacy` (and a 2026-07-28 pin in the full matrix), Inspector `modern`/`legacy`, TypeScript and C# SDK as they negotiate |
| Our client → their server | TypeScript, Python and C# SDK fixture servers; the TypeScript SDK's HTTP+SSE server | stdio, Streamable HTTP, HTTP+SSE | `--era auto`, forced `legacy`, and forced `modern` in the full matrix (a legacy-only server must answer with a typed error naming both sides' eras) |
| Our client → published servers | `server-everything`, `mcp-server-time` (reduced); plus `server-filesystem` and `server-memory` (full) | stdio | as above |

Checks per pairing (`connect` records the negotiated version): tools list, a text call, an image call, resources list and read, prompts list and get, an unknown tool (error), progress, cancellation (the call is abandoned and the connection still works), and an elicitation round (server asks, client answers). The Inspector and the published servers get the subset those tools can drive. A check a given SDK cannot perform in an era is a result, not an omission: the row shows what it did on the wire.

## Running

```sh
export INTEROP_CACHE=/some/directory/outside/the/repository
interop/install.sh                                  # Node (user-local if missing), the npm packages, a uv virtual environment
interop/run.sh --out results --matrix reduced       # one pairing per kind
interop/run.sh --out results --matrix full          # every pairing above
interop/run.sh --out results --only ours__py-server # cases whose id contains the text
```

`run.sh` builds the three .NET drivers (output outside the repository: set `ArtifactsPath`, or it uses `$INTEROP_WORK/artifacts`), starts each server on loopback, runs each client with a timeout and stops only the processes it started. Ports come from `INTEROP_PORT_BASE` (default 49300) upward; our servers pick port 0 and print the one they got. It writes `results.md`, `results.json`, and per case `cases/ID.jsonl` (one line per check), `ID.err`, `ID.exit` and the server logs. Case ids read `client__server__transport__era`.

Nothing here runs in continuous integration; it is run at milestones.

## Pins

| Oracle | Version | Source |
|---|---|---|
| `@modelcontextprotocol/sdk` (TypeScript) | 1.32.2 | npm |
| `@modelcontextprotocol/inspector` | 2.14.0 | npm |
| `mcp` (Python) | 2.3.0 | PyPI |
| `ModelContextProtocol`, `ModelContextProtocol.AspNetCore` (C#) | 2.4.0 | NuGet, this directory's `cs/` project only |
| `@modelcontextprotocol/server-everything` | 2.2.1 | npm |
| `@modelcontextprotocol/server-filesystem` | 2.2.0 | npm |
| `@modelcontextprotocol/server-memory` | 2.1.0 | npm |
| `mcp-server-time` | 2026.11.4 | PyPI |
| `zod` (the TypeScript server's schemas) | 3.25.76 | npm |
| Node | 22.22.1 | official tarball when `node` is not on PATH |
| Python | 3.13 | `uv` |

## Layout

- `ours/Server`, `ours/Client`: our fixture server (stdio, ASP.NET Core, built-in listener) and our probe client.
- `ts/`, `py/`, `cs/`: a probe client and a fixture server on each reference SDK. The fixture is the same everywhere: tools `echo`, `image`, `slow` (progress), `wait` (until cancelled), `confirm` (elicitation), the resource `mem://hello` and the prompt `greet`.
- `inspector.sh`: the Inspector CLI probes; `report.mjs`: results table.

## Notes on observed behaviour

- The official C# SDK server over HTTP asks the client for input only in its stateful mode (`Stateless = false`), which negotiates a legacy revision; stateless it answers elicitation with a tool error. The fixture server runs stateful.
- A server that does not declare a capability is not asked for that list by the generic probe.
- Python SDK `ctx.elicit` is a server-to-client request and exists only on legacy sessions; the fixture asks through the SDK's resolver, which works in both eras.
