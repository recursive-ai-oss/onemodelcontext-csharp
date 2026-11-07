using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.Hosting;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

public class HttpClientTests
{
  private static readonly Uri Url = new("http://127.0.0.1:47600/mcp");
  private static readonly JsonElement ObjectSchema = Json.Parse("""{"type":"object"}""");

  private static ContextServer NewServer(params string[] versions)
  {
    var server = new ContextServer("http-e2e", "1.0", versions.Length == 0 ? null : new ContextServerOptions { SupportedVersions = versions });
    server.Tools.Add(
      new ToolDefinition("echo", "Echoes.", Json.Parse("""{"type":"object","properties":{"text":{"type":"string"}}}""")),
      (args, _) => ValueTask.FromResult(CallToolResult.FromText(args.ValueKind == JsonValueKind.Object && args.TryGetProperty("text", out var t) ? t.GetString()! : "ok")));
    server.Resources.Add(new ResourceDefinition("mem://a", "a"), (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, Text = "A" }] }));
    return server;
  }

  private static ContextClientOptions Options(EraPreference era = EraPreference.Auto, params string[] versions) => new()
  {
    Era = era,
    EraCache = null,
    SupportedVersions = versions.Length == 0 ? ProtocolVersions.Supported : versions,
    ProbeTimeout = TimeSpan.FromSeconds(10),
  };

  private static HttpTransportOptions Fast(Func<HttpRequestMessage, CancellationToken, ValueTask>? authorize = null) => new()
  {
    DefaultRetryDelay = TimeSpan.FromMilliseconds(5),
    StreamIdleTimeout = TimeSpan.FromSeconds(20),
    AuthorizeRequest = authorize,
  };

  private sealed class Setup : IAsyncDisposable
  {
    private readonly StreamableHttpEndpoint _endpoint;

    internal Setup(ContextServer server)
    {
      Server = server;
      _endpoint = new StreamableHttpEndpoint(server);
      Handler = new EndpointHandler(_endpoint);
    }

    internal ContextServer Server { get; }

    internal EndpointHandler Handler { get; }

    internal ContextClient Client { get; private set; } = null!;

    internal async Task<Setup> ConnectAsync(ContextClientOptions? options = null, HttpTransportOptions? transport = null)
    {
      Client = await ContextClient.ConnectAsync(ContextTransport.Http(Url, new HttpClient(Handler), transport ?? Fast()), options ?? Options(), TestContext.Current.CancellationToken);
      return this;
    }

    public async ValueTask DisposeAsync()
    {
      if (Client is not null)
      {
        await Client.DisposeAsync();
      }

      _endpoint.Dispose();
    }
  }

  private static async Task<SubscriptionEvent> NextAsync(ContextSubscription subscription, TimeSpan? within = null)
  {
    using var timeout = new CancellationTokenSource(within ?? TimeSpan.FromSeconds(10));
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
    await foreach (var item in subscription.ReadAllAsync(linked.Token))
    {
      return item;
    }

    throw new InvalidOperationException("The subscription ended.");
  }

  // ---- modern ------------------------------------------------------------------------------------------------------------------

  [Fact]
  public async Task A_modern_server_is_reached_with_the_request_metadata_headers()
  {
    await using var setup = await new Setup(NewServer()).ConnectAsync();
    var client = setup.Client;

    Assert.Equal(ProtocolEra.Modern, client.Era);
    var result = await client.CallToolAsync("echo", Json.Parse("""{"text":"hi"}"""), TestContext.Current.CancellationToken);
    Assert.Equal("hi", result.Content.OfType<TextContent>().Single().Text);

    var discover = setup.Handler.Sent.First();
    Assert.Equal("server/discover", discover.Header("Mcp-Method"));
    Assert.Equal("2026-07-28", discover.Header("MCP-Protocol-Version"));
    Assert.Contains("text/event-stream", discover.Header("Accept"));
    Assert.Contains("application/json", discover.Header("Accept"));

    var call = setup.Handler.Sent.Single(r => r.RpcMethod == "tools/call");
    Assert.Equal("tools/call", call.Header("Mcp-Method"));
    Assert.Equal("echo", call.Header("Mcp-Name"));
    Assert.Null(call.Header("Mcp-Session-Id"));
    Assert.All(setup.Handler.Sent, r => Assert.Equal("POST", r.Method));
  }

  [Fact]
  public async Task Resource_reads_and_prompts_name_their_target_in_the_header()
  {
    var server = NewServer();
    server.Prompts.Add(new PromptDefinition("greet"), (_, _) => ValueTask.FromResult(new GetPromptResult { Messages = [new PromptMessage { Role = "user", Content = TextContent.Of("hi") }] }));
    await using var setup = await new Setup(server).ConnectAsync();

    await setup.Client.ReadResourceAsync("mem://a", TestContext.Current.CancellationToken);
    await setup.Client.GetPromptAsync("greet", null, TestContext.Current.CancellationToken);

    Assert.Equal("mem://a", setup.Handler.Sent.Single(r => r.RpcMethod == "resources/read").Header("Mcp-Name"));
    Assert.Equal("greet", setup.Handler.Sent.Single(r => r.RpcMethod == "prompts/get").Header("Mcp-Name"));
  }

  [Fact]
  public async Task A_name_that_is_not_plain_ascii_goes_in_the_header_as_base64()
  {
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("grüß", "Unicode name.", ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("ok")));
    await using var setup = await new Setup(server).ConnectAsync();

    await setup.Client.CallToolAsync("grüß", default, TestContext.Current.CancellationToken);

    var header = setup.Handler.Sent.Single(r => r.RpcMethod == "tools/call").Header("Mcp-Name");
    Assert.Equal("=?base64?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("grüß")) + "?=", header);
  }

  [Fact]
  public async Task Parameters_marked_with_x_mcp_header_are_mirrored_and_omitted_when_absent()
  {
    var server = NewServer();
    server.Tools.Add(
      new ToolDefinition("query", "Mirrors parameters.", Json.Parse("""{"type":"object","properties":{"region":{"type":"string","x-mcp-header":"Region"},"limit":{"type":"integer","x-mcp-header":"Limit"},"dry":{"type":"boolean","x-mcp-header":"Dry"},"sql":{"type":"string"}}}""")),
      (_, _) => ValueTask.FromResult(CallToolResult.FromText("ok")));
    await using var setup = await new Setup(server).ConnectAsync();
    await setup.Client.ListToolsAsync(TestContext.Current.CancellationToken);

    await setup.Client.CallToolAsync("query", Json.Parse("""{"region":"us-west1","limit":42,"sql":"select 1"}"""), TestContext.Current.CancellationToken);
    await setup.Client.CallToolAsync("query", Json.Parse("""{"region":"Hello, 世界","dry":false,"limit":null}"""), TestContext.Current.CancellationToken);

    var calls = setup.Handler.Sent.Where(r => r.RpcMethod == "tools/call").ToArray();
    Assert.Equal("us-west1", calls[0].Header("Mcp-Param-Region"));
    Assert.Equal("42", calls[0].Header("Mcp-Param-Limit"));
    Assert.Null(calls[0].Header("Mcp-Param-Dry"));
    Assert.Null(calls[0].Header("Mcp-Param-Sql"));
    Assert.Equal("=?base64?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Hello, 世界")) + "?=", calls[1].Header("Mcp-Param-Region"));
    Assert.Equal("false", calls[1].Header("Mcp-Param-Dry"));
    Assert.Null(calls[1].Header("Mcp-Param-Limit"));
  }

  [Fact]
  public async Task A_call_the_server_rejects_for_missing_parameter_headers_lists_the_tools_and_tries_again()
  {
    var server = NewServer();
    server.Tools.Add(
      new ToolDefinition("query", "Mirrors a parameter.", Json.Parse("""{"type":"object","properties":{"region":{"type":"string","x-mcp-header":"Region"}}}""")),
      (_, _) => ValueTask.FromResult(CallToolResult.FromText("ok")));
    await using var setup = await new Setup(server).ConnectAsync();

    // The client never listed the tools, so the first call goes out without the header and the server refuses it.
    var result = await setup.Client.CallToolAsync("query", Json.Parse("""{"region":"eu"}"""), TestContext.Current.CancellationToken);

    Assert.Equal("ok", result.Content.OfType<TextContent>().Single().Text);
    var calls = setup.Handler.Sent.Where(r => r.RpcMethod == "tools/call").ToArray();
    Assert.Equal(2, calls.Length);
    Assert.Null(calls[0].Header("Mcp-Param-Region"));
    Assert.Equal("eu", calls[1].Header("Mcp-Param-Region"));
    Assert.Contains(setup.Handler.Sent, r => r.RpcMethod == "tools/list");
  }

  [Theory]
  [InlineData("""{"type":"object","properties":{"a":{"type":"number","x-mcp-header":"A"}}}""", "not a string, integer or boolean")]
  [InlineData("""{"type":"object","properties":{"a":{"type":"string","x-mcp-header":""}}}""", "non-empty")]
  [InlineData("""{"type":"object","properties":{"a":{"type":"string","x-mcp-header":"Bad Name"}}}""", "not a valid HTTP field name")]
  [InlineData("""{"type":"object","properties":{"a":{"type":"string","x-mcp-header":"X"},"b":{"type":"string","x-mcp-header":"x"}}}""", "more than once")]
  [InlineData("""{"type":"object","properties":{"a":{"type":"array","items":{"type":"string","x-mcp-header":"A"}}}}""", "reachable through properties")]
  [InlineData("""{"type":"object","properties":{"a":{"anyOf":[{"type":"string","x-mcp-header":"A"}]}}}""", "reachable through properties")]
  public async Task A_tool_with_an_invalid_header_annotation_is_left_out_of_the_list(string schema, string reason)
  {
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("bad", "Broken annotation.", Json.Parse(schema)), (_, _) => ValueTask.FromResult(CallToolResult.FromText("ok")));
    var warnings = new List<string>();
    using var subscription = ContextDiagnostics.Subscribe(e =>
    {
      if (e.Message.Contains("'bad'", StringComparison.Ordinal))
      {
        warnings.Add(e.Message);
      }
    });
    await using var setup = await new Setup(server).ConnectAsync();

    var tools = await setup.Client.ListToolsAsync(TestContext.Current.CancellationToken);

    Assert.Equal(["echo"], tools.Select(t => t.Name));
    Assert.Contains(warnings, w => w.Contains(reason, StringComparison.Ordinal));
  }

  [Fact]
  public async Task A_nested_annotation_reachable_through_properties_is_mirrored()
  {
    var server = NewServer();
    server.Tools.Add(
      new ToolDefinition("nested", "Nested.", Json.Parse("""{"type":"object","properties":{"where":{"type":"object","properties":{"zone":{"type":"string","x-mcp-header":"Zone"}}}}}""")),
      (_, _) => ValueTask.FromResult(CallToolResult.FromText("ok")));
    await using var setup = await new Setup(server).ConnectAsync();
    await setup.Client.ListToolsAsync(TestContext.Current.CancellationToken);

    await setup.Client.CallToolAsync("nested", Json.Parse("""{"where":{"zone":"a"}}"""), TestContext.Current.CancellationToken);

    Assert.Equal("a", setup.Handler.Sent.Single(r => r.RpcMethod == "tools/call").Header("Mcp-Param-Zone"));
  }

  [Fact]
  public async Task The_authorize_hook_and_static_headers_reach_every_request_and_redirects_are_not_followed()
  {
    var setup = new Setup(NewServer());
    var calls = 0;
    setup.Handler.Intercept = (sent, _, _) =>
    {
      if (sent.RpcMethod == "tools/call" && Interlocked.Increment(ref calls) == 1)
      {
        var redirect = EndpointHandler.Plain(HttpStatusCode.TemporaryRedirect);
        redirect.Headers.Location = new Uri("http://127.0.0.1:1/elsewhere");
        return Task.FromResult<HttpResponseMessage?>(redirect);
      }

      return Task.FromResult<HttpResponseMessage?>(null);
    };
    var token = "throwaway-" + Guid.NewGuid().ToString("N");
    await setup.ConnectAsync(transport: new HttpTransportOptions
    {
      Headers = new Dictionary<string, string> { ["X-Api-Version"] = "7" },
      AuthorizeRequest = (request, _) =>
      {
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        return ValueTask.CompletedTask;
      },
    });
    await using var owned = setup;

    var error = await Assert.ThrowsAsync<TransportException>(async () => await setup.Client.CallToolAsync("echo", default, TestContext.Current.CancellationToken));

    Assert.Contains("redirected", error.Message, StringComparison.Ordinal);
    Assert.DoesNotContain(token, error.Message, StringComparison.Ordinal);
    Assert.All(setup.Handler.Sent, r =>
    {
      Assert.Equal("Bearer " + token, r.Header("Authorization"));
      Assert.Equal("7", r.Header("X-Api-Version"));
      Assert.Equal("127.0.0.1:47600", r.Uri.Authority);
    });
    Assert.Equal(1, calls);
  }

  [Fact]
  public async Task A_401_fails_connecting_with_an_authorization_error_that_names_the_status_but_no_credentials()
  {
    var setup = new Setup(NewServer());
    setup.Handler.Intercept = (_, _, _) => Task.FromResult<HttpResponseMessage?>(EndpointHandler.Plain(HttpStatusCode.Unauthorized, "token abc is bad"));
    var token = "throwaway-" + Guid.NewGuid().ToString("N");

    var error = await Assert.ThrowsAsync<AuthorizationException>(async () => await setup.ConnectAsync(transport: new HttpTransportOptions
    {
      AuthorizeRequest = (request, _) =>
      {
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        return ValueTask.CompletedTask;
      },
    }));

    Assert.Equal(401, error.StatusCode);
    var text = error.ToString();
    Assert.Contains("401", text, StringComparison.Ordinal);
    Assert.DoesNotContain(token, text, StringComparison.Ordinal);
    await setup.DisposeAsync();
  }

  [Fact]
  public async Task An_unreachable_server_fails_connecting_with_a_transport_error()
  {
    using var http = new HttpClient(new RefusingHandler());

    await Assert.ThrowsAsync<TransportException>(async () =>
      await ContextClient.ConnectAsync(ContextTransport.Http(Url, http, Fast()), Options(), TestContext.Current.CancellationToken));
  }

  private sealed class RefusingHandler : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
      throw new HttpRequestException("Connection refused");
  }

  [Fact]
  public void The_url_is_checked_and_the_origin_names_the_server()
  {
    Assert.Throws<ArgumentException>(() => ContextTransport.Http(new Uri("ftp://example.com/mcp")));
    Assert.Throws<ArgumentException>(() => ContextTransport.Http(new Uri("https://user:secret@example.com/mcp")));
    Assert.Throws<ArgumentException>(() => ContextTransport.Http(new Uri("/mcp", UriKind.Relative)));
    Assert.Equal("https://example.com:8443", ContextTransport.Http(new Uri("https://Example.com:8443/a/b?c=d")).ServerKey);
  }

  [Fact]
  public async Task The_era_is_remembered_for_the_origin_so_the_next_connect_skips_the_probe()
  {
    var cache = new EraCache();
    var options = new ContextClientOptions { EraCache = cache, ProbeTimeout = TimeSpan.FromSeconds(10) };
    var setup = new Setup(NewServer("2025-11-25"));
    await setup.ConnectAsync(options);
    var firstRequests = setup.Handler.Sent.Count;
    await setup.Client.DisposeAsync();

    await setup.ConnectAsync(options);
    await using var owned = setup;

    Assert.Equal(ProtocolEra.Legacy, setup.Client.Era);
    Assert.True(cache.TryGet("http://127.0.0.1:47600", out var era));
    Assert.Equal(ProtocolEra.Legacy, era);
    // The first connect probed (discover, then initialize); the second went straight to initialize.
    Assert.DoesNotContain(setup.Handler.Sent.Skip(firstRequests), r => r.RpcMethod == "server/discover");
  }

  // ---- legacy ------------------------------------------------------------------------------------------------------------------

  [Theory]
  [InlineData("2025-11-25")]
  [InlineData("2025-06-18")]
  [InlineData("2025-03-26")]
  public async Task A_legacy_server_gets_the_session_and_the_negotiated_version_on_each_request_and_a_delete_on_close(string version)
  {
    var setup = new Setup(NewServer(version));
    await setup.ConnectAsync(Options(EraPreference.Auto, version));
    var client = setup.Client;

    Assert.Equal(ProtocolEra.Legacy, client.Era);
    Assert.Equal(version, client.ProtocolVersion);
    var result = await client.CallToolAsync("echo", Json.Parse("""{"text":"hi"}"""), TestContext.Current.CancellationToken);
    Assert.Equal("hi", result.Content.OfType<TextContent>().Single().Text);

    await setup.DisposeAsync();

    var initialize = setup.Handler.Sent.Single(r => r.RpcMethod == "initialize");
    Assert.Null(initialize.Header("Mcp-Session-Id"));
    var call = setup.Handler.Sent.Single(r => r.RpcMethod == "tools/call");
    Assert.False(string.IsNullOrEmpty(call.Header("Mcp-Session-Id")));
    Assert.Equal(version, call.Header("MCP-Protocol-Version"));
    Assert.Null(call.Header("Mcp-Method"));
    var delete = setup.Handler.Sent.Single(r => r.Method == "DELETE");
    Assert.Equal(call.Header("Mcp-Session-Id"), delete.Header("Mcp-Session-Id"));
  }

  [Fact]
  public async Task The_client_probes_with_discover_and_falls_back_to_initialize_on_a_legacy_only_endpoint()
  {
    await using var setup = await new Setup(NewServer("2025-11-25")).ConnectAsync();

    Assert.Equal(ProtocolEra.Legacy, setup.Client.Era);
    Assert.Contains(setup.Handler.Sent, r => r.RpcMethod == "server/discover");
    Assert.Contains(setup.Handler.Sent, r => r.RpcMethod == "initialize");
  }

  // ---- change notifications ---------------------------------------------------------------------------------------------------

  [Fact]
  public async Task A_modern_listen_stream_delivers_list_changes_and_resource_updates_the_client_asked_for()
  {
    var server = NewServer();
    await using var setup = await new Setup(server).ConnectAsync();

    await using var subscription = await setup.Client.ListenAsync(
      new SubscriptionFilter { ToolsListChanged = true, ResourceSubscriptions = ["mem://a", "mem://not-served"] },
      TestContext.Current.CancellationToken);

    Assert.True(subscription.Acknowledged.ToolsListChanged);
    Assert.Null(subscription.Acknowledged.PromptsListChanged);
    server.Tools.Add(new ToolDefinition("late", "Added later.", ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("ok")));
    var changed = await NextAsync(subscription);
    Assert.Equal(SubscriptionEventKind.ToolsListChanged, changed.Kind);

    // Prompts were not asked for.
    server.Prompts.Add(new PromptDefinition("later"), (_, _) => ValueTask.FromResult(new GetPromptResult { Messages = [] }));
    await server.NotifyResourceUpdatedAsync("mem://a", TestContext.Current.CancellationToken);
    var updated = await NextAsync(subscription);
    Assert.Equal(SubscriptionEventKind.ResourceUpdated, updated.Kind);
    Assert.Equal("mem://a", updated.Uri);

    Assert.Equal(SubscriptionEnd.Closed, await CloseAsync(subscription));
  }

  [Fact]
  public async Task Closing_a_listen_stream_cancels_the_request_and_the_server_ending_it_is_reported()
  {
    var server = NewServer();
    await using var setup = await new Setup(server).ConnectAsync();
    var first = await setup.Client.ListenAsync(new SubscriptionFilter { ToolsListChanged = true }, TestContext.Current.CancellationToken);
    var second = await setup.Client.ListenAsync(new SubscriptionFilter { ToolsListChanged = true }, TestContext.Current.CancellationToken);

    await first.DisposeAsync();
    Assert.Equal(SubscriptionEnd.Closed, await first.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

    server.EndSubscriptions();
    Assert.Equal(SubscriptionEnd.ServerEnded, await second.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task A_legacy_session_hears_changes_on_the_get_stream_through_the_same_api()
  {
    var server = NewServer("2025-11-25");
    await using var setup = await new Setup(server).ConnectAsync(Options(EraPreference.Legacy));
    await using var subscription = await setup.Client.ListenAsync(
      new SubscriptionFilter { ToolsListChanged = true, PromptsListChanged = true, ResourceSubscriptions = ["mem://a"] },
      TestContext.Current.CancellationToken);

    Assert.True(subscription.Acknowledged.ToolsListChanged);
    Assert.Null(subscription.Acknowledged.PromptsListChanged);
    Assert.Equal(["mem://a"], subscription.Acknowledged.ResourceSubscriptions);

    // The GET stream opens just after initialize; keep telling until the server has someone to tell.
    var update = server.NotifyResourceUpdatedAsync("mem://a", TestContext.Current.CancellationToken).AsTask();
    SubscriptionEvent got = default;
    for (var attempt = 0; attempt < 100; attempt++)
    {
      await update;
      try
      {
        got = await NextAsync(subscription, TimeSpan.FromMilliseconds(100));
        break;
      }
      catch (OperationCanceledException)
      {
        update = server.NotifyResourceUpdatedAsync("mem://a", TestContext.Current.CancellationToken).AsTask();
      }
    }

    Assert.Equal(SubscriptionEventKind.ResourceUpdated, got.Kind);
    Assert.Equal("mem://a", got.Uri);
    server.Tools.Add(new ToolDefinition("late", "Added later.", ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("ok")));
    Assert.Equal(SubscriptionEventKind.ToolsListChanged, (await NextAsync(subscription)).Kind);
    Assert.Contains(setup.Handler.Sent, r => r.Method == "GET");
  }

  private static async Task<SubscriptionEnd> CloseAsync(ContextSubscription subscription)
  {
    await subscription.DisposeAsync();
    return await subscription.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
  }

  // ---- broken and hostile streams -----------------------------------------------------------------------------------------------

  [Fact]
  public async Task A_modern_response_stream_that_breaks_is_asked_again_as_a_new_request_with_a_new_id()
  {
    var setup = new Setup(NewServer());
    var broken = 0;
    setup.Handler.Intercept = (sent, _, _) =>
      Task.FromResult<HttpResponseMessage?>(sent.RpcMethod == "tools/call" && Interlocked.Increment(ref broken) == 1 ? EndpointHandler.Sse(": starting\n\n") : null);
    await setup.ConnectAsync();
    await using var owned = setup;

    var result = await setup.Client.CallToolAsync("echo", Json.Parse("""{"text":"again"}"""), TestContext.Current.CancellationToken);

    Assert.Equal("again", result.Content.OfType<TextContent>().Single().Text);
    var calls = setup.Handler.Sent.Where(r => r.RpcMethod == "tools/call").ToArray();
    Assert.Equal(2, calls.Length);
    Assert.NotEqual(IdOf(calls[0]), IdOf(calls[1]));
  }

  [Fact]
  public async Task A_modern_request_gives_up_after_the_reissue_limit_with_a_transport_error()
  {
    var setup = new Setup(NewServer());
    setup.Handler.Intercept = (sent, _, _) =>
      Task.FromResult<HttpResponseMessage?>(sent.RpcMethod == "tools/call" ? EndpointHandler.Sse(": nothing\n\n") : null);
    await setup.ConnectAsync(transport: new HttpTransportOptions { MaxReissues = 1 });
    await using var owned = setup;

    var error = await Assert.ThrowsAsync<TransportException>(async () => await setup.Client.CallToolAsync("echo", default, TestContext.Current.CancellationToken));

    Assert.Contains("ended before the response", error.Message, StringComparison.Ordinal);
    Assert.Equal(2, setup.Handler.Sent.Count(r => r.RpcMethod == "tools/call"));
  }

  [Fact]
  public async Task A_legacy_stream_that_ends_early_is_resumed_with_last_event_id_after_the_retry_wait()
  {
    var setup = new Setup(NewServer("2025-11-25"));
    string? requestId = null;
    setup.Handler.Intercept = (sent, _, _) =>
    {
      if (sent.RpcMethod == "tools/call")
      {
        requestId = IdOf(sent);
        return Task.FromResult<HttpResponseMessage?>(EndpointHandler.Sse("id: 7\nretry: 5\ndata:\n\n"));
      }

      if (sent.Method == "GET" && sent.Header("Last-Event-ID") == "7")
      {
        return Task.FromResult<HttpResponseMessage?>(EndpointHandler.Sse(
          "id: 8\ndata: {\"jsonrpc\":\"2.0\",\"id\":" + requestId + ",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"resumed\"}]}}\n\n"));
      }

      return Task.FromResult<HttpResponseMessage?>(null);
    };
    await setup.ConnectAsync(Options(EraPreference.Legacy));
    await using var owned = setup;

    var result = await setup.Client.CallToolAsync("echo", default, TestContext.Current.CancellationToken);

    Assert.Equal("resumed", result.Content.OfType<TextContent>().Single().Text);
    var resume = setup.Handler.Sent.Single(r => r.Method == "GET" && r.Header("Last-Event-ID") == "7");
    Assert.False(string.IsNullOrEmpty(resume.Header("Mcp-Session-Id")));
    Assert.Single(setup.Handler.Sent, r => r.RpcMethod == "tools/call");
  }

  [Fact]
  public async Task An_event_larger_than_the_limit_fails_that_request_and_nothing_else()
  {
    var setup = new Setup(NewServer());
    setup.Handler.Intercept = (sent, _, _) =>
      Task.FromResult<HttpResponseMessage?>(sent.RpcMethod == "tools/call" && sent.Body.Contains("big", StringComparison.Ordinal) ? EndpointHandler.Sse("data: " + new string('x', 5000) + "\n\n") : null);
    await setup.ConnectAsync(transport: new HttpTransportOptions { MaxEventBytes = 1024 });
    await using var owned = setup;

    var error = await Assert.ThrowsAsync<TransportException>(async () => await setup.Client.CallToolAsync("echo", Json.Parse("""{"text":"big"}"""), TestContext.Current.CancellationToken));

    Assert.Contains("larger than 1024", error.Message, StringComparison.Ordinal);
    var fine = await setup.Client.CallToolAsync("echo", Json.Parse("""{"text":"small"}"""), TestContext.Current.CancellationToken);
    Assert.Equal("small", fine.Content.OfType<TextContent>().Single().Text);
  }

  [Fact]
  public async Task Junk_lines_comments_and_unknown_fields_before_the_response_are_skipped()
  {
    var setup = new Setup(NewServer());
    setup.Handler.Intercept = async (sent, request, ct) =>
    {
      if (sent.RpcMethod != "tools/call")
      {
        return null;
      }

      var real = await setup.Handler.PassthroughAsync(request, ct);
      var text = await real.Content.ReadAsStringAsync(ct);
      return EndpointHandler.Sse(string.Concat(Enumerable.Repeat(":\n", 500)) + "garbage line\nunknown: field\nretry: nope\n\n" + SseOf(text));
    };
    await setup.ConnectAsync();
    await using var owned = setup;

    var result = await setup.Client.CallToolAsync("echo", Json.Parse("""{"text":"through"}"""), TestContext.Current.CancellationToken);

    Assert.Equal("through", result.Content.OfType<TextContent>().Single().Text);
  }

  private static string SseOf(string body)
  {
    // The endpoint answers a call with JSON or an event stream; either way, hand the message over as one event.
    var trimmed = body.Trim();
    return trimmed.StartsWith('{') ? "data: " + trimmed + "\n\n" : trimmed + "\n\n";
  }

  [Fact]
  public async Task A_stream_that_goes_silent_is_dropped_after_the_idle_timeout_and_the_request_fails_in_bounded_time()
  {
    var setup = new Setup(NewServer());
    var open = new List<OpenStream>();
    setup.Handler.Intercept = (sent, _, _) =>
    {
      if (sent.RpcMethod != "tools/call")
      {
        return Task.FromResult<HttpResponseMessage?>(null);
      }

      var stream = new OpenStream();
      lock (open)
      {
        open.Add(stream);
      }

      _ = stream.WriteAsync(": hello\n\n");
      return Task.FromResult<HttpResponseMessage?>(EndpointHandler.Sse(stream));
    };
    await setup.ConnectAsync(transport: new HttpTransportOptions { StreamIdleTimeout = TimeSpan.FromMilliseconds(150), MaxReissues = 1 });
    await using var owned = setup;

    var started = DateTime.UtcNow;
    await Assert.ThrowsAsync<TransportException>(async () => await setup.Client.CallToolAsync("echo", default, TestContext.Current.CancellationToken));

    Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    Assert.Equal(2, open.Count);
  }

  // ---- the 2024-11-05 HTTP+SSE transport ----------------------------------------------------------------------------------------

  [Fact]
  public async Task A_server_with_only_the_old_http_sse_transport_is_reached_through_the_endpoint_event()
  {
    var server = NewServer("2024-11-05");
    using var old = new OldSseServer(server);
    using var http = new HttpClient(old);

    await using var client = await ContextClient.ConnectAsync(ContextTransport.Http(new Uri("http://127.0.0.1:47600/sse"), http, Fast()), Options(), TestContext.Current.CancellationToken);

    Assert.Equal(ProtocolEra.Legacy, client.Era);
    Assert.Equal("2024-11-05", client.ProtocolVersion);
    var result = await client.CallToolAsync("echo", Json.Parse("""{"text":"old"}"""), TestContext.Current.CancellationToken);
    Assert.Equal("old", result.Content.OfType<TextContent>().Single().Text);
    Assert.All(old.Posts, p => Assert.StartsWith("/messages?sessionId=", p, StringComparison.Ordinal));
  }

  [Fact]
  public async Task An_endpoint_event_that_names_another_origin_is_refused()
  {
    using var old = new OldSseServer(NewServer("2024-11-05")) { EndpointData = "http://127.0.0.1:1/steal" };
    using var http = new HttpClient(old);

    await Assert.ThrowsAnyAsync<ContextException>(async () =>
      await ContextClient.ConnectAsync(ContextTransport.Http(new Uri("http://127.0.0.1:47600/sse"), http, Fast()), Options(), TestContext.Current.CancellationToken));
    Assert.Empty(old.Posts);
  }

  [Fact]
  public async Task The_fallback_can_be_turned_off()
  {
    using var old = new OldSseServer(NewServer("2024-11-05"));
    using var http = new HttpClient(old);

    await Assert.ThrowsAnyAsync<ContextException>(async () =>
      await ContextClient.ConnectAsync(ContextTransport.Http(new Uri("http://127.0.0.1:47600/sse"), http, new HttpTransportOptions { AllowSseFallback = false }), Options(), TestContext.Current.CancellationToken));
  }

  private static string IdOf(SentRequest request)
  {
    using var document = JsonDocument.Parse(request.Body);
    return document.RootElement.GetProperty("id").GetRawText();
  }

  /// <summary>The 2024-11-05 HTTP+SSE server: GET opens the stream and names the POST endpoint; answers travel on the stream.</summary>
  private sealed class OldSseServer(ContextServer server) : HttpMessageHandler
  {
    private readonly ServerSessionState _session = new();
    private OpenStream? _stream;

    internal string EndpointData { get; init; } = "/messages?sessionId=s1";

    internal List<string> Posts { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      var path = request.RequestUri!.PathAndQuery;
      if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/sse")
      {
        _stream = new OpenStream();
        await _stream.WriteAsync("event: endpoint\ndata: " + EndpointData + "\n\n");
        return EndpointHandler.Sse(_stream);
      }

      if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/messages")
      {
        Posts.Add(path);
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var reply = await server.HandleMessageAsync(body, _session, null, null, cancellationToken);
        if (reply is not null)
        {
          await _stream!.WriteAsync("event: message\ndata: " + reply + "\n\n");
        }

        return new HttpResponseMessage(HttpStatusCode.Accepted);
      }

      // Cannot POST /sse, as an Express-style server says.
      return EndpointHandler.Plain(HttpStatusCode.NotFound, "Cannot " + request.Method + " " + path);
    }
  }
}
