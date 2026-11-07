using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Auth;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.Hosting;
using RecursiveAI.OneModelContext.Protocol;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

public sealed class ResourceServerTests : IDisposable
{
  private const string Metadata = "https://mcp.test/.well-known/oauth-protected-resource/mcp";
  private const string AliceToken = "tok_alice_0123456789abcdef0123456789";
  private const string BobToken = "tok_bob_0123456789abcdef0123456789ab";
  private const string AdminToken = "tok_admin_0123456789abcdef012345678";
  private static readonly JsonElement ObjectSchema = Json.Parse("""{"type":"object"}""");

  private readonly List<AuditRecord> _audit = [];
  private readonly List<string> _principals = [];
  private readonly List<DiagnosticEvent> _events = [];
  private readonly IDisposable _subscription;
  private readonly StreamableHttpEndpoint _endpoint;
  private readonly ResourceServer _resourceServer;

  public ResourceServerTests()
  {
    var validator = new StaticTokenValidator()
      .Add(AliceToken, "alice", ["files:read"])
      .Add(BobToken, "bob", ["files:read", "files:write"])
      .Add(AdminToken, "admin", ["admin"]);
    _resourceServer = new ResourceServer(new ResourceServerOptions(new Uri(TestIssuer.Resource), validator)
    {
      AuthorizationServers = ["https://issuer.test"],
      ScopesSupported = ["files:read", "files:write"],
      ResourceName = "Test files",
      ScopeHierarchy = new ScopeHierarchy().Imply("admin", "files:write", "files:read"),
    });

    var server = new ContextServer("auth-test", "1.0", new ContextServerOptions { Audit = _audit.Add });
    server.Tools.Add(new ToolDefinition("read", "Reads.", ObjectSchema), (_, ctx) =>
    {
      _principals.Add(ctx.Principal?.FindFirst("sub")?.Value ?? "(none)");
      return ValueTask.FromResult(CallToolResult.FromText("read ok"));
    });
    server.Tools.Add(new ToolDefinition("write", "Writes.", ObjectSchema) { RequiredScopes = ["files:write"] }, (_, _) => ValueTask.FromResult(CallToolResult.FromText("write ok")));
    server.Resources.Add(new ResourceDefinition("file:///secret", "secret") { RequiredScopes = ["files:write"] }, (_, _) => throw new InvalidOperationException("not reached"));
    server.Prompts.Add(new PromptDefinition("review") { RequiredScopes = ["files:write"] }, (_, _) => throw new InvalidOperationException("not reached"));
    _endpoint = new StreamableHttpEndpoint(server, new StreamableHttpOptions { ResourceServer = _resourceServer });
    _subscription = ContextDiagnostics.Subscribe(_events.Add);
  }

  public void Dispose()
  {
    _subscription.Dispose();
    _endpoint.Dispose();
  }

  private static string Rpc(string method, string parameters, int id = 1) =>
    $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"{{{method}}}","params":{{{parameters}}}}""";

  private const string Meta = """{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"t","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}""";

  private TestExchange Modern(string method, string name, string parameters, string? token = null, params (string, string)[] more)
  {
    var headers = new List<(string, string)> { ("MCP-Protocol-Version", "2026-07-28"), ("Mcp-Method", method), ("Mcp-Name", name) };
    if (token is not null)
    {
      headers.Add(("Authorization", "Bearer " + token));
    }

    headers.AddRange(more);
    return TestExchange.Post(Rpc(method, parameters), [.. headers]).Run(_endpoint);
  }

  private TestExchange CallTool(string tool, string? token) =>
    Modern("tools/call", tool, $$"""{"name":"{{tool}}","arguments":{},"_meta":{{Meta}}}""", token);

  [Fact]
  public async Task A_request_without_a_token_is_challenged_with_the_metadata_url()
  {
    var exchange = CallTool("read", null);
    await exchange.BodyAsync();

    Assert.Equal(401, exchange.Status);
    Assert.Equal($"Bearer resource_metadata=\"{Metadata}\"", exchange.ResponseHeaders.GetFirst("WWW-Authenticate"));
    Assert.Empty(_principals);
  }

  [Fact]
  public async Task Server_wide_required_scopes_appear_in_the_challenge_and_gate_every_request()
  {
    var validator = new StaticTokenValidator().Add(AliceToken, "alice", ["files:read"]);
    var server = new ContextServer("t", "1");
    using var endpoint = new StreamableHttpEndpoint(server, new StreamableHttpOptions
    {
      ResourceServer = new ResourceServer(new ResourceServerOptions(new Uri(TestIssuer.Resource), validator) { RequiredScopes = ["mcp:use"] }),
    });

    var none = TestExchange.Post(Rpc("ping", "{}")).Run(endpoint);
    await none.BodyAsync();
    var lacking = TestExchange.Post(Rpc("ping", "{}"), ("Authorization", "Bearer " + AliceToken)).Run(endpoint);
    await lacking.BodyAsync();

    Assert.Equal(401, none.Status);
    Assert.Equal($"Bearer scope=\"mcp:use\", resource_metadata=\"{Metadata}\"", none.ResponseHeaders.GetFirst("WWW-Authenticate"));
    Assert.Equal(403, lacking.Status);
    Assert.Contains("error=\"insufficient_scope\"", lacking.ResponseHeaders.GetFirst("WWW-Authenticate"), StringComparison.Ordinal);
    Assert.Contains("scope=\"mcp:use\"", lacking.ResponseHeaders.GetFirst("WWW-Authenticate"), StringComparison.Ordinal);
  }

  [Fact]
  public async Task An_invalid_token_gets_401_invalid_token_and_the_token_is_not_echoed_anywhere()
  {
    const string Bad = "tok_wrong_0123456789abcdef0123456789";
    var exchange = CallTool("read", Bad);
    var body = await exchange.BodyAsync();

    Assert.Equal(401, exchange.Status);
    var challenge = exchange.ResponseHeaders.GetFirst("WWW-Authenticate")!;
    Assert.StartsWith("Bearer error=\"invalid_token\"", challenge, StringComparison.Ordinal);
    Assert.Contains("resource_metadata=\"" + Metadata + "\"", challenge, StringComparison.Ordinal);
    Assert.DoesNotContain(Bad, challenge + body, StringComparison.Ordinal);
    AssertNoTokenInDiagnostics(Bad);
  }

  [Fact]
  public async Task A_valid_token_reaches_the_handler_as_a_principal_and_the_audit_record()
  {
    var exchange = CallTool("read", AliceToken);
    var body = await exchange.JsonAsync();

    Assert.Equal(200, exchange.Status);
    Assert.Equal("read ok", body.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    Assert.Equal(["alice"], _principals);
    var record = Assert.Single(_audit);
    Assert.Equal("alice", record.Principal!.FindFirst("sub")!.Value);
    Assert.Equal("tools/call", record.Method);
    AssertNoTokenInDiagnostics(AliceToken);
  }

  [Fact]
  public async Task A_tool_that_needs_a_scope_gets_a_step_up_challenge_naming_it()
  {
    var denied = CallTool("write", AliceToken);
    await denied.BodyAsync();
    var allowed = CallTool("write", BobToken);
    var allowedBody = await allowed.JsonAsync();

    Assert.Equal(403, denied.Status);
    Assert.Equal($"Bearer error=\"insufficient_scope\", error_description=\"The token lacks a scope this operation needs.\", scope=\"files:write\", resource_metadata=\"{Metadata}\"", denied.ResponseHeaders.GetFirst("WWW-Authenticate"));
    Assert.Equal(200, allowed.Status);
    Assert.Equal("write ok", allowedBody.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
  }

  [Fact]
  public async Task A_broader_scope_satisfies_a_narrower_requirement()
  {
    var exchange = CallTool("write", AdminToken);
    await exchange.BodyAsync();

    Assert.Equal(200, exchange.Status);
  }

  [Fact]
  public async Task Resources_and_prompts_enforce_their_scopes_too()
  {
    var resource = Modern("resources/read", "file:///secret", $$"""{"uri":"file:///secret","_meta":{{Meta}}}""", AliceToken);
    await resource.BodyAsync();
    var prompt = Modern("prompts/get", "review", $$"""{"name":"review","_meta":{{Meta}}}""", AliceToken);
    await prompt.BodyAsync();

    Assert.Equal(403, resource.Status);
    Assert.Equal(403, prompt.Status);
  }

  [Fact]
  public async Task A_missing_tool_is_the_normal_protocol_error_not_an_authorization_failure()
  {
    var exchange = CallTool("nothing", AliceToken);
    var body = await exchange.JsonAsync();

    Assert.Equal(-32602, body.GetProperty("error").GetProperty("code").GetInt32());
  }

  [Theory]
  [InlineData("/.well-known/oauth-protected-resource/mcp")]
  [InlineData("/.well-known/oauth-protected-resource")]
  public async Task The_metadata_document_is_public_and_complete(string path)
  {
    var exchange = new TestExchange("GET") { Path = path }.Run(_endpoint);
    var doc = await exchange.JsonAsync();

    Assert.Equal(200, exchange.Status);
    Assert.Equal("application/json", exchange.ResponseHeaders.GetFirst("Content-Type"));
    Assert.Equal(TestIssuer.Resource, doc.GetProperty("resource").GetString());
    Assert.Equal(["https://issuer.test"], doc.GetProperty("authorization_servers").EnumerateArray().Select(e => e.GetString()));
    Assert.Equal(["files:read", "files:write"], doc.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString()));
    Assert.Equal(["header"], doc.GetProperty("bearer_methods_supported").EnumerateArray().Select(e => e.GetString()));
    Assert.Equal("Test files", doc.GetProperty("resource_name").GetString());
  }

  [Fact]
  public void A_resource_without_a_path_serves_only_the_root_metadata_path()
  {
    var rs = new ResourceServer(new ResourceServerOptions(new Uri("https://mcp.test"), new StaticTokenValidator()));

    Assert.Equal("https://mcp.test/.well-known/oauth-protected-resource", rs.MetadataUrl);
    Assert.True(rs.IsMetadataPath("/.well-known/oauth-protected-resource"));
    Assert.False(rs.IsMetadataPath("/.well-known/oauth-protected-resource/mcp"));
    Assert.Equal("https://mcp.test", rs.Resource);
  }

  [Fact]
  public async Task Only_a_get_of_the_metadata_path_is_public()
  {
    var post = TestExchange.Post("{}", ("Content-Type", "application/json")).Run(_endpoint);
    await post.BodyAsync();

    var other = new TestExchange("GET") { Path = "/.well-known/oauth-protected-resource/other" }.Run(_endpoint);
    await other.BodyAsync();

    Assert.Equal(401, post.Status);
    Assert.Equal(401, other.Status);
  }

  [Fact]
  public async Task Several_authorization_headers_are_a_bad_request_and_other_schemes_are_unauthenticated()
  {
    var two = TestExchange.Post(Rpc("ping", "{}"), ("Authorization", "Bearer " + AliceToken), ("Authorization", "Bearer " + BobToken)).Run(_endpoint);
    await two.BodyAsync();
    var basic = TestExchange.Post(Rpc("ping", "{}"), ("Authorization", "Basic dXNlcjpwYXNz")).Run(_endpoint);
    await basic.BodyAsync();
    var spaces = TestExchange.Post(Rpc("ping", "{}"), ("Authorization", "Bearer a b")).Run(_endpoint);
    await spaces.BodyAsync();

    Assert.Equal(400, two.Status);
    Assert.Equal(401, basic.Status);
    Assert.DoesNotContain("error=", basic.ResponseHeaders.GetFirst("WWW-Authenticate"), StringComparison.Ordinal);
    Assert.Equal(401, spaces.Status);
  }

  [Fact]
  public async Task The_bearer_scheme_name_is_case_insensitive()
  {
    var exchange = TestExchange.Post(Rpc("ping", "{}"), ("MCP-Protocol-Version", "2026-07-28"), ("Mcp-Method", "ping"), ("Authorization", "bEaReR " + AliceToken)).Run(_endpoint);
    await exchange.BodyAsync();

    Assert.NotEqual(401, exchange.Status);
  }

  [Fact]
  public async Task A_caller_the_host_authenticated_is_not_asked_for_a_token_but_its_scopes_are_checked()
  {
    var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "host-user"), new Claim("scope", "files:read")], "Cookies"));
    // A TestExchange carries its caller in User, as the ASP.NET Core adapter does.
    var a = new TestExchange("POST", new System.IO.MemoryStream(Encoding.UTF8.GetBytes(Rpc("tools/call", $$"""{"name":"read","arguments":{},"_meta":{{Meta}}}"""))), null, ("Content-Type", "application/json"), ("Accept", "application/json"), ("MCP-Protocol-Version", "2026-07-28"), ("Mcp-Method", "tools/call"), ("Mcp-Name", "read")) { User = principal }.Run(_endpoint);
    var b = new TestExchange("POST", new System.IO.MemoryStream(Encoding.UTF8.GetBytes(Rpc("tools/call", $$"""{"name":"write","arguments":{},"_meta":{{Meta}}}"""))), null, ("Content-Type", "application/json"), ("Accept", "application/json"), ("MCP-Protocol-Version", "2026-07-28"), ("Mcp-Method", "tools/call"), ("Mcp-Name", "write")) { User = principal }.Run(_endpoint);
    await a.BodyAsync();
    await b.BodyAsync();

    Assert.Equal(200, a.Status);
    Assert.Equal(403, b.Status);
  }

  [Fact]
  public async Task A_legacy_session_belongs_to_the_caller_that_created_it()
  {
    const string Init = """{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"1"}}""";
    var init = TestExchange.Post(Rpc("initialize", Init), ("Authorization", "Bearer " + AliceToken)).Run(_endpoint);
    await init.BodyAsync();
    var session = init.ResponseHeaders.GetFirst("Mcp-Session-Id")!;
    Assert.NotNull(session);

    var mine = TestExchange.Post(Rpc("ping", "{}", 2), ("Mcp-Session-Id", session), ("Authorization", "Bearer " + AliceToken)).Run(_endpoint);
    await mine.BodyAsync();
    var theirs = TestExchange.Post(Rpc("ping", "{}", 3), ("Mcp-Session-Id", session), ("Authorization", "Bearer " + BobToken)).Run(_endpoint);
    await theirs.BodyAsync();
    var delete = new TestExchange("DELETE", null, null, ("Mcp-Session-Id", session), ("Authorization", "Bearer " + BobToken)).Run(_endpoint);
    await delete.BodyAsync();

    Assert.Equal(200, mine.Status);
    Assert.Equal(404, theirs.Status);
    Assert.Equal(404, delete.Status);
  }

  [Fact]
  public async Task Legacy_clients_get_the_same_challenge_and_scope_checks()
  {
    const string Init = """{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"1"}}""";
    var anonymous = TestExchange.Post(Rpc("initialize", Init)).Run(_endpoint);
    await anonymous.BodyAsync();
    var init = TestExchange.Post(Rpc("initialize", Init), ("Authorization", "Bearer " + AliceToken)).Run(_endpoint);
    await init.BodyAsync();
    var session = init.ResponseHeaders.GetFirst("Mcp-Session-Id")!;
    var write = TestExchange.Post(Rpc("tools/call", """{"name":"write","arguments":{}}""", 5), ("Mcp-Session-Id", session), ("Authorization", "Bearer " + AliceToken)).Run(_endpoint);
    await write.BodyAsync();

    Assert.Equal(401, anonymous.Status);
    Assert.Equal(403, write.Status);
    Assert.Contains("scope=\"files:write\"", write.ResponseHeaders.GetFirst("WWW-Authenticate"), StringComparison.Ordinal);
  }

  [Fact]
  public async Task A_validator_that_throws_or_reports_an_outage_gives_503_and_leaks_nothing()
  {
    const string Secret = "tok_secret_0123456789abcdef0123456789";
    var throwing = new ResourceServer(new ResourceServerOptions(new Uri(TestIssuer.Resource), new DelegateTokenValidator((token, _, _) => throw new InvalidOperationException("boom " + token))));
    using var endpoint = new StreamableHttpEndpoint(new ContextServer("t", "1"), new StreamableHttpOptions { ResourceServer = throwing });

    var exchange = TestExchange.Post(Rpc("ping", "{}"), ("Authorization", "Bearer " + Secret)).Run(endpoint);
    var body = await exchange.BodyAsync();

    Assert.Equal(503, exchange.Status);
    Assert.DoesNotContain(Secret, body, StringComparison.Ordinal);
    AssertNoTokenInDiagnostics(Secret);
  }

  [Fact]
  public async Task A_validator_that_echoes_the_token_in_its_explanation_is_scrubbed()
  {
    const string Secret = "tok_echo_0123456789abcdef0123456789ab";
    var echo = new ResourceServer(new ResourceServerOptions(new Uri(TestIssuer.Resource), new DelegateTokenValidator((token, _, _) => ValueTask.FromResult(TokenValidationResult.Failure("no such token " + token)))));
    using var endpoint = new StreamableHttpEndpoint(new ContextServer("t", "1"), new StreamableHttpOptions { ResourceServer = echo });

    var exchange = TestExchange.Post(Rpc("ping", "{}"), ("Authorization", "Bearer " + Secret)).Run(endpoint);
    var body = await exchange.BodyAsync();

    Assert.Equal(401, exchange.Status);
    Assert.DoesNotContain(Secret, body + exchange.ResponseHeaders.GetFirst("WWW-Authenticate"), StringComparison.Ordinal);
    AssertNoTokenInDiagnostics(Secret);
  }

  [Fact]
  public void The_resource_must_be_https_or_loopback_without_a_fragment()
  {
    var validator = new StaticTokenValidator();
    Assert.Throws<ArgumentException>(() => new ResourceServer(new ResourceServerOptions(new Uri("http://mcp.example.com/mcp"), validator)));
    Assert.Throws<ArgumentException>(() => new ResourceServer(new ResourceServerOptions(new Uri("https://mcp.example.com/mcp#frag"), validator)));
    Assert.Throws<ArgumentException>(() => new ResourceServer(new ResourceServerOptions(new Uri("https://mcp.example.com/mcp"), validator) { AuthorizationServers = ["http://auth.example.com"] }));
    _ = new ResourceServer(new ResourceServerOptions(new Uri("http://127.0.0.1:5000/mcp"), validator));
  }

  [Fact]
  public void Scope_hierarchies_are_transitive_and_survive_cycles()
  {
    var h = new ScopeHierarchy().Imply("a", "b").Imply("b", "c").Imply("c", "a");

    Assert.True(h.Satisfies(["a"], "c"));
    Assert.True(h.Satisfies(["c"], "b"));
    Assert.False(h.Satisfies(["a"], "z"));
    Assert.False(h.Satisfies([], "a"));
  }

  [Fact]
  public async Task Without_a_resource_server_the_endpoint_does_not_authenticate_and_passes_the_host_caller_through()
  {
    using var endpoint = new StreamableHttpEndpoint(new ContextServer("t", "1"));

    var exchange = TestExchange.Post(Rpc("initialize", """{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"1"}}""")).Run(endpoint);
    await exchange.BodyAsync();

    Assert.Equal(200, exchange.Status);
  }

  private void AssertNoTokenInDiagnostics(string token)
  {
    lock (_events)
    {
      foreach (var e in _events)
      {
        Assert.DoesNotContain(token, e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(token, e.Exception?.ToString() ?? string.Empty, StringComparison.Ordinal);
      }
    }
  }
}
