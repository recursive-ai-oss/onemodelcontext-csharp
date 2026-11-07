# OneModelContextServer

A server for the Model Context Protocol®, built with OneModelContext.

```
dotnet run
dotnet publish -c Release      # a native executable, no runtime needed
```

Try it with the command line tool: `dotnet tool install -g RecursiveAI.OneModelContext.Cli`, then
`onemodelcontext inspect "dotnet run --project ."`.

Add tools in `Tools.cs`: a public method with `[Tool]`, parameters for the arguments and a doc comment for the descriptions.

Model Context Protocol® is a registered trademark of LF Projects, LLC. OneModelContext is an independent project and is not affiliated with or endorsed by LF Projects, LLC or the Model Context Protocol project.
