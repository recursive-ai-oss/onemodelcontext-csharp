using System.Globalization;
using System.Security.Cryptography;
using RecursiveAI.OneModelContext.AspNetCore;
using RecursiveAI.OneModelContext.Auth.Server;
using RecursiveAI.OneModelContext.Jose;

// conformance-authorization-server --port N --client-id ID [--client-secret S] --resource URI --callback-port P
//   Serves an authorization server on 127.0.0.1:N that approves every request for the one client it is told about, so that the
//   conformance suite (which plays the browser) can run its authorization-code scenarios against it. Port 0 picks a free port.
string Arg(string name, string? fallback = null)
{
  var i = Array.IndexOf(args, name);
  return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback ?? throw new ArgumentException("missing " + name);
}

var port = int.Parse(Arg("--port", "0"), CultureInfo.InvariantCulture);
var clientId = Arg("--client-id");
var secret = Arg("--client-secret", string.Empty);
var resource = Arg("--resource");
var callbackPort = Arg("--callback-port", "3000");

if (port == 0)
{
  // The issuer must be known before the routes are built, so find a free loopback port first.
  var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
  probe.Start();
  port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
  probe.Stop();
}

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();

// Loopback only: the suite runs on the same machine.
builder.WebHost.UseUrls("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
var app = builder.Build();
var issuer = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);

using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var store = new MemoryAuthorizationStore();
await store.SaveClientAsync(new RegisteredClient
{
  ClientId = clientId,
  Name = "Conformance client",
  RedirectUris = ["http://127.0.0.1:" + callbackPort + "/callback"],
  TokenEndpointAuthMethod = secret.Length == 0 ? "none" : "client_secret_basic",
  SecretHash = secret.Length == 0 ? null : ClientSecret.Hash(secret),
  GrantTypes = ["authorization_code", "refresh_token", "client_credentials"],
  CreatedAt = DateTimeOffset.UtcNow,
}, CancellationToken.None);

using var authorizationServer = new AuthorizationServer(new AuthorizationServerOptions
{
  Issuer = new Uri(issuer),
  Resources = [resource],
  SigningKeys = [JsonWebSigningKey.FromECDsa(key, "conformance-1")],
  Store = store,
  AllowMissingResource = true,
  AuthorizeUser = (_, _) => ValueTask.FromResult(AuthorizationDecision.Approve("conformance-user")),
});

// One line per request (method, path, status; never the query or headers) so a run can be followed.
app.Use(async (context, next) =>
{
  await next();
  Console.Error.WriteLine(context.Request.Method + " " + context.Request.Path + " -> " + context.Response.StatusCode.ToString(CultureInfo.InvariantCulture));
});
app.MapAuthorizationServer(authorizationServer);
await app.StartAsync();
Console.WriteLine("listening " + issuer);
await app.WaitForShutdownAsync();
