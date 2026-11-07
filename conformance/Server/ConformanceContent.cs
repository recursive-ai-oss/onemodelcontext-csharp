using System.Text.Json;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.ConformanceServer;

/// <summary>The tools, resources and prompts the conformance scenarios call, written from the scenarios' names and the protocol's feature pages.</summary>
internal static class ConformanceContent
{
  // A 1x1 PNG and a short WAV header, enough for a client to treat them as image and audio.
  private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
  private const string Wav = "UklGRiQAAABXQVZFZm10IBAAAAABAAEAQB8AAIA+AAACABAAZGF0YQAAAAA=";

  private static readonly JsonElement NoArguments = Parse("""{"type":"object","properties":{}}""");

  internal static ContextServer Create()
  {
    var server = new ContextServer("recursiveai-onemodelcontext-conformance", "0.1.0")
    {
      Instructions = "A server for the protocol's conformance suite.",
    };
    AddTools(server);
    AddResources(server);
    AddPrompts(server);
    return server;
  }

  private static JsonElement Parse(string json)
  {
    using var document = JsonDocument.Parse(json);
    return document.RootElement.Clone();
  }

  private static ValueTask<CallToolResult> Text(string text) => ValueTask.FromResult(CallToolResult.FromText(text));

  private static void AddTools(ContextServer server)
  {
    server.Tools.Add(new ToolDefinition("test_simple_text", "Returns simple text.", NoArguments), (_, _) => Text("This is a simple text response for testing."));

    server.Tools.Add(
      new ToolDefinition("test_image_content", "Returns an image.", NoArguments),
      (_, _) => ValueTask.FromResult(new CallToolResult { Content = [new ImageContent { Data = Png, MimeType = "image/png" }] }));

    server.Tools.Add(
      new ToolDefinition("test_audio_content", "Returns audio.", NoArguments),
      (_, _) => ValueTask.FromResult(new CallToolResult { Content = [new AudioContent { Data = Wav, MimeType = "audio/wav" }] }));

    server.Tools.Add(
      new ToolDefinition("test_embedded_resource", "Returns an embedded resource.", NoArguments),
      (_, _) => ValueTask.FromResult(new CallToolResult
      {
        Content = [new EmbeddedResource { Resource = new TextResourceContents { Uri = "test://embedded-resource", MimeType = "text/plain", Text = "This is an embedded resource content." } }],
      }));

    server.Tools.Add(
      new ToolDefinition("test_multiple_content_types", "Returns text, an image and a resource.", NoArguments),
      (_, _) => ValueTask.FromResult(new CallToolResult
      {
        Content =
        [
          TextContent.Of("Multiple content types test:"),
          new ImageContent { Data = Png, MimeType = "image/png" },
          new EmbeddedResource { Resource = new TextResourceContents { Uri = "test://mixed-content-resource", MimeType = "application/json", Text = """{"test":"data","value":123}""" } },
        ],
      }));

    server.Tools.Add(
      new ToolDefinition("test_tool_with_logging", "Logs while it runs.", NoArguments),
      async (_, ctx) =>
      {
        await ctx.LogAsync("info", "Tool execution started");
        await ctx.LogAsync("info", "Tool processing data");
        await ctx.LogAsync("info", "Tool execution completed");
        return CallToolResult.FromText("Logging test completed");
      });

    server.Tools.Add(new ToolDefinition("test_error_handling", "Always fails.", NoArguments), (_, _) => throw new ToolException("This tool intentionally returns an error for testing"));

    server.Tools.Add(
      new ToolDefinition("test_tool_with_progress", "Reports progress.", NoArguments),
      async (_, ctx) =>
      {
        await ctx.ReportProgressAsync(0, 100, "Starting");
        await ctx.ReportProgressAsync(50, 100, "Halfway");
        await ctx.ReportProgressAsync(100, 100, "Done");
        return CallToolResult.FromText("Progress test completed");
      });

    server.Tools.Add(
      new ToolDefinition("test_reconnection", "Closes its event stream and finishes later.", NoArguments),
      async (_, ctx) =>
      {
        if (RecursiveAI.OneModelContext.Hosting.HttpRequestScope.Current is { } scope)
        {
          await scope.CloseSseConnectionAsync(TimeSpan.FromMilliseconds(100), ctx.CancellationToken);
          await Task.Delay(300, ctx.CancellationToken);
        }

        return CallToolResult.FromText("Reconnection test completed");
      });
  }

  private static void AddResources(ContextServer server)
  {
    server.Resources.Add(
      new ResourceDefinition("test://static-text", "Static Text") { MimeType = "text/plain", Description = "A static text resource." },
      (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, MimeType = "text/plain", Text = "This is the content of the static text resource." }] }));

    server.Resources.Add(
      new ResourceDefinition("test://static-binary", "Static Binary") { MimeType = "image/png", Description = "A static binary resource." },
      (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [new BlobResourceContents { Uri = uri, MimeType = "image/png", Blob = Png }] }));

    server.ResourceTemplates.Add(
      new ResourceTemplateDefinition("test://template/{id}/data", "Template") { MimeType = "application/json", Description = "A templated resource." },
      (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, MimeType = "application/json", Text = JsonSerializer.Serialize(new Dictionary<string, string> { ["uri"] = uri }, ConformanceJson.Default.DictionaryStringString) }] }));
  }

  private static void AddPrompts(ContextServer server)
  {
    server.Prompts.Add(
      new PromptDefinition("test_simple_prompt", "A prompt without arguments."),
      (_, _) => ValueTask.FromResult(new GetPromptResult { Messages = [new PromptMessage { Role = "user", Content = TextContent.Of("This is a simple prompt for testing.") }] }));

    server.Prompts.Add(
      new PromptDefinition("test_prompt_with_arguments", "A prompt with arguments.")
      {
        Arguments = [new PromptArgument { Name = "arg1", Description = "First argument", Required = true }, new PromptArgument { Name = "arg2", Description = "Second argument", Required = true }],
      },
      (arguments, _) => ValueTask.FromResult(new GetPromptResult
      {
        Messages = [new PromptMessage { Role = "user", Content = TextContent.Of($"Prompt with arguments: arg1='{arguments.GetValueOrDefault("arg1")}', arg2='{arguments.GetValueOrDefault("arg2")}'") }],
      }));

    server.Prompts.Add(
      new PromptDefinition("test_prompt_with_embedded_resource", "A prompt that embeds a resource.") { Arguments = [new PromptArgument { Name = "resourceUri", Required = true }] },
      (arguments, _) => ValueTask.FromResult(new GetPromptResult
      {
        Messages =
        [
          new PromptMessage { Role = "user", Content = new EmbeddedResource { Resource = new TextResourceContents { Uri = arguments.GetValueOrDefault("resourceUri") ?? "test://example-resource", MimeType = "text/plain", Text = "Embedded resource content for testing." } } },
          new PromptMessage { Role = "user", Content = TextContent.Of("Please process the embedded resource above.") },
        ],
      }));

    server.Prompts.Add(
      new PromptDefinition("test_prompt_with_image", "A prompt that includes an image."),
      (_, _) => ValueTask.FromResult(new GetPromptResult
      {
        Messages =
        [
          new PromptMessage { Role = "user", Content = new ImageContent { Data = Png, MimeType = "image/png" } },
          new PromptMessage { Role = "user", Content = TextContent.Of("Please analyze the image above.") },
        ],
      }));
  }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ConformanceJson : System.Text.Json.Serialization.JsonSerializerContext;
