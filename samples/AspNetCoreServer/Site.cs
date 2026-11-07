using Microsoft.AspNetCore.Builder;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.AspNetCore;

namespace AspNetCoreServer;

public static class Site
{
  /// <summary>Builds the web application. Any middleware added before <c>MapModelContext</c> (authentication, rate limiting) runs before the protocol handler.</summary>
  public static WebApplication Build(string[] args, Action<WebApplication>? beforeMap = null)
  {
    var builder = WebApplication.CreateSlimBuilder(args);
    var app = builder.Build();

    var server = new ContextServer("aspnetcore-sample", "1.0.0");
    new Greeter().AddTo(server);

    beforeMap?.Invoke(app);

    // One endpoint answers the 2026-07-28 revision and the older ones. Add .RequireAuthorization() once the host has an authentication scheme.
    app.MapModelContext("/mcp", server);
    return app;
  }
}
