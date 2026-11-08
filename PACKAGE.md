# OneModelContext: a .NET SDK for the Model Context Protocol®

**1.0.** The public API is stable. Tasks, Skills and Server Cards are marked `[Experimental]` until their MCP extensions are stable.

OneModelContext is a clean-room .NET 10 library for MCP, the Model Context Protocol, on both sides. The core package depends on the .NET
base class library only and is trim- and NativeAOT-safe.

- Client and server for the 2026-07-28 revision, and one legacy codec for 2024-11-05 through 2025-11-25, behind one API.
- stdio, any duplex `Stream`, Streamable HTTP (client, host-agnostic server core, a small built-in listener) and, in `.AspNetCore`, `MapModelContext`.
- Tools, resources and prompts declared with `[Tool]`, `[Resource]` and `[Prompt]` (a source generator writes the schemas) or registered while a server runs.
- A resource server, an OAuth 2.1 client and an embedded authorization server; MCP Apps, and Tasks, Skills and Server Cards as experimental extensions.
- Source-generated System.Text.Json serialization for every protocol message; unknown fields and `_meta` survive a round trip.
- Bounded by default: message size, nesting depth, concurrent requests and waits all have documented limits.

Source and documentation: https://github.com/recursive-ai-oss/onemodelcontext-csharp

Licence: `MIT OR Apache-2.0`, Copyright (c) 2026 Recursive AI, LLC.

Model Context Protocol® is a registered trademark of LF Projects, LLC. OneModelContext is an independent project and is not affiliated with or endorsed by LF Projects, LLC or the Model Context Protocol project.
