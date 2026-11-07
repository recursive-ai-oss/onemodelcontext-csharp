using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Extensions.AI;

/// <summary>A server tool seen as an <see cref="AIFunction"/>: name, description and input schema from the server, calls through the client.</summary>
internal sealed class ContextToolFunction(ContextClient client, Tool tool) : AIFunction
{
  public override string Name => tool.Name;

  public override string Description => tool.Description ?? tool.Title ?? string.Empty;

  public override JsonElement JsonSchema => tool.InputSchema;

  public override JsonElement? ReturnJsonSchema => tool.OutputSchema.ValueKind == JsonValueKind.Object ? tool.OutputSchema : null;

  public override JsonSerializerOptions JsonSerializerOptions => AIJsonUtilities.DefaultOptions;

  protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
  {
    var result = await client.CallToolAsync(tool.Name, ArgumentsToJson(arguments, JsonSerializerOptions), cancellationToken).ConfigureAwait(false);
    return ContextAIContent.ToFunctionResult(result);
  }

  // Values from a model arrive as JsonElement; values from code are serialized with the options' type metadata (source-generated, so AOT-safe).
  private static JsonElement ArgumentsToJson(AIFunctionArguments arguments, JsonSerializerOptions options)
  {
    var buffer = new ArrayBufferWriter<byte>();
    using (var writer = new Utf8JsonWriter(buffer))
    {
      writer.WriteStartObject();
      foreach (var (key, value) in arguments)
      {
        writer.WritePropertyName(key);
        switch (value)
        {
          case null:
            writer.WriteNullValue();
            break;
          case JsonElement element:
            element.WriteTo(writer);
            break;
          case JsonNode node:
            node.WriteTo(writer);
            break;
          case string text:
            writer.WriteStringValue(text);
            break;
          default:
            JsonSerializer.Serialize(writer, value, options.GetTypeInfo(value.GetType()));
            break;
        }
      }

      writer.WriteEndObject();
    }

    using var document = JsonDocument.Parse(buffer.WrittenMemory);
    return document.RootElement.Clone();
  }
}

/// <summary>Turns a client's tools into <see cref="AIFunction"/>s that any <see cref="IChatClient"/> function-calling loop can invoke.</summary>
public static class ContextClientAIExtensions
{
  /// <summary>
  /// Lists the server's tools (every page, cached per the server's <c>ttlMs</c>) as functions.
  /// Each function keeps the tool's name, description and JSON Schema, and calls the server when invoked.
  /// </summary>
  /// <param name="client">A connected client, on either protocol era.</param>
  /// <param name="cancellationToken">Cancels the listing.</param>
  /// <returns>One function per tool, in the server's order.</returns>
  /// <remarks>
  /// An invocation returns, in this order: the tool's structured content (a <see cref="JsonElement"/>) when it has any; the text
  /// (a <c>string</c>) when every content block is text; otherwise a list of <see cref="AIContent"/> (text, <see cref="DataContent"/> for
  /// images, audio and blobs, <see cref="UriContent"/> for resource links). A tool failure (<c>isError</c>) returns its text prefixed
  /// with "Error: " so the model can react, rather than throwing; a protocol failure throws.
  /// </remarks>
  /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
  public static async ValueTask<IList<AIFunction>> ToAIFunctionsAsync(this ContextClient client, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(client);
    var tools = await client.ListToolsAsync(cancellationToken).ConfigureAwait(false);
    var functions = new List<AIFunction>(tools.Count);
    foreach (var tool in tools)
    {
      functions.Add(new ContextToolFunction(client, tool));
    }

    return functions;
  }

  /// <summary>Wraps one tool, as listed by <see cref="ContextClient.ListToolsAsync"/>, as a function.</summary>
  /// <param name="client">A connected client.</param>
  /// <param name="tool">The tool.</param>
  /// <returns>The function.</returns>
  /// <exception cref="ArgumentNullException">An argument is null.</exception>
  public static AIFunction ToAIFunction(this ContextClient client, Tool tool)
  {
    ArgumentNullException.ThrowIfNull(client);
    ArgumentNullException.ThrowIfNull(tool);
    return new ContextToolFunction(client, tool);
  }

  /// <summary>Maps a tool result's content blocks to <see cref="AIContent"/>.</summary>
  /// <param name="result">The result.</param>
  /// <returns>Text, data (images, audio, blobs) and URI content, in order.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
  public static IList<AIContent> ToAIContents(this CallToolResult result)
  {
    ArgumentNullException.ThrowIfNull(result);
    return ContextAIContent.Map(result.Content);
  }
}
