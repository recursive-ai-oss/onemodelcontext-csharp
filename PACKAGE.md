# OneModelContext: a .NET SDK for the Model Context Protocol®

**Pre-release, unpublished.** This package is not on nuget.org yet; the API will change until the first release.

OneModelContext is a clean-room .NET 10 library for MCP, the Model Context Protocol, on both sides. The core package depends on the .NET
base class library only and is trim- and NativeAOT-safe.

- Client and server for the 2026-07-28 revision, and one legacy codec for 2024-11-05 through 2025-11-25, behind one API.
- Newline-delimited JSON-RPC over stdio and over any duplex `Stream`.
- Tools, resources and prompts that can be registered while a server runs.
- Source-generated System.Text.Json serialization for every protocol message; unknown fields and `_meta` survive a round trip.
- Bounded by default: message size, nesting depth, concurrent requests and waits all have documented limits.

Source and documentation: https://github.com/recursive-ai-oss/onemodelcontext-csharp

Licence: `MIT OR Apache-2.0`, Copyright (c) 2026 Recursive AI, LLC.

Model Context Protocol® is a registered trademark of LF Projects, LLC. OneModelContext is an independent project and is not affiliated with or endorsed by LF Projects, LLC or the Model Context Protocol project.
