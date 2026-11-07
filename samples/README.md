# Samples

Small programs that show how OneModelContext is used. Each one runs on both protocol eras (2026-07-28 and the older revisions) without changes to its code, and each has tests in `tests/RecursiveAI.OneModelContext.SamplesTests`.

| Sample | Shows | AOT |
|---|---|---|
| [`IndexedSearch`](IndexedSearch) | A search tool over an in-memory index, served over stdio in one line. `[Tool]` on a partial class; the generator writes the schema and the invoker. | yes |
| [`AspNetCoreServer`](AspNetCoreServer) | Generated tools served over Streamable HTTP on ASP.NET Core with `app.MapModelContext("/mcp", server)`. Host middleware (authentication, rate limiting) runs before the protocol handler. | yes |
| [`LiveControl`](LiveControl) | A running application exposing operations that are only known at runtime: dynamic tools on a `ContextServer`, served over a Unix socket (`ContextTransport.Stream`) and through `ContextServer.OpenSession`, the message-level entry an in-page channel uses. Operations come and go while agents are connected. | yes |
| [`ToolBridge`](ToolBridge) | Tools defined by another framework (name, description, JSON schema, async delegate) served as tools, including one that asks the user first with `RequestContext.ConfirmAsync`, answered on the client by one `OnConfirm` callback. | yes |
| [`Explorer`](Explorer) | A client for any stdio server: connect (the era is negotiated), list tools, call one, read a resource, get a prompt, answer the server's questions from typed host callbacks (`OnConfirm`, `OnElicit`). Connects with `ModelContext.ConnectAsync("<command>", onConfirm: ...)`. | yes |
| [`ProtectedServer`](ProtectedServer) | `MapModelContext(...).RequireAuthorization()` with the resource server: a static bearer token, a 401 challenge that points at the protected resource metadata, and a client that connects with the token. A server on the internet swaps the validator for JWT or introspection. | yes |
| [`QuickPath`](QuickPath) | The reflection quick path: tools, a resource and a prompt from lambdas, for trying things out. Not trimmable and not NativeAOT. | no |
| [`HttpServer`](HttpServer) | Two tools served on the built-in HTTP listener with `ModelContext.ServeHttpAsync` (loopback only). | yes |
| [`AppsServer`](AppsServer) | A tool with a `ui://` view (MCP Apps) and a text fallback for clients that do not render it, served over stdio, `--http`, or a self-demo on each era. | yes |
| [`HostedServer`](HostedServer) | A generic-host app serving a tool class whose constructor takes a service from the container, over stdio, with `services.AddContextServer<Greeter>().UseStdio()`. The host stops when the client hangs up. | yes |
| [`ChatTools`](ChatTools) | A server's tools handed to a function-calling `IChatClient` with `client.ToAIFunctionsAsync()`, on each protocol era. Uses a scripted chat client; swap in a real one. | yes |
| [`StdioServer`](StdioServer) | A notebook server with tools, a resource template and a prompt. | yes |

## Running them

```sh
dotnet run --project samples/IndexedSearch                    # waits for a client on stdin
dotnet run --project samples/LiveControl                      # connects to itself and prints what happens
dotnet run --project samples/AspNetCoreServer                 # Streamable HTTP on http://localhost:5000/mcp
dotnet run --project samples/ToolBridge                       # the confirmation flow on each era
dotnet run --project samples/Explorer -- --call search --args '{"query":"coffee"}' -- dotnet run --project samples/IndexedSearch
```

`Explorer` takes `--era auto|modern|legacy`, `--call TOOL [--args JSON]`, `--read URI`, `--prompt NAME [--arg k=v]` and then `--` and the server's command line.

## NativeAOT

```sh
dotnet publish samples/IndexedSearch -c Release -p:SmokeMode=aot -warnaserror -o out   # Explorer publishes the same way
samples/IndexedSearch/check-native.sh out/indexed-search
```

The script drives the native binary over stdin and stdout on both eras and checks `tools/list` and `tools/call`.

Run `tools/aot-check.sh` to publish core, `IndexedSearch`, `HttpServer` and the CLI with NativeAOT (warnings fail it) and run each.

## The CLI against a legacy server

`onemodelcontext inspect "dotnet run --project samples/StdioServer" --era legacy` shows the older handshake against any sample; `tests/RecursiveAI.OneModelContext.CliTests` also runs `inspect` and `call` against a server that only speaks 2025-03-26, 2025-06-18 and the full older set.

## Tests

```sh
dotnet run --project tests/RecursiveAI.OneModelContext.SamplesTests
```

Model Context Protocol® is a registered trademark of LF Projects, LLC. OneModelContext is an independent project and is not affiliated with or endorsed by LF Projects, LLC or the Model Context Protocol project.

© Recursive AI, LLC. Licensed under MIT OR Apache-2.0.
