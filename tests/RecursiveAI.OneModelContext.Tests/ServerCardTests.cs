using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Extensions.ServerCards;
using RecursiveAI.OneModelContext.Hosting;
using RecursiveAI.OneModelContext.Http;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>Server Cards (Beta): publishing from the HTTP core and the listener, fetching through the guarded fetcher, the AI Catalog.</summary>
public class ServerCardTests
{
  private static System.Threading.CancellationToken Ct => TestContext.Current.CancellationToken;

  private static ContextServer ServerWithCard()
  {
    var server = new ContextServer("weather", "2.1.0");
    server.UseServerCard(server.CreateServerCard("com.example/weather", "Forecasts for any city.", "https://example.com/mcp"));
    return server;
  }

  private static HttpServer StartListener(ContextServer server, StreamableHttpOptions? options = null, Action<IHttpExchange>? onOther = null)
  {
    var endpoint = new StreamableHttpEndpoint(server, options);
    var listener = new HttpServer(e => e.Request.Path is "/mcp" or "/mcp/server-card" ? endpoint.HandleAsync(e) : ValueTask.CompletedTask);
    listener.Start();
    return listener;
  }

  private static GuardedHttpFetcher LoopbackFetcher() =>
    new(new GuardedFetchOptions { Addresses = new AddressPolicy().Allow("127.0.0.1"), AllowHttpToAllowListedAddresses = true, MaxBytes = 64 * 1024 });

  [Fact]
  public void The_builder_follows_the_server_and_the_card_is_checked_against_the_schema()
  {
    var server = new ContextServer("weather", "2.1.0");
    var card = server.CreateServerCard("com.example/weather", "Forecasts.", "https://example.com/mcp");
    Assert.Equal("2.1.0", card.Version);
    Assert.Equal(server.SupportedVersions, card.Remotes![0].SupportedProtocolVersions);

    foreach (var bad in new[]
    {
      card.Altered("name", "weather"),
      card.Altered("name", "a/b/c"),
      card.Altered("name", "com example/weather"),
      card.Altered("version", "^1.2.3"),
      card.Altered("version", "1.x"),
      card.Altered("description", new string('x', 101)),
      card.Altered("description", string.Empty),
      card.Altered("remote", "ftp://example.com"),
    })
    {
      Assert.Throws<ArgumentException>(() => server.UseServerCard(bad));
    }
  }

  [Fact]
  public async Task The_card_is_served_with_the_headers_its_specification_asks_for()
  {
    await using var listener = StartListener(ServerWithCard());
    using var http = new HttpClient();
    var url = new Uri(listener.BaseAddress!, "mcp/server-card");

    // A foreign Origin is how a browser asks: the card is public, so it is answered, with CORS open.
    using var request = new HttpRequestMessage(HttpMethod.Get, url);
    request.Headers.TryAddWithoutValidation("Accept", ServerCard.MediaType);
    request.Headers.TryAddWithoutValidation("Origin", "https://app.example");
    using var response = await http.SendAsync(request, Ct);

    Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    Assert.Equal(ServerCard.MediaType, response.Content.Headers.ContentType!.MediaType);
    Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    Assert.Equal("GET", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
    Assert.Equal("Content-Type, If-None-Match", response.Headers.GetValues("Access-Control-Allow-Headers").Single());
    Assert.Equal("ETag", response.Headers.GetValues("Access-Control-Expose-Headers").Single());
    Assert.Equal("public, max-age=3600", response.Headers.CacheControl!.ToString());
    var eTag = response.Headers.ETag!.ToString();

    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
    Assert.Equal(ServerCard.SchemaUrl, body.RootElement.GetProperty("$schema").GetString());
    Assert.Equal("com.example/weather", body.RootElement.GetProperty("name").GetString());
    Assert.Equal("2.1.0", body.RootElement.GetProperty("version").GetString());

    // A matching validator gets 304 and no body.
    using var conditional = new HttpRequestMessage(HttpMethod.Get, url);
    conditional.Headers.TryAddWithoutValidation("If-None-Match", eTag);
    using var notModified = await http.SendAsync(conditional, Ct);
    Assert.Equal(System.Net.HttpStatusCode.NotModified, notModified.StatusCode);
    Assert.Equal(eTag, notModified.Headers.ETag!.ToString());

    // The preflight a browser sends first.
    using var preflight = await http.SendAsync(new HttpRequestMessage(HttpMethod.Options, url), Ct);
    Assert.Equal(System.Net.HttpStatusCode.NoContent, preflight.StatusCode);
    Assert.Equal("*", preflight.Headers.GetValues("Access-Control-Allow-Origin").Single());

    // A client that wants something else is told so, and the MCP endpoint itself is untouched by all this.
    using var wrongType = new HttpRequestMessage(HttpMethod.Get, url);
    wrongType.Headers.TryAddWithoutValidation("Accept", "text/html");
    Assert.Equal(System.Net.HttpStatusCode.NotAcceptable, (await http.SendAsync(wrongType, Ct)).StatusCode);
    using var foreignMcp = new HttpRequestMessage(HttpMethod.Post, new Uri(listener.BaseAddress!, "mcp")) { Content = new StringContent("{}") };
    foreignMcp.Headers.TryAddWithoutValidation("Origin", "https://app.example");
    Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await http.SendAsync(foreignMcp, Ct)).StatusCode);
  }

  [Fact]
  public async Task The_card_answers_a_rebound_host_with_403_and_a_server_without_a_card_with_404()
  {
    await using var listener = StartListener(ServerWithCard());
    using var http = new HttpClient();
    using var rebound = new HttpRequestMessage(HttpMethod.Get, new Uri(listener.BaseAddress!, "mcp/server-card"));
    rebound.Headers.Host = "attacker.example";
    Assert.True((int)(await http.SendAsync(rebound, Ct)).StatusCode is 403 or 421);

    await using var plain = StartListener(new ContextServer("plain", "1.0"));
    Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync(new Uri(plain.BaseAddress!, "mcp/server-card"), Ct)).StatusCode);
  }

  [Fact]
  public async Task A_client_fetches_the_card_and_revalidates_it()
  {
    await using var listener = StartListener(ServerWithCard());
    using var fetcher = LoopbackFetcher();
    using var client = new ServerCardClient(fetcher);
    var url = new Uri(listener.BaseAddress!, "mcp/server-card");

    var first = await client.FetchAsync(url, null, Ct);
    Assert.Equal("Forecasts for any city.", first.Card!.Description);
    Assert.Equal("https://example.com/mcp", first.Card.Remotes![0].Url);
    Assert.NotNull(first.ETag);
    Assert.Equal(TimeSpan.FromHours(1), first.CacheLifetime);

    var second = await client.FetchAsync(url, first.ETag, Ct);
    Assert.True(second.NotModified);
    Assert.Null(second.Card);
  }

  [Fact]
  public async Task Fetching_goes_through_the_guard_so_private_addresses_are_refused()
  {
    await using var listener = StartListener(ServerWithCard());
    using var client = new ServerCardClient();
    var error = await Assert.ThrowsAsync<ServerCardException>(async () => await client.FetchAsync(new Uri(listener.BaseAddress!, "mcp/server-card"), null, Ct));
    Assert.DoesNotContain("Forecasts", error.Message, StringComparison.Ordinal);
    await Assert.ThrowsAsync<ServerCardException>(async () => await client.FetchAsync(new Uri("https://169.254.169.254/latest/meta-data"), null, Ct));
    await Assert.ThrowsAsync<ServerCardException>(async () => await client.FetchAsync(new Uri("https://[::1]/server-card"), null, Ct));
    await Assert.ThrowsAsync<ServerCardException>(async () => await client.FetchAsync(new Uri("https://10.0.0.1/server-card"), null, Ct));
  }

  [Fact]
  public async Task A_response_that_is_not_a_card_is_refused()
  {
    using var fixture = new FixtureServer();
    fixture.Add("/html", "text/html", "<html></html>");
    fixture.Add("/notjson", ServerCard.MediaType, "not json");
    fixture.Add("/incomplete", ServerCard.MediaType, """{"name":"com.example/x"}""");
    fixture.Add("/big", ServerCard.MediaType, new string(' ', 200_000) + "{}");
    using var fetcher = LoopbackFetcher();
    using var client = new ServerCardClient(fetcher);

    foreach (var path in new[] { "/html", "/notjson", "/incomplete", "/big", "/missing" })
    {
      await Assert.ThrowsAsync<ServerCardException>(async () => await client.FetchAsync(new Uri(fixture.Url + path), null, Ct));
    }
  }

  [Fact]
  public async Task The_ai_catalog_lists_cards_by_url_or_inline_and_skips_other_types()
  {
    using var fixture = new FixtureServer();
    fixture.Add(
      "/.well-known/ai-catalog.json",
      "application/ai-catalog+json",
      """
      {"specVersion":"1.0","entries":[
        {"identifier":"urn:air:example.com:mcp:weather","type":"application/mcp-server-card+json","url":"/cards/weather"},
        {"identifier":"urn:air:example.com:mcp:inline","type":"application/mcp-server-card+json","data":{"$schema":"https://static.modelcontextprotocol.io/schemas/v1/server-card.schema.json","name":"com.example/inline","version":"1.0.0","description":"Inline card.","x-extra":1}},
        {"identifier":"urn:air:example.com:agent:other","type":"application/a2a-agent-card+json","url":"/cards/agent"},
        {"identifier":"urn:air:example.com:mcp:broken","type":"application/mcp-server-card+json","url":"/cards/missing"},
        {"identifier":"urn:air:example.com:mcp:both","type":"application/mcp-server-card+json","url":"/cards/weather","data":{}}
      ]}
      """);
    fixture.Add(
      "/cards/weather",
      ServerCard.MediaType,
      """{"$schema":"https://static.modelcontextprotocol.io/schemas/v1/server-card.schema.json","name":"com.example/weather","version":"2.1.0","description":"Forecasts."}""");
    using var fetcher = LoopbackFetcher();
    using var client = new ServerCardClient(fetcher);

    var found = await client.DiscoverAsync(new Uri(fixture.Url), Ct);

    Assert.Equal(["urn:air:example.com:mcp:weather", "urn:air:example.com:mcp:inline"], found.Select(f => f.Identifier));
    Assert.Equal(new Uri(fixture.Url + "/cards/weather"), found[0].Url);
    Assert.Null(found[1].Url);
    Assert.True(found[1].Card.AdditionalProperties!.ContainsKey("x-extra"));

    using var empty = new FixtureServer();
    await Assert.ThrowsAsync<ServerCardException>(async () => await client.DiscoverAsync(new Uri(empty.Url), Ct));
  }

  [Fact]
  public void Unknown_fields_in_a_card_survive_a_round_trip()
  {
    var json = """{"$schema":"https://static.modelcontextprotocol.io/schemas/v1/server-card.schema.json","name":"com.example/x","version":"1","description":"d","future":{"a":1},"remotes":[{"type":"sse","url":"https://x.example/sse","extra":true}]}""";
    var card = JsonSerializer.Deserialize(json, ServerCardsJsonContext.Default.ServerCard)!;
    var again = JsonSerializer.Serialize(card, ServerCardsJsonContext.Default.ServerCard);
    using var document = JsonDocument.Parse(again);
    Assert.Equal(1, document.RootElement.GetProperty("future").GetProperty("a").GetInt32());
    Assert.True(document.RootElement.GetProperty("remotes")[0].GetProperty("extra").GetBoolean());
  }

  /// <summary>A loopback HTTP server that answers fixed documents.</summary>
  private sealed class FixtureServer : IDisposable
  {
    private readonly Dictionary<string, (string Type, string Body)> _routes = [];
    private readonly HttpServer _server;

    internal FixtureServer()
    {
      _server = new HttpServer(async exchange =>
      {
        var response = exchange.Response;
        if (_routes.TryGetValue(exchange.Request.Path, out var route))
        {
          response.StatusCode = 200;
          response.SetHeader("Content-Type", route.Type);
          await response.Body.WriteAsync(Encoding.UTF8.GetBytes(route.Body), exchange.Aborted);
        }
        else
        {
          response.StatusCode = 404;
        }

        await response.Body.FlushAsync(exchange.Aborted);
      });
      _server.Start();
    }

    internal string Url => _server.BaseAddress!.ToString().TrimEnd('/');

    internal void Add(string path, string type, string body) => _routes[path] = (type, body);

    public void Dispose() => _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
  }
}

internal static class ServerCardTestExtensions
{
  internal static ServerCard Altered(this ServerCard card, string field, string value) => field switch
  {
    "name" => new ServerCard { Name = value, Version = card.Version, Description = card.Description, Remotes = card.Remotes },
    "version" => new ServerCard { Name = card.Name, Version = value, Description = card.Description, Remotes = card.Remotes },
    "description" => new ServerCard { Name = card.Name, Version = card.Version, Description = value, Remotes = card.Remotes },
    _ => new ServerCard { Name = card.Name, Version = card.Version, Description = card.Description, Remotes = [new ServerCardRemote { Type = "streamable-http", Url = value }] },
  };
}
