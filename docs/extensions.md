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

## Experimental extensions

Tasks, Skills and Server Cards are Beta extensions. Their types carry `[Experimental]`, so using one is a compile error until the project
opts in with `<NoWarn>$(NoWarn);OMCX0001</NoWarn>` (Tasks), `OMCX0002` (Skills) or `OMCX0003` (Server Cards). They are off until the host
calls `UseTasks`, `UseSkills` or `UseServerCard`. A Beta extension leaves `[Experimental]` when it reaches Stable or Core; a change of its
`revision` ships in a minor release. All three live in core.

### Tasks (`io.modelcontextprotocol/tasks`)

A tool can answer `tools/call` with a task handle instead of its result; the client polls `tasks/get`, answers questions with
`tasks/update` and stops it with `tasks/cancel`. There is no `tasks/list`. The extension is specified against 2026-07-28, so earlier
revisions do not see it and the tool runs to completion inside the call.

```csharp
server.UseTasks(new TaskServerOptions { Store = new FileTaskStore("tasks"), Ttl = TimeSpan.FromHours(1) });
server.AddTaskTool(definition, async (arguments, task) =>
{
    await task.UpdateStatusAsync("indexing");          // shown to pollers
    var ok = await task.ConfirmAsync("Replace the old index?");   // the task becomes input_required until the client answers
    return CallToolResult.FromText(ok ? "done" : "kept");
}, new TaskToolOptions { RequireTasks = false });
```

```csharp
// Client
var outcome = await client.CallToolAsTaskAsync("reindex");        // needs TasksExtension.Client() in ContextClientOptions.Extensions
var final = outcome.Task is { } task ? await task.WaitAsync() : null;  // answers input requests with the client's handlers
```

- A caller that did not declare the extension gets the result inside the call, or `-32021` for a tool added with `RequireTasks`. A plain
  `CallToolAsync` on a client that declared the extension follows a task to its end.
- Input needed before the work starts goes in `TaskToolOptions.Prepare` (it can elicit, as any tool can); the task exists only after it
  returns. Questions during the work use the `TaskContext`. A task asks only for capabilities the creating request declared.
- Tasks belong to the principal that created them (issuer and subject; anonymous callers share one owner). Another principal's task is
  `-32602` not found, the same as an expired or unknown one.
- **Stores.** `ITaskStore` is a small compare-and-swap contract (`CreateAsync`, `GetAsync`, `UpdateAsync` on `Version`, `DeleteAsync`,
  `DeleteExpiredAsync`) that a relational store can implement. `MemoryTaskStore` (the default) holds tasks in one process: use it for a
  single instance only. `FileTaskStore` keeps them in a directory with a lock file and atomic renames: one machine, any number of processes
  there, and tasks survive a restart.
- **A task whose worker is gone stays `working` until its TTL.** The store has no worker heartbeat: if the process that ran the work dies,
  pollers keep seeing `working` until the task expires (default one hour, `TaskServerOptions.Ttl`). Clients should put a limit on how
  long they wait.
- **Limits and defaults** (all on `TaskServerOptions`): TTL one hour, poll interval one second, 256 running tasks per instance, 16
  outstanding input requests per task, 15 minutes for the client to answer.
- Task ids are bearer-like handles: they are never put in audit records, diagnostics or telemetry (the `Mcp-Name` header carries them, so
  keep access logs of the HTTP endpoint private).
- `notifications/tasks` over `subscriptions/listen` is not implemented (the suite skips that scenario as well); clients poll.
- The 2025-11-25 experimental core `tasks/*` methods are not implemented.

### Skills over MCP (`io.modelcontextprotocol/skills`)

A server publishes [Agent Skills](https://agentskills.io/specification) as resources under `skill://`, and describes them with
`skills/list` and `skills/get`; `resources/directory/read` lists folders. The extension is specified against 2026-07-28; earlier revisions
see the skill files as ordinary resources.

```csharp
server.UseSkills();                                            // directoryRead on, 100 per page
server.AddSkill(SkillDefinition.FromDirectory("skills/pdf-processing"));
server.AddSkill(SkillDefinition.FromDirectory("skills/refunds", pathPrefix: "acme/billing"));   // skill://acme/billing/refunds/...
```

- `SkillDefinition` reads the files once, so the digests published are those of the bytes served. It refuses a name that breaks the Agent
  Skills rules (1 to 64 of `a-z 0-9 -`, no leading, trailing or doubled hyphen), a skill path whose last segment is not the frontmatter
  name, a path segment outside `A-Z a-z 0-9 . _ ~ - + @ = ,`, and more than 512 files or 16 MiB. Symbolic links are skipped.
- `SKILL.md` is served as `text/markdown` with the skill's name and description; text files go as text and others as base64, so the digest
  (SHA-256 over the raw bytes, `sha256:` and lowercase hex) is the same either way. Nested skills share files with the enclosing skill.
- The frontmatter is read by a small YAML reader that supports mappings, sequences, quoted and plain scalars, block scalars, short flow
  collections and comments. Anchors, tags, complex keys and merge keys are refused with an error, not guessed. The reader is bounded in
  size (64 KiB) and depth, and runs under a fuzz target.
- The client finds the extension in the server's declaration and sends none of its own, so the server answers every caller on 2026-07-28
  (`SkillsExtension.Client()` is optional).
- **Client.** `ListSkillsAsync` and `GetSkillAsync` return `SkillEntry` values (frontmatter, files with digest and size, or dynamic).
  `LoadSkillAsync` reads `SKILL.md` and checks size, digest and that its frontmatter equals the entry's; `ReadSkillFileAsync` reads a file
  the entry lists and checks it. A mismatch, a file the entry does not list, an entry without `resources`, a skill over the limits throw
  `SkillVerificationException` (`Failure` says which) and the content is not returned. Recover by fetching the entry again with
  `GetSkillAsync`; a changed file set voids any approval your host stored. Nothing is fetched ahead of need.
- A digest match shows that content and entry agree, not that either is trustworthy: both come from the same server. Treat skill content as
  untrusted model input, tag it with the server it came from, and do not honour `allowed-tools` from it without the user's approval.
- Not built: skills with generated ("dynamic") content on the server side; clients read them but get no integrity check.

### Server Cards

A Server Card is a small public JSON document that describes one remote server before anyone connects (`application/mcp-server-card+json`,
schema `v1`). It does not list tools, resources or prompts.

```csharp
server.UseServerCard(server.CreateServerCard("com.example/weather", "Forecasts for any city.", "https://example.com/mcp"));
```

- **Publishing.** The HTTP core, the built-in listener and `MapModelContext` answer `GET <endpoint>/server-card` (and the CORS preflight)
  with the document, `Access-Control-Allow-Origin: *`, `Cache-Control: public, max-age=3600` and an `ETag`; a matching `If-None-Match`
  gets 304 and an `Accept` that excludes the type gets 406. The card is answered before the `Origin` check and before authentication
  because browsers must read it; the `Host` check still applies. A server without a card answers 404. A host that routes by path (the
  listener) must pass `<path>/server-card` to the endpoint; `ModelContext.ServeHttpAsync` and `MapModelContext` do. Put nothing private in a
  card. Serve it over HTTPS in production.
- `CreateServerCard` fills `version` from `serverInfo` and the remote's `supportedProtocolVersions` from the server. `UseServerCard` refuses a
  card that breaks the schema (name shape, lengths, version ranges, remote type or URL).
- **Fetching.** `ServerCardClient.FetchAsync(url, previousETag)` and `DiscoverAsync(origin)` (the AI Catalog at
  `/.well-known/ai-catalog.json`, entries of the card type with `url` or inline `data`, at most 100) go through `GuardedHttpFetcher`:
  public addresses only unless the caller's fetcher allow-lists more, no redirects, no cookies or credentials, a 256 KiB body cap and a
  timeout. Failures are `ServerCardException`.
- A card is advisory. Never use it for security or access decisions, and prefer what the live connection reports when the two differ.

The suite pinned for this repository has Tasks and Skills scenarios (run with the scenario list's `extension` revision) and none for
Server Cards, which unit tests cover over the listener and ASP.NET Core.
