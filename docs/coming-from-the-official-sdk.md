# Coming from the official C# SDK

A task map for developers who know the official C# SDK (`ModelContextProtocol`, `ModelContextProtocol.AspNetCore`) and want the same
thing in OneModelContext. It lists where to look, not which is better: the two libraries make different choices (this one has a BCL-only
core, source-generated tools and a protocol-era layer under a single API), and either may suit a project.

The left column follows the official SDK's published documentation ([csharp.sdk.modelcontextprotocol.io](https://csharp.sdk.modelcontextprotocol.io),
[Microsoft Learn](https://learn.microsoft.com/dotnet/ai/quickstarts/build-mcp-server)). Nothing is mapped one-to-one by shape; each row says what to
write here. Samples with the same names live in [`samples/`](../samples/README.md).

## Packages

| Task | There | Here |
|---|---|---|
| Core client and low-level server | `ModelContextProtocol.Core` | `RecursiveAI.OneModelContext` (base class library only) |
| Hosting, DI, attribute discovery | `ModelContextProtocol` | `RecursiveAI.OneModelContext` for discovery (the generator ships inside it); `RecursiveAI.OneModelContext.Hosting` for DI |
| HTTP server on ASP.NET Core | `ModelContextProtocol.AspNetCore` | `RecursiveAI.OneModelContext.AspNetCore`; the core also has a small built-in HTTP listener |
| Tools as `AIFunction`s | `McpClientTool` is an `AIFunction` | `RecursiveAI.OneModelContext.Extensions.AI` (`ToAIFunctionsAsync()`) |

## Tasks

| Task | There | Here |
|---|---|---|
| Declare a tool | `[McpServerToolType]` class, `[McpServerTool, Description("...")]` method | `partial` class, `[Tool]` on the method. The description and parameter descriptions come from `/// <summary>` and `/// <param>` comments; the generator writes the schema and an AOT-safe invoker. Annotations are properties: `[Tool(ReadOnly = true)]`. |
| Declare a resource | `[McpServerResourceType]` / `[McpServerResource]` | `[Resource("notes://{title}")]` on a method of the same partial class |
| Declare a prompt | `[McpServerPromptType]` / `[McpServerPrompt]` | `[Prompt]` on a method |
| Register what the generator found | `.WithToolsFromAssembly()` | `new Notebook().AddTo(server)`; `Notebook` implements `IContextRegistrar`, so it can be passed straight to `ModelContext.ServeAsync` |
| Register at runtime | `McpServerTool.Create(...)` and the server's collections | `server.Tools.Add(new ToolDefinition(name, description, schema), handler)`, and `server.Resources`, `server.Prompts` |
| Quick lambda | (use a delegate with the tool factory) | `server.Tool("name", lambda)`; reflection based, so it is marked not trimmable and not NativeAOT-safe |
| Run a server over stdio | `AddMcpServer().WithStdioServerTransport()` on a host | `await ModelContext.ServeAsync(new Notebook());` |
| Run over HTTP, small | ASP.NET Core `MapMcp()` | `await ModelContext.ServeHttpAsync(new Notebook(), port: 3001);` (built-in listener, loopback only) |
| Host over HTTP on ASP.NET Core | `app.MapMcp()` | `app.MapModelContext("/mcp", server)`, then `.RequireAuthorization()` once authentication is configured |
| DI hosting | `services.AddMcpServer()...` | `services.AddContextServer<Notebook>().UseStdio()` (or `.UseListener(port: 3001)`) from `.Hosting`; scoped services through `context.GetRequiredService<T>()` |
| Reach the server object in a tool | a parameter of type `McpServer` | a parameter of type `RequestContext` (also carries the cancellation token, progress, the signed-in principal and `ConfirmAsync`/`ElicitAsync`) |
| Connect a client to a stdio server | `StdioClientTransport` + `McpClient.CreateAsync` | `await ModelContext.ConnectAsync("npx -y some-server")` (a command line), or `ContextClient.ConnectAsync(ContextTransport.Process(...))` |
| Connect over HTTP | `HttpClientTransport` | `await ModelContext.ConnectAsync(new Uri("https://host/mcp"))`; pass your own `HttpClient` through `ContextTransport.Http(uri, httpClient)` |
| List and call tools | `ListToolsAsync`, `CallToolAsync(name, dictionary)` | `client.ListToolsAsync()`, `client.CallToolAsync(name, arguments)`; the result has `.Text`, `.Content`, `.Structured` and `.IsError` |
| Resources and prompts (client) | `ListResourcesAsync`, `ReadResourceAsync`, `GetPromptAsync` | the same three names on `ContextClient`, plus `ListResourceTemplatesAsync`, `ListPromptsAsync`, `CompleteAsync` |
| Progress | `IProgress<ProgressNotificationValue>` | `await context.ReportProgressAsync(done, total, message)` |
| Ask the user (elicitation) | `server.ElicitAsync(new ElicitRequestParams { ... })` | `await context.ConfirmAsync("Delete 3 files?")` for yes/no, or `context.ElicitAsync<T>(message, schema, typeInfo)`; see the replay rule in [multi-round-trip.md](multi-round-trip.md) |
| Answer elicitation on the client | `ElicitationHandler` in `McpClientOptions.Handlers` | `onConfirm:` / `onElicit:` arguments of `ModelContext.ConnectAsync`, or the same on `ContextClientOptions` |
| Require sign-in on the server | ASP.NET Core authentication, then `[Authorize]` | `ResourceServer` with a validator (JWT, introspection, static token or delegate), `AddResourceServer`, `RequireAuthorization()`; see [resource-server.md](resource-server.md) |
| Sign in on the client | `ClientOAuthOptions` on the HTTP transport | `OAuthClient` and its `CreateHttpClient()`; see [oauth-client.md](oauth-client.md) |
| Inspect a server from a shell | the Inspector | the Inspector, or `onemodelcontext inspect <server>` / `call` / `read` / `prompt` |

## Behaviour that differs

- **The protocol era is negotiated for you.** The client probes the modern revision (2026-07-28) and falls back to `initialize` for older
  servers; a server answers both on one endpoint. Write handlers against the modern semantics. `--era` / `ContextClientOptions.Era` pins one.
- **A handler that asks for input is re-run on the modern era.** See [multi-round-trip.md](multi-round-trip.md).
- **Tool names are snake case by default** (`SaveNote` becomes `save_note`). Set `Name` on the attribute to choose another.
- **Unsolicited sampling, roots and logging** exist but are `[Obsolete]` with `OMC` diagnostic IDs, following the revision that deprecates them.

## Type names and namespaces

The official SDK uses an `Mcp` prefix (`McpClient`, `McpServer`, `McpException`, `McpServerTool`). OneModelContext avoids the abbreviation in
public identifiers, so names differ:

| There | Here |
|---|---|
| `McpClient` | `ContextClient` |
| `McpServer` | `ContextServer` (the object you register on); `RequestContext` (what a handler receives) |
| `McpServerTool` / `McpClientTool` | `ToolDefinition` / `Tool` |
| `McpException`, `McpProtocolException` | `ContextException`, `ProtocolException` (JSON-RPC error with code), `AuthorizationException` |
| `ModelContextProtocol.Protocol` | `RecursiveAI.OneModelContext.Protocol` |
| `ModelContextProtocol.Client` / `.Server` | `RecursiveAI.OneModelContext` (client and server both live in the root namespace) |

A project that references both libraries can keep them apart with namespace aliases:

```csharp
using Official = ModelContextProtocol.Client;
using OneModelContext = RecursiveAI.OneModelContext;

OneModelContext.ContextClient one = await OneModelContext.ModelContext.ConnectAsync("my-server");
```

Both libraries can run in one process. They share nothing but the wire protocol.

Model Context Protocol® is a registered trademark of LF Projects, LLC. OneModelContext is an independent project and is not affiliated with or endorsed by LF Projects, LLC or the Model Context Protocol project.
