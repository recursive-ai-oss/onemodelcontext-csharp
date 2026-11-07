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
