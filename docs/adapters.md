# Adapters: `.Hosting` and `.Extensions.AI`

Two small packages, each referencing the core package and the one Microsoft package it adapts.

## `RecursiveAI.OneModelContext.Hosting`

References `Microsoft.Extensions.Hosting.Abstractions` only. The abstractions are enough: the hosted services derive from
`BackgroundService`, tool classes are created with `ActivatorUtilities`, and the logging bridge uses `LoggerMessage`. The generic host itself
(`Microsoft.Extensions.Hosting`) is the app's choice.

```csharp
builder.Services.AddSingleton<SearchIndex>();
builder.Services.AddContextServer<IndexTools>().UseStdio();   // or .UseListener(port: 3001), or .UseTransport(sp => ...)
```

- **Tool classes** (`AddTools<T>()`) are generated partial classes. The container supplies the constructor arguments, or the class's own
  registration is used when it has one. There is one instance for the life of the server, so a constructor takes singletons. `AddTools(factory)`
  creates the class without reflection. `Configure((server, services) => ...)` is for dynamic registration.
- **Scoped services** are not constructor parameters. Every tool, resource and prompt call runs in its own DI scope, and the handler reaches it
  with `context.GetRequiredService<T>()` (or `GetRequestServices()`); the scope is disposed when the call ends. The scope travels as an
  ambient value through the call's async flow, because generated invokers hold their class instance. On the modern era a handler that asks
  for input is re-run from the start on retry (see `multi-round-trip.md`), so it gets a new scope each attempt.
- **Lifetimes.** `UseStdio` and `UseTransport` serve one connection and ask the host to stop when it closes (stdin closed). `UseListener`
  runs the built-in HTTP listener on loopback with its default limits; `ContextListener.Started` completes with the bound address. Both stop
  with the host. Standard output carries the protocol in a stdio host, so send the console logger to standard error.
- **Logging.** Diagnostics events become `ILogger` entries in the category `RecursiveAI.OneModelContext`. Only the event text and an
  exception's type and message are logged (never the exception object or a stack), after bearer tokens, JWTs and token-like parameters
  (`access_token=`, `code=`, ...) are replaced by `[redacted]`.
- **Clients.** `AddContextClient(...)` registers a `ContextClientProvider`; `await provider.GetClientAsync()` connects on first use (the
  era is negotiated as usual) and reconnects after a closed connection.
- **Clock.** A `TimeProvider` registered in the container becomes the server's and client's clock.

## `RecursiveAI.OneModelContext.Extensions.AI`

References `Microsoft.Extensions.AI.Abstractions` only.

- `client.ToAIFunctionsAsync()` lists the server's tools as `AIFunction`s with the tool's name, description and input schema. The result of
  an invocation is the structured content when there is some, the text when every block is text, otherwise a list of `AIContent` (text,
  `DataContent` for images, audio and blobs, `UriContent` for resource links). A tool failure comes back as text starting with
  "Error: " so the model can react.
- `server.AddAIFunction(f)` / `AddAIFunctions(tools)` serve functions as tools. The function's arguments are `JsonElement`s. An exception
  in the function is a tool failure with the fixed text "The tool failed.".
- `ChatClientSampling.CreateHandler(chatClient)` answers `sampling/createMessage` for `ContextClientOptions.OnSample`. Sampling is deprecated
  in 2026-07-28 (`OMC0002`); the adapter suppresses the warning in its own implementation and your use of `OnSample` raises it at your call
  site. The server's `tools` and `toolChoice` are not forwarded.
- AOT: the adapter is trim- and AOT-annotated. Arguments from code (not from a model) are serialized with the function's
  `JsonSerializerOptions`, so types outside MEAI's defaults need a source-generated context on that function.

Model Context Protocol® is a registered trademark of LF Projects, LLC. OneModelContext is an independent project and is not affiliated with or endorsed by LF Projects, LLC or the Model Context Protocol project.
