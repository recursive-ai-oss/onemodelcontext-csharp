using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RecursiveAI.OneModelContext.Extensions.AI;
using RecursiveAI.OneModelContext.Protocol;
using Xunit;
using McpText = RecursiveAI.OneModelContext.Protocol.TextContent;
using AIText = Microsoft.Extensions.AI.TextContent;

namespace RecursiveAI.OneModelContext.AdapterTests;

public sealed class ExtensionsAITests
{
  private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

  private static ContextServer ServerWithTools()
  {
    var server = new ContextServer("tools", "1.0.0");
    server.Tools.Add(
      new ToolDefinition("add", "Adds two numbers.", JsonDocument.Parse("""{"type":"object","properties":{"a":{"type":"integer"},"b":{"type":"integer"}},"required":["a","b"]}""")),
      (args, _) => ValueTask.FromResult(CallToolResult.FromText((args.GetProperty("a").GetInt32() + args.GetProperty("b").GetInt32()).ToString(System.Globalization.CultureInfo.InvariantCulture))));
    server.Tools.Add(
      new ToolDefinition("picture", "Returns a caption and an image.", JsonDocument.Parse("""{"type":"object"}""")),
      (_, _) => ValueTask.FromResult(new CallToolResult { Content = [McpText.Of("a cat"), new ImageContent { Data = Convert.ToBase64String(Png), MimeType = "image/png" }] }));
    server.Tools.Add(
      new ToolDefinition("point", "Returns structured data.", JsonDocument.Parse("""{"type":"object"}""")),
      (_, _) => ValueTask.FromResult(new CallToolResult { Content = [McpText.Of("{\"x\":1}")], StructuredContent = JsonDocument.Parse("""{"x":1}""").RootElement }));
    server.Tools.Add(
      new ToolDefinition("fail", "Fails.", JsonDocument.Parse("""{"type":"object"}""")),
      (_, _) => ValueTask.FromResult(CallToolResult.FromError("no luck")));
    return server;
  }

  private static async Task<(ContextClient Client, Func<Task> Stop)> ConnectAsync(ContextServer server, EraPreference era, ContextClientOptions? options = null)
  {
    var wire = new Wire();
    var stop = new CancellationTokenSource();
    var serving = server.RunAsync(wire.Server(), stop.Token);
    var client = await ContextClient.ConnectAsync(wire.Client(), options ?? new ContextClientOptions { Era = era }, TestContext.Current.CancellationToken);
    return (client, async () =>
    {
      await client.DisposeAsync();
      await stop.CancelAsync();
      try
      {
        await serving;
      }
      catch (OperationCanceledException)
      {
      }
    });
  }

  [Theory]
  [MemberData(nameof(Eras.Both), MemberType = typeof(Eras))]
  public async Task Server_tools_become_functions_with_name_description_and_schema(EraPreference era)
  {
    var (client, stop) = await ConnectAsync(ServerWithTools(), era);
    var functions = await client.ToAIFunctionsAsync(TestContext.Current.CancellationToken);

    var add = functions.Single(f => f.Name == "add");
    Assert.Equal("Adds two numbers.", add.Description);
    Assert.Equal("integer", add.JsonSchema.GetProperty("properties").GetProperty("a").GetProperty("type").GetString());
    await stop();
  }

  [Theory]
  [MemberData(nameof(Eras.Both), MemberType = typeof(Eras))]
  public async Task A_chat_client_calls_a_server_tool_through_function_invocation(EraPreference era)
  {
    var (client, stop) = await ConnectAsync(ServerWithTools(), era);
    var functions = await client.ToAIFunctionsAsync(TestContext.Current.CancellationToken);

    var model = new FakeChatClient((messages, options) =>
    {
      var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
      if (result is not null)
      {
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, "the sum is " + result.Result));
      }

      Assert.Contains(options!.Tools!, t => t.Name == "add");
      return new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "add", new Dictionary<string, object?> { ["a"] = 2, ["b"] = 40 })]));
    });

    using var chat = new FunctionInvokingChatClient(model);
    var response = await chat.GetResponseAsync("add", new ChatOptions { Tools = [.. functions] }, TestContext.Current.CancellationToken);

    Assert.Equal("the sum is 42", response.Text);
    await stop();
  }

  [Theory]
  [MemberData(nameof(Eras.Both), MemberType = typeof(Eras))]
  public async Task Results_map_to_text_data_structured_and_error_content(EraPreference era)
  {
    var (client, stop) = await ConnectAsync(ServerWithTools(), era);
    var functions = await client.ToAIFunctionsAsync(TestContext.Current.CancellationToken);
    object? Invoke(string name) => functions.Single(f => f.Name == name).InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult();

    var picture = Assert.IsAssignableFrom<IList<AIContent>>(Invoke("picture"));
    Assert.Equal("a cat", Assert.IsType<AIText>(picture[0]).Text);
    var image = Assert.IsType<DataContent>(picture[1]);
    Assert.Equal("image/png", image.MediaType);
    Assert.Equal(Png, image.Data.ToArray());

    var point = Assert.IsType<JsonElement>(Invoke("point"));
    Assert.Equal(1, point.GetProperty("x").GetInt32());

    Assert.Equal("Error: no luck", Invoke("fail"));
    await stop();
  }

  [Theory]
  [MemberData(nameof(Eras.Both), MemberType = typeof(Eras))]
  public async Task Functions_are_served_as_tools(EraPreference era)
  {
    var server = new ContextServer("fn", "1.0.0");
    server.AddAIFunctions([
      AIFunctionFactory.Create(([Description("First number")] int a, int b) => a * b, "multiply", "Multiplies."),
      AIFunctionFactory.Create(() => "plain", "plain"),
      AIFunctionFactory.Create(() => new DataContent(Png, "image/png"), "image"),
      AIFunctionFactory.Create(string () => throw new InvalidOperationException("private detail"), "broken"),
    ]);

    var (client, stop) = await ConnectAsync(server, era);
    var tools = await client.ListToolsAsync(TestContext.Current.CancellationToken);
    Assert.Equal(["broken", "image", "multiply", "plain"], tools.Select(t => t.Name).ToArray());

    var multiply = tools.Single(t => t.Name == "multiply");
    Assert.Equal("Multiplies.", multiply.Description);
    Assert.Equal("First number", multiply.InputSchema.GetProperty("properties").GetProperty("a").GetProperty("description").GetString());

    Assert.Equal("42", (await client.CallToolAsync("multiply", Json("""{"a":6,"b":7}"""), TestContext.Current.CancellationToken)).Text);
    Assert.Equal("plain", (await client.CallToolAsync("plain", default, TestContext.Current.CancellationToken)).Text);

    var image = (await client.CallToolAsync("image", default, TestContext.Current.CancellationToken)).Content.OfType<ImageContent>().Single();
    Assert.Equal(Png, Convert.FromBase64String(image.Data));

    var broken = await client.CallToolAsync("broken", default, TestContext.Current.CancellationToken);
    Assert.True(broken.IsError);
    Assert.DoesNotContain("private detail", broken.Text);
    await stop();
  }

  [Theory]
  [MemberData(nameof(Eras.Both), MemberType = typeof(Eras))]
  public async Task A_function_served_as_a_tool_round_trips_to_a_function(EraPreference era)
  {
    var server = new ContextServer("fn", "1.0.0");
    server.AddAIFunction(AIFunctionFactory.Create((string text) => text.ToUpperInvariant(), "shout", "Upper-cases."));
    var (client, stop) = await ConnectAsync(server, era);

    var function = Assert.Single(await client.ToAIFunctionsAsync(TestContext.Current.CancellationToken));
    var result = await function.InvokeAsync(new AIFunctionArguments { ["text"] = "hi" }, TestContext.Current.CancellationToken);
    Assert.Equal("HI", result);
    await stop();
  }

  [Theory]
  [MemberData(nameof(Eras.Both), MemberType = typeof(Eras))]
  public async Task Sampling_is_answered_by_a_chat_client(EraPreference era)
  {
    var server = new ContextServer("sampler", "1.0.0");
    server.Tools.Add(
      new ToolDefinition("ask", "Asks the host's model.", JsonDocument.Parse("""{"type":"object"}""")),
      async (_, context) =>
      {
        var answer = await context.SampleAsync(new CreateMessageRequestParams
        {
          Messages = [new SamplingMessage { Role = "user", Content = [McpText.Of("ping")] }],
          SystemPrompt = "be brief",
          MaxTokens = 50,
        });
        return CallToolResult.FromText(answer.Model + ":" + string.Concat(answer.Content.OfType<McpText>().Select(c => c.Text)));
      });

    var model = new FakeChatClient((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, "pong")) { ModelId = "fake-1", FinishReason = ChatFinishReason.Stop });
    var (client, stop) = await ConnectAsync(server, era, new ContextClientOptions { Era = era, OnSample = ChatClientSampling.CreateHandler(model) });

    Assert.Equal("fake-1:pong", (await client.CallToolAsync("ask", default, TestContext.Current.CancellationToken)).Text);

    var (messages, options) = Assert.Single(model.Requests);
    Assert.Equal("ping", Assert.Single(messages).Text);
    Assert.Equal(ChatRole.User, messages[0].Role);
    Assert.Equal("be brief", options!.Instructions);
    Assert.Equal(50, options.MaxOutputTokens);
    await stop();
  }

  private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;
}
