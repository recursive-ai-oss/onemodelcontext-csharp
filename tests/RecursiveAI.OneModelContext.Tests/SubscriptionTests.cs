using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Protocol;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

// subscriptions/listen over a stream connection (stdio framing): the same events as over HTTP, filtered per stream.
public class SubscriptionTests
{
  private static ContextServer NewServer()
  {
    var server = new ContextServer("listen", "1.0");
    server.Tools.Add(new ToolDefinition("echo", "Echoes.", Json.Parse("""{"type":"object"}""")), (_, _) => ValueTask.FromResult(CallToolResult.FromText("ok")));
    server.Resources.Add(new ResourceDefinition("mem://a", "a"), (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, Text = "A" }] }));
    server.Prompts.Add(new PromptDefinition("p"), (_, _) => ValueTask.FromResult(new GetPromptResult { Messages = [] }));
    return server;
  }

  private static async Task<(ContextClient Client, Task Serving)> ConnectAsync(ContextServer server, EraPreference era)
  {
    var (a, b) = PipeEnd.CreatePair();
    var serving = server.RunAsync(ContextTransport.Stream(a), TestContext.Current.CancellationToken);
    var client = await ContextClient.ConnectAsync(ContextTransport.Stream(b), new ContextClientOptions { Era = era, EraCache = null }, TestContext.Current.CancellationToken);
    return (client, serving);
  }

  private static async Task<SubscriptionEvent> NextAsync(ContextSubscription subscription)
  {
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
    await foreach (var item in subscription.ReadAllAsync(linked.Token))
    {
      return item;
    }

    throw new InvalidOperationException("The subscription ended.");
  }

  [Theory]
  [InlineData(EraPreference.Modern)]
  [InlineData(EraPreference.Legacy)]
  public async Task Registry_changes_and_resource_updates_reach_a_stream_by_what_it_asked_for(EraPreference era)
  {
    var server = NewServer();
    var (client, serving) = await ConnectAsync(server, era);
    await using var closing = client;
    await using var subscription = await client.ListenAsync(
      new SubscriptionFilter { ToolsListChanged = true, PromptsListChanged = true, ResourceSubscriptions = ["mem://a"] },
      TestContext.Current.CancellationToken);

    server.Tools.Add(new ToolDefinition("two", "Second.", Json.Parse("""{"type":"object"}""")), (_, _) => ValueTask.FromResult(CallToolResult.FromText("ok")));
    Assert.Equal(SubscriptionEventKind.ToolsListChanged, (await NextAsync(subscription)).Kind);
    server.Prompts.Remove("p");
    Assert.Equal(SubscriptionEventKind.PromptsListChanged, (await NextAsync(subscription)).Kind);

    // Resource list changes were not asked for, and a resource nobody subscribed to is not announced.
    server.Resources.Add(new ResourceDefinition("mem://b", "b"), (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [] }));
    await server.NotifyResourceUpdatedAsync("mem://b", TestContext.Current.CancellationToken);
    await server.NotifyResourceUpdatedAsync("mem://a", TestContext.Current.CancellationToken);
    var updated = await NextAsync(subscription);
    Assert.Equal(SubscriptionEventKind.ResourceUpdated, updated.Kind);
    Assert.Equal("mem://a", updated.Uri);

    await subscription.DisposeAsync();
    Assert.Equal(SubscriptionEnd.Closed, await subscription.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task Two_streams_on_one_connection_each_get_their_own_events()
  {
    var server = NewServer();
    var (client, _) = await ConnectAsync(server, EraPreference.Modern);
    await using var closing = client;
    await using var tools = await client.ListenAsync(new SubscriptionFilter { ToolsListChanged = true }, TestContext.Current.CancellationToken);
    await using var prompts = await client.ListenAsync(new SubscriptionFilter { PromptsListChanged = true }, TestContext.Current.CancellationToken);

    server.Prompts.Remove("p");
    server.Tools.Remove("echo");

    Assert.Equal(SubscriptionEventKind.PromptsListChanged, (await NextAsync(prompts)).Kind);
    Assert.Equal(SubscriptionEventKind.ToolsListChanged, (await NextAsync(tools)).Kind);
  }

  [Fact]
  public async Task A_list_change_drops_the_clients_cached_list()
  {
    var server = new ContextServer("cached", "1.0", new ContextServerOptions { ListCacheTtl = TimeSpan.FromMinutes(5) });
    server.Tools.Add(new ToolDefinition("one", "One.", Json.Parse("""{"type":"object"}""")), (_, _) => ValueTask.FromResult(CallToolResult.FromText("ok")));
    var (client, _) = await ConnectAsync(server, EraPreference.Modern);
    await using var closing = client;
    await using var subscription = await client.ListenAsync(new SubscriptionFilter { ToolsListChanged = true }, TestContext.Current.CancellationToken);
    Assert.Single(await client.ListToolsAsync(TestContext.Current.CancellationToken));

    server.Tools.Add(new ToolDefinition("two", "Two.", Json.Parse("""{"type":"object"}""")), (_, _) => ValueTask.FromResult(CallToolResult.FromText("ok")));
    await NextAsync(subscription);

    Assert.Equal(2, (await client.ListToolsAsync(TestContext.Current.CancellationToken)).Count);
  }

  [Fact]
  public async Task The_server_ending_its_streams_is_reported_and_disposing_the_client_ends_the_rest()
  {
    var server = NewServer();
    var (client, _) = await ConnectAsync(server, EraPreference.Modern);
    var first = await client.ListenAsync(new SubscriptionFilter { ToolsListChanged = true }, TestContext.Current.CancellationToken);
    server.EndSubscriptions();
    Assert.Equal(SubscriptionEnd.ServerEnded, await first.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

    var second = await client.ListenAsync(new SubscriptionFilter { ToolsListChanged = true }, TestContext.Current.CancellationToken);
    await client.DisposeAsync();
    Assert.Equal(SubscriptionEnd.ConnectionLost, await second.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
  }
}
