using System.Text.Json;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.Extensions.Skills;
using RecursiveAI.OneModelContext.Extensions.Tasks;
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
    AddModernDiagnostics(server);
    AddInputRequired(server);
    AddTasks(server);
    AddSkills(server);
    // Suggests values that start with what was typed, from a fixed list.
    server.Completions = (request, _) =>
    {
      string[] known = ["paris", "park", "party", "python", "pytorch"];
      var matches = known.Where(k => k.StartsWith(request.Argument.Value, StringComparison.OrdinalIgnoreCase)).ToList();
      return ValueTask.FromResult(new CompleteResult { Completion = new CompletionValues { Values = matches, Total = matches.Count, HasMore = false } });
    };
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

    // The subscription scenarios call these to make the server's lists change while a listen stream is open. Adding or removing a registry
    // entry is the change; the server tells every stream that asked for it.
    server.Tools.Add(
      new ToolDefinition("test_trigger_tool_change", "Adds or removes a tool, so the tool list changes.", NoArguments),
      (_, _) =>
      {
        if (!server.Tools.Remove("test_dynamic_tool"))
        {
          server.Tools.Add(new ToolDefinition("test_dynamic_tool", "Appears and disappears.", NoArguments), (_, _) => Text("dynamic"));
        }

        return Text("Tool list changed");
      });

    server.Tools.Add(
      new ToolDefinition("test_trigger_prompt_change", "Adds or removes a prompt, so the prompt list changes.", NoArguments),
      (_, _) =>
      {
        if (!server.Prompts.Remove("test_dynamic_prompt"))
        {
          server.Prompts.Add(
            new PromptDefinition("test_dynamic_prompt", "Appears and disappears."),
            (_, _) => ValueTask.FromResult(new GetPromptResult { Messages = [new PromptMessage { Role = "user", Content = TextContent.Of("dynamic") }] }));
        }

        return Text("Prompt list changed");
      });

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
      new ToolDefinition("test_sampling", "Asks the client's model for a completion.", Parse("""{"type":"object","properties":{"prompt":{"type":"string","description":"The prompt to send."}},"required":["prompt"]}""")),
      async (arguments, ctx) =>
      {
        var prompt = arguments.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
        var answer = await ctx.SampleAsync(new CreateMessageRequestParams
        {
          Messages = [new SamplingMessage { Role = "user", Content = [TextContent.Of(prompt)] }],
          MaxTokens = 100,
        });
        var text = answer.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? "";
        return CallToolResult.FromText("LLM response: " + text);
      });

    AddElicitation(server, "test_elicitation", "Asks the user for a name and an email address.", """{"type":"object","properties":{"username":{"type":"string","description":"User's response"},"email":{"type":"string","description":"User's email address"}},"required":["username","email"]}""");

    // Defaults for every primitive type (SEP-1034).
    AddElicitation(server, "test_elicitation_sep1034_defaults", "Asks for values that all have defaults.", """{"type":"object","properties":{"name":{"type":"string","default":"John Doe"},"age":{"type":"integer","default":30},"score":{"type":"number","default":95.5},"status":{"type":"string","enum":["active","inactive","pending"],"default":"active"},"verified":{"type":"boolean","default":true}}}""");

    // Every enum form (SEP-1330): untitled, titled, legacy enumNames, and the multi-select forms.
    AddElicitation(server, "test_elicitation_sep1330_enums", "Asks for values using every enum form.", """{"type":"object","properties":{"untitledSingle":{"type":"string","enum":["option1","option2","option3"]},"titledSingle":{"type":"string","oneOf":[{"const":"value1","title":"First Option"},{"const":"value2","title":"Second Option"},{"const":"value3","title":"Third Option"}]},"legacyEnum":{"type":"string","enum":["opt1","opt2","opt3"],"enumNames":["Option One","Option Two","Option Three"]},"untitledMulti":{"type":"array","items":{"type":"string","enum":["option1","option2","option3"]}},"titledMulti":{"type":"array","items":{"anyOf":[{"const":"value1","title":"First Choice"},{"const":"value2","title":"Second Choice"},{"const":"value3","title":"Third Choice"}]}}}}""");

    // A tool whose input schema uses 2020-12 keywords: $schema, $defs, $ref and additionalProperties.
    server.Tools.Add(
      new ToolDefinition("json_schema_2020_12_tool", "A tool whose schema uses JSON Schema 2020-12 keywords.", Parse("""{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","$defs":{"address":{"$anchor":"address","type":"object","properties":{"street":{"type":"string"},"city":{"type":"string"}}}},"properties":{"name":{"type":"string"},"address":{"$ref":"#/$defs/address"},"kind":{"type":"string"},"company":{"type":"string"},"email":{"type":"string"}},"allOf":[{"required":["name"]},{"anyOf":[{"required":["address"]},{"required":["email"]}]}],"if":{"properties":{"kind":{"const":"business"}},"required":["kind"]},"then":{"required":["company"]},"else":{},"additionalProperties":false}""")),
      (_, _) => Text("JSON Schema 2020-12 tool executed"));

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

  // Diagnostic tools the stateless-server scenarios look for by name.
  private static void AddModernDiagnostics(ContextServer server)
  {
    // Needs sampling: with no declared sampling capability the request is refused with MissingRequiredClientCapability (-32021).
    server.Tools.Add(
      new ToolDefinition("test_missing_capability", "Needs a capability the client may not have declared.", NoArguments),
      async (_, ctx) =>
      {
        var answer = await ctx.SampleAsync(new CreateMessageRequestParams { Messages = [new SamplingMessage { Role = "user", Content = [TextContent.Of("hello")] }], MaxTokens = 10 });
        return CallToolResult.FromText("sampled with " + answer.Model);
      });

    // Asks for input; on 2026-07-28 the question comes back in the result stream, never as an independent request.
    server.Tools.Add(
      new ToolDefinition("test_streaming_elicitation", "Asks the user a question.", NoArguments),
      async (_, ctx) =>
      {
        var answer = await ctx.ElicitAsync("Which colour?", Parse("""{"type":"object","properties":{"colour":{"type":"string"}}}"""));
        return CallToolResult.FromText("action=" + answer.Action);
      });

    server.Tools.Add(
      new ToolDefinition("test_logging_tool", "Logs when the request asked for logs.", NoArguments),
      async (_, ctx) =>
      {
        await ctx.LogAsync("info", "A log line");
        return CallToolResult.FromText("logged");
      });

    // x-mcp-header mirrors an argument into a header the server checks against the body (SEP-2243).
    server.Tools.Add(
      new ToolDefinition("test_custom_headers", "Arguments mirrored into headers.", Parse("""{"type":"object","properties":{"region":{"type":"string","x-mcp-header":"Region"},"priority":{"type":"integer","x-mcp-header":"Priority"},"urgent":{"type":"boolean","x-mcp-header":"Urgent"},"note":{"type":"string"}}}""")),
      (_, _) => Text("custom headers accepted"));
  }

  private static readonly JsonElement NameForm = Parse("""{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""");

  // Multi round trip requests (basic/patterns/mrtr): each ask is answered from inputResponses on the retry or ends the attempt.
  private static void AddInputRequired(ContextServer server)
  {
    server.Tools.Add(
      new ToolDefinition("test_input_required_result_elicitation", "Asks the user for their name.", NoArguments),
      async (_, ctx) =>
      {
        var answer = await ctx.ElicitAsync("What is your name?", NameForm, "user_name");
        var name = answer.Content is { } values && values.TryGetValue("name", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "";
        return CallToolResult.FromText("Hello, " + name + "!");
      });

    // The signed requestState of the first round comes back changed in the second; the framework refuses it before this handler runs.
    server.Tools.Add(
      new ToolDefinition("test_input_required_result_tampered_state", "Asks for input with signed state.", NoArguments),
      async (_, ctx) =>
      {
        ctx.State["step"] = "awaiting-name";
        var answer = await ctx.ElicitAsync("What is your name?", NameForm);
        return CallToolResult.FromText("action=" + answer.Action);
      });

    server.Tools.Add(
      new ToolDefinition("test_input_required_result_sampling", "Asks the client's model.", NoArguments),
      async (_, ctx) =>
      {
        var answer = await ctx.SampleAsync(new CreateMessageRequestParams { Messages = [new SamplingMessage { Role = "user", Content = [TextContent.Of("Say hello")] }], MaxTokens = 50 });
        return CallToolResult.FromText("Sampled by " + answer.Model);
      });

    server.Tools.Add(
      new ToolDefinition("test_input_required_result_list_roots", "Asks the client for its roots.", NoArguments),
      async (_, ctx) =>
      {
        var roots = await ctx.ListRootsAsync();
        return CallToolResult.FromText("Roots: " + roots.Roots.Count);
      });

    server.Tools.Add(
      new ToolDefinition("test_input_required_result_request_state", "Asks for input and keeps state between attempts.", NoArguments),
      async (_, ctx) =>
      {
        ctx.State["step"] = "awaiting-name";
        var answer = await ctx.ElicitAsync("What is your name?", NameForm);
        return CallToolResult.FromText("State was " + ctx.State["step"] + ", action=" + answer.Action);
      });

    server.Tools.Add(
      new ToolDefinition("test_input_required_result_multiple_inputs", "Asks for several kinds of input at once.", NoArguments),
      async (_, ctx) =>
      {
        var elicit = ctx.ElicitAsync("What is your name?", NameForm).AsTask();
        var sample = ctx.SampleAsync(new CreateMessageRequestParams { Messages = [new SamplingMessage { Role = "user", Content = [TextContent.Of("Say hello")] }], MaxTokens = 50 }).AsTask();
        var roots = ctx.ListRootsAsync().AsTask();
        await Task.WhenAll(elicit, sample, roots);
        return CallToolResult.FromText("All three answered");
      });

    server.Tools.Add(
      new ToolDefinition("test_input_required_result_multi_round", "Asks twice, one round each.", NoArguments),
      async (_, ctx) =>
      {
        var first = await ctx.ElicitAsync("What is your name?", NameForm);
        var second = await ctx.ElicitAsync("What is your favourite colour?", NameForm);
        return CallToolResult.FromText("Rounds: " + first.Action + ", " + second.Action);
      });

    // Asks only for what the client declared.
    server.Tools.Add(
      new ToolDefinition("test_input_required_result_capabilities", "Asks only for declared capabilities.", NoArguments),
      async (_, ctx) =>
      {
        var asks = new List<Task>();
        if (ctx.ClientCapabilities.Elicitation is not null)
        {
          asks.Add(ctx.ElicitAsync("What is your name?", NameForm).AsTask());
        }

        if (ctx.ClientCapabilities.Sampling is not null)
        {
          asks.Add(ctx.SampleAsync(new CreateMessageRequestParams { Messages = [new SamplingMessage { Role = "user", Content = [TextContent.Of("Say hello")] }], MaxTokens = 50 }).AsTask());
        }

        if (ctx.ClientCapabilities.Roots is not null)
        {
          asks.Add(ctx.ListRootsAsync().AsTask());
        }

        await Task.WhenAll(asks);
        return CallToolResult.FromText("Asked " + asks.Count);
      });

    server.Prompts.Add(
      new PromptDefinition("test_input_required_result_prompt", "A prompt that needs input first."),
      async (_, ctx) =>
      {
        var answer = await ctx.ElicitAsync("What is your name?", NameForm);
        return new GetPromptResult { Messages = [new PromptMessage { Role = "user", Content = TextContent.Of("Hello, action=" + answer.Action) }] };
      });
  }

  // Two skills, one under an organizational prefix, with supporting files in nested folders, for the Skills extension scenarios.
  private static void AddSkills(ContextServer server)
  {
    static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text);
    server.UseSkills();
    server.AddSkill(SkillDefinition.FromFiles(
      "pdf-processing",
      [
        new("SKILL.md", Utf8("---\nname: pdf-processing\ndescription: Extract, fill, and assemble PDF documents\n---\n\n# PDF processing\n\nChoose the matching template from `templates/`.\n")),
        new("templates/invoice.md", Utf8("# Invoice\n\nCustomer:\nAmount:\n")),
        new("templates/purchase-order.md", Utf8("# Purchase order\n\nSupplier:\nItems:\n")),
        new("templates/regional/eu.md", Utf8("# EU\n")),
      ]));
    server.AddSkill(SkillDefinition.FromFiles(
      "acme/billing/refunds",
      [
        new("SKILL.md", Utf8("---\nname: refunds\ndescription: Process customer refund requests per company policy\nlicense: Apache-2.0\n---\n\nSee examples/email.md.\n")),
        new("examples/email.md", Utf8("Dear customer,\n")),
      ]));
  }

  // The Tasks extension scenarios name their fixtures: a sync tool, task tools that sleep, fail, ask for input, and one that gathers input
  // before it becomes a task.
  private static void AddTasks(ContextServer server)
  {
    server.UseTasks();
    var secondsSchema = Parse("""{"type":"object","properties":{"seconds":{"type":"number"}}}""");

    server.Tools.Add(
      new ToolDefinition("greet", "Greets by name.", Parse("""{"type":"object","properties":{"name":{"type":"string"}}}""")),
      (arguments, _) => Text("Hello, " + (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : "world") + "!"));

    server.AddTaskTool(new ToolDefinition("slow_compute", "Sleeps, then answers.", secondsSchema), async (arguments, task) =>
    {
      var seconds = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("seconds", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 1;
      await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 120)), task.CancellationToken);
      return CallToolResult.FromText("Computed after " + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " seconds.");
    });

    // A tool that ran and reported an error: the task is completed with isError, not failed.
    server.AddTaskTool(new ToolDefinition("failing_job", "Always reports a tool error.", NoArguments), async (_, task) =>
    {
      await Task.Delay(TimeSpan.FromSeconds(1), task.CancellationToken);
      return CallToolResult.FromError("The job failed.");
    }, new TaskToolOptions { RequireTasks = true });

    server.AddTaskTool(new ToolDefinition("protocol_error_job", "Fails with a protocol error.", NoArguments), (_, _) =>
      throw new ProtocolException(-32603, "The job crashed."));

    server.AddTaskTool(new ToolDefinition("confirm_delete", "Asks before deleting.", NoArguments), async (_, task) =>
    {
      var answer = await task.ElicitAsync("confirm", "Delete everything?", Parse("""{"type":"object","properties":{"confirm":{"type":"boolean"}}}"""));
      return CallToolResult.FromText("Delete: " + answer.Action);
    });

    server.AddTaskTool(new ToolDefinition("multi_input", "Asks two questions at once.", NoArguments), async (_, task) =>
    {
      var first = task.ElicitAsync("first", "First?", NameForm).AsTask();
      var second = task.ElicitAsync("second", "Second?", NameForm).AsTask();
      var answers = await Task.WhenAll(first, second);
      return CallToolResult.FromText("Answered: " + answers[0].Action + ", " + answers[1].Action);
    });

    // Input is gathered with the ordinary retry exchange; the task is created on the final round and its result reflects the answer.
    server.AddTaskTool(
      new ToolDefinition("test_tool_with_task", "Gathers a name, then works in the background.", NoArguments),
      (arguments, _) =>
      {
        var name = arguments.TryGetProperty("user_name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : "";
        return ValueTask.FromResult(CallToolResult.FromText("Hello, " + name + "!"));
      },
      new TaskToolOptions
      {
        RequireTasks = true,
        Prepare = async (arguments, ctx) =>
        {
          var answer = await ctx.ElicitAsync("What is your name?", NameForm, "user_name");
          var name = answer.Content is { } values && values.TryGetValue("name", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "";
          return Parse(JsonSerializer.Serialize(new Dictionary<string, string> { ["user_name"] = name ?? "" }, ConformanceJson.Default.DictionaryStringString));
        },
      });
  }

  private static void AddElicitation(ContextServer server, string name, string description, string schema)
  {
    var requested = Parse(schema);
    server.Tools.Add(
      new ToolDefinition(name, description, Parse("""{"type":"object","properties":{"message":{"type":"string","description":"What to ask."}}}""")),
      async (arguments, ctx) =>
      {
        var message = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "Please provide values";
        var answer = await ctx.ElicitAsync(message, requested);
        var content = answer.Content is null ? "{}" : JsonSerializer.Serialize(answer.Content, ConformanceJson.Default.DictionaryStringJsonElement);
        return CallToolResult.FromText("User response: action=" + answer.Action + ", content=" + content);
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
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, JsonElement>))]
internal sealed partial class ConformanceJson : System.Text.Json.Serialization.JsonSerializerContext;
