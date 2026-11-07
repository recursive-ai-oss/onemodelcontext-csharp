# Changelog

All notable changes are recorded here, following [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- The JSON-RPC 2.0 base layer: message model, a reader that never throws on bad input, a writer, newline-delimited framing over any duplex
  stream with a bounded line length, and a connection that correlates responses, runs incoming requests concurrently and supports
  cancellation.
- System.Text.Json source-generated records for the messages of the 2026-07-28 revision and the legacy revisions back to 2024-11-05.
- `ContextServer`, `ContextClient`, `RequestContext` and `ContextTransport`, with the codec contract that the two protocol eras implement.
- Typed errors, an `ActivitySource` and `Meter` named `RecursiveAI.OneModelContext`, a diagnostics event stream and a per-request audit hook.
- The `RecursiveAI.OneModelContext` and `RecursiveAI.OneModelContext.Cli` packages, with the source generator bundled as an analyzer.
- `[Tool]`, `[Resource]` and `[Prompt]` with a source generator that writes JSON Schema 2020-12 input and output schemas from signatures and doc
  comments, and invokers that bind arguments through source-generated System.Text.Json metadata without reflection (diagnostics `OMCG0001`
  to `OMCG0011`). See `docs/tool-schemas.md`.
- `server.Tool(...)`, `server.Resource(...)` and `server.Prompt(...)`: a reflection quick path that writes the same schemas and is annotated
  as not trim- or AOT-safe.
- `ModelContext.ServeAsync` and `ModelContext.ConnectAsync` for the happy path, and a NativeAOT stdio server sample.
- The Streamable HTTP client (`ContextTransport.Http`, `ModelContext.ConnectAsync(Uri)`), speaking both eras with sessions, the GET stream
  and `Last-Event-ID` resumption, a client-only fallback to the deprecated HTTP+SSE transport, an SSE reader with bounded events, a hook
  for adding credentials to every request, and `subscriptions/listen` on the client. See `docs/http-client.md`.
- `ModelContext.ConnectAsync("https://...")` connects a URL over Streamable HTTP, with the same callbacks as a command line.
