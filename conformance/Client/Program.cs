using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RecursiveAI.OneModelContext.OAuth;

// Runs one authorization scenario of the conformance suite. The suite appends the server URL as the argument and names the scenario and
// protocol version in the environment (MCP_CONFORMANCE_SCENARIO, MCP_CONFORMANCE_PROTOCOL_VERSION); some scenarios hand over credentials in
// MCP_CONFORMANCE_CONTEXT, a JSON object.
var url = new Uri(args[^1]);
var scenario = Environment.GetEnvironmentVariable("MCP_CONFORMANCE_SCENARIO") ?? string.Empty;
var version = Environment.GetEnvironmentVariable("MCP_CONFORMANCE_PROTOCOL_VERSION");
var context = Environment.GetEnvironmentVariable("MCP_CONFORMANCE_CONTEXT") is { Length: > 0 } text ? JsonNode.Parse(text)?.AsObject() : null;
var modern = version == "2026-07-28";
// The "browser" of a headless run: it requests the authorization URL and reads the redirect from the answer, without following it.
using var browserHttp = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
var agent = new HeadlessUserAgent(new Uri("http://localhost:3000/callback"), browserHttp);

var options = BuildOptions(url, scenario, context, agent);
using var oauth = new OAuthClient(options);
using var http = new HttpClient(new OAuthHttpHandler(oauth));
var client = new RpcClient(http, url, modern, version);
try
{
  if (!modern)
  {
    await client.CallAsync("initialize", new JsonObject
    {
      ["protocolVersion"] = version ?? "2025-11-25",
      ["capabilities"] = new JsonObject(),
      ["clientInfo"] = new JsonObject { ["name"] = "onemodelcontext-conformance", ["version"] = "0" },
    });
    await client.NotifyAsync("notifications/initialized");
  }

  var tools = await client.CallAsync("tools/list", new JsonObject());
  var first = tools?["tools"]?.AsArray().FirstOrDefault()?["name"]?.GetValue<string>();
  if (first is not null)
  {
    await client.CallAsync("tools/call", new JsonObject { ["name"] = first, ["arguments"] = new JsonObject() });
  }

  return 0;
}
catch (Exception ex) when (ex is OAuthException or HttpRequestException or InvalidOperationException)
{
  // The suite reads the checks from its own server; the text is for people.
  Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
  return 1;
}

static OAuthClientOptions BuildOptions(Uri url, string scenario, JsonObject? context, IAuthorizationUserAgent agent)
{
  string? Ctx(string name) => context?[name]?.GetValue<string>();
  var clientId = Ctx("client_id");
  var secret = Ctx("client_secret");
  if (scenario == "auth/client-credentials-basic")
  {
    return new OAuthClientOptions(url) { ClientCredentials = new ClientCredentialsGrant(clientId!, secret!) };
  }

  if (scenario == "auth/client-credentials-jwt")
  {
    var pem = Ctx("private_key_pem")!;
    AsymmetricAlgorithm key = ECDsa.Create();
    try
    {
      key.ImportFromPem(pem);
    }
    catch (CryptographicException)
    {
      key = RSA.Create();
      key.ImportFromPem(pem);
    }

    return new OAuthClientOptions(url) { ClientCredentials = new ClientCredentialsGrant(clientId!, key, Ctx("kid")) };
  }

  if (scenario == "auth/enterprise-managed-authorization")
  {
    var idToken = Ctx("idp_id_token")!;
    return new OAuthClientOptions(url)
    {
      Enterprise = new EnterpriseManagedAuthorization(new Uri(Ctx("idp_token_endpoint")!), _ => ValueTask.FromResult(idToken), clientId!)
      {
        ClientSecret = secret,
        IdentityProviderClientId = Ctx("idp_client_id"),
      },
    };
  }

  var preRegistered = scenario is "auth/pre-registration" or "auth/token-endpoint-auth-basic" or "auth/token-endpoint-auth-post" && clientId is not null
    ? new PreRegisteredClient(clientId, secret)
    : null;
  return new OAuthClientOptions(url)
  {
    UserAgent = agent,
    PreRegistered = preRegistered,
    ClientIdMetadataUrl = Ctx("client_id_metadata_url") ?? "https://conformance-test.local/client-metadata.json",
  };
}

internal sealed class HeadlessUserAgent(Uri redirect, HttpClient http) : IAuthorizationUserAgent
{
  public Uri RedirectUri => redirect;

  public async ValueTask<Uri> AuthorizeAsync(Uri authorizationUrl, CancellationToken cancellationToken)
  {
    using var response = await http.GetAsync(authorizationUrl, cancellationToken);
    return response.Headers.Location is { } location
      ? (location.IsAbsoluteUri ? location : new Uri(authorizationUrl, location))
      : throw new InvalidOperationException("The authorization endpoint did not redirect.");
  }
}

internal sealed class RpcClient(HttpClient http, Uri url, bool modern, string? version)
{
  private int _id;
  private string? _session;

  public async Task<JsonNode?> CallAsync(string method, JsonObject parameters)
  {
    if (modern)
    {
      parameters["_meta"] = new JsonObject
      {
        ["io.modelcontextprotocol/protocolVersion"] = version,
        ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
        ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "onemodelcontext-conformance", ["version"] = "0" },
      };
    }

    var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++_id, ["method"] = method, ["params"] = parameters };
    using var response = await SendAsync(method, parameters["name"]?.GetValue<string>(), body);
    if (!response.IsSuccessStatusCode)
    {
      throw new HttpRequestException("The server answered " + (int)response.StatusCode + " to " + method + ".");
    }

    if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessions))
    {
      _session = sessions.First();
    }

    var text = await response.Content.ReadAsStringAsync();
    if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
    {
      text = text.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim()).FirstOrDefault(l => l.Contains("\"result\"", StringComparison.Ordinal) || l.Contains("\"error\"", StringComparison.Ordinal)) ?? "{}";
    }

    var message = JsonNode.Parse(text);
    return message?["result"];
  }

  public async Task NotifyAsync(string method)
  {
    using var response = await SendAsync(method, null, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method });
  }

  private Task<HttpResponseMessage> SendAsync(string method, string? name, JsonObject body)
  {
    var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
    if (modern)
    {
      request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", version);
      request.Headers.TryAddWithoutValidation("Mcp-Method", method);
      if (name is not null && method == "tools/call")
      {
        request.Headers.TryAddWithoutValidation("Mcp-Name", name);
      }
    }
    else if (_session is not null)
    {
      request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _session);
      request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", version ?? "2025-11-25");
    }

    return http.SendAsync(request);
  }
}
