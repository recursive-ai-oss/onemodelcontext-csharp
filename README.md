# OneModelContext: a .NET SDK for the Model Context Protocol®

**Status: pre-release, unpublished.** Nothing here is on nuget.org yet, and the API will change until the first release.

OneModelContext is a clean-room .NET 10 library for MCP, the Model Context Protocol, on both sides: client and server. The core package
(`RecursiveAI.OneModelContext`) depends on the .NET base class library only, and is trim- and NativeAOT-safe.

What it is built to do:

- **Both protocol eras, one API.** The stateless 2026-07-28 revision first, and one legacy codec for 2024-11-05 through 2025-11-25. A
  client negotiates the era per server; a server answers both on one stdio stream or endpoint. Your code is written against the modern
  semantics and the era is a codec detail.
- **Transports.** Newline-delimited JSON-RPC over stdio and over any duplex `Stream`. HTTP transports follow.
- **Tools, resources and prompts**, registered while the server runs, with `RequestContext` for cancellation, progress, user
  confirmation and elicitation.
- **Safe by default.** Every size, depth and time limit is a documented default on a public option.

## Where it stands

This repository holds the shared foundation: the JSON-RPC 2.0 layer (reader, writer, newline framing and a concurrent connection), the
protocol records for every revision as System.Text.Json source-generated types, the server and client shapes, the codec contract and the
diagnostics and audit hooks. The two protocol codecs, the HTTP transports, authorization and the `[Tool]` source generator are not part of
it yet. See [CHANGELOG.md](CHANGELOG.md).

## Layout

| Path | What it holds |
|---|---|
| `src/RecursiveAI.OneModelContext` | The library. `JsonRpc` is the base layer and holds no protocol types; `Protocol` holds the message records; `Wire` holds the codec contract. |
| `src/RecursiveAI.OneModelContext.Generator` | The incremental source generator, bundled into the core package as an analyzer. |
| `src/RecursiveAI.OneModelContext.Cli` | The `onemodelcontext` command line tool. |
| `tests/` | Unit tests and the NativeAOT smoke program. |
| `conformance/` | A runner for the protocol's official conformance suite. |

## Build

```sh
dotnet build -c Release
dotnet test -c Release
```

Build output goes to `artifacts/` (ignored). Set `ArtifactsPath` to put it elsewhere.

## Licence

`MIT OR Apache-2.0`, Copyright (c) 2026 Recursive AI, LLC. See [LICENSE-MIT](LICENSE-MIT) and [LICENSE-APACHE](LICENSE-APACHE).

Model Context Protocol® is a registered trademark of LF Projects, LLC. OneModelContext is an independent project and is not affiliated with or endorsed by LF Projects, LLC or the Model Context Protocol project.
