using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Auth;
using RecursiveAI.OneModelContext.Auth.Server;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.Hosting;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.Jose;
using RecursiveAI.OneModelContext.Protocol;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

public sealed class AddressPolicyTests
{
  [Theory]
  [InlineData("127.0.0.1")]
  [InlineData("127.255.255.254")]
  [InlineData("10.0.0.1")]
  [InlineData("172.16.0.1")]
  [InlineData("172.31.255.255")]
  [InlineData("192.168.1.1")]
  [InlineData("169.254.169.254")]
  [InlineData("100.64.0.1")]
  [InlineData("0.0.0.0")]
  [InlineData("192.0.0.1")]
  [InlineData("192.0.2.5")]
  [InlineData("198.18.0.1")]
  [InlineData("198.51.100.1")]
  [InlineData("203.0.113.9")]
  [InlineData("224.0.0.1")]
  [InlineData("240.0.0.1")]
  [InlineData("255.255.255.255")]
  [InlineData("::")]
  [InlineData("::1")]
  [InlineData("::ffff:127.0.0.1")]
  [InlineData("::ffff:10.1.2.3")]
  [InlineData("::ffff:169.254.169.254")]
  [InlineData("fe80::1")]
  [InlineData("fc00::1")]
  [InlineData("fd12:3456::1")]
  [InlineData("fec0::1")]
  [InlineData("ff02::1")]
  [InlineData("64:ff9b::7f00:1")]
  [InlineData("2001:db8::1")]
  [InlineData("2001::1")]
  [InlineData("2002:7f00:1::1")]
  [InlineData("2002:0a00:0001::1")]
  [InlineData("::127.0.0.1")]
  public void Special_use_addresses_are_not_public(string text) => Assert.False(AddressPolicy.IsPublic(IPAddress.Parse(text)));

  [Theory]
  [InlineData("8.8.8.8")]
  [InlineData("1.1.1.1")]
  [InlineData("172.32.0.1")]
  [InlineData("100.128.0.1")]
  [InlineData("2606:4700:4700::1111")]
  [InlineData("2002:0808:0808::1")]
  [InlineData("::ffff:8.8.8.8")]
  public void Global_unicast_addresses_are_public(string text) => Assert.True(AddressPolicy.IsPublic(IPAddress.Parse(text)));

  [Fact]
  public void The_allow_list_admits_exactly_the_listed_networks()
  {
    var policy = new AddressPolicy().Allow("10.1.0.0/16").Allow("127.0.0.1").Allow("::1");

    Assert.True(policy.IsAllowed(IPAddress.Parse("10.1.200.3")));
    Assert.False(policy.IsAllowed(IPAddress.Parse("10.2.0.1")));
    Assert.True(policy.IsAllowed(IPAddress.Parse("127.0.0.1")));
    Assert.False(policy.IsAllowed(IPAddress.Parse("127.0.0.2")));
    Assert.True(policy.IsAllowed(IPAddress.Parse("::1")));
    Assert.True(policy.IsAllowed(IPAddress.Parse("::ffff:127.0.0.1")));
    Assert.True(policy.IsAllowed(IPAddress.Parse("8.8.8.8")));
    Assert.Throws<ArgumentException>(() => policy.Allow("10.0.0.0/33"));
    Assert.Throws<ArgumentException>(() => policy.Allow("not an address"));
  }
}

public sealed class GuardedFetcherTests : IDisposable
{
  private readonly LoopbackServer _server = new();

  public void Dispose() => _server.Dispose();

  private static GuardedFetchOptions Allowing(int maxBytes = 16 * 1024, TimeSpan? timeout = null, Func<string, CancellationToken, ValueTask<IPAddress[]>>? resolver = null) => new()
  {
    Addresses = new AddressPolicy().Allow("127.0.0.0/8"),
    MaxBytes = maxBytes,
    Timeout = timeout ?? TimeSpan.FromSeconds(5),
    Resolver = resolver,
    CertificateValidation = LoopbackServer.Trust,
  };

  [Fact]
  public async Task An_allow_listed_loopback_server_is_fetched_over_tls()
  {
    _server.AddJson("/doc", """{"a":1}""", "max-age=120");
    using var fetcher = new GuardedHttpFetcher(Allowing());

    var result = await fetcher.GetAsync(new Uri(_server.Url("/doc")), TestContext.Current.CancellationToken);

    Assert.True(result.Succeeded, result.Failure);
    Assert.Equal("application/json", result.ContentType);
    Assert.Equal("""{"a":1}""", Encoding.UTF8.GetString(result.Body));
    Assert.Equal(TimeSpan.FromSeconds(120), result.CacheLifetime);
  }

  [Theory]
  [InlineData("https://127.0.0.1:{0}/doc")]
  [InlineData("https://[::1]:{0}/doc")]
  [InlineData("https://[::ffff:127.0.0.1]:{0}/doc")]
  [InlineData("https://[::ffff:7f00:1]:{0}/doc")]
  [InlineData("https://2130706433:{0}/doc")]
  [InlineData("https://0x7f000001:{0}/doc")]
  [InlineData("https://0x7f.1:{0}/doc")]
  [InlineData("https://017700000001:{0}/doc")]
  [InlineData("https://127.1:{0}/doc")]
  [InlineData("https://localhost:{0}/doc")]
  [InlineData("https://LOCALHOST.:{0}/doc")]
  [InlineData("https://10.1.2.3:{0}/doc")]
  [InlineData("https://169.254.169.254:{0}/latest/meta-data")]
  [InlineData("https://[fd00::1]:{0}/doc")]
  [InlineData("https://192.168.0.10:{0}/doc")]
  public async Task Private_loopback_and_link_local_targets_in_every_spelling_are_refused_without_connecting(string template)
  {
    _server.AddJson("/doc", "{}");
    using var fetcher = new GuardedHttpFetcher(new GuardedFetchOptions { CertificateValidation = LoopbackServer.Trust });

    var result = await fetcher.GetAsync(new Uri(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, _server.Port)), TestContext.Current.CancellationToken);

    Assert.False(result.Succeeded);
    Assert.Equal(0, _server.Hits);
    Assert.Empty(result.Body);
  }

  [Fact]
  public async Task A_name_that_resolves_to_a_private_address_is_refused_and_the_resolved_address_is_what_is_dialled()
  {
    _server.AddJson("/doc", "{}");
    var answers = new Dictionary<string, IPAddress[]>
    {
      ["evil.test"] = [IPAddress.Parse("10.0.0.5")],
      ["mixed.test"] = [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("127.0.0.1")],
      ["loop.test"] = [IPAddress.Parse("127.0.0.1")],
      ["none.test"] = [],
    };
    ValueTask<IPAddress[]> Resolve(string host, CancellationToken _) => ValueTask.FromResult(answers[host]);

    using var strict = new GuardedHttpFetcher(new GuardedFetchOptions { Resolver = Resolve, CertificateValidation = LoopbackServer.Trust });
    foreach (var host in new[] { "evil.test", "mixed.test", "loop.test", "none.test" })
    {
      var refused = await strict.GetAsync(new Uri($"https://{host}:{_server.Port}/doc"), TestContext.Current.CancellationToken);
      Assert.False(refused.Succeeded, host);
    }

    Assert.Equal(0, _server.Hits);

    // Allow-listed, the name is dialled at the address the resolver gave, whatever the URL says.
    using var allowing = new GuardedHttpFetcher(Allowing(resolver: Resolve));
    var ok = await allowing.GetAsync(new Uri($"https://loop.test:{_server.Port}/doc"), TestContext.Current.CancellationToken);
    Assert.True(ok.Succeeded, ok.Failure);
    var evil = await allowing.GetAsync(new Uri($"https://evil.test:{_server.Port}/doc"), TestContext.Current.CancellationToken);
    Assert.False(evil.Succeeded);
  }

  [Theory]
  [InlineData("http://127.0.0.1:1/doc")]
  [InlineData("ftp://example.test/doc")]
  [InlineData("https://user:pw@example.test/doc")]
  [InlineData("https://example.test/doc#frag")]
  public async Task Only_plain_https_urls_are_tried(string url)
  {
    using var fetcher = new GuardedHttpFetcher(Allowing());

    var result = await fetcher.GetAsync(new Uri(url), TestContext.Current.CancellationToken);

    Assert.False(result.Succeeded);
  }

  [Fact]
  public async Task Plain_http_is_fetched_only_for_allow_listed_addresses_and_only_when_enabled()
  {
    using var plain = new LoopbackServer(tls: false);
    plain.AddJson("/doc", "{}");
    var options = new GuardedFetchOptions { Addresses = new AddressPolicy().Allow("127.0.0.1"), AllowHttpToAllowListedAddresses = true };
    using var enabled = new GuardedHttpFetcher(options);
    using var disabled = new GuardedHttpFetcher(new GuardedFetchOptions { Addresses = new AddressPolicy().Allow("127.0.0.1") });
    using var notListed = new GuardedHttpFetcher(new GuardedFetchOptions { AllowHttpToAllowListedAddresses = true });

    Assert.True((await enabled.GetAsync(new Uri(plain.Url("/doc")), TestContext.Current.CancellationToken)).Succeeded);
    Assert.False((await disabled.GetAsync(new Uri(plain.Url("/doc")), TestContext.Current.CancellationToken)).Succeeded);
    Assert.False((await notListed.GetAsync(new Uri(plain.Url("/doc")), TestContext.Current.CancellationToken)).Succeeded);
  }

  [Fact]
  public async Task Redirects_are_never_followed()
  {
    _server.Add("/redir", new LoopbackServer.Route(302, Location: _server.Url("/doc")));
    _server.AddJson("/doc", "{}");
    using var fetcher = new GuardedHttpFetcher(Allowing());

    var result = await fetcher.GetAsync(new Uri(_server.Url("/redir")), TestContext.Current.CancellationToken);

    Assert.False(result.Succeeded);
    Assert.Equal(1, _server.Hits);
  }

  [Fact]
  public async Task A_non_200_answer_is_a_failure()
  {
    _server.Add("/nope", new LoopbackServer.Route(404, Body: "{}"u8.ToArray()));
    _server.Add("/ok-ish", new LoopbackServer.Route(204));
    using var fetcher = new GuardedHttpFetcher(Allowing());

    Assert.False((await fetcher.GetAsync(new Uri(_server.Url("/nope")), TestContext.Current.CancellationToken)).Succeeded);
    Assert.False((await fetcher.GetAsync(new Uri(_server.Url("/ok-ish")), TestContext.Current.CancellationToken)).Succeeded);
  }

  [Fact]
  public async Task An_oversized_body_is_refused_whether_or_not_it_declares_its_length()
  {
    var big = new byte[5000];
    Array.Fill(big, (byte)'a');
    _server.Add("/declared", new LoopbackServer.Route(Body: big));
    using var fetcher = new GuardedHttpFetcher(Allowing(maxBytes: 1024));

    var declared = await fetcher.GetAsync(new Uri(_server.Url("/declared")), TestContext.Current.CancellationToken);

    Assert.False(declared.Succeeded);
    Assert.Contains("larger", declared.Failure, StringComparison.Ordinal);
  }

  [Fact]
  public async Task A_server_that_stops_sending_is_cut_off_at_the_timeout()
  {
    _server.Add("/slow", new LoopbackServer.Route(Body: new byte[100], Stall: true, ContentLength: 100));
    using var fetcher = new GuardedHttpFetcher(Allowing(timeout: TimeSpan.FromMilliseconds(400)));
    var started = DateTime.UtcNow;

    var result = await fetcher.GetAsync(new Uri(_server.Url("/slow")), TestContext.Current.CancellationToken);

    Assert.False(result.Succeeded);
    Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
  }

  [Fact]
  public async Task Cache_control_lifetimes_are_reported()
  {
    _server.AddJson("/none", "{}", "no-store");
    _server.AddJson("/age", "{}", "public, max-age=30");
    _server.AddJson("/plain", "{}");
    using var fetcher = new GuardedHttpFetcher(Allowing());

    Assert.Equal(TimeSpan.Zero, (await fetcher.GetAsync(new Uri(_server.Url("/none")), TestContext.Current.CancellationToken)).CacheLifetime);
    Assert.Equal(TimeSpan.FromSeconds(30), (await fetcher.GetAsync(new Uri(_server.Url("/age")), TestContext.Current.CancellationToken)).CacheLifetime);
    Assert.Null((await fetcher.GetAsync(new Uri(_server.Url("/plain")), TestContext.Current.CancellationToken)).CacheLifetime);
  }
}

public sealed class ClientMetadataDocumentTests : IDisposable
{
  private readonly LoopbackServer _server = new();
  private AsHarness? _h;

  public void Dispose()
  {
    _h?.Dispose();
    _server.Dispose();
  }

  private AsHarness Harness(int maxBytes = 16 * 1024, bool allowListed = true)
  {
    _h?.Dispose();
    return _h = new AsHarness(cimd: new ClientMetadataDocumentOptions
    {
      Fetch = new GuardedFetchOptions
      {
        Addresses = allowListed ? new AddressPolicy().Allow("127.0.0.1") : new AddressPolicy(),
        MaxBytes = maxBytes,
        CertificateValidation = LoopbackServer.Trust,
      },
    });
  }

  private string Document(string path, string? extra = null, string? clientId = null, string redirect = "http://127.0.0.1/cb", string? cache = "max-age=600")
  {
    var url = _server.Url(path);
    _server.AddJson(path, $$"""{"client_id":"{{clientId ?? url}}","client_name":"Fixture App","redirect_uris":["{{redirect}}"],"token_endpoint_auth_method":"none"{{extra}}}""", cache);
    return url;
  }

  [Fact]
  public async Task A_client_identified_by_a_metadata_document_completes_the_flow_with_pkce()
  {
    var h = Harness();
    var clientId = Document("/client.json");

    var (code, verifier) = await h.CodeAsync(clientId, "http://127.0.0.1:50100/cb");
    var tokens = await h.RedeemAsync(code, verifier, clientId, "http://127.0.0.1:50100/cb");

    Assert.True(tokens.TryGetProperty("access_token", out _));
    var asked = Assert.Single(h.Requests);
    Assert.True(asked.IsMetadataDocumentClient);
    Assert.Equal("Fixture App", asked.ClientName);
    Assert.Equal(clientId, asked.ClientId);
    var refreshed = await h.TokenAsync([("grant_type", "refresh_token"), ("refresh_token", tokens.GetProperty("refresh_token").GetString()), ("client_id", clientId)]);
    Assert.Equal(200, refreshed.Status);
  }

  [Fact]
  public async Task The_document_is_cached_per_cache_control_and_fetched_again_afterwards()
  {
    var h = Harness();
    var clientId = Document("/client.json");
    var (_, challenge) = AsHarness.Pkce();

    await h.AuthorizeAsync(challenge, ("client_id", clientId), ("redirect_uri", "http://127.0.0.1/cb"));
    await h.AuthorizeAsync(challenge, ("client_id", clientId), ("redirect_uri", "http://127.0.0.1/cb"));
    Assert.Equal(1, _server.Hits);

    h.Time.Advance(TimeSpan.FromMinutes(11));
    await h.AuthorizeAsync(challenge, ("client_id", clientId), ("redirect_uri", "http://127.0.0.1/cb"));
    Assert.Equal(2, _server.Hits);
  }

  [Fact]
  public async Task A_no_store_document_is_fetched_every_time()
  {
    var h = Harness();
    var clientId = Document("/client.json", cache: "no-store");
    var (_, challenge) = AsHarness.Pkce();

    await h.AuthorizeAsync(challenge, ("client_id", clientId), ("redirect_uri", "http://127.0.0.1/cb"));
    await h.AuthorizeAsync(challenge, ("client_id", clientId), ("redirect_uri", "http://127.0.0.1/cb"));

    Assert.Equal(2, _server.Hits);
  }

  [Fact]
  public async Task A_failed_fetch_is_remembered_briefly_so_a_bad_url_cannot_be_used_to_hammer_the_fetcher()
  {
    var h = Harness();
    var (_, challenge) = AsHarness.Pkce();
    var clientId = _server.Url("/missing.json");

    for (var i = 0; i < 5; i++)
    {
      var response = await h.AuthorizeAsync(challenge, ("client_id", clientId), ("redirect_uri", "http://127.0.0.1/cb"));
      Assert.Equal(400, response.Status);
    }

    Assert.Equal(1, _server.Hits);
  }

  [Fact]
  public async Task Without_an_allow_list_entry_a_loopback_document_is_never_fetched()
  {
    var h = Harness(allowListed: false);
    var clientId = Document("/client.json");
    var (_, challenge) = AsHarness.Pkce();

    var response = await h.AuthorizeAsync(challenge, ("client_id", clientId), ("redirect_uri", "http://127.0.0.1/cb"));

    Assert.Equal(400, response.Status);
    Assert.Equal(0, _server.Hits);
    Assert.Empty(h.Requests);
  }

  [Theory]
  [InlineData("https://127.0.0.1/c.json")]
  [InlineData("https://10.0.0.5/c.json")]
  [InlineData("https://169.254.169.254/latest/meta-data/")]
  [InlineData("https://[::1]/c.json")]
  [InlineData("https://[::ffff:7f00:1]/c.json")]
  [InlineData("https://2130706433/c.json")]
  [InlineData("https://0x7f.1/c.json")]
  [InlineData("https://localhost/c.json")]
  public async Task Metadata_urls_that_point_inside_are_refused_at_the_authorization_endpoint(string clientId)
  {
    using var h = new AsHarness();
    var (_, challenge) = AsHarness.Pkce();

    var response = await h.AuthorizeAsync(challenge, ("client_id", clientId), ("redirect_uri", "http://127.0.0.1/cb"));

    Assert.Equal(400, response.Status);
    Assert.Empty(h.Requests);
  }

  [Theory]
  [InlineData("mismatch")]
  [InlineData("secret_method")]
  [InlineData("client_secret")]
  [InlineData("no_redirects")]
  [InlineData("bad_redirect")]
  [InlineData("bad_grant")]
  [InlineData("html")]
  [InlineData("redirect_response")]
  [InlineData("not_json")]
  public async Task Malformed_or_unsafe_documents_do_not_make_a_client(string defect)
  {
    var h = Harness();
    var url = _server.Url("/client.json");
    switch (defect)
    {
      case "mismatch":
        Document("/client.json", clientId: "https://other.test/client.json");
        break;
      case "secret_method":
        _server.AddJson("/client.json", $$"""{"client_id":"{{url}}","redirect_uris":["http://127.0.0.1/cb"],"token_endpoint_auth_method":"client_secret_basic"}""");
        break;
      case "client_secret":
        Document("/client.json", extra: ",\"client_secret\":\"s\"");
        break;
      case "no_redirects":
        _server.AddJson("/client.json", $$"""{"client_id":"{{url}}"}""");
        break;
      case "bad_redirect":
        Document("/client.json", redirect: "javascript:alert(1)");
        break;
      case "bad_grant":
        Document("/client.json", extra: ",\"grant_types\":[\"client_credentials\"]");
        break;
      case "html":
        _server.Add("/client.json", new LoopbackServer.Route(ContentType: "text/html", Body: Encoding.UTF8.GetBytes($$"""{"client_id":"{{url}}","redirect_uris":["http://127.0.0.1/cb"]}""")));
        break;
      case "redirect_response":
        _server.Add("/client.json", new LoopbackServer.Route(302, Location: "https://elsewhere.test/c.json"));
        break;
      default:
        _server.Add("/client.json", new LoopbackServer.Route(Body: "not json"u8.ToArray()));
        break;
    }

    var (_, challenge) = AsHarness.Pkce();
    var response = await h.AuthorizeAsync(challenge, ("client_id", url), ("redirect_uri", "http://127.0.0.1/cb"));

    Assert.Equal(400, response.Status);
    Assert.Null(response.ResponseHeaders.GetFirst("Location"));
  }

  [Fact]
  public async Task An_oversized_document_is_refused()
  {
    var h = Harness(maxBytes: 1024);
    var padding = new string('x', 3000);
    var url = Document("/client.json", extra: $",\"pad\":\"{padding}\"");
    var (_, challenge) = AsHarness.Pkce();

    var response = await h.AuthorizeAsync(challenge, ("client_id", url), ("redirect_uri", "http://127.0.0.1/cb"));

    Assert.Equal(400, response.Status);
  }

  [Fact]
  public async Task A_redirect_uri_outside_the_document_is_refused()
  {
    var h = Harness();
    var url = Document("/client.json");
    var (_, challenge) = AsHarness.Pkce();

    var response = await h.AuthorizeAsync(challenge, ("client_id", url), ("redirect_uri", "https://evil.test/cb"));

    Assert.Equal(400, response.Status);
    Assert.Null(response.ResponseHeaders.GetFirst("Location"));
  }

  [Theory]
  [InlineData("http://example.test/c.json")]
  [InlineData("https://example.test")]
  [InlineData("https://example.test/a/../c.json")]
  [InlineData("https://example.test/a/./c.json")]
  [InlineData("https://example.test/a/%2e%2e/c.json")]
  [InlineData("https://user@example.test/c.json")]
  [InlineData("https://example.test/c.json#x")]
  public void Client_id_urls_must_follow_the_draft(string value)
  {
    Assert.False(ClientMetadataResolver.IsValidClientIdUrl(value, out _));
  }

  [Fact]
  public void A_plain_https_url_with_a_path_is_a_valid_client_id() => Assert.True(ClientMetadataResolver.IsValidClientIdUrl("https://example.test:8443/clients/app.json?v=2", out _));

  [Fact]
  public async Task A_pre_registered_client_wins_over_a_url_of_the_same_name()
  {
    var h = Harness();
    var url = Document("/client.json");
    await h.Store.SaveClientAsync(new RegisteredClient { ClientId = url, RedirectUris = ["https://pre.test/cb"], CreatedAt = h.Time.GetUtcNow() }, TestContext.Current.CancellationToken);
    var (_, challenge) = AsHarness.Pkce();

    var viaDocument = await h.AuthorizeAsync(challenge, ("client_id", url), ("redirect_uri", "http://127.0.0.1/cb"));
    var viaRegistration = await h.AuthorizeAsync(challenge, ("client_id", url), ("redirect_uri", "https://pre.test/cb"));

    Assert.Equal(400, viaDocument.Status);
    Assert.Equal(302, viaRegistration.Status);
    Assert.Equal(0, _server.Hits);
  }
}

public sealed class AuthorizationServerClientsTests : IDisposable
{
  private readonly AsHarness _h = new();

  public void Dispose() => _h.Dispose();

  private static string Basic(string id, string secret) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(id + ":" + secret));

  private Task<AsExchange> ClientCredentials(params (string Name, string? Value)[] form) =>
    _h.TokenAsync([("grant_type", "client_credentials"), ("resource", AsHarness.Resource), .. form]);

  [Fact]
  public async Task Client_credentials_work_with_basic_and_post_authentication_and_issue_no_refresh_token()
  {
    var basic = await _h.TokenAsync([("grant_type", "client_credentials"), ("resource", AsHarness.Resource), ("scope", "files:read admin")], ("Authorization", Basic("svc", AsHarness.ClientSecretValue)));
    var post = await ClientCredentials(("client_id", "svc"), ("client_secret", AsHarness.ClientSecretValue), ("scope", "files:write"));

    Assert.Equal(200, basic.Status);
    Assert.Equal("files:read", basic.Json.GetProperty("scope").GetString());
    Assert.False(basic.Json.TryGetProperty("refresh_token", out _));
    Assert.Equal(200, post.Status);
    var jwt = JsonWebToken.Parse(post.Json.GetProperty("access_token").GetString()!, 8192, out _)!;
    Assert.Equal("svc", jwt.Payload.GetProperty("sub").GetString());
    Assert.Equal(AsHarness.Resource, jwt.Payload.GetProperty("aud").GetString());
  }

  [Fact]
  public async Task Client_credentials_refuse_wrong_secrets_public_clients_and_mixed_methods()
  {
    var wrong = await ClientCredentials(("client_id", "svc"), ("client_secret", "nope"));
    var unknown = await ClientCredentials(("client_id", "ghost"), ("client_secret", "nope"));
    var publicClient = await ClientCredentials(("client_id", "app"));
    var both = await _h.TokenAsync([("grant_type", "client_credentials"), ("resource", AsHarness.Resource), ("client_id", "svc"), ("client_secret", AsHarness.ClientSecretValue)], ("Authorization", Basic("svc", AsHarness.ClientSecretValue)));
    var missingSecret = await ClientCredentials(("client_id", "svc"));
    var noResource = await _h.TokenAsync([("grant_type", "client_credentials"), ("client_id", "svc"), ("client_secret", AsHarness.ClientSecretValue)]);
    var tooMuch = await ClientCredentials(("client_id", "svc"), ("client_secret", AsHarness.ClientSecretValue), ("scope", "admin"));

    Assert.Equal(401, wrong.Status);
    Assert.Equal("invalid_client", wrong.Json.GetProperty("error").GetString());
    Assert.Equal("invalid_client", unknown.Json.GetProperty("error").GetString());
    Assert.Equal("unauthorized_client", publicClient.Json.GetProperty("error").GetString());
    Assert.Equal("invalid_request", both.Json.GetProperty("error").GetString());
    Assert.Equal("invalid_client", missingSecret.Json.GetProperty("error").GetString());
    Assert.Equal("invalid_target", noResource.Json.GetProperty("error").GetString());
    Assert.Equal("invalid_scope", tooMuch.Json.GetProperty("error").GetString());
  }

  [Fact]
  public async Task A_failed_basic_authentication_is_challenged()
  {
    var response = await _h.TokenAsync([("grant_type", "client_credentials"), ("resource", AsHarness.Resource)], ("Authorization", Basic("svc", "wrong")));

    Assert.Equal(401, response.Status);
    Assert.StartsWith("Basic", response.ResponseHeaders.GetFirst("WWW-Authenticate"), StringComparison.Ordinal);
  }

  [Fact]
  public async Task Unsupported_and_forbidden_grant_types_are_told_apart()
  {
    var unsupported = await _h.TokenAsync([("grant_type", "password"), ("client_id", "app")]);
    var forbidden = await _h.TokenAsync([("grant_type", "client_credentials"), ("client_id", "app")]);
    var jwtBearerOff = await _h.TokenAsync([("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"), ("client_id", "app")]);
    var wrongType = await _h.SendAsync(new AsExchange("POST", "/token", string.Empty, "{}", "application/json"));

    Assert.Equal("unsupported_grant_type", unsupported.Json.GetProperty("error").GetString());
    Assert.Equal("unauthorized_client", forbidden.Json.GetProperty("error").GetString());
    Assert.Equal("unsupported_grant_type", jwtBearerOff.Json.GetProperty("error").GetString());
    Assert.Equal("invalid_request", wrongType.Json.GetProperty("error").GetString());
  }

  private string Assertion(Action<Dictionary<string, object>>? change = null, string alg = "ES256")
  {
    var now = _h.Time.GetUtcNow().ToUnixTimeSeconds();
    var claims = new Dictionary<string, object>
    {
      ["iss"] = "kj",
      ["sub"] = "kj",
      ["aud"] = AsHarness.Issuer + "/token",
      ["exp"] = now + 120,
      ["jti"] = Guid.NewGuid().ToString("N"),
    };
    change?.Invoke(claims);
    var key = JsonWebSigningKey.FromECDsa(_h.ClientKey, "ck");
    Assert.Equal(alg, key.Algorithm);
    return key.SignJwt("JWT", JsonSerializer.SerializeToUtf8Bytes(claims));
  }

  private Task<AsExchange> WithAssertion(string assertion) =>
    _h.TokenAsync([("grant_type", "client_credentials"), ("resource", AsHarness.Resource), ("client_assertion_type", "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"), ("client_assertion", assertion)]);

  [Fact]
  public async Task Private_key_jwt_authenticates_a_client_and_an_assertion_works_once()
  {
    var assertion = Assertion();

    var first = await WithAssertion(assertion);
    var replay = await WithAssertion(assertion);

    Assert.Equal(200, first.Status);
    Assert.Equal(401, replay.Status);
  }

  [Theory]
  [InlineData("aud")]
  [InlineData("iss")]
  [InlineData("sub")]
  [InlineData("expired")]
  [InlineData("far_future")]
  [InlineData("no_jti")]
  [InlineData("no_exp")]
  public async Task Defective_client_assertions_are_refused(string defect)
  {
    var assertion = Assertion(c =>
    {
      switch (defect)
      {
        case "aud": c["aud"] = "https://other-as.test"; break;
        case "iss": c["iss"] = "someone"; break;
        case "sub": c["sub"] = "someone"; break;
        case "expired": c["exp"] = _h.Time.GetUtcNow().ToUnixTimeSeconds() - 300; break;
        case "far_future": c["exp"] = _h.Time.GetUtcNow().ToUnixTimeSeconds() + 36000; break;
        case "no_jti": c.Remove("jti"); break;
        default: c.Remove("exp"); break;
      }
    });

    var response = await WithAssertion(assertion);

    Assert.Equal(401, response.Status);
  }

  [Fact]
  public async Task An_assertion_signed_by_another_key_or_with_the_none_algorithm_is_refused()
  {
    using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var now = _h.Time.GetUtcNow().ToUnixTimeSeconds();
    var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["iss"] = "kj", ["sub"] = "kj", ["aud"] = AsHarness.Issuer, ["exp"] = now + 60, ["jti"] = "j1" });
    var forged = JsonWebSigningKey.FromECDsa(other, "ck").SignJwt("JWT", payload);
    var none = System.Buffers.Text.Base64Url.EncodeToString("""{"alg":"none"}"""u8) + "." + System.Buffers.Text.Base64Url.EncodeToString(payload) + ".";

    Assert.Equal(401, (await WithAssertion(forged)).Status);
    Assert.Equal(401, (await WithAssertion(none)).Status);
  }

  [Fact]
  public async Task A_secret_client_cannot_authenticate_with_an_assertion_and_the_reverse()
  {
    var assertionForSecretClient = await _h.TokenAsync([("grant_type", "client_credentials"), ("resource", AsHarness.Resource), ("client_id", "svc"), ("client_assertion_type", "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"), ("client_assertion", Assertion())]);
    var secretForKeyClient = await ClientCredentials(("client_id", "kj"), ("client_secret", "anything"));

    Assert.Equal(401, assertionForSecretClient.Status);
    Assert.Equal(401, secretForKeyClient.Status);
  }
}

public sealed class EnterpriseAuthorizationTests : IDisposable
{
  private const string IdpIssuer = "https://idp.test";

  private readonly ECDsa _idpKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
  private readonly AsHarness _h;
  private readonly JsonWebSigningKey _signer;

  public EnterpriseAuthorizationTests()
  {
    _signer = JsonWebSigningKey.FromECDsa(_idpKey, "idp-1");
    var jwks = new MemoryStream();
    using (var w = new Utf8JsonWriter(jwks))
    {
      w.WriteStartObject();
      w.WriteStartArray("keys");
      _signer.WritePublicJwk(w);
      w.WriteEndArray();
      w.WriteEndObject();
    }

    var set = JsonWebKeySet.Parse(jwks.ToArray())!;
    _h = new AsHarness(enterprise: new EnterpriseAuthorizationOptions
    {
      IdentityProviders = [new TrustedIdentityProvider(IdpIssuer, new StaticJwksProvider(set))],
    });
  }

  public void Dispose()
  {
    _h.Dispose();
    _idpKey.Dispose();
  }

  private string IdJag(Action<Dictionary<string, object>>? change = null, string type = "oauth-id-jag+jwt", JsonWebSigningKey? key = null)
  {
    var now = _h.Time.GetUtcNow().ToUnixTimeSeconds();
    var claims = new Dictionary<string, object>
    {
      ["iss"] = IdpIssuer,
      ["sub"] = "user-42",
      ["aud"] = AsHarness.Issuer,
      ["client_id"] = "svc",
      ["jti"] = Guid.NewGuid().ToString("N"),
      ["iat"] = now,
      ["exp"] = now + 300,
      ["resource"] = AsHarness.Resource,
      ["scope"] = "files:read files:write",
    };
    change?.Invoke(claims);
    return (key ?? _signer).SignJwt(type, JsonSerializer.SerializeToUtf8Bytes(claims));
  }

  private Task<AsExchange> Redeem(string assertion, params (string Name, string? Value)[] extra) =>
    _h.TokenAsync([("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"), ("assertion", assertion), ("client_id", "svc"), ("client_secret", AsHarness.ClientSecretValue), .. extra]);

  [Fact]
  public async Task A_valid_id_jag_yields_an_access_token_for_the_assertions_resource_without_a_refresh_token()
  {
    var response = await Redeem(IdJag());

    Assert.Equal(200, response.Status);
    var body = response.Json;
    Assert.Equal("files:read files:write", body.GetProperty("scope").GetString());
    Assert.Equal(AsHarness.Resource, body.GetProperty("resource").GetString());
    Assert.False(body.TryGetProperty("refresh_token", out _));
    var result = await _h.PlainValidator(await _h.PublishedKeysAsync()).ValidateAsync(body.GetProperty("access_token").GetString()!, new TokenValidationRequest(AsHarness.Resource), TestContext.Current.CancellationToken);
    Assert.True(result.IsValid, result.Description);
    Assert.Equal("user-42", result.Principal!.FindFirst("sub")!.Value);
    Assert.Equal("svc", result.Principal.FindFirst("client_id")!.Value);
  }

  [Fact]
  public async Task The_request_may_narrow_scope_and_pick_the_resource()
  {
    var response = await Redeem(IdJag(c => c["resource"] = new[] { AsHarness.Resource, AsHarness.OtherResource }), ("scope", "files:read"), ("resource", AsHarness.OtherResource));

    Assert.Equal(200, response.Status);
    Assert.Equal("files:read", response.Json.GetProperty("scope").GetString());
    Assert.Equal(AsHarness.OtherResource, response.Json.GetProperty("resource").GetString());
  }

  [Fact]
  public async Task Metadata_advertises_the_jwt_bearer_grant_and_the_id_jag_profile()
  {
    var doc = (await _h.SendAsync(new AsExchange("GET", "/.well-known/oauth-authorization-server"))).Json;

    Assert.Contains("urn:ietf:params:oauth:grant-type:jwt-bearer", doc.GetProperty("grant_types_supported").EnumerateArray().Select(e => e.GetString()));
    Assert.Equal(["urn:ietf:params:oauth:grant-profile:id-jag"], doc.GetProperty("authorization_grant_profiles_supported").EnumerateArray().Select(e => e.GetString()));
  }

  [Theory]
  [InlineData("aud_other")]
  [InlineData("aud_array_two")]
  [InlineData("aud_missing")]
  [InlineData("client_other")]
  [InlineData("expired")]
  [InlineData("future_iat")]
  [InlineData("too_long")]
  [InlineData("no_jti")]
  [InlineData("no_sub")]
  [InlineData("untrusted_iss")]
  [InlineData("wrong_type")]
  [InlineData("no_type")]
  [InlineData("bad_signature")]
  [InlineData("none")]
  public async Task Defective_id_jags_are_refused_with_invalid_grant(string defect)
  {
    var now = _h.Time.GetUtcNow().ToUnixTimeSeconds();
    string assertion;
    using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    switch (defect)
    {
      case "aud_other": assertion = IdJag(c => c["aud"] = "https://another-as.test"); break;
      case "aud_array_two": assertion = IdJag(c => c["aud"] = new[] { AsHarness.Issuer, "https://another-as.test" }); break;
      case "aud_missing": assertion = IdJag(c => c.Remove("aud")); break;
      case "client_other": assertion = IdJag(c => c["client_id"] = "app"); break;
      case "expired": assertion = IdJag(c => { c["iat"] = now - 900; c["exp"] = now - 300; }); break;
      case "future_iat": assertion = IdJag(c => { c["iat"] = now + 600; c["exp"] = now + 900; }); break;
      case "too_long": assertion = IdJag(c => c["exp"] = now + 7200); break;
      case "no_jti": assertion = IdJag(c => c.Remove("jti")); break;
      case "no_sub": assertion = IdJag(c => c.Remove("sub")); break;
      case "untrusted_iss": assertion = IdJag(c => c["iss"] = "https://evil-idp.test"); break;
      case "wrong_type": assertion = IdJag(type: "JWT"); break;
      case "no_type": assertion = IdJag(type: "at+jwt"); break;
      case "bad_signature": assertion = IdJag(key: JsonWebSigningKey.FromECDsa(stranger, "idp-1")); break;
      default:
        assertion = System.Buffers.Text.Base64Url.EncodeToString("""{"alg":"none","typ":"oauth-id-jag+jwt"}"""u8) + "." + System.Buffers.Text.Base64Url.EncodeToString("""{"iss":"https://idp.test","sub":"u","aud":"https://as.test","client_id":"svc","jti":"1","iat":1,"exp":99999999999}"""u8) + ".";
        break;
    }

    var response = await Redeem(assertion);

    Assert.Equal(400, response.Status);
    Assert.Equal("invalid_grant", response.Json.GetProperty("error").GetString());
    Assert.False(response.Json.TryGetProperty("access_token", out _));
  }

  [Fact]
  public async Task A_replayed_id_jag_is_refused_but_a_refused_request_does_not_spend_it()
  {
    var assertion = IdJag();
    var badScope = await Redeem(assertion, ("scope", "admin"));
    var first = await Redeem(assertion);
    var replay = await Redeem(assertion);

    Assert.Equal("invalid_scope", badScope.Json.GetProperty("error").GetString());
    Assert.Equal(200, first.Status);
    Assert.Equal(400, replay.Status);
    Assert.Equal("invalid_grant", replay.Json.GetProperty("error").GetString());
    Assert.Contains(_h.SecurityEvents, e => e.Contains("replayed", StringComparison.Ordinal));
  }

  [Fact]
  public async Task The_resource_must_be_one_the_assertion_and_this_server_both_allow()
  {
    var notInAssertion = await Redeem(IdJag(), ("resource", AsHarness.OtherResource));
    var unknown = await Redeem(IdJag(c => c["resource"] = "https://evil.test/mcp"));
    var ambiguous = await Redeem(IdJag(c => c["resource"] = new[] { AsHarness.Resource, AsHarness.OtherResource }));

    Assert.Equal("invalid_target", notInAssertion.Json.GetProperty("error").GetString());
    Assert.Equal("invalid_target", unknown.Json.GetProperty("error").GetString());
    Assert.Equal("invalid_target", ambiguous.Json.GetProperty("error").GetString());
  }

  [Fact]
  public async Task A_host_can_map_the_subject_or_refuse_the_user()
  {
    _h.Build([_h.Key1], enterprise: new EnterpriseAuthorizationOptions
    {
      IdentityProviders = [new TrustedIdentityProvider(IdpIssuer, new StaticJwksProvider(JsonWebKeySet.Parse(Encoding.UTF8.GetBytes((await PublicJwks()))!)!))],
      ResolveSubject = (assertion, _) => ValueTask.FromResult(assertion.Subject == "user-42" ? "local:" + assertion.Subject : null),
    });

    var mapped = await Redeem(IdJag());
    var refused = await Redeem(IdJag(c => c["sub"] = "intruder"));

    Assert.Equal(200, mapped.Status);
    Assert.Equal("local:user-42", JsonWebToken.Parse(mapped.Json.GetProperty("access_token").GetString()!, 8192, out _)!.Payload.GetProperty("sub").GetString());
    Assert.Equal("invalid_grant", refused.Json.GetProperty("error").GetString());
  }

  private Task<string> PublicJwks()
  {
    using var ms = new MemoryStream();
    using (var w = new Utf8JsonWriter(ms))
    {
      w.WriteStartObject();
      w.WriteStartArray("keys");
      _signer.WritePublicJwk(w);
      w.WriteEndArray();
      w.WriteEndObject();
    }

    return Task.FromResult(Encoding.UTF8.GetString(ms.ToArray()));
  }

  [Fact]
  public async Task A_public_client_may_present_an_id_jag_unless_the_host_requires_authentication()
  {
    await _h.Store.SaveClientAsync(new RegisteredClient
    {
      ClientId = "pub",
      RedirectUris = ["https://pub.test/cb"],
      GrantTypes = ["urn:ietf:params:oauth:grant-type:jwt-bearer"],
      CreatedAt = _h.Time.GetUtcNow(),
    }, TestContext.Current.CancellationToken);

    var allowed = await _h.TokenAsync([("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"), ("assertion", IdJag(c => c["client_id"] = "pub")), ("client_id", "pub")]);
    _h.Build([_h.Key1], enterprise: new EnterpriseAuthorizationOptions
    {
      IdentityProviders = [new TrustedIdentityProvider(IdpIssuer, new StaticJwksProvider(JsonWebKeySet.Parse(Encoding.UTF8.GetBytes(await PublicJwks()))!))],
      RequireConfidentialClient = true,
    });
    var refused = await _h.TokenAsync([("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"), ("assertion", IdJag(c => c["client_id"] = "pub")), ("client_id", "pub")]);

    Assert.Equal(200, allowed.Status);
    Assert.Equal("unauthorized_client", refused.Json.GetProperty("error").GetString());
  }
}

public sealed class AuthorizationServerFlowTests : IDisposable
{
  private readonly AsHarness _h = new();

  public void Dispose() => _h.Dispose();

  [Fact]
  public async Task Login_happens_at_the_host_and_resumes_through_a_signed_link()
  {
    var signedIn = false;
    _h.Decide = context => signedIn
      ? AuthorizationDecision.Approve("alice")
      : AuthorizationDecision.Interact(async (response, ct) =>
      {
        response.StatusCode = 200;
        response.SetHeader("Content-Type", "text/html");
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes("<form>login</form>"), ct);
      });
    var (verifier, challenge) = AsHarness.Pkce();

    var first = await _h.AuthorizeAsync(challenge);
    Assert.Equal(200, first.Status);
    Assert.Equal("<form>login</form>", first.BodyText);
    var resume = _h.Requests.Single().ResumeUrl;
    Assert.StartsWith("https://as.test/authorize?resume=", resume.AbsoluteUri, StringComparison.Ordinal);

    signedIn = true;
    var second = await _h.SendAsync(new AsExchange("GET", resume.AbsolutePath, resume.Query[1..]));

    Assert.Equal(302, second.Status);
    var q = second.RedirectQuery;
    Assert.Equal("st-1", q["state"]);
    Assert.Equal(AsHarness.Issuer, q["iss"]);
    var tokens = await _h.RedeemAsync(q["code"], verifier);
    Assert.Equal("files:read", tokens.GetProperty("scope").GetString());
    Assert.Equal(AsHarness.Resource, _h.Requests[1].Resource);
  }

  [Fact]
  public async Task A_host_can_redirect_to_an_upstream_identity_provider()
  {
    _h.Decide = _ => AuthorizationDecision.Redirect(new Uri("https://login.example.test/signin?return=x"));
    var (_, challenge) = AsHarness.Pkce();

    var response = await _h.AuthorizeAsync(challenge);

    Assert.Equal(302, response.Status);
    Assert.Equal("https://login.example.test/signin?return=x", response.ResponseHeaders.GetFirst("Location"));
  }

  [Fact]
  public async Task A_tampered_expired_or_foreign_resume_link_is_refused()
  {
    _h.Decide = _ => AuthorizationDecision.Interact((r, _) =>
    {
      r.StatusCode = 200;
      return Task.CompletedTask;
    });
    var (_, challenge) = AsHarness.Pkce();
    await _h.AuthorizeAsync(challenge);
    var resume = _h.Requests.Single().ResumeUrl;
    var token = Uri.UnescapeDataString(resume.Query.AsSpan("?resume=".Length));
    var dot = token.IndexOf('.', StringComparison.Ordinal);

    // Swap the redirect URI inside the signed payload for another registered one.
    var payload = Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(token.AsSpan(0, dot))).Replace("https://app.test/cb", "http://127.0.0.1/cb", StringComparison.Ordinal);
    var forged = System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload)) + token[dot..];
    var flipped = token[..^2] + (token[^2] == 'A' ? 'B' : 'A') + token[^1];

    foreach (var bad in new[] { forged, flipped, "garbage", token[..dot], string.Empty })
    {
      var response = await _h.SendAsync(new AsExchange("GET", "/authorize", "resume=" + Uri.EscapeDataString(bad)));
      Assert.Equal(400, response.Status);
      Assert.Null(response.ResponseHeaders.GetFirst("Location"));
    }

    _h.Time.Advance(TimeSpan.FromMinutes(16));
    var expired = await _h.SendAsync(new AsExchange("GET", "/authorize", resume.Query[1..]));
    Assert.Equal(400, expired.Status);
    Assert.Single(_h.Requests);
  }

  [Fact]
  public async Task A_resume_link_from_one_server_key_is_worthless_to_a_server_with_another()
  {
    _h.Decide = _ => AuthorizationDecision.Interact((r, _) => Task.CompletedTask);
    var (_, challenge) = AsHarness.Pkce();
    await _h.AuthorizeAsync(challenge);
    var resume = _h.Requests.Single().ResumeUrl;

    using var other = new AsHarness();
    var response = await other.SendAsync(new AsExchange("GET", "/authorize", resume.Query[1..]));

    Assert.Equal(400, response.Status);
  }

  [Fact]
  public async Task Registration_is_off_by_default_and_a_deprecated_option_turns_it_on()
  {
    Assert.False(await _h.Server.HandleAsync(new AsExchange("POST", "/register", string.Empty, "{}", "application/json")));

    using var h = new AsHarness(registration: true);
    var body = """{"redirect_uris":["http://127.0.0.1/cb"],"application_type":"native","token_endpoint_auth_method":"none","client_name":"Dyn"}""";
    var created = await h.SendAsync(new AsExchange("POST", "/register", string.Empty, body, "application/json"));

    Assert.Equal(201, created.Status);
    var clientId = created.Json.GetProperty("client_id").GetString()!;
    Assert.False(created.Json.TryGetProperty("client_secret", out _));
    var metadata = (await h.SendAsync(new AsExchange("GET", "/.well-known/oauth-authorization-server"))).Json;
    Assert.Equal("https://as.test/register", metadata.GetProperty("registration_endpoint").GetString());
    var (code, verifier) = await h.CodeAsync(clientId, "http://127.0.0.1:4444/cb");
    Assert.True((await h.RedeemAsync(code, verifier, clientId, "http://127.0.0.1:4444/cb")).TryGetProperty("access_token", out _));

    var confidential = await h.SendAsync(new AsExchange("POST", "/register", string.Empty, """{"redirect_uris":["https://web.test/cb"]}""", "application/json"));
    Assert.Equal(201, confidential.Status);
    Assert.True(confidential.Json.TryGetProperty("client_secret", out var secret));
    var stored = await h.Store.FindClientAsync(confidential.Json.GetProperty("client_id").GetString()!, TestContext.Current.CancellationToken);
    Assert.DoesNotContain(secret.GetString()!, stored!.SecretHash!, StringComparison.Ordinal);
    Assert.True(ClientSecret.Verify(secret.GetString()!, stored.SecretHash));
  }

  [Theory]
  [InlineData("""{"redirect_uris":[]}""")]
  [InlineData("""{"redirect_uris":["javascript:alert(1)"]}""")]
  [InlineData("""{"redirect_uris":["http://evil.test/cb"]}""")]
  [InlineData("""{"redirect_uris":["http://127.0.0.1/cb"]}""")]
  [InlineData("""{"redirect_uris":["https://a.test/cb#f"]}""")]
  [InlineData("""{"redirect_uris":["https://a.test/cb"],"token_endpoint_auth_method":"private_key_jwt"}""")]
  [InlineData("""{"redirect_uris":["https://a.test/cb"],"grant_types":["client_credentials"]}""")]
  [InlineData("not json")]
  public async Task Bad_registrations_are_refused(string body)
  {
    using var h = new AsHarness(registration: true);

    var response = await h.SendAsync(new AsExchange("POST", "/register", string.Empty, body, "application/json"));

    Assert.Equal(400, response.Status);
  }

  [Fact]
  public async Task Client_secrets_are_stored_hashed_and_verified_in_constant_time_form()
  {
    var hash = ClientSecret.Hash("s3cret-value-0123456789");

    Assert.StartsWith("pbkdf2-sha256$", hash, StringComparison.Ordinal);
    Assert.DoesNotContain("s3cret", hash, StringComparison.Ordinal);
    Assert.NotEqual(hash, ClientSecret.Hash("s3cret-value-0123456789"));
    Assert.True(ClientSecret.Verify("s3cret-value-0123456789", hash));
    Assert.False(ClientSecret.Verify("s3cret-value-012345678", hash));
    Assert.False(ClientSecret.Verify("anything", null));
    Assert.False(ClientSecret.Verify("anything", "garbage"));
    Assert.False(ClientSecret.Verify("anything", "pbkdf2-sha256$1$AA==$AA=="));
    Assert.Equal(43, ClientSecret.Generate().Length);
    await Task.CompletedTask;
  }

  [Fact]
  public async Task Nothing_secret_reaches_diagnostics_security_events_or_responses_that_should_not_carry_it()
  {
    var events = new List<DiagnosticEvent>();
    using var subscription = ContextDiagnostics.Subscribe(events.Add);
    var (code, verifier) = await _h.CodeAsync();
    var tokens = await _h.RedeemAsync(code, verifier);
    var access = tokens.GetProperty("access_token").GetString()!;
    var refresh = tokens.GetProperty("refresh_token").GetString()!;

    // Provoke every kind of failure the server reports.
    await _h.TokenAsync([("grant_type", "authorization_code"), ("code", code), ("code_verifier", verifier), ("client_id", "app"), ("redirect_uri", AsHarness.RedirectUri)]);
    await _h.TokenAsync([("grant_type", "refresh_token"), ("refresh_token", refresh), ("client_id", "app")]);
    await _h.TokenAsync([("grant_type", "refresh_token"), ("refresh_token", refresh), ("client_id", "app")]);
    var badSecret = await _h.TokenAsync([("grant_type", "client_credentials"), ("resource", AsHarness.Resource), ("client_id", "svc"), ("client_secret", "wrong-secret-value-123456")]);
    var failedValidation = await _h.Server.CreateTokenValidator().ValidateAsync(access, new TokenValidationRequest(AsHarness.Resource), TestContext.Current.CancellationToken);

    var seen = string.Join('\n', events.Select(e => e.Message + e.Exception)) + string.Join('\n', _h.SecurityEvents) + failedValidation.Description + badSecret.BodyText;
    foreach (var secret in new[] { code, verifier, access, refresh, "wrong-secret-value-123456", AsHarness.ClientSecretValue })
    {
      Assert.DoesNotContain(secret, seen, StringComparison.Ordinal);
    }

    Assert.NotEmpty(_h.SecurityEvents);
  }

  [Fact]
  public async Task Bad_input_never_throws_out_of_the_handler()
  {
    var inputs = new (string Method, string Path, string Query, string? Body, string Type)[]
    {
      ("GET", "/authorize", "%zz=%", null, "x"),
      ("GET", "/authorize", new string('a', 50000), null, "x"),
      ("POST", "/authorize", string.Empty, "client_id=%E0%A4%A", "application/x-www-form-urlencoded"),
      ("POST", "/token", string.Empty, "grant_type=%", "application/x-www-form-urlencoded"),
      ("POST", "/token", string.Empty, new string('a', 100_000), "application/x-www-form-urlencoded"),
      ("POST", "/token", string.Empty, "grant_type=authorization_code&client_id=app&code=" + new string('a', 5000), "application/x-www-form-urlencoded"),
      ("POST", "/revoke", string.Empty, "token=", "application/x-www-form-urlencoded"),
      ("GET", "/authorize", "resume=" + new string('.', 9000), null, "x"),
      ("POST", "/token", string.Empty, "grant_type=refresh_token&refresh_token=x&client_id=app&client_assertion=a.b.c&client_assertion_type=x", "application/x-www-form-urlencoded"),
    };

    foreach (var (method, path, query, body, type) in inputs)
    {
      var exchange = new AsExchange(method, path, query, body, type);
      Assert.True(await _h.Server.HandleAsync(exchange));
      Assert.InRange(exchange.Status, 200, 499);
    }
  }

  [Fact]
  public async Task Duplicate_token_parameters_and_oversized_bodies_are_refused()
  {
    var twice = await _h.SendAsync(new AsExchange("POST", "/token", string.Empty, "grant_type=refresh_token&grant_type=authorization_code"));
    var large = await _h.SendAsync(new AsExchange("POST", "/token", string.Empty, new string('a', 100_000)));

    Assert.Equal("invalid_request", twice.Json.GetProperty("error").GetString());
    Assert.Equal("invalid_request", large.Json.GetProperty("error").GetString());
  }

  [Fact]
  public async Task The_whole_flow_fronts_a_resource_server_and_survives_a_refresh()
  {
    var keys = await _h.PublishedKeysAsync();
    var resourceServer = new ResourceServer(new ResourceServerOptions(new Uri(AsHarness.Resource), _h.PlainValidator(keys))
    {
      AuthorizationServers = [AsHarness.Issuer],
      ScopesSupported = ["files:read"],
    });
    var server = new ContextServer("e2e", "1");
    server.Tools.Add(new ToolDefinition("who", "Names the caller.", Json.Parse("""{"type":"object"}""")), (_, ctx) => ValueTask.FromResult(CallToolResult.FromText(ctx.Principal?.FindFirst("sub")?.Value + "/" + ctx.Principal?.FindFirst("client_id")?.Value)));
    using var endpoint = new StreamableHttpEndpoint(server, new StreamableHttpOptions { ResourceServer = resourceServer });
    const string Meta = """{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"t","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}""";
    async Task<(int Status, string Body)> Call(string? token)
    {
      var headers = new List<(string, string)> { ("MCP-Protocol-Version", "2026-07-28"), ("Mcp-Method", "tools/call"), ("Mcp-Name", "who") };
      if (token is not null)
      {
        headers.Add(("Authorization", "Bearer " + token));
      }

      var exchange = TestExchange.Post($$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"who","arguments":{},"_meta":{{{Meta}}}}}""", [.. headers]).Run(endpoint);
      var body = await exchange.BodyAsync();
      return (exchange.Status, body);
    }

    var anonymous = await Call(null);
    Assert.Equal(401, anonymous.Status);

    var tokens = await _h.FullFlowAsync();
    var first = await Call(tokens.GetProperty("access_token").GetString());
    Assert.Equal(200, first.Status);
    Assert.Contains("alice/app", first.Body, StringComparison.Ordinal);

    _h.Time.Advance(TimeSpan.FromMinutes(11));
    var expired = await Call(tokens.GetProperty("access_token").GetString());
    Assert.Equal(401, expired.Status);

    var refreshed = (await _h.TokenAsync([("grant_type", "refresh_token"), ("refresh_token", tokens.GetProperty("refresh_token").GetString()), ("client_id", "app")])).Json;
    var second = await Call(refreshed.GetProperty("access_token").GetString());
    Assert.Equal(200, second.Status);
    Assert.Contains("alice/app", second.Body, StringComparison.Ordinal);
  }
}

public sealed class AuthorizationStoreTests : IDisposable
{
  private readonly string _dir = Path.Combine(Path.GetTempPath(), "omc-store-" + Guid.NewGuid().ToString("N"));

  public void Dispose()
  {
    if (Directory.Exists(_dir))
    {
      Directory.Delete(_dir, recursive: true);
    }
  }

  private static AuthorizationCodeGrant Code(string hash, DateTimeOffset expires) => new()
  {
    CodeHash = hash,
    ClientId = "app",
    RedirectUri = "https://app.test/cb",
    Subject = "alice",
    Resource = "https://mcp.test/mcp",
    CodeChallenge = new string('c', 43),
    FamilyId = "fam",
    ExpiresAt = expires,
  };

  public static TheoryData<string> Stores => ["memory", "file"];

  private IAuthorizationStore Create(string kind) => kind == "memory" ? new MemoryAuthorizationStore() : new FileAuthorizationStore(Path.Combine(_dir, "auth.json"));

  [Theory]
  [MemberData(nameof(Stores))]
  public async Task A_code_is_redeemed_exactly_once_even_under_contention(string kind)
  {
    var store = Create(kind);
    var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    await store.SaveCodeAsync(Code("h1", now.AddMinutes(1)), TestContext.Current.CancellationToken);

    var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(async () => await store.RedeemCodeAsync("h1", now, TestContext.Current.CancellationToken))));

    Assert.Equal(1, results.Count(r => r.Status == RedemptionStatus.Redeemed));
    Assert.Equal(31, results.Count(r => r.Status == RedemptionStatus.AlreadyRedeemed));
    Assert.Equal(RedemptionStatus.Unknown, (await store.RedeemCodeAsync("nope", now, TestContext.Current.CancellationToken)).Status);
  }

  [Theory]
  [MemberData(nameof(Stores))]
  public async Task Assertion_ids_are_recorded_once_until_they_expire(string kind)
  {
    var store = Create(kind);
    var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    Assert.True(await store.TryRecordAssertionAsync("a", now.AddMinutes(5), now, TestContext.Current.CancellationToken));
    Assert.False(await store.TryRecordAssertionAsync("a", now.AddMinutes(5), now.AddMinutes(1), TestContext.Current.CancellationToken));
    Assert.True(await store.TryRecordAssertionAsync("a", now.AddMinutes(15), now.AddMinutes(6), TestContext.Current.CancellationToken));
  }

  [Theory]
  [MemberData(nameof(Stores))]
  public async Task Revocations_and_purging_behave(string kind)
  {
    var store = Create(kind);
    var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    await store.RevokeAsync("jti:1", now.AddMinutes(10), TestContext.Current.CancellationToken);
    await store.SaveCodeAsync(Code("old", now.AddMinutes(1)), TestContext.Current.CancellationToken);

    Assert.True(await store.IsRevokedAsync("jti:1", TestContext.Current.CancellationToken));
    Assert.False(await store.IsRevokedAsync("jti:2", TestContext.Current.CancellationToken));
    await store.PurgeAsync(now.AddMinutes(30), TestContext.Current.CancellationToken);
    Assert.False(await store.IsRevokedAsync("jti:1", TestContext.Current.CancellationToken));
    Assert.Equal(RedemptionStatus.Unknown, (await store.RedeemCodeAsync("old", now, TestContext.Current.CancellationToken)).Status);
  }

  [Fact]
  public async Task The_file_store_survives_a_restart_and_keeps_only_hashes()
  {
    var path = Path.Combine(_dir, "auth.json");
    var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    var first = new FileAuthorizationStore(path);
    await first.SaveClientAsync(new RegisteredClient { ClientId = "c1", RedirectUris = ["https://a.test/cb"], SecretHash = ClientSecret.Hash("plain-secret-0123456789"), CreatedAt = now, IsMetadataDocument = true }, TestContext.Current.CancellationToken);
    await first.SaveCodeAsync(Code("code-hash", now.AddMinutes(1)), TestContext.Current.CancellationToken);
    await first.SaveRefreshTokenAsync(new RefreshTokenGrant { TokenHash = "rt-hash", FamilyId = "fam", ClientId = "c1", Subject = "u", Resource = "r", ExpiresAt = now.AddDays(1), FamilyExpiresAt = now.AddDays(2) }, TestContext.Current.CancellationToken);
    await first.RevokeAsync("family:fam", now.AddDays(1), TestContext.Current.CancellationToken);

    var second = new FileAuthorizationStore(path);

    var client = await second.FindClientAsync("c1", TestContext.Current.CancellationToken);
    Assert.NotNull(client);
    Assert.Equal(["https://a.test/cb"], client.RedirectUris);
    Assert.False(client.IsMetadataDocument);
    Assert.True(ClientSecret.Verify("plain-secret-0123456789", client.SecretHash));
    Assert.Equal(RedemptionStatus.Redeemed, (await second.RedeemCodeAsync("code-hash", now, TestContext.Current.CancellationToken)).Status);
    Assert.NotNull(await second.FindRefreshTokenAsync("rt-hash", TestContext.Current.CancellationToken));
    Assert.True(await second.IsRevokedAsync("family:fam", TestContext.Current.CancellationToken));
    Assert.DoesNotContain("plain-secret", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

    // The redemption above was persisted too.
    var third = new FileAuthorizationStore(path);
    Assert.Equal(RedemptionStatus.AlreadyRedeemed, (await third.RedeemCodeAsync("code-hash", now, TestContext.Current.CancellationToken)).Status);
  }

  [Fact]
  public async Task The_file_is_private_to_its_owner_on_unix()
  {
    if (OperatingSystem.IsWindows())
    {
      return;
    }

    var path = Path.Combine(_dir, "sub", "auth.json");
    var store = new FileAuthorizationStore(path);
    await store.RevokeAsync("jti:1", DateTimeOffset.UtcNow.AddDays(1), TestContext.Current.CancellationToken);

    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
  }

  [Fact]
  public void A_file_that_is_not_a_store_is_refused_and_left_alone_without_echoing_its_content()
  {
    Directory.CreateDirectory(_dir);
    var path = Path.Combine(_dir, "auth.json");
    File.WriteAllText(path, "access_token=SECRET-LOOKING-VALUE-123");

    var ex = Assert.Throws<InvalidDataException>(() => new FileAuthorizationStore(path));

    Assert.DoesNotContain("SECRET-LOOKING", ex.ToString(), StringComparison.Ordinal);
    Assert.Equal("access_token=SECRET-LOOKING-VALUE-123", File.ReadAllText(path));
  }
}
