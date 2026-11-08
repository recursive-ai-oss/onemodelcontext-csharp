# OneModelContext: a .NET SDK for the Model Context Protocol®

**Status: 1.0.** The public API is stable and checked by package validation. Tasks, Skills and Server Cards follow experimental MCP extensions and are marked `[Experimental]` until those extensions are stable.

OneModelContext is a clean-room .NET 10 library for MCP, the Model Context Protocol, on both sides: client and server. The core package
(`RecursiveAI.OneModelContext`) depends on the .NET base class library only, and is trim- and NativeAOT-safe.

## A server in ten lines

```csharp
public partial class IndexTools(SearchIndex index)
{
    /// <summary>Searches the index.</summary>
    /// <param name="query">What to look for.</param>
    /// <param name="limit">Most results to return.</param>
    [Tool(ReadOnly = true)]
    public Task<SearchHits> Search(string query, int limit = 10) => index.SearchAsync(query, limit);
}

await ModelContext.ServeAsync(new IndexTools(index));          // stdio
// await ModelContext.ServeHttpAsync(new IndexTools(index));   // Streamable HTTP on 127.0.0.1:3001
```

```csharp
await using var client = await ModelContext.ConnectAsync("npx -y @modelcontextprotocol/server-filesystem .");
var tools = await client.ListToolsAsync();
var result = await client.CallToolAsync("read_file", new { path = "README.md" });
Console.WriteLine(result.Text);
```

The generator writes each tool's JSON Schema 2020-12 from the signature and its doc comments, and an AOT-safe invoker. More in
[`samples/`](samples/README.md).

## What it does

- **Both protocol eras, one API.** The stateless 2026-07-28 revision first, and one legacy codec for 2024-11-05 through 2025-11-25. A
  client negotiates the era per server; a server answers both on one stdio stream or endpoint. Your code is written against the modern
  semantics and the era is a codec detail.
- **Transports.** [stdio and any duplex `Stream`](docs/stdio.md); Streamable HTTP as a client, as a host-agnostic server core, on a small built-in HTTP/1.1
  listener (loopback by default) and on ASP.NET Core with `MapModelContext`; the deprecated HTTP+SSE transport as a client fallback.
- **Tools, resources and prompts**, declared with attributes or registered while the server runs, with `RequestContext` for cancellation,
  progress, user confirmation and elicitation. A handler that asks for input behaves the same on both eras ([multi-round-trip](docs/multi-round-trip.md)).
- **Authorization.** A resource server (protected resource metadata, challenges, step-up, audience checks; JWT/JWKS, introspection,
  static-token and delegate validators), a full OAuth 2.1 client, Client Credentials and Enterprise-Managed Authorization, and an embedded
  `AuthorizationServer` whose login and consent stay with the host. See [resource-server](docs/resource-server.md),
  [oauth-client](docs/oauth-client.md) and [authorization-server](docs/authorization-server.md).
- **Extensions.** A public extension mechanism, MCP Apps, and Tasks, Skills and Server Cards, which are `[Experimental]` until their
  specifications are stable. See [extensions](docs/extensions.md).
- **Sampling, Roots and Logging** are implemented and marked `[Obsolete]` (diagnostic IDs `OMC0001`-`OMC0003`), following the revision that deprecates them.
- **Observability.** An `ActivitySource` and `Meter` named `RecursiveAI.OneModelContext`, a diagnostics event stream and a per-call audit hook.
- **Safe by default.** Every size, depth and time limit is a documented default on a public option ([limits](docs/http-limits.md)).
- **A command line tool** (`onemodelcontext inspect | call | read | prompt | login | logout | proxy`) and `dotnet new` templates.

Coming from the official C# SDK? See [the task map](docs/coming-from-the-official-sdk.md).

## Packages

All packages share one version.

| Package | Adds | Depends on |
|---|---|---|
| `RecursiveAI.OneModelContext` | Protocol (both eras), client, server, transports, built-in listener, auth, extensions, stores, the `[Tool]` generator | the .NET base class library |
| `RecursiveAI.OneModelContext.AspNetCore` | `MapModelContext`, the authentication handler, authorization server endpoints | ASP.NET Core (framework reference) |
| `RecursiveAI.OneModelContext.Hosting` | `AddContextServer<T>()`, hosted stdio and listener lifetimes, `ILogger` bridge | `Microsoft.Extensions.Hosting.Abstractions` |
| `RecursiveAI.OneModelContext.Extensions.AI` | Tools as `AIFunction`s both ways; Sampling answered by an `IChatClient` | `Microsoft.Extensions.AI.Abstractions` |
| `RecursiveAI.OneModelContext.EntityFrameworkCore` | Authorization, token and task stores on your `DbContext`, for several instances | EF Core and EF Core Relational |
| `RecursiveAI.OneModelContext.OneLite` | The same stores in one embedded database file, for a single host | `RecursiveAI.OneLite` |
| `RecursiveAI.OneModelContext.Cli` | The `onemodelcontext` tool | the core package |
| `RecursiveAI.OneModelContext.Templates` | `dotnet new onemodelcontext-server` and `onemodelcontext-client` | none |

Third-party licences: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Repository layout

| Path | What it holds |
|---|---|
| `src/` | The packages above. In the core, `JsonRpc`, `Sse`, `Http`, `OAuth` and `Jose` are the base layer and hold no protocol types. |
| `docs/` | Notes on [HTTP limits](docs/http-limits.md), the [HTTP client](docs/http-client.md), [tool schemas](docs/tool-schemas.md), [stores](docs/stores.md), [adapters](docs/adapters.md) and the pages linked above |
| `samples/` | Small programs for each way of using the library, with tests ([samples/README.md](samples/README.md)) |
| `tests/` | Unit tests, the sample tests and the NativeAOT smoke programs |
| `templates/` | The `dotnet new` template pack |
| `conformance/`, `interop/`, `bench/`, `fuzz/` | Runners for the protocol's conformance suite, an interop matrix against other SDKs and servers, benchmarks and fuzz targets |
| `tools/` | `aot-check.sh`: publishes core, a stdio server, an HTTP server and the CLI with NativeAOT and runs them |

## Build

```sh
dotnet build -c Release
dotnet test -c Release
tools/aot-check.sh        # NativeAOT publishes; needs a native toolchain (clang or gcc)
```

Build output goes to `artifacts/` (ignored). Set `ArtifactsPath` to put it elsewhere. See [CHANGELOG.md](CHANGELOG.md).

## Licence

`MIT OR Apache-2.0`, Copyright (c) 2026 Recursive AI, LLC. See [LICENSE-MIT](LICENSE-MIT) and [LICENSE-APACHE](LICENSE-APACHE).

Model Context Protocol® is a registered trademark of LF Projects, LLC. OneModelContext is an independent project and is not affiliated with or endorsed by LF Projects, LLC or the Model Context Protocol project.
