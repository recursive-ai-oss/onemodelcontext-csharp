using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.Extensions.Skills;
using RecursiveAI.OneModelContext.OAuth;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;

// conformance-client URL
//
// The suite starts this program with its test server's URL as the last argument and names the scenario in MCP_CONFORMANCE_SCENARIO.
// MCP_CONFORMANCE_PROTOCOL_VERSION, when set, is the one revision the client may speak. Whatever the scenario, the client connects, lists
// what the server offers and uses all of it with arguments built from the schemas, which is what each scenario observes on the wire.
var url = args.LastOrDefault();
if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var endpoint))
{
  Console.Error.WriteLine("usage: conformance-client URL");
  return 2;
}

var scenario = Environment.GetEnvironmentVariable("MCP_CONFORMANCE_SCENARIO") ?? "";
var version = Environment.GetEnvironmentVariable("MCP_CONFORMANCE_PROTOCOL_VERSION");
Console.Error.WriteLine($"scenario '{scenario}' against {endpoint.GetLeftPart(UriPartial.Authority)}{endpoint.AbsolutePath}");

var options = new ContextClientOptions
{
  Capabilities = new ClientCapabilities { Elicitation = new ElicitationCapability() },
  RequestHandler = new Answers(),
  Extensions = [SkillsExtension.Client()],
  EraCache = null,
  SupportedVersions = string.IsNullOrEmpty(version) ? ProtocolVersions.Supported : [version],
};

// The authorization scenarios run the OAuth client under the HTTP transport; the others connect plainly.
using var browser = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
OAuthClient? oauth = null;
HttpClient? authenticated = null;
if (scenario.StartsWith("auth/", StringComparison.Ordinal))
{
  oauth = new OAuthClient(AuthScenarios.Options(endpoint, scenario, new HeadlessUserAgent(new Uri("http://localhost:3000/callback"), browser)));
  authenticated = oauth.CreateHttpClient();
}

using var oauthLifetime = oauth;
using var authenticatedLifetime = authenticated;
ContextClient client;
try
{
  client = await ContextClient.ConnectAsync(ContextTransport.Http(endpoint, authenticated), options);
}
catch (ContextException ex)
{
  Console.Error.WriteLine($"connect: {ex.GetType().Name}: {ex.Message}");
  return 1;
}

await using var closing = client;
Console.Error.WriteLine($"negotiated {client.ProtocolVersion} ({client.Era})");

// Not a suite scenario: against this repository's conformance server, opens a subscription, makes the server change its lists and
// waits for the notifications (a check of change notifications over a real socket).
if (scenario == "listen")
{
  await using var subscription = await client.ListenAsync(new SubscriptionFilter { ToolsListChanged = true, PromptsListChanged = true });
  Console.Error.WriteLine($"acknowledged tools={subscription.Acknowledged.ToolsListChanged} prompts={subscription.Acknowledged.PromptsListChanged}");
  using var window = new CancellationTokenSource(TimeSpan.FromSeconds(10));
  var seen = new List<SubscriptionEventKind>();
  var reader = Task.Run(async () =>
  {
    await foreach (var item in subscription.ReadAllAsync(window.Token))
    {
      seen.Add(item.Kind);
      Console.Error.WriteLine($"event {item.Kind}");
      if (seen.Count == 2)
      {
        return;
      }
    }
  });
  await client.CallToolAsync("test_trigger_tool_change");
  await client.CallToolAsync("test_trigger_prompt_change");
  try
  {
    await reader;
  }
  catch (OperationCanceledException)
  {
    Console.Error.WriteLine("timed out");
    return 1;
  }

  return seen.Contains(SubscriptionEventKind.ToolsListChanged) && seen.Contains(SubscriptionEventKind.PromptsListChanged) ? 0 : 1;
}

// The Skills extension scenarios watch how a host handles a skill: list, then load a skill and read its files only when needed, and
// refuse content that does not match the entry. This client loads every skill it is offered and reads every file the entry lists.
if (scenario.StartsWith("sep-2640-client", StringComparison.Ordinal))
{
  return await SkillsScenario.Run(client, scenario);
}

var failures = 0;
async Task Try(string what, Func<Task> work)
{
  try
  {
    await work();
  }
  catch (Exception ex) when (ex is ContextException or OperationCanceledException or TimeoutException)
  {
    failures++;
    Console.Error.WriteLine($"{what}: {ex.GetType().Name}: {ex.Message}");
  }
}

IReadOnlyList<Tool> tools = [];
await Try("tools/list", async () => tools = await client.ListToolsAsync());

// Some scenarios say which calls to make (MCP_CONFORMANCE_CONTEXT, a JSON object with toolCalls); otherwise every tool is called.
var planned = new List<(string Name, JsonElement Arguments)>();
if (Environment.GetEnvironmentVariable("MCP_CONFORMANCE_CONTEXT") is { Length: > 0 } context)
{
  using var document = JsonDocument.Parse(context);
  if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("toolCalls", out var calls) && calls.ValueKind == JsonValueKind.Array)
  {
    foreach (var call in calls.EnumerateArray())
    {
      planned.Add((call.GetProperty("name").GetString()!, call.TryGetProperty("arguments", out var a) ? a.Clone() : default));
    }
  }
}

if (planned.Count == 0)
{
  foreach (var tool in tools)
  {
    planned.Add((tool.Name, Sample.Arguments(tool, tools)));
  }
}

foreach (var (name, arguments) in planned)
{
  await Try($"tools/call {name}", async () =>
  {
    var result = await client.CallToolAsync(name, arguments);
    Console.Error.WriteLine($"called {name}: {result.Content.Count} block(s){(result.IsError == true ? ", isError" : "")}");
  });
}

IReadOnlyList<Resource> resources = [];
if (client.ServerCapabilities.Resources is not null)
{
  await Try("resources/list", async () => resources = await client.ListResourcesAsync());
  foreach (var resource in resources)
  {
    await Try($"resources/read {resource.Uri}", async () => await client.ReadResourceAsync(resource.Uri));
  }
}

if (client.ServerCapabilities.Prompts is not null)
{
  IReadOnlyList<Prompt> prompts = [];
  await Try("prompts/list", async () => prompts = await client.ListPromptsAsync());
  foreach (var prompt in prompts)
  {
    var arguments = (prompt.Arguments ?? []).ToDictionary(a => a.Name, _ => "test");
    await Try($"prompts/get {prompt.Name}", async () => await client.GetPromptAsync(prompt.Name, arguments));
  }
}

Console.Error.WriteLine(failures == 0 ? "done" : $"done with {failures} failed call(s)");
return 0;

/// <summary>Answers what a server asks of a client: it accepts an elicitation with the schema's defaults, so a form is always filled.</summary>
internal sealed class Answers : IClientRequestHandler
{
  public ValueTask<Result> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
  {
    if (method != Methods.ElicitationCreate)
    {
      throw new ProtocolException(-32601, "Method not found: " + method + ".");
    }

    var content = new Dictionary<string, JsonElement>();
    if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("requestedSchema", out var schema))
    {
      foreach (var (name, value) in Sample.Properties(schema))
      {
        content[name] = value;
      }
    }

    return ValueTask.FromResult<Result>(new ElicitResult { Action = "accept", Content = content });
  }
}

/// <summary>Values for a JSON Schema: the declared default, constant or first enum member, otherwise something of the right type.</summary>
internal static class Sample
{
  internal static JsonElement Arguments(Tool tool, IReadOnlyList<Tool> all)
  {
    var arguments = new JsonObject();
    foreach (var (name, value) in Properties(tool.InputSchema))
    {
      arguments[name] = JsonNode.Parse(value.GetRawText());
    }

    // The schema scenario asks a client to hand the schema it received for another tool back through an echo tool.
    if (tool.InputSchema.TryGetProperty("properties", out var properties) && properties.TryGetProperty("schema", out var schema)
      && schema.TryGetProperty("type", out var type) && type.GetString() == "object"
      && all.FirstOrDefault(t => t != tool && t.Name.StartsWith("json_schema", StringComparison.Ordinal)) is { } subject)
    {
      arguments["schema"] = JsonNode.Parse(subject.InputSchema.GetRawText());
    }

    return JsonSerializer.SerializeToElement(arguments);
  }

  private static JsonElement Nested(JsonElement schema)
  {
    var arguments = new JsonObject();
    foreach (var (name, value) in Properties(schema))
    {
      arguments[name] = JsonNode.Parse(value.GetRawText());
    }

    return JsonSerializer.SerializeToElement(arguments);
  }

  internal static IEnumerable<KeyValuePair<string, JsonElement>> Properties(JsonElement schema)
  {
    if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
    {
      yield break;
    }

    foreach (var property in properties.EnumerateObject())
    {
      yield return new(property.Name, ValueFor(property.Value, 0));
    }
  }

  private static JsonElement ValueFor(JsonElement schema, int depth)
  {
    if (schema.ValueKind == JsonValueKind.Object)
    {
      if (schema.TryGetProperty("default", out var fallback))
      {
        return fallback.Clone();
      }

      if (schema.TryGetProperty("const", out var constant))
      {
        return constant.Clone();
      }

      if (schema.TryGetProperty("enum", out var members) && members.ValueKind == JsonValueKind.Array && members.GetArrayLength() > 0)
      {
        return members[0].Clone();
      }

      var type = schema.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
      switch (type)
      {
        case "integer":
        case "number":
          return JsonSerializer.SerializeToElement(1);
        case "boolean":
          return JsonSerializer.SerializeToElement(true);
        case "array":
          return JsonSerializer.SerializeToElement(Array.Empty<int>());
        case "object" when depth < 4:
          return Nested(schema);
      }
    }

    return JsonSerializer.SerializeToElement("test");
  }
}

/// <summary>The browser of a headless run: requests the authorization URL and reads the redirect from the answer, without following it.</summary>
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

/// <summary>What each authorization scenario hands the client (in MCP_CONFORMANCE_CONTEXT) and how the client is set up for it.</summary>
internal static class AuthScenarios
{
  public static OAuthClientOptions Options(Uri url, string scenario, IAuthorizationUserAgent agent)
  {
    var context = Environment.GetEnvironmentVariable("MCP_CONFORMANCE_CONTEXT") is { Length: > 0 } text ? JsonDocument.Parse(text).RootElement : default;
    string? Ctx(string name) => context.ValueKind == JsonValueKind.Object && context.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    var clientId = Ctx("client_id");
    var secret = Ctx("client_secret");
    switch (scenario)
    {
      case "auth/client-credentials-basic":
        return new OAuthClientOptions(url) { ClientCredentials = new ClientCredentialsGrant(clientId!, secret!) };
      case "auth/client-credentials-jwt":
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
      case "auth/enterprise-managed-authorization":
        var idToken = Ctx("idp_id_token")!;
        return new OAuthClientOptions(url)
        {
          Enterprise = new EnterpriseManagedAuthorization(new Uri(Ctx("idp_token_endpoint")!), _ => ValueTask.FromResult(idToken), clientId!)
          {
            ClientSecret = secret,
            IdentityProviderClientId = Ctx("idp_client_id"),
          },
        };
      default:
        return new OAuthClientOptions(url)
        {
          UserAgent = agent,
          PreRegistered = scenario is "auth/pre-registration" or "auth/token-endpoint-auth-basic" or "auth/token-endpoint-auth-post" && clientId is not null ? new PreRegisteredClient(clientId, secret) : null,
          ClientIdMetadataUrl = Ctx("client_id_metadata_url") ?? "https://conformance-test.local/client-metadata.json",
        };
    }
  }
}

/// <summary>What the Skills extension scenarios watch a host do.</summary>
internal static class SkillsScenario
{
  internal static async Task<int> Run(ContextClient client, string scenario)
  {
    var refused = 0;
    var entries = await client.ListSkillsAsync();
    Console.Error.WriteLine($"listed {entries.Count} skill(s)");

    // Nobody approves a skill here, so nothing needs loading: listing alone must not cause a single file read (Skills extension,
    // "Lazy Retrieval"). The verification scenarios below load, which is what they observe.
    if (scenario == "sep-2640-client-no-prefetch")
    {
      return 0;
    }

    foreach (var listed in entries)
    {
      var entry = listed;
      try
      {
        // Loading starts by confirming the entry with skills/get, the way a host does after the user approves a skill. A server that
        // cannot answer it leaves the listed entry in force.
        try
        {
          entry = await client.GetSkillAsync(entry.Uri);
        }
        catch (SkillVerificationException)
        {
          Console.Error.WriteLine($"skills/get gave no usable entry for {entry.Uri}; using the listed one");
        }

        var loaded = await client.LoadSkillAsync(entry);
        Console.Error.WriteLine($"loaded {entry.Uri} ({loaded.SkillMd.Length} bytes)");
        foreach (var file in entry.Resources ?? [])
        {
          if (file.Uri == entry.Uri)
          {
            continue;
          }

          try
          {
            var bytes = await client.ReadSkillFileAsync(entry, file.Uri);
            Console.Error.WriteLine($"read {file.Uri} ({bytes.Length} bytes)");
          }
          catch (SkillVerificationException ex)
          {
            refused++;
            Console.Error.WriteLine($"refused {file.Uri}: {ex.Failure}");
          }
        }
      }
      catch (SkillVerificationException ex)
      {
        // Skills extension, "Verification on Read": the content is not used; the host refreshes the entry and starts again from it.
        refused++;
        Console.Error.WriteLine($"refused {entry.Uri}: {ex.Failure}");
        try
        {
          entry = await client.GetSkillAsync(entry.Uri);
          Console.Error.WriteLine($"refreshed {entry.Uri}");
        }
        catch (SkillVerificationException)
        {
          Console.Error.WriteLine($"skills/get gave no usable entry for {entry.Uri}");
        }
      }
    }

    Console.Error.WriteLine($"done, refused {refused}");
    return 0;
  }
}
