using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Extensions.AI;

/// <summary>Serves <see cref="AIFunction"/>s as MCP tools.</summary>
public static class ContextServerAIExtensions
{
  /// <summary>
  /// Registers a function as a tool on the server (dynamic registration; a tool of the same name is replaced). The tool carries the
  /// function's name, description and JSON Schema (and its return schema as <c>outputSchema</c> when that is an object schema).
  /// </summary>
  /// <param name="server">The server.</param>
  /// <param name="function">The function to expose. Its arguments arrive as <see cref="JsonElement"/> values, which is what
  /// functions made by <c>AIFunctionFactory</c> expect.</param>
  /// <returns>The server, for chaining.</returns>
  /// <remarks>
  /// A return value maps to content: <c>string</c> and <see cref="AIContent"/> (text, images, audio, other data, URIs) directly; a
  /// <see cref="JsonElement"/> or any other value as its JSON text, also sent as <c>structuredContent</c> when the function declares an
  /// object return schema. An exception from the function is a tool failure the model sees as "The tool failed.", never a protocol error.
  /// </remarks>
  /// <exception cref="ArgumentNullException">An argument is null.</exception>
  /// <exception cref="ArgumentException">The function's schema is not a JSON object.</exception>
  public static ContextServer AddAIFunction(this ContextServer server, AIFunction function)
  {
    ArgumentNullException.ThrowIfNull(server);
    ArgumentNullException.ThrowIfNull(function);
    var schema = function.JsonSchema.ValueKind == JsonValueKind.Object ? function.JsonSchema : EmptyObjectSchema();
    var definition = new ToolDefinition(function.Name, function.Description, schema)
    {
      OutputSchema = function.ReturnJsonSchema is { ValueKind: JsonValueKind.Object } output && IsObjectSchema(output) ? output.Clone() : default,
    };

    server.Tools.Add(definition, async (arguments, context) =>
    {
      var values = new AIFunctionArguments();
      if (arguments.ValueKind == JsonValueKind.Object)
      {
        foreach (var property in arguments.EnumerateObject())
        {
          values[property.Name] = property.Value.Clone();
        }
      }

      var result = await function.InvokeAsync(values, context.CancellationToken).ConfigureAwait(false);
      return ContextAIContent.ToToolResult(result, function);
    });
    return server;
  }

  /// <summary>Registers every <see cref="AIFunction"/> in <paramref name="tools"/>; other tool kinds (hosted tools, declarations without an implementation) are skipped.</summary>
  /// <param name="server">The server.</param>
  /// <param name="tools">The tools, such as the list <c>AIFunctionFactory.Create</c> results are put in.</param>
  /// <returns>The server, for chaining.</returns>
  /// <exception cref="ArgumentNullException">An argument is null.</exception>
  public static ContextServer AddAIFunctions(this ContextServer server, IEnumerable<AITool> tools)
  {
    ArgumentNullException.ThrowIfNull(server);
    ArgumentNullException.ThrowIfNull(tools);
    foreach (var tool in tools)
    {
      if (tool is AIFunction function)
      {
        server.AddAIFunction(function);
      }
    }

    return server;
  }

  private static bool IsObjectSchema(JsonElement schema) =>
    schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "object";

  private static JsonElement EmptyObjectSchema()
  {
    using var document = JsonDocument.Parse("""{"type":"object","properties":{}}""");
    return document.RootElement.Clone();
  }
}
