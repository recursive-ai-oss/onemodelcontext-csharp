using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Diagnostics;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

public class ClientServerTests
{
  private static readonly JsonElement ObjectSchema = Json.Parse("""{"type":"object"}""");

  private sealed class Pair : IAsyncDisposable
  {
    private readonly CancellationTokenSource _stop = new();

    public required ContextServer Server { get; init; }

    public required ContextClient Client { get; init; }

    public required Task Serving { get; init; }

    public async ValueTask DisposeAsync()
    {
      await Client.DisposeAsync();
      await _stop.CancelAsync();
      await Serving.WaitAsync(TimeSpan.FromSeconds(10));
    }
  }

  private static async Task<Pair> ConnectAsync(ContextServer server, ContextClientOptions? clientOptions = null)
  {
    var (a, b) = PipeEnd.CreatePair();
    var serving = server.RunAsync(ContextTransport.Stream(a), TestContext.Current.CancellationToken);
    var client = await ContextClient.ConnectAsync(
      ContextTransport.Stream(b),
      clientOptions ?? new ContextClientOptions { Codecs = [new TestClientCodec(ProtocolEra.Modern, ProtocolVersions.V20260728)] },
      TestContext.Current.CancellationToken);
    return new Pair { Server = server, Client = client, Serving = serving };
  }

  private static ContextServer NewServer() =>
    new("e2e", "1.0", new ContextServerOptions { Codecs = [new TestServerCodec(ProtocolEra.Modern, ProtocolVersions.V20260728)] });

  [Theory]
  [InlineData("2026-07-28")]
  [InlineData("2025-11-25")]
  [InlineData("2025-06-18")]
  [InlineData("2025-03-26")]
  [InlineData("2024-11-05")]
  public async Task Tool_argument_overloads_preserve_objects_and_existing_call_shapes(string version)
  {
    var server = new ContextServer("arguments", "1");
    server.Tools.Add(new ToolDefinition("echo", "Echoes arguments.", ObjectSchema), (args, _) =>
      ValueTask.FromResult(CallToolResult.FromText(args.ValueKind == JsonValueKind.Undefined ? "none" : args.GetRawText())));
    await using var pair = await ConnectAsync(server, new ContextClientOptions { SupportedVersions = [version] });
    var ct = TestContext.Current.CancellationToken;
    Assert.Equal(version, pair.Client.ProtocolVersion);
    Assert.Equal("{\"path\":\"README.md\"}", (await pair.Client.CallToolAsync("echo", new { path = "README.md" }, ct)).Text);
    Assert.Equal("{\"path\":\"README.md\"}", (await pair.Client.CallToolAsync("echo", new ToolArguments("README.md"), ToolArgumentsJson.Default.ToolArguments, ct)).Text);
    Assert.Equal("{\"path\":\"a\"}", (await pair.Client.CallToolAsync("echo", Json.Parse("{\"path\":\"a\"}"), ct)).Text);
    Assert.Equal("{}", (await pair.Client.CallToolAsync("echo", cancellationToken: ct)).Text);
    Assert.Equal("{}", (await pair.Client.CallToolAsync("echo", default, ct)).Text);
    Assert.Equal("{}", (await pair.Client.CallToolAsync("echo", default, progress: null, cancellationToken: ct)).Text);
  }

  [Fact]
  public async Task The_client_lists_and_calls_what_the_server_registered()
  {
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("add", "Adds.", ObjectSchema), (args, _) => ValueTask.FromResult(CallToolResult.FromText((args.GetProperty("a").GetInt32() + args.GetProperty("b").GetInt32()).ToString(System.Globalization.CultureInfo.InvariantCulture))));
    server.Resources.Add(new ResourceDefinition("mem://a", "a"), (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, Text = "A" }] }));
    server.Prompts.Add(new PromptDefinition("p"), (_, _) => ValueTask.FromResult(new GetPromptResult { Messages = [new PromptMessage { Role = "user", Content = TextContent.Of("hi") }] }));
    await using var pair = await ConnectAsync(server);

    var tools = await pair.Client.ListToolsAsync(TestContext.Current.CancellationToken);
    var sum = await pair.Client.CallToolAsync("add", Json.Parse("""{"a":2,"b":3}"""), TestContext.Current.CancellationToken);
    var resource = await pair.Client.ReadResourceAsync("mem://a", TestContext.Current.CancellationToken);
    var prompt = await pair.Client.GetPromptAsync("p", null, TestContext.Current.CancellationToken);

    Assert.Equal("add", Assert.Single(tools).Name);
    Assert.Equal("5", sum.Text);
    Assert.Equal("A", Assert.IsType<TextResourceContents>(resource.Contents[0]).Text);
    Assert.Equal("hi", Assert.IsType<TextContent>(prompt.Messages[0].Content).Text);
    Assert.Equal(ProtocolEra.Modern, pair.Client.Era);
    Assert.Equal("2026-07-28", pair.Client.ProtocolVersion);
    Assert.Equal("test-server", pair.Client.ServerInfo.Name);
  }

  [Fact]
  public async Task Listing_follows_pages()
  {
    var server = new ContextServer("e2e", "1.0", new ContextServerOptions { Codecs = [new TestServerCodec(ProtocolEra.Modern, ProtocolVersions.V20260728)], MaxPageSize = 3 });
    for (var i = 0; i < 10; i++)
    {
      server.Tools.Add(new ToolDefinition("t" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture), "d", ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("x")));
    }

    await using var pair = await ConnectAsync(server);

    var tools = await pair.Client.ListToolsAsync(TestContext.Current.CancellationToken);

    Assert.Equal(10, tools.Count);
    Assert.Equal(Enumerable.Range(0, 10).Select(i => "t" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture)), tools.Select(t => t.Name));
  }

  [Fact]
  public async Task A_protocol_error_reaches_the_caller_as_a_typed_exception_and_a_tool_error_does_not()
  {
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("fail", "d", ObjectSchema), (_, _) => throw new ToolException("no such file"));
    await using var pair = await ConnectAsync(server);

    var failed = await pair.Client.CallToolAsync("fail", default, TestContext.Current.CancellationToken);
    var unknown = await Assert.ThrowsAsync<ProtocolException>(async () => await pair.Client.CallToolAsync("missing", default, TestContext.Current.CancellationToken));
    var notFound = await Assert.ThrowsAsync<ProtocolException>(async () => await pair.Client.ReadResourceAsync("mem://none", TestContext.Current.CancellationToken));

    Assert.True(failed.IsError);
    Assert.Equal("no such file", failed.Text);
    Assert.Equal(-32602, unknown.Code);
    Assert.Equal(-32602, notFound.Code);
  }

  [Fact]
  public async Task Many_concurrent_calls_each_get_their_own_answer()
  {
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("echo", "d", ObjectSchema), async (args, _) =>
    {
      await Task.Yield();
      return CallToolResult.FromText(args.GetProperty("n").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture));
    });
    await using var pair = await ConnectAsync(server);

    var calls = Enumerable.Range(0, 200).Select(async i => (await pair.Client.CallToolAsync("echo", Json.Parse("{\"n\":" + i + "}"), TestContext.Current.CancellationToken)).Text).ToArray();
    var results = await Task.WhenAll(calls);

    Assert.Equal(Enumerable.Range(0, 200).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), results);
  }

  [Fact]
  public async Task Cancelling_a_call_cancels_the_servers_handler()
  {
    var server = NewServer();
    var entered = new TaskCompletionSource();
    var handlerCancelled = new TaskCompletionSource();
    server.Tools.Add(new ToolDefinition("hang", "d", ObjectSchema), async (_, ctx) =>
    {
      entered.SetResult();
      try
      {
        await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
      }
      catch (OperationCanceledException)
      {
        handlerCancelled.SetResult();
        throw;
      }

      return CallToolResult.FromText("never");
    });
    await using var pair = await ConnectAsync(server);
    using var cts = new CancellationTokenSource();

    var call = pair.Client.CallToolAsync("hang", default, cts.Token).AsTask();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    await cts.CancelAsync();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    await handlerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
  }

  [Fact]
  public async Task A_handler_can_ask_the_client_a_question_and_the_clients_handler_answers()
  {
    var server = NewServer();
    server.Tools.Add(new ToolDefinition("rm", "d", ObjectSchema), async (_, ctx) => CallToolResult.FromText(await ctx.ConfirmAsync("Delete 3 files?") ? "deleted" : "cancelled"));
    var answers = new List<string>();
    var options = new ContextClientOptions
    {
      Codecs = [new TestClientCodec(ProtocolEra.Modern, ProtocolVersions.V20260728)],
      Capabilities = new ClientCapabilities { Elicitation = new ElicitationCapability() },
      RequestHandler = new LambdaHandler((method, parameters) =>
      {
        answers.Add(method + ":" + parameters.GetProperty("message").GetString());
        return new ElicitResult { Action = "accept", Content = new Dictionary<string, JsonElement> { ["confirm"] = Json.Parse("true") } };
      }),
    };
    await using var pair = await ConnectAsync(server, options);

    var result = await pair.Client.CallToolAsync("rm", default, TestContext.Current.CancellationToken);

    Assert.Equal("deleted", result.Text);
    Assert.Equal(["elicitation/create:Delete 3 files?"], answers);
  }

  [Fact]
  public async Task Connecting_without_any_codec_says_so()
  {
    var (a, b) = PipeEnd.CreatePair();
    await using var _ = a;

    var ex = await Assert.ThrowsAsync<ContextException>(async () => await ContextClient.ConnectAsync(ContextTransport.Stream(b), new ContextClientOptions { Codecs = [] }, TestContext.Current.CancellationToken));

    Assert.Contains("No protocol codec", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task When_no_era_negotiates_the_error_names_both_sides()
  {
    var (a, b) = PipeEnd.CreatePair();
    await using var _ = a;
    var options = new ContextClientOptions { Codecs = [new RefusedClientCodec(ProtocolEra.Modern), new RefusedClientCodec(ProtocolEra.Legacy)], SupportedVersions = ["2026-07-28", "2025-06-18"] };

    var ex = await Assert.ThrowsAsync<ProtocolVersionException>(async () => await ContextClient.ConnectAsync(ContextTransport.Stream(b), options, TestContext.Current.CancellationToken));

    Assert.Contains("modern 2026-07-28", ex.Message, StringComparison.Ordinal);
    Assert.Contains("legacy 2025-06-18", ex.Message, StringComparison.Ordinal);
    Assert.Equal(["2026-07-28", "2025-06-18"], ex.Local);
  }

  [Fact]
  public async Task The_era_preference_skips_the_other_codec()
  {
    var (a, b) = PipeEnd.CreatePair();
    await using var _ = a;
    var options = new ContextClientOptions { Era = EraPreference.Legacy, Codecs = [new TestClientCodec(ProtocolEra.Modern, "2026-07-28")] };

    var ex = await Assert.ThrowsAsync<ContextException>(async () => await ContextClient.ConnectAsync(ContextTransport.Stream(b), options, TestContext.Current.CancellationToken));

    Assert.Contains("Legacy", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Losing_the_connection_during_a_call_is_a_transport_exception()
  {
    var (a, b) = PipeEnd.CreatePair();
    var client = await ContextClient.ConnectAsync(ContextTransport.Stream(b), new ContextClientOptions { Codecs = [new TestClientCodec(ProtocolEra.Modern, "2026-07-28")] }, TestContext.Current.CancellationToken);
    var call = client.CallToolAsync("x", default, TestContext.Current.CancellationToken).AsTask();
    await new LineReader(a).ReadLineAsync();

    await a.DisposeAsync();

    await Assert.ThrowsAsync<TransportException>(() => call);
    await client.DisposeAsync();
  }

  [Fact]
  public async Task The_server_ends_when_the_peer_closes()
  {
    var server = NewServer();
    var (a, b) = PipeEnd.CreatePair();
    var serving = server.RunAsync(ContextTransport.Stream(a), TestContext.Current.CancellationToken);

    await b.DisposeAsync();

    await serving.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
  }

  [Fact]
  public async Task Cancelling_over_the_wire_by_notification_stops_the_handler_and_sends_no_response()
  {
    var server = NewServer();
    var entered = new TaskCompletionSource();
    var cancelled = new TaskCompletionSource();
    server.Tools.Add(new ToolDefinition("hang", "d", ObjectSchema), async (_, ctx) =>
    {
      entered.SetResult();
      try
      {
        await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
      }
      catch (OperationCanceledException)
      {
        cancelled.SetResult();
        throw;
      }

      return CallToolResult.FromText("x");
    });
    var (a, b) = PipeEnd.CreatePair();
    var serving = server.RunAsync(ContextTransport.Stream(a), TestContext.Current.CancellationToken);
    var reader = new LineReader(b);

    await b.WriteAsync("""{"jsonrpc":"2.0","id":"c1","method":"tools/call","params":{"name":"hang"}}"""u8.ToArray().Concat("\n"u8.ToArray()).ToArray(), TestContext.Current.CancellationToken);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    await b.WriteAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"c1","reason":"user"}}"""u8.ToArray().Concat("\n"u8.ToArray()).ToArray(), TestContext.Current.CancellationToken);
    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    await b.WriteAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""u8.ToArray().Concat("\n"u8.ToArray()).ToArray(), TestContext.Current.CancellationToken);

    var next = await reader.ReadJsonAsync();
    Assert.Equal(2, next.GetProperty("id").GetInt32());
    await b.DisposeAsync();
    await serving.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
  }

  [Fact]
  public async Task Stdout_style_framing_keeps_unicode_and_embedded_newlines_in_strings_intact()
  {
    var server = NewServer();
    var text = "line1\nline2\r\n\u00e9\u4e2d\u6587 \ud83d\ude00 \"quoted\" \\ backslash";
    server.Tools.Add(new ToolDefinition("echo", "d", ObjectSchema), (args, _) => ValueTask.FromResult(CallToolResult.FromText(args.GetProperty("t").GetString()!)));
    await using var pair = await ConnectAsync(server);

    var result = await pair.Client.CallToolAsync("echo", Json.Parse("{\"t\":" + JsonSerializer.Serialize(text, WireJsonContext.Default.String) + "}"), TestContext.Current.CancellationToken);

    Assert.Equal(text, result.Text);
  }

  private sealed class LambdaHandler(Func<string, JsonElement, Result> answer) : IClientRequestHandler
  {
    public ValueTask<Result> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken) => ValueTask.FromResult(answer(method, parameters));
  }
}

public class RegistryAndTemplateTests
{
  [Theory]
  [InlineData("file:///{path}", "file:///readme.md", true, "path", "readme.md")]
  [InlineData("file:///{path}", "file:///a/b", false, null, null)]
  [InlineData("file:///{+path}", "file:///a/b%20c", true, "path", "a/b c")]
  [InlineData("users/{id}/posts/{post}", "users/7/posts/hello", true, "post", "hello")]
  [InlineData("users/{id}", "users/", false, null, null)]
  [InlineData("users/{id}", "other/1", false, null, null)]
  [InlineData("{a}-{b}", "x-y", true, "b", "y")]
  [InlineData("{?q}", "?q=1", false, null, null)]
  [InlineData("{unclosed", "x", false, null, null)]
  public void Templates_match_whole_uris(string template, string uri, bool matches, string? variable, string? value)
  {
    var result = UriTemplateMatcher.TryMatch(template, uri, out var variables);

    Assert.Equal(matches, result);
    if (matches)
    {
      Assert.Equal(value, variables[variable!]);
    }
  }

  [Fact]
  public void Adding_the_same_key_replaces_and_listing_is_ordinal()
  {
    var server = new ContextServer("t", "1");
    var schema = Json.Parse("""{"type":"object"}""");
    server.Tools.Add(new ToolDefinition("b", "one", schema), (_, _) => default);
    server.Tools.Add(new ToolDefinition("B", "upper", schema), (_, _) => default);
    server.Tools.Add(new ToolDefinition("b", "two", schema), (_, _) => default);

    Assert.Equal(["B", "b"], server.Tools.Definitions.Select(t => t.Name));
    Assert.Equal("two", server.Tools.Definitions[1].Description);
    Assert.Equal(2, server.Tools.Count);
  }

  [Fact]
  public void A_tool_needs_a_name_and_an_object_schema()
  {
    Assert.Throws<ArgumentException>(() => new ToolDefinition("", "d", Json.Parse("{}")));
    Assert.Throws<ArgumentException>(() => new ToolDefinition("t", "d", Json.Parse("[]")));
    Assert.Throws<ArgumentException>(() => new ToolDefinition("t", "d", default(JsonElement)));
    using var doc = JsonDocument.Parse("""{"type":"object"}""");
    Assert.Equal("t", new ToolDefinition("t", "d", doc).Name);
  }

  [Fact]
  public async Task Concurrent_registration_loses_nothing()
  {
    var server = new ContextServer("t", "1");
    var schema = Json.Parse("""{"type":"object"}""");

    await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() =>
      server.Tools.Add(new ToolDefinition("t" + i, "d", schema), (_, _) => default), TestContext.Current.CancellationToken)));

    Assert.Equal(100, server.Tools.Count);
  }
}

public class DiagnosticsTests
{
  [Fact]
  public async Task Requests_produce_activities_and_a_duration_metric_when_something_listens()
  {
    // The listeners are process-wide, so requests from tests running in parallel arrive here too, on other threads.
    var activities = new System.Collections.Concurrent.ConcurrentQueue<System.Diagnostics.Activity>();
    using var activityListener = new System.Diagnostics.ActivityListener
    {
      ShouldListenTo = source => source.Name == "RecursiveAI.OneModelContext",
      Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllData,
      ActivityStopped = activities.Enqueue,
    };
    System.Diagnostics.ActivitySource.AddActivityListener(activityListener);
    var measurements = new System.Collections.Concurrent.ConcurrentQueue<string>();
    using var meterListener = new System.Diagnostics.Metrics.MeterListener();
    meterListener.InstrumentPublished = (instrument, listener) =>
    {
      if (instrument.Meter.Name == "RecursiveAI.OneModelContext")
      {
        listener.EnableMeasurementEvents(instrument);
      }
    };
    meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
    {
      foreach (var tag in tags)
      {
        if (tag.Key == "mcp.method.name")
        {
          measurements.Enqueue(instrument.Name + ":" + tag.Value);
        }
      }
    });
    meterListener.Start();
    var server = new ContextServer("t", "1", new ContextServerOptions { Codecs = [new TestServerCodec(ProtocolEra.Modern, "2026-07-28")] });

    await server.HandleMessageAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", cancellationToken: TestContext.Current.CancellationToken);

    Assert.Contains(activities, a => a.OperationName == "tools/list" && a.Kind == System.Diagnostics.ActivityKind.Server);
    Assert.Contains("mcp.server.operation.duration:tools/list", measurements);
  }

  [Fact]
  public void Diagnostic_subscribers_can_unsubscribe()
  {
    // Other tests publish concurrently, so count only this test's own notes.
    var marker = Guid.NewGuid().ToString("N");
    var seen = 0;
    var subscription = ContextDiagnostics.Subscribe(e =>
    {
      if (e.Message.StartsWith(marker, StringComparison.Ordinal))
      {
        Interlocked.Increment(ref seen);
      }
    });
    ContextDiagnostics.Publish(DiagnosticKind.Note, marker + "a");
    subscription.Dispose();
    ContextDiagnostics.Publish(DiagnosticKind.Note, marker + "b");

    Assert.Equal(1, seen);
  }
}

public class BaseLayerTests
{
  [Fact]
  public void The_json_rpc_layer_references_no_protocol_types()
  {
    // The base layer must stay reusable for other protocols: nothing in RecursiveAI.OneModelContext.JsonRpc may mention a type from
    // another namespace of this library.
    const string BaseNamespace = "RecursiveAI.OneModelContext.JsonRpc";
    var assembly = typeof(JsonRpcConnection).Assembly;
    var offenders = new List<string>();
    var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;

    void Check(Type? type, string where)
    {
      if (type is null)
      {
        return;
      }

      if (type.IsGenericType)
      {
        foreach (var argument in type.GetGenericArguments())
        {
          Check(argument, where);
        }
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
    // The text of the base layer's own sources is checked the same way by the build: no "using" of the protocol namespaces is possible
    // without a type from them showing up above.
  }
}

public sealed record ToolArguments(string path);

[JsonSerializable(typeof(ToolArguments))]
internal sealed partial class ToolArgumentsJson : JsonSerializerContext
{
}
