using AspNetCoreServer;

// A Streamable HTTP server on ASP.NET Core. It listens on the loopback interface unless ASPNETCORE_URLS or --urls says otherwise.
await Site.Build(args).RunAsync();
