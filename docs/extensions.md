# Extensions

An extension adds methods, notifications, `_meta` keys or capability settings to the Model Context Protocol (MCP) without a new core
revision. The mechanism is public and the official extensions use it. This page covers writing one and what MCP Apps adds. Source:
the MCP extensions overview and the Apps specification (revision 2026-01-26).

## Writing and registering an extension

An extension is off until the host opts in.

```csharp
var server = new ContextServer("demo", "1.0.0");
server.Extensions.Add(
    new ExtensionRegistration("com.example/echo", settings: default, revisions: null)
        .Request("com.example/echo/say", (parameters, context) => ValueTask.FromResult(parameters))
        .Notification("com.example/echo/ping", (parameters, peer, ct) => ValueTask.CompletedTask));
```

- **Identifier:** `{vendor-prefix}/{name}`, the prefix a reversed domain name you own (`ExtensionId.IsValid`). Prefixes the protocol reserves
  (`io.modelcontextprotocol`, `*.mcp`, `*.modelcontextprotocol`) are refused for third parties.
- **Settings:** a JSON object advertised to the peer. `ExtensionRegistration.Create<T>(id, settings, typeInfo)` serializes a record through
  source-generated metadata (AOT-safe).
- **Revisions:** the core revisions the extension works on; `null` means all. On other revisions it is not advertised and its methods answer
  method-not-found.
- **`MetaKeys`:** the `_meta` keys the extension owns, documented for peers; unknown `_meta` always survives decode and encode.
- **Registration is frozen** when added to the registry; change the registration before `Extensions.Add`.

### Negotiation

- **2026-07-28:** the client puts `extensions` in `_meta` `clientCapabilities` on every request; the server lists its extensions in
  `server/discover` capabilities.
- **2025-11-25 and earlier:** `capabilities.extensions` in `initialize`, both ways.
- A handler asks the request: `context.SupportsExtension(id)`, `context.TryGetExtensionSettings(id, out settings)` or
  `context.GetExtensionSettings<T>(id, typeInfo)`. `ContextClient.ServerExtensions` and `ServerSupportsExtension(id)` show what the server
  offers after connect, on either era.
- Extensions a peer declares that we do not know are kept and ignored.

### Methods and notifications

`Request(method, handler)` and `Notification(method, handler)` dispatch on both eras. Extension methods answer **only peers that declared
the extension**; others get method-not-found (-32601). The client calls them with `CallExtensionAsync(method, params)` and
`SendExtensionNotificationAsync`; a server notifies with `RequestContext.NotifyExtensionAsync`. A client registers notification handlers on
`ClientExtension.Notification`. Server-to-client extension *requests* and MRTR input requests inside extension calls are not supported.

### Degradation and required mode

By default a peer without the extension just gets the plain behaviour. `Required = true` on the registration rejects requests from peers that
lack it: **-32021** `MissingRequiredClientCapability` on 2026-07-28; on the legacy revisions, which define no such code, the existing
invalid-params mapping (**-32602**) with a message naming the extension. A `ClientExtension` with `Required = true` makes `ConnectAsync` throw
`ExtensionRequiredException` when the server lacks it.

### Client side

`ContextClientOptions.Extensions` takes `ClientExtension` entries (id, settings); only those are declared.

## MCP Apps (`io.modelcontextprotocol/ui`, stable)

The server declares `ui://` HTML resources and tools that point at them; a host that supports Apps renders the view. OneModelContext
renders nothing: it carries the resource, its metadata and a typed model of the view-to-host messages (`UiMessages`, `UiMethods`) so a host
can bridge them.

```csharp
server.UseApps();                                     // opt in
server.AddUiResource("ui://weather/forecast", "forecast_view", html, new UiResourceMeta { PrefersBorder = true });
server.AddAppTool(definition, "ui://weather/forecast", handler, visibility: ["model", "app"]);
```

From the generator: `[Tool(UiResource = "ui://weather/forecast", UiVisibility = new[] { "model", "app" })]` (a bad URI or visibility is
compile error OMCG0012). Register the resource with `AddUiResource`.

Behaviour:
- A peer supports Apps only if its extension settings list `text/html;profile=mcp-app` in `mimeTypes`. For any other peer `_meta.ui` is
  removed from tools, **app-only tools are hidden and cannot be called** (unknown tool, -32602), and the handler can return a text fallback
  (`context.SupportsApps()`).
- Because the tool list now depends on the caller, `tools/list` results are `cacheScope: private` whenever a tool adapter is registered.
- The flat `ui/resourceUri` key is read from other servers but never written.
- A `resourceUri` that is not `ui://` means no UI.
- `UiContentSecurityPolicy` builds the CSP and permission metadata and refuses values that could break out of the header. The secure default
  allows no external origins.
- UI resources appear in `resources/list`; read them with `client.ReadUiResourceAsync(uri)`, and `tool.GetUi()` gives a tool's UI metadata.
- `ui/initialize` is modelled with `clientInfo` and `appCapabilities`; fields the types do not know are preserved.

See `samples/AppsServer`.

The suite version pinned for this repository has no Apps or extension-negotiation scenarios; the behaviour is covered by unit tests on
both eras.
