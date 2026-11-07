using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.AspNetCore;
using RecursiveAI.OneModelContext.Auth;
using RecursiveAI.OneModelContext.Hosting;

namespace ProtectedServer;

public static class Site
{
  /// <summary>
  /// Builds a Streamable HTTP server whose endpoint needs a bearer token. The token here is static and kept hashed in memory, which suits a
  /// local server; a server on the internet validates JWTs or introspects tokens instead (see docs/resource-server.md).
  /// </summary>
  /// <param name="args">Command line; <c>--urls</c> picks the address (loopback port 5000 by default).</param>
  /// <param name="token">The token the caller must present. It is never logged.</param>
  /// <param name="authorizationServer">The issuer named in the protected resource metadata, so clients know where to log in.</param>
  public static WebApplication Build(string[] args, string token, string authorizationServer = "https://login.example.com")
  {
    var builder = WebApplication.CreateSlimBuilder(args);
    var origin = (builder.Configuration["urls"] ?? "http://127.0.0.1:5000").Split(';')[0].TrimEnd('/');

    var validator = new StaticTokenValidator().Add(token, "alice", ["notes:read"]);
    var resourceServer = new ResourceServer(new ResourceServerOptions(new Uri(origin + "/mcp"), validator)
    {
      AuthorizationServers = [authorizationServer],
      ScopesSupported = ["notes:read"],
    });

    builder.Services.AddAuthentication(ResourceServerDefaults.AuthenticationScheme).AddResourceServer(resourceServer);
    builder.Services.AddAuthorization();
    var app = builder.Build();
    app.UseAuthentication();
    app.UseAuthorization();

    var server = new ContextServer("protected-sample", "1.0.0");
    new Notes().AddTo(server);

    // Without a valid token the endpoint answers 401 with a WWW-Authenticate challenge that points at the protected resource metadata.
    app.MapModelContext("/mcp", server, new StreamableHttpOptions { ResourceServer = resourceServer }).RequireAuthorization();
    return app;
  }
}
