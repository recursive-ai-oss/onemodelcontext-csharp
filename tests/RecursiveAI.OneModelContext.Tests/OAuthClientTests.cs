using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Auth;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.Hosting;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.Jose;
using RecursiveAI.OneModelContext.OAuth;
using RecursiveAI.OneModelContext.Protocol;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>An MCP endpoint behind a resource server on the listener, validating the test authorization server's tokens.</summary>
internal sealed class TestProtectedResource : IAsyncDisposable
{
  private readonly HttpServer _http;
  private StreamableHttpEndpoint? _endpoint;
  private ResourceServer? _resourceServer;

  internal TestProtectedResource(TestAuthServer auth, Action<ResourceServerOptionsBuilder>? configure = null)
  {
    _http = new HttpServer(HandleAsync);
    _http.Start();
    Url = new Uri(_http.BaseAddress!, "/mcp");
    var validator = new JwtTokenValidator(auth.Issuer, new StaticJwksProvider(auth.Keys), auth.Time);
    var builder = new ResourceServerOptionsBuilder { AuthorizationServers = [auth.Issuer], ScopesSupported = ["files:read"] };
    configure?.Invoke(builder);
    _resourceServer = new ResourceServer(new ResourceServerOptions(Url, validator)
    {
      AuthorizationServers = builder.AuthorizationServers,
      ScopesSupported = builder.ScopesSupported,
    });
    var schema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
    var server = new ContextServer("oauth-test", "1");
    server.Tools.Add(new ToolDefinition("read", "Reads.", schema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("read ok")));
    server.Tools.Add(new ToolDefinition("write", "Writes.", schema) { RequiredScopes = ["files:write"] }, (_, _) => ValueTask.FromResult(CallToolResult.FromText("write ok")));
    _endpoint = new StreamableHttpEndpoint(server, new StreamableHttpOptions { ResourceServer = _resourceServer });
  }

  internal Uri Url { get; }

  public async ValueTask DisposeAsync()
  {
    await _http.DisposeAsync();
    _endpoint?.Dispose();
  }

  private ValueTask HandleAsync(IHttpExchange exchange) =>
    _endpoint is { } endpoint && (exchange.Request.Path == "/mcp" || (_resourceServer?.IsMetadataPath(exchange.Request.Path) ?? false))
      ? endpoint.HandleAsync(exchange)
      : TestAuthServer.WriteJsonAsync(exchange, 404, "{}");
}

internal sealed class ResourceServerOptionsBuilder
{
  internal IReadOnlyList<string> AuthorizationServers { get; set; } = [];

  internal IReadOnlyList<string> ScopesSupported { get; set; } = [];
}

/// <summary>The user agent of a test: asks the test authorization server what a browser would be redirected to.</summary>
internal sealed class TestUserAgent(TestAuthServer server, Uri redirect) : IAuthorizationUserAgent
{
  internal int Runs { get; private set; }

  internal List<Uri> Requests { get; } = [];

  internal string? Iss { get; set; } = "default";

  internal string? State { get; set; } = "default";

  public Uri RedirectUri => redirect;

  public ValueTask<Uri> AuthorizeAsync(Uri authorizationUrl, CancellationToken cancellationToken)
  {
    Runs++;
    Requests.Add(authorizationUrl);
    return ValueTask.FromResult(server.Authorize(authorizationUrl, Iss, State));
  }
}

public class OAuthClientTests
{
  private const string Meta = """{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"t","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}""";
  private static readonly Uri Redirect = new("http://127.0.0.1:9/callback");

  private static HttpRequestMessage Call(Uri url, string tool)
  {
    var request = new HttpRequestMessage(HttpMethod.Post, url)
    {
      Content = new StringContent($$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{},"_meta":{{{Meta}}}}}""", Encoding.UTF8, "application/json"),
    };
    request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2026-07-28");
    request.Headers.TryAddWithoutValidation("Mcp-Method", "tools/call");
    request.Headers.TryAddWithoutValidation("Mcp-Name", tool);
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
    return request;
  }

  private static async Task<string> TextAsync(HttpResponseMessage response)
  {
    var body = await response.Content.ReadAsStringAsync();
    return response.IsSuccessStatusCode ? JsonDocument.Parse(body).RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()! : body;
  }

  private static OAuthClientOptions Interactive(Uri resource, TestUserAgent agent, ITokenStore? store = null, TimeProvider? time = null) =>
    new(resource) { UserAgent = agent, Store = store ?? new MemoryTokenStore(), TimeProvider = time };

  [Fact]
  public async Task A_client_signs_in_against_the_resource_server_then_steps_up_with_the_union_of_scopes()
  {
    await using var auth = new TestAuthServer();
    await using var resource = new TestProtectedResource(auth);
    var agent = new TestUserAgent(auth, Redirect);
    using var oauth = new OAuthClient(Interactive(resource.Url, agent));
    using var http = new HttpClient(new OAuthHttpHandler(oauth));

    using var read = await http.SendAsync(Call(resource.Url, "read"), TestContext.Current.CancellationToken);
    Assert.Equal("read ok", await TextAsync(read));
    using var write = await http.SendAsync(Call(resource.Url, "write"), TestContext.Current.CancellationToken);
    Assert.Equal("write ok", await TextAsync(write));

    Assert.Equal(2, agent.Runs);
    var first = AuthorizationResponse.ParseQuery(agent.Requests[0].Query, out _);
    var second = AuthorizationResponse.ParseQuery(agent.Requests[1].Query, out _);
    Assert.Equal("files:read", first["scope"]);
    Assert.Equal("files:read files:write", second["scope"]);
    foreach (var query in new[] { first, second })
    {
      Assert.Equal("S256", query["code_challenge_method"]);
      Assert.Equal(resource.Url.AbsoluteUri, query["resource"]);
      Assert.Equal("code", query["response_type"]);
    }

    Assert.All(auth.TokenCalls, call =>
    {
      Assert.Equal(resource.Url.AbsoluteUri, call.Form["resource"]);
      Assert.True(call.Form.ContainsKey("code_verifier"));
    });
    Assert.Equal(1, auth.Registrations);

    // The later calls reuse the token: no further authorization.
    using var again = await http.SendAsync(Call(resource.Url, "write"), TestContext.Current.CancellationToken);
    Assert.Equal("write ok", await TextAsync(again));
    Assert.Equal(2, agent.Runs);
  }

  [Fact]
  public async Task A_response_with_the_wrong_state_is_refused_before_anything_is_redeemed()
  {
    await using var auth = new TestAuthServer();
    await using var resource = new TestProtectedResource(auth);
    var agent = new TestUserAgent(auth, Redirect) { State = "someone-elses-state" };
    using var oauth = new OAuthClient(Interactive(resource.Url, agent));
    using var http = new HttpClient(new OAuthHttpHandler(oauth));

    var ex = await Assert.ThrowsAsync<OAuthException>(() => http.SendAsync(Call(resource.Url, "read"), TestContext.Current.CancellationToken));

    Assert.Equal("state_mismatch", ex.Error);
    Assert.Empty(auth.TokenCalls);
  }

  [Theory]
  [InlineData("https://other.example")]
  [InlineData("HTTP://127.0.0.1")]
  [InlineData(null)]
  public async Task A_response_from_another_issuer_or_with_no_issuer_is_refused_before_redemption(string? iss)
  {
    await using var auth = new TestAuthServer();
    await using var resource = new TestProtectedResource(auth);
    var agent = new TestUserAgent(auth, Redirect) { Iss = iss };
    using var oauth = new OAuthClient(Interactive(resource.Url, agent));
    using var http = new HttpClient(new OAuthHttpHandler(oauth));

    var ex = await Assert.ThrowsAsync<OAuthException>(() => http.SendAsync(Call(resource.Url, "read"), TestContext.Current.CancellationToken));

    Assert.StartsWith("issuer_", ex.Error, StringComparison.Ordinal);
    Assert.Empty(auth.TokenCalls);
  }

  [Fact]
  public async Task An_issuer_that_differs_only_by_a_trailing_slash_or_case_is_not_the_recorded_issuer()
  {
    await using var auth = new TestAuthServer();
    await using var resource = new TestProtectedResource(auth);
    foreach (var iss in new[] { auth.Issuer + "/", auth.Issuer.ToUpperInvariant() })
    {
      var agent = new TestUserAgent(auth, Redirect) { Iss = iss };
      using var oauth = new OAuthClient(Interactive(resource.Url, agent));
      using var http = new HttpClient(new OAuthHttpHandler(oauth));
      var ex = await Assert.ThrowsAsync<OAuthException>(() => http.SendAsync(Call(resource.Url, "read"), TestContext.Current.CancellationToken));
      Assert.Equal("issuer_mismatch", ex.Error);
    }

    Assert.Empty(auth.TokenCalls);
  }

  [Fact]
  public void An_issuer_error_response_is_not_shown_when_the_issuer_does_not_match()
  {
    var callback = new Uri("http://127.0.0.1:9/callback?state=s&iss=https%3A%2F%2Fevil.example&error=access_denied&error_description=secret-looking-text");

    var ex = Assert.Throws<OAuthException>(() => AuthorizationResponse.Validate(callback, "s", "https://good.example", issParameterSupported: false));

    Assert.Equal("issuer_mismatch", ex.Error);
    Assert.DoesNotContain("access_denied", ex.Message, StringComparison.Ordinal);
    Assert.DoesNotContain("secret-looking-text", ex.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(true, "present", true)]
  [InlineData(true, "absent", false)]
  [InlineData(false, "present", true)]
  [InlineData(false, "absent", true)]
  public void The_iss_table_of_the_authorization_page_is_applied(bool advertised, string iss, bool accepted)
  {
    var callback = new Uri("http://127.0.0.1:9/callback?code=abc&state=s" + (iss == "present" ? "&iss=https%3A%2F%2Fgood.example" : string.Empty));

    if (accepted)
    {
      Assert.Equal("abc", AuthorizationResponse.Validate(callback, "s", "https://good.example", advertised).Code);
    }
    else
    {
      Assert.Throws<OAuthException>(() => AuthorizationResponse.Validate(callback, "s", "https://good.example", advertised));
    }
  }

  [Fact]
  public async Task Metadata_that_names_another_issuer_is_not_used()
  {
    await using var auth = new TestAuthServer { MetadataIssuer = "https://elsewhere.example" };
    await using var resource = new TestProtectedResource(auth);
    var agent = new TestUserAgent(auth, Redirect);
    using var oauth = new OAuthClient(Interactive(resource.Url, agent));
    using var http = new HttpClient(new OAuthHttpHandler(oauth));

    var ex = await Assert.ThrowsAsync<OAuthException>(() => http.SendAsync(Call(resource.Url, "read"), TestContext.Current.CancellationToken));

    Assert.Equal("metadata_issuer_mismatch", ex.Error);
    Assert.Equal(0, agent.Runs);
  }

  [Fact]
  public async Task A_server_without_pkce_support_is_not_authorized_against()
  {
    await using var auth = new TestAuthServer { PkceMethods = null };
    await using var resource = new TestProtectedResource(auth);
    var agent = new TestUserAgent(auth, Redirect);
    using var oauth = new OAuthClient(Interactive(resource.Url, agent));
    using var http = new HttpClient(new OAuthHttpHandler(oauth));

    var ex = await Assert.ThrowsAsync<OAuthException>(() => http.SendAsync(Call(resource.Url, "read"), TestContext.Current.CancellationToken));

    Assert.Equal("pkce_unsupported", ex.Error);
    Assert.Equal(0, agent.Runs);
  }

  [Fact]
  public async Task Protected_resource_metadata_for_another_resource_is_refused()
  {
    await using var auth = new TestAuthServer();
    await using var resource = new TestProtectedResource(auth);
    var agent = new TestUserAgent(auth, Redirect);
    // The client was told to use a different path than the one the metadata describes.
    var wrong = new Uri(resource.Url, "/other");
    using var oauth = new OAuthClient(Interactive(wrong, agent));
    using var http = new HttpClient(new OAuthHttpHandler(oauth));
    using var request = Call(wrong, "read");

    using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

    // /other is a 404 and has no challenge, so nothing was authorized.
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    Assert.Equal(0, agent.Runs);
  }

  [Fact]
  public async Task A_token_is_sent_only_to_the_origin_of_its_resource()
  {
    await using var auth = new TestAuthServer();
    await using var resource = new TestProtectedResource(auth);
    var seen = new List<string?>();
    await using var other = new HttpServer(e =>
    {
      seen.Add(e.Request.Headers.GetFirst("Authorization"));
      return TestAuthServer.WriteJsonAsync(e, 200, "{}");
    });
    other.Start();
    var agent = new TestUserAgent(auth, Redirect);
    using var oauth = new OAuthClient(Interactive(resource.Url, agent));
    using var http = new HttpClient(new OAuthHttpHandler(oauth));
    using var signedIn = await http.SendAsync(Call(resource.Url, "read"), TestContext.Current.CancellationToken);
    Assert.True(signedIn.IsSuccessStatusCode);

    using var elsewhere = await http.GetAsync(new Uri(other.BaseAddress!, "/mcp"), TestContext.Current.CancellationToken);
    using var sameHostOtherPort = await http.GetAsync(new Uri(other.BaseAddress!, "/.well-known/x"), TestContext.Current.CancellationToken);

    Assert.Equal(HttpStatusCode.OK, elsewhere.StatusCode);
    Assert.All(seen, header => Assert.Null(header));
    Assert.Equal(2, seen.Count);
  }

  [Fact]
  public async Task A_redirect_from_the_resource_is_returned_not_followed_and_carries_no_credential()
  {
    await using var auth = new TestAuthServer();
    var landing = new List<string?>();
    await using var target = new HttpServer(e =>
    {
      landing.Add(e.Request.Headers.GetFirst("Authorization"));
      return TestAuthServer.WriteJsonAsync(e, 200, "{}");
    });
    target.Start();
    var hits = 0;
    await using var resourceHost = new HttpServer(e =>
    {
      hits++;
      e.Response.SetHeader("Location", new Uri(target.BaseAddress!, "/stolen").AbsoluteUri);
      return TestAuthServer.WriteJsonAsync(e, 307, "{}");
    });
    resourceHost.Start();
    var url = new Uri(resourceHost.BaseAddress!, "/mcp");
    var store = new MemoryTokenStore();
    await store.SetAsync("token:" + url.GetLeftPart(UriPartial.Authority) + "/mcp", new TokenRecord { Issuer = auth.Issuer, AccessToken = "stored-token-0123456789" }, TestContext.Current.CancellationToken);
    using var oauth = new OAuthClient(Interactive(url, new TestUserAgent(auth, Redirect), store));
    using var http = new HttpClient(new OAuthHttpHandler(oauth));

    using var response = await http.SendAsync(Call(url, "read"), TestContext.Current.CancellationToken);

    Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
    Assert.Equal(1, hits);
    Assert.Empty(landing);
  }

  [Fact]
  public void A_handler_that_follows_redirects_is_refused()
  {
    using var oauth = new OAuthClient(new OAuthClientOptions(new Uri("https://mcp.test/mcp")));

    Assert.Throws<ArgumentException>(() => new OAuthHttpHandler(oauth, new SocketsHttpHandler { AllowAutoRedirect = true }));
  }

  [Fact]
  public async Task A_token_endpoint_that_redirects_is_a_failure_and_the_target_is_never_contacted()
  {
    await using var auth = new TestAuthServer();
    var landing = 0;
    await using var target = new HttpServer(e =>
    {
      landing++;
      return TestAuthServer.WriteJsonAsync(e, 200, "{}");
    });
    target.Start();
    auth.RedirectTo = new Uri(target.BaseAddress!, "/steal").AbsoluteUri;
    await using var resource = new TestProtectedResource(auth);
    using var oauth = new OAuthClient(Interactive(resource.Url, new TestUserAgent(auth, Redirect)));
    using var http = new HttpClient(new OAuthHttpHandler(oauth));

    var ex = await Assert.ThrowsAsync<OAuthException>(() => http.SendAsync(Call(resource.Url, "read"), TestContext.Current.CancellationToken));

    Assert.Contains("302", ex.Message, StringComparison.Ordinal);
    Assert.Equal(0, landing);
  }

  [Theory]
  [InlineData("code")]
  [InlineData("code_verifier")]
  public async Task An_error_text_that_echoes_a_secret_of_the_request_is_scrubbed(string echoed)
  {
    await using var auth = new TestAuthServer { ErrorDescriptionEcho = echoed };
    await using var resource = new TestProtectedResource(auth);
    var agent = new TestUserAgent(auth, Redirect);
    using var oauth = new OAuthClient(Interactive(resource.Url, agent));
    using var http = new HttpClient(new OAuthHttpHandler(oauth));

    var ex = await Assert.ThrowsAsync<OAuthException>(() => http.SendAsync(Call(resource.Url, "read"), TestContext.Current.CancellationToken));

    var secret = auth.TokenCalls.Single().Form[echoed];
    Assert.DoesNotContain(secret, ex.ToString(), StringComparison.Ordinal);
    Assert.Contains("[redacted]", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task No_diagnostic_event_or_exception_contains_the_tokens_of_a_session()
  {
    var events = new List<string>();
    using var subscription = ContextDiagnostics.Subscribe(e => events.Add(e.ToString() ?? string.Empty));
    await using var auth = new TestAuthServer();
    await using var resource = new TestProtectedResource(auth);
    var store = new MemoryTokenStore();
    using var oauth = new OAuthClient(Interactive(resource.Url, new TestUserAgent(auth, Redirect), store));
    using var http = new HttpClient(new OAuthHttpHandler(oauth));
    using var response = await http.SendAsync(Call(resource.Url, "read"), TestContext.Current.CancellationToken);
    var record = (await store.GetAsync("token:" + resource.Url.AbsoluteUri, TestContext.Current.CancellationToken))!;

    Assert.NotNull(record.AccessToken);
    Assert.DoesNotContain(record.AccessToken, string.Join('\n', events), StringComparison.Ordinal);
    Assert.DoesNotContain(record.AccessToken!, record.ToString(), StringComparison.Ordinal);
    Assert.DoesNotContain(record.RefreshToken!, record.ToString(), StringComparison.Ordinal);
  }

  [Fact]
  public async Task An_expired_token_is_refreshed_with_the_rotated_refresh_token()
  {
    var clock = new ManualTimeProvider();
    await using var auth = new TestAuthServer(clock) { AccessSeconds = 120, ScopesSupported = ["files:read", "offline_access"] };
    await using var resource = new TestProtectedResource(auth);
    var agent = new TestUserAgent(auth, Redirect);
    var store = new MemoryTokenStore();
    using var oauth = new OAuthClient(Interactive(resource.Url, agent, store, clock));
    using var http = new HttpClient(new OAuthHttpHandler(oauth));
    using var first = await http.SendAsync(Call(resource.Url, "read"), TestContext.Current.CancellationToken);
    Assert.True(first.IsSuccessStatusCode);
    var before = (await store.GetAsync("token:" + resource.Url.AbsoluteUri, TestContext.Current.CancellationToken))!;
    Assert.Contains("offline_access", agent.Requests[0].Query, StringComparison.Ordinal);

    clock.Advance(TimeSpan.FromMinutes(10));
    using var second = await http.SendAsync(Call(resource.Url, "read"), TestContext.Current.CancellationToken);

    Assert.True(second.IsSuccessStatusCode);
    Assert.Equal(1, agent.Runs);
    var refresh = auth.TokenCalls.Last();
    Assert.Equal("refresh_token", refresh.Form["grant_type"]);
    Assert.Equal(before.RefreshToken, refresh.Form["refresh_token"]);
    Assert.Equal(resource.Url.AbsoluteUri, refresh.Form["resource"]);
    var after = (await store.GetAsync("token:" + resource.Url.AbsoluteUri, TestContext.Current.CancellationToken))!;
    Assert.NotEqual(before.RefreshToken, after.RefreshToken);
  }

  [Fact]
  public async Task Registered_client_credentials_are_kept_per_issuer_and_never_reused_for_another()
  {
    await using var first = new TestAuthServer();
    await using var second = new TestAuthServer();
    await using var resource = new TestProtectedResource(first);
    var store = new MemoryTokenStore();
    var agent = new TestUserAgent(first, Redirect);
    using (var oauth = new OAuthClient(Interactive(resource.Url, agent, store)))
    using (var http = new HttpClient(new OAuthHttpHandler(oauth)))
    {
      using var response = await http.SendAsync(Call(resource.Url, "read"), TestContext.Current.CancellationToken);
      Assert.True(response.IsSuccessStatusCode);
    }

    Assert.Equal("dyn-1", (await store.GetAsync("client:" + first.Issuer, TestContext.Current.CancellationToken))!.ClientId);
    Assert.Null(await store.GetAsync("client:" + second.Issuer, TestContext.Current.CancellationToken));

    // The same client against a second server registers there on its own.
    await using var resource2 = new TestProtectedResource(second);
    var agent2 = new TestUserAgent(second, Redirect);
    using var oauth2 = new OAuthClient(Interactive(resource2.Url, agent2, store));
    using var http2 = new HttpClient(new OAuthHttpHandler(oauth2));
    using var response2 = await http2.SendAsync(Call(resource2.Url, "read"), TestContext.Current.CancellationToken);
    Assert.True(response2.IsSuccessStatusCode);
    Assert.Equal(1, second.Registrations);
    Assert.NotNull(await store.GetAsync("client:" + second.Issuer, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task Pre_registered_credentials_are_refused_at_another_issuer()
  {
    await using var auth = new TestAuthServer();
    await using var resource = new TestProtectedResource(auth);
    var agent = new TestUserAgent(auth, Redirect);
    var options = new OAuthClientOptions(resource.Url) { UserAgent = agent, PreRegistered = new PreRegisteredClient("client-1", "secret-0123456789", "https://some-other-issuer.example") };
    using var oauth = new OAuthClient(options);
    using var http = new HttpClient(new OAuthHttpHandler(oauth));

    var ex = await Assert.ThrowsAsync<OAuthException>(() => http.SendAsync(Call(resource.Url, "read"), TestContext.Current.CancellationToken));

    Assert.Equal("issuer_not_expected", ex.Error);
    Assert.Empty(auth.TokenCalls);
    Assert.Equal(0, agent.Runs);
  }

  [Fact]
  public async Task Step_up_stops_when_the_challenge_asks_for_nothing_new()
  {
    await using var auth = new TestAuthServer();
    var calls = 0;
    await using var stubborn = new HttpServer(e =>
    {
      calls++;
      e.Response.SetHeader("WWW-Authenticate", $"Bearer error=\"insufficient_scope\", scope=\"files:read\"");
      return TestAuthServer.WriteJsonAsync(e, 403, "{}");
    });
    stubborn.Start();
    var url = new Uri(stubborn.BaseAddress!, "/mcp");
    var store = new MemoryTokenStore();
    await store.SetAsync("token:" + url.AbsoluteUri, new TokenRecord { Issuer = auth.Issuer, AccessToken = "tok-0123456789abc", Scope = "files:read" }, TestContext.Current.CancellationToken);
    var agent = new TestUserAgent(auth, Redirect);
    using var oauth = new OAuthClient(Interactive(url, agent, store));
    using var http = new HttpClient(new OAuthHttpHandler(oauth));

    using var response = await http.SendAsync(Call(url, "read"), TestContext.Current.CancellationToken);

    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    Assert.Equal(1, calls);
    Assert.Equal(0, agent.Runs);
  }

  [Fact]
  public async Task Client_credentials_with_a_secret_authenticate_with_basic_and_get_a_token_for_the_resource()
  {
    await using var auth = new TestAuthServer { ExpectedClientSecret = "s3cret-0123456789" };
    await using var resource = new TestProtectedResource(auth);
    var options = new OAuthClientOptions(resource.Url) { ClientCredentials = new ClientCredentialsGrant("service-1", "s3cret-0123456789") { ExpectedIssuer = auth.Issuer } };
    using var oauth = new OAuthClient(options);
    using var http = new HttpClient(new OAuthHttpHandler(oauth));

    using var response = await http.SendAsync(Call(resource.Url, "read"), TestContext.Current.CancellationToken);

    Assert.Equal("read ok", await TextAsync(response));
    var call = Assert.Single(auth.TokenCalls);
    Assert.StartsWith("Basic ", call.Authorization, StringComparison.Ordinal);
    Assert.False(call.Form.ContainsKey("client_secret"));
    Assert.Equal("client_credentials", call.Form["grant_type"]);
    Assert.Equal(resource.Url.AbsoluteUri, call.Form["resource"]);
  }

  [Fact]
  public async Task Client_credentials_with_a_private_key_present_a_verifiable_assertion_and_no_secret()
  {
    using var key = RSA.Create(2048);
    await using var auth = new TestAuthServer { ClientAssertionKey = key };
    await using var resource = new TestProtectedResource(auth);
    var options = new OAuthClientOptions(resource.Url) { ClientCredentials = new ClientCredentialsGrant("service-2", key, "kid-1") };
    using var oauth = new OAuthClient(options);
    using var http = new HttpClient(new OAuthHttpHandler(oauth));

    using var response = await http.SendAsync(Call(resource.Url, "read"), TestContext.Current.CancellationToken);

    Assert.Equal("read ok", await TextAsync(response));
    var call = Assert.Single(auth.TokenCalls);
    Assert.Equal(ClientAssertion.AssertionType, call.Form["client_assertion_type"]);
    Assert.Null(call.Authorization);
    var payload = JsonDocument.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(call.Form["client_assertion"].Split('.')[1])).RootElement;
    Assert.Equal("service-2", payload.GetProperty("sub").GetString());
    Assert.Equal("service-2", payload.GetProperty("iss").GetString());
    Assert.Contains(auth.Issuer + "/token", payload.GetProperty("aud").EnumerateArray().Select(a => a.GetString()));
  }

  [Fact]
  public async Task Client_credentials_are_not_sent_to_an_authorization_server_other_than_the_pinned_one()
  {
    await using var auth = new TestAuthServer { ExpectedClientSecret = "s3cret-0123456789" };
    await using var resource = new TestProtectedResource(auth);
    var options = new OAuthClientOptions(resource.Url) { ClientCredentials = new ClientCredentialsGrant("service-1", "s3cret-0123456789") { ExpectedIssuer = "https://trusted.example" } };
    using var oauth = new OAuthClient(options);
    using var http = new HttpClient(new OAuthHttpHandler(oauth));

    var ex = await Assert.ThrowsAsync<OAuthException>(() => http.SendAsync(Call(resource.Url, "read"), TestContext.Current.CancellationToken));

    Assert.Equal("issuer_not_expected", ex.Error);
    Assert.Empty(auth.TokenCalls);
  }

  [Fact]
  public async Task Enterprise_managed_authorization_exchanges_the_id_token_then_redeems_the_grant()
  {
    await using var auth = new TestAuthServer();
    await using var idp = new TestAuthServer();
    await using var resource = new TestProtectedResource(auth);
    var enterprise = new EnterpriseManagedAuthorization(new Uri(idp.Issuer + "/token"), _ => ValueTask.FromResult("id-token-0123456789"), "mcp-client-1")
    {
      IdentityProviderClientId = "idp-client-1",
      ExpectedIssuer = auth.Issuer,
    };
    using var oauth = new OAuthClient(new OAuthClientOptions(resource.Url) { Enterprise = enterprise });
    using var http = new HttpClient(new OAuthHttpHandler(oauth));

    using var response = await http.SendAsync(Call(resource.Url, "read"), TestContext.Current.CancellationToken);

    Assert.Equal("read ok", await TextAsync(response));
    var exchange = Assert.Single(idp.TokenCalls);
    Assert.Equal("urn:ietf:params:oauth:token-type:id-jag", exchange.Form["requested_token_type"]);
    Assert.Equal(auth.Issuer, exchange.Form["audience"]);
    Assert.Equal(resource.Url.AbsoluteUri, exchange.Form["resource"]);
    Assert.Equal("id-token-0123456789", exchange.Form["subject_token"]);
    Assert.Equal("idp-client-1", exchange.Form["client_id"]);
    var redeem = Assert.Single(auth.TokenCalls);
    Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", redeem.Form["grant_type"]);
    Assert.Equal("idjag-0123456789", redeem.Form["assertion"]);
    Assert.Equal("mcp-client-1", redeem.Form["client_id"]);
  }

  [Fact]
  public async Task A_401_at_connect_is_an_authorization_exception_that_carries_the_challenge()
  {
    await using var auth = new TestAuthServer();
    await using var resource = new TestProtectedResource(auth);

    var ex = await Assert.ThrowsAsync<AuthorizationException>(async () => await ContextClient.ConnectAsync(ContextTransport.Http(resource.Url), cancellationToken: TestContext.Current.CancellationToken));

    Assert.Equal(401, ex.StatusCode);
    Assert.NotNull(ex.Challenge);
    Assert.EndsWith("/.well-known/oauth-protected-resource/mcp", ex.Challenge!.ResourceMetadata, StringComparison.Ordinal);
    Assert.False(ex.InsufficientScope);
  }

  [Fact]
  public async Task A_host_can_answer_authorization_exceptions_itself_with_the_request_hook()
  {
    await using var auth = new TestAuthServer();
    await using var resource = new TestProtectedResource(auth);
    var agent = new TestUserAgent(auth, Redirect);
    using var oauth = new OAuthClient(Interactive(resource.Url, agent));
    var transport = new HttpTransportOptions { AuthorizeRequest = (request, ct) => oauth.AuthorizeRequestAsync(request, ct) };
    var ct = TestContext.Current.CancellationToken;

    var first = await Assert.ThrowsAsync<AuthorizationException>(async () => await ContextClient.ConnectAsync(ContextTransport.Http(resource.Url, null, transport), cancellationToken: ct));
    await oauth.AuthorizeAsync(first.Challenge, null, stepUp: false, ct);
    await using var client = await ContextClient.ConnectAsync(ContextTransport.Http(resource.Url, null, transport), cancellationToken: ct);
    Assert.Equal("read ok", (await client.CallToolAsync("read", cancellationToken: ct)).Text);

    // Mid-session: the token lacks files:write.
    var second = await Assert.ThrowsAsync<AuthorizationException>(async () => await client.CallToolAsync("write", cancellationToken: ct));
    Assert.Equal(403, second.StatusCode);
    Assert.True(second.InsufficientScope);
    Assert.Equal("files:write", second.Challenge!.Scope);
    await oauth.AuthorizeAsync(second.Challenge, null, second.InsufficientScope, ct);
    Assert.Equal("write ok", (await client.CallToolAsync("write", cancellationToken: ct)).Text);
    Assert.Equal("files:read files:write", AuthorizationResponse.ParseQuery(agent.Requests[1].Query, out _)["scope"]);
  }

  [Fact]
  public async Task The_real_client_connects_and_steps_up_through_the_handler_with_no_help_from_the_host()
  {
    await using var auth = new TestAuthServer();
    await using var resource = new TestProtectedResource(auth);
    var agent = new TestUserAgent(auth, Redirect);
    using var oauth = new OAuthClient(Interactive(resource.Url, agent));
    using var http = oauth.CreateHttpClient();
    var ct = TestContext.Current.CancellationToken;

    await using var client = await ContextClient.ConnectAsync(ContextTransport.Http(resource.Url, http), cancellationToken: ct);

    Assert.Equal("read ok", (await client.CallToolAsync("read", cancellationToken: ct)).Text);
    Assert.Equal("write ok", (await client.CallToolAsync("write", cancellationToken: ct)).Text);
    Assert.Equal(2, agent.Runs);
  }

  [Fact]
  public void The_oauth_layer_references_no_protocol_types()
  {
    // Like the JSON-RPC layer, OAuth must stay reusable for other protocols: nothing in its namespace may mention a type from another
    // namespace of this library.
    const string BaseNamespace = "RecursiveAI.OneModelContext.OAuth";
    var assembly = typeof(OAuthClient).Assembly;
    var offenders = new List<string>();
    var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;

    void Check(Type? type, string where)
    {
      if (type is null)
      {
        return;
      }

      foreach (var argument in type.IsGenericType ? type.GetGenericArguments() : [])
      {
        Check(argument, where);
      }

      if (type.HasElementType)
      {
        Check(type.GetElementType(), where);
      }

      if (type.Assembly == assembly && type.Namespace is { } ns && ns != BaseNamespace && ns.StartsWith("RecursiveAI.OneModelContext", StringComparison.Ordinal))
      {
        offenders.Add(where + " -> " + type.FullName);
      }
    }

    foreach (var type in assembly.GetTypes().Where(t => t.Namespace == BaseNamespace))
    {
      Check(type.BaseType, type.Name);
      foreach (var field in type.GetFields(flags))
      {
        Check(field.FieldType, type.Name + "." + field.Name);
      }

      foreach (var property in type.GetProperties(flags))
      {
        Check(property.PropertyType, type.Name + "." + property.Name);
      }

      foreach (var method in type.GetMethods(flags).Cast<System.Reflection.MethodBase>().Concat(type.GetConstructors(flags)))
      {
        foreach (var parameter in method.GetParameters())
        {
          Check(parameter.ParameterType, type.Name + "." + method.Name);
        }

        if (method is System.Reflection.MethodInfo info)
        {
          Check(info.ReturnType, type.Name + "." + method.Name);
        }
      }
    }

    Assert.Empty(offenders);
  }

  [Fact]
  public void Metadata_with_invalid_utf8_in_a_string_is_read_without_that_value_and_does_not_throw()
  {
    // Found by fuzzing: JsonElement.GetString throws on invalid UTF-8, and the document is the server's to write.
    byte[] json = [.. "{\"resource\":\"https"u8, 0xA3, .. "://r.example/mcp\",\"authorization_servers\":[\"https://a.example\"]}"u8];

    var parsed = ResourceMetadata.Parse(json);

    Assert.NotNull(parsed);
    Assert.Null(parsed.Resource);
    Assert.Equal(["https://a.example"], parsed.AuthorizationServers);
  }

  [Fact]
  public void Bearer_challenges_are_parsed_with_quoted_and_unquoted_values_and_other_schemes_skipped()
  {
    var parsed = BearerChallenge.Parse([
      "Basic realm=\"x\", Bearer error=\"insufficient_scope\", scope=\"a b\", resource_metadata=\"https://r.example/.well-known/p\"",
      "Bearer realm=example, error_description=\"say \\\"hi\\\"\"",
    ]);

    Assert.Equal(2, parsed.Count);
    Assert.Equal("insufficient_scope", parsed[0].Error);
    Assert.Equal("a b", parsed[0].Scope);
    Assert.Equal("https://r.example/.well-known/p", parsed[0].ResourceMetadata);
    Assert.Equal("say \"hi\"", parsed[1].ErrorDescription);
    Assert.Empty(BearerChallenge.Parse(["", ",,,", "=== \"unterminated"]));
  }

  [Fact]
  public async Task The_file_store_is_owner_only_survives_a_restart_and_seals_with_a_protector()
  {
    var dir = Path.Combine(Path.GetTempPath(), "oauth-store-" + Guid.NewGuid().ToString("N"));
    try
    {
      var path = Path.Combine(dir, "nested", "tokens.json");
      var record = new TokenRecord { Issuer = "https://a.example", ClientId = "c", AccessToken = "access-0123456789", RefreshToken = "refresh-0123456789", ExpiresAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), Scope = "x y" };
      using (var store = new FileTokenStore(path))
      {
        await store.SetAsync("k", record, TestContext.Current.CancellationToken);
      }

      if (!OperatingSystem.IsWindows())
      {
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
      }

      using var reopened = new FileTokenStore(path);
      var read = await reopened.GetAsync("k", TestContext.Current.CancellationToken);
      Assert.Equal("refresh-0123456789", read!.RefreshToken);
      Assert.Equal(record.ExpiresAt, read.ExpiresAt);
      Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));

      var sealedPath = Path.Combine(dir, "sealed.json");
      using var sealedStore = new FileTokenStore(sealedPath, new XorProtector());
      await sealedStore.SetAsync("k", record, TestContext.Current.CancellationToken);
      Assert.DoesNotContain("refresh-0123456789", await File.ReadAllTextAsync(sealedPath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
      Assert.Equal("access-0123456789", (await sealedStore.GetAsync("k", TestContext.Current.CancellationToken))!.AccessToken);

      // A file the protector cannot open is an empty store, not an exception.
      using var wrong = new FileTokenStore(sealedPath);
      Assert.Null(await wrong.GetAsync("k", TestContext.Current.CancellationToken));
      await sealedStore.RemoveAsync("k", TestContext.Current.CancellationToken);
      Assert.Null(await sealedStore.GetAsync("k", TestContext.Current.CancellationToken));
    }
    finally
    {
      if (Directory.Exists(dir))
      {
        Directory.Delete(dir, recursive: true);
      }
    }
  }

  [Fact]
  public async Task The_loopback_receiver_returns_the_redirect_and_ignores_other_requests()
  {
    LoopbackRedirectReceiver? created = null;
    using var receiver = new LoopbackRedirectReceiver((url, token) =>
    {
      _ = Task.Run(async () =>
      {
        using var browser = new HttpClient();
        using var stray = await browser.GetAsync(new Uri(created!.RedirectUri, "/favicon.ico"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, stray.StatusCode);
        using var done = await browser.GetAsync(new Uri(created!.RedirectUri + "?code=abc&state=" + Uri.EscapeDataString(url.Query)), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
      }, token);
      return ValueTask.CompletedTask;
    });
    created = receiver;

    var callback = await receiver.AuthorizeAsync(new Uri("https://auth.example/authorize?x=1"), TestContext.Current.CancellationToken);

    Assert.Equal(receiver.RedirectUri.AbsolutePath, callback.AbsolutePath);
    Assert.Equal("abc", AuthorizationResponse.ParseQuery(callback.Query, out _)["code"]);
    Assert.True(receiver.RedirectUri.IsLoopback);
  }

  private sealed class XorProtector : ITokenProtector
  {
    public byte[] Protect(byte[] plain) => [.. plain.Select(b => (byte)(b ^ 0x5a))];

    public byte[] Unprotect(byte[] protectedBytes) => Protect(protectedBytes);
  }
}
