using System.Text.Json;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.Protocol;

namespace ToolBridge;

/// <summary>
/// A tool as another library or framework defines it: a name, a description, a JSON Schema and an async function over the
/// arguments. This is the common denominator of agent frameworks, which is why it makes a good bridge point.
/// </summary>
/// <param name="Name">The tool name.</param>
/// <param name="Description">What it does, written for a model.</param>
/// <param name="InputSchema">A JSON Schema object for the arguments.</param>
/// <param name="RequiresApproval">True when a person must agree before it runs.</param>
/// <param name="InvokeAsync">Runs it and returns text. Throw to report a failure.</param>
public sealed record BridgedTool(
  string Name,
  string Description,
  string InputSchema,
  bool RequiresApproval,
  Func<JsonElement, CancellationToken, Task<string>> InvokeAsync);

/// <summary>Serves tools defined elsewhere as tools of a <see cref="ContextServer"/>.</summary>
public static class Bridge
{
  /// <summary>Registers every tool on the server. Call it again later to add more; a tool with the same name is replaced.</summary>
  /// <param name="server">The server.</param>
  /// <param name="tools">The tools to serve.</param>
  public static void Register(ContextServer server, IEnumerable<BridgedTool> tools)
  {
    foreach (var tool in tools)
    {
      // The server keeps the parsed schema for the life of the tool, so this document is deliberately never disposed.
      var schema = JsonDocument.Parse(tool.InputSchema);
      var definition = new ToolDefinition(tool.Name, tool.Description, schema)
      {
        // A tool that needs approval may do something the user cares about; say so to hosts that read annotations.
        Annotations = new ToolAnnotations { DestructiveHint = tool.RequiresApproval ? true : null },
      };
      server.Tools.Add(definition, (arguments, context) => RunAsync(tool, arguments, context));
    }
  }

  private static async ValueTask<CallToolResult> RunAsync(BridgedTool tool, JsonElement arguments, RequestContext context)
  {
    // On a 2026-07-28 connection this handler runs again from the top when the client retries with its answer, so everything
    // above ConfirmAsync must be safe to repeat; on 2025-11-25 and earlier it is one await on a request over the held connection.
    // The same line serves both (see docs/multi-round-trip.md).
    if (tool.RequiresApproval && !await context.ConfirmAsync($"Run '{tool.Name}' with {arguments.GetRawText()}?").ConfigureAwait(false))
    {
      return CallToolResult.FromError("The user declined.");
    }

    try
    {
      return CallToolResult.FromText(await tool.InvokeAsync(arguments, context.CancellationToken).ConfigureAwait(false));
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      // A failing tool is a result the model can read and react to, not a protocol error.
      return CallToolResult.FromError(ex.Message);
    }
  }
}
