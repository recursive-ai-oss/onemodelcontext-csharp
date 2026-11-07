using System.Text.Json;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.Protocol;

namespace Explorer;

/// <summary>What <see cref="Tour.RunAsync"/> learned about a server.</summary>
/// <param name="Era">The protocol era the client settled on.</param>
/// <param name="Version">The negotiated protocol revision.</param>
/// <param name="ServerName">The server's name.</param>
/// <param name="Tools">Tool names, in the order the server listed them.</param>
/// <param name="SecondListWasCached">True when listing tools twice cost one request, because the server allowed caching.</param>
/// <param name="CallResult">The text of the tool call asked for, or null.</param>
/// <param name="ResourceText">The text of the resource read, or null.</param>
/// <param name="PromptText">The text of the prompt fetched, or null.</param>
public sealed record TourReport(
  ProtocolEra Era,
  string Version,
  string ServerName,
  IReadOnlyList<string> Tools,
  bool SecondListWasCached,
  string? CallResult,
  string? ResourceText,
  string? PromptText);

/// <summary>What to do after looking around.</summary>
/// <param name="Tool">A tool to call, or null.</param>
/// <param name="ToolArguments">The tool's arguments as a JSON object.</param>
/// <param name="ResourceUri">A resource to read, or null.</param>
/// <param name="Prompt">A prompt to fetch, or null.</param>
/// <param name="PromptArguments">The prompt's arguments.</param>
public sealed record TourRequest(string? Tool = null, string ToolArguments = "{}", string? ResourceUri = null, string? Prompt = null, IReadOnlyDictionary<string, string>? PromptArguments = null);

/// <summary>A tour of any server through a connected client: what it is, what it offers, and one of each kind of request.</summary>
public static class Tour
{
  /// <summary>Looks around and does what <paramref name="request"/> asks.</summary>
  /// <param name="client">A connected client.</param>
  /// <param name="request">The calls to make.</param>
  /// <param name="output">Where to print what happens.</param>
  /// <param name="cancellationToken">Cancels the tour.</param>
  public static async Task<TourReport> RunAsync(ContextClient client, TourRequest request, TextWriter output, CancellationToken cancellationToken = default)
  {
    // The client negotiated before ConnectAsync returned: modern servers answer server/discover, older ones initialize.
    output.WriteLine($"{client.ServerInfo.Name} {client.ServerInfo.Version}, protocol {client.ProtocolVersion} ({client.Era} era)");
    if (client.Instructions is { Length: > 0 } instructions)
    {
      output.WriteLine($"instructions: {instructions}");
    }

    // A server that says how long its list stays valid (ttlMs) lets the client answer the second listing from memory; FromCache says so.
    await client.ListToolsPageAsync(cancellationToken: cancellationToken);
    var cached = (await client.ListToolsPageAsync(cancellationToken: cancellationToken)).FromCache;
    var tools = await client.ListToolsAsync(cancellationToken);
    output.WriteLine($"tools ({tools.Count}, listed twice: {(cached ? "second from cache" : "both from the server")}):");
    foreach (var tool in tools)
    {
      output.WriteLine($"  {tool.Name}: {tool.Description}");
    }

    // Ask only for what the server says it has; the others would be method-not-found.
    if (client.ServerCapabilities.Resources is not null)
    {
      foreach (var resource in await client.ListResourcesAsync(cancellationToken))
      {
        output.WriteLine($"  resource {resource.Uri} ({resource.Name})");
      }

      foreach (var template in await client.ListResourceTemplatesAsync(cancellationToken))
      {
        output.WriteLine($"  resource template {template.UriTemplate} ({template.Name})");
      }
    }

    if (client.ServerCapabilities.Prompts is not null)
    {
      foreach (var prompt in await client.ListPromptsAsync(cancellationToken))
      {
        output.WriteLine($"  prompt {prompt.Name}");
      }
    }

    string? callText = null;
    if (request.Tool is not null)
    {
      using var arguments = JsonDocument.Parse(request.ToolArguments);
      var result = await client.CallToolAsync(request.Tool, arguments.RootElement, cancellationToken);
      callText = result.Text;
      output.WriteLine($"call {request.Tool}{(result.IsError == true ? " (tool reported an error)" : "")}: {callText}");
    }

    string? resourceText = null;
    if (request.ResourceUri is not null)
    {
      var read = await client.ReadResourceAsync(request.ResourceUri, cancellationToken);
      resourceText = string.Join('\n', read.Contents.OfType<TextResourceContents>().Select(c => c.Text));
      output.WriteLine($"read {request.ResourceUri}: {resourceText}");
    }

    string? promptText = null;
    if (request.Prompt is not null)
    {
      var prompt = await client.GetPromptAsync(request.Prompt, request.PromptArguments, cancellationToken);
      promptText = string.Join('\n', prompt.Messages.Select(m => m.Content is TextContent t ? $"{m.Role}: {t.Text}" : $"{m.Role}: [{m.Content.Type}]"));
      output.WriteLine($"prompt {request.Prompt}: {promptText}");
    }

    return new TourReport(client.Era, client.ProtocolVersion, client.ServerInfo.Name, [.. tools.Select(t => t.Name)], cached, callText, resourceText, promptText);
  }
}
