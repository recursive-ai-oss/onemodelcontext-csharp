using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using RecursiveAI.OneModelContext.JsonRpc;

namespace RecursiveAI.OneModelContext.Protocol;

// Parameters and results, named for the schema.ts types of 2026-07-28, plus the members and messages of 2024-11-05 to 2025-11-25.
// A member that exists in one era only says so; the codec decides what to write and what to accept.

/// <summary>Parameters of <c>initialize</c> (legacy).</summary>
public sealed class InitializeRequestParams : RequestParams
{
  /// <summary>The newest revision the client speaks.</summary>
  [JsonPropertyName("protocolVersion")]
  public required string ProtocolVersion { get; init; }

  /// <summary>What the client supports.</summary>
  [JsonPropertyName("capabilities")]
  public required ClientCapabilities Capabilities { get; init; }

  /// <summary>Who the client is.</summary>
  [JsonPropertyName("clientInfo")]
  public required Implementation ClientInfo { get; init; }
}

/// <summary>Result of <c>initialize</c> (legacy).</summary>
public sealed class InitializeResult : Result
{
  /// <summary>The revision the server chose.</summary>
  [JsonPropertyName("protocolVersion")]
  public required string ProtocolVersion { get; init; }

  /// <summary>What the server supports.</summary>
  [JsonPropertyName("capabilities")]
  public required ServerCapabilities Capabilities { get; init; }

  /// <summary>Who the server is.</summary>
  [JsonPropertyName("serverInfo")]
  public required Implementation ServerInfo { get; init; }

  /// <summary>Hints for the model on using the server.</summary>
  [JsonPropertyName("instructions")]
  public string? Instructions { get; init; }
}

/// <summary>Result of <c>server/discover</c> (2026-07-28).</summary>
public sealed class DiscoverResult : CacheableResult
{
  /// <summary>The revisions the server speaks.</summary>
  [JsonPropertyName("supportedVersions")]
  public required List<string> SupportedVersions { get; init; }

  /// <summary>What the server supports.</summary>
  [JsonPropertyName("capabilities")]
  public required ServerCapabilities Capabilities { get; init; }

  /// <summary>Hints for the model on using the server.</summary>
  [JsonPropertyName("instructions")]
  public string? Instructions { get; init; }
}

/// <summary>The <c>data</c> of error -32022 (2026-07-28).</summary>
public sealed class UnsupportedProtocolVersionData : WireObject
{
  /// <summary>The revisions the server speaks.</summary>
  [JsonPropertyName("supported")]
  public required List<string> Supported { get; init; }

  /// <summary>The revision the client asked for.</summary>
  [JsonPropertyName("requested")]
  public string? Requested { get; init; }
}

/// <summary>The <c>data</c> of error -32021 (2026-07-28).</summary>
public sealed class MissingRequiredClientCapabilityData : WireObject
{
  /// <summary>The capabilities the request needs.</summary>
  [JsonPropertyName("requiredCapabilities")]
  public required ClientCapabilities RequiredCapabilities { get; init; }
}

/// <summary>Result asking the client to supply input and call again (2026-07-28 <c>InputRequiredResult</c>).</summary>
public sealed class InputRequiredResult : Result
{
  /// <summary>The requests to answer, by key.</summary>
  [JsonPropertyName("inputRequests")]
  public Dictionary<string, InputRequest>? InputRequests { get; init; }

  /// <summary>Opaque state to send back with the retry. Protected by the server; clients must not read or change it.</summary>
  [JsonPropertyName("requestState")]
  public string? RequestState { get; init; }
}

/// <summary>Parameters of <c>notifications/cancelled</c>.</summary>
public sealed class CancelledNotificationParams : RequestParams
{
  /// <summary>The request to cancel (optional in 2025-11-25 for task cancellation).</summary>
  [JsonPropertyName("requestId")]
  public JsonRpcId? RequestId { get; init; }

  /// <summary>Why.</summary>
  [JsonPropertyName("reason")]
  public string? Reason { get; init; }
}

/// <summary>Parameters of <c>notifications/progress</c>.</summary>
public sealed class ProgressNotificationParams : RequestParams
{
  /// <summary>The token from the request's <c>_meta.progressToken</c>.</summary>
  [JsonPropertyName("progressToken")]
  public required JsonRpcId ProgressToken { get; init; }

  /// <summary>Work done so far; increases with each notification.</summary>
  [JsonPropertyName("progress")]
  public required double Progress { get; init; }

  /// <summary>Total work, if known.</summary>
  [JsonPropertyName("total")]
  public double? Total { get; init; }

  /// <summary>A human-readable status (2025-03-26 and later).</summary>
  [JsonPropertyName("message")]
  public string? Message { get; init; }
}

/// <summary>Parameters of <c>notifications/message</c>.</summary>
public sealed class LoggingMessageNotificationParams : RequestParams
{
  /// <summary>One of <see cref="LoggingLevels"/>.</summary>
  [JsonPropertyName("level")]
  public required string Level { get; init; }

  /// <summary>The logger name.</summary>
  [JsonPropertyName("logger")]
  public string? Logger { get; init; }

  /// <summary>Any JSON value.</summary>
  [JsonPropertyName("data")]
  public required JsonElement Data { get; init; }
}

/// <summary>Parameters of <c>logging/setLevel</c> (legacy).</summary>
public sealed class SetLevelRequestParams : RequestParams
{
  /// <summary>The minimum level to deliver.</summary>
  [JsonPropertyName("level")]
  public required string Level { get; init; }
}

/// <summary>Parameters of <c>resources/subscribe</c> and <c>resources/unsubscribe</c> (legacy).</summary>
public sealed class ResourceSubscriptionParams : RequestParams
{
  /// <summary>The resource URI.</summary>
  [JsonPropertyName("uri")]
  public required string Uri { get; init; }
}

/// <summary>Parameters of <c>notifications/resources/updated</c>.</summary>
public sealed class ResourceUpdatedNotificationParams : RequestParams
{
  /// <summary>The resource that changed.</summary>
  [JsonPropertyName("uri")]
  public required string Uri { get; init; }
}

/// <summary>Parameters of <c>subscriptions/listen</c> (2026-07-28).</summary>
public sealed class SubscriptionsListenRequestParams : RequestParams
{
  /// <summary>The notifications wanted.</summary>
  [JsonPropertyName("notifications")]
  public required SubscriptionFilter Notifications { get; init; }
}

/// <summary>Parameters of <c>notifications/subscriptions/acknowledged</c> (2026-07-28).</summary>
public sealed class SubscriptionsAcknowledgedNotificationParams : RequestParams
{
  /// <summary>The filter the server accepted.</summary>
  [JsonPropertyName("notifications")]
  public required SubscriptionFilter Notifications { get; init; }
}

/// <summary>Result of <c>subscriptions/listen</c> (2026-07-28), sent when the stream ends.</summary>
public sealed class SubscriptionsListenResult : Result;

/// <summary>Parameters of <c>notifications/elicitation/complete</c> (2025-11-25).</summary>
public sealed class ElicitationCompleteNotificationParams : RequestParams
{
  /// <summary>The id from the URL-mode elicitation request.</summary>
  [JsonPropertyName("elicitationId")]
  public required string ElicitationId { get; init; }
}

/// <summary>Result of <c>resources/list</c>.</summary>
public sealed class ListResourcesResult : PaginatedResult
{
  /// <summary>The resources.</summary>
  [JsonPropertyName("resources")]
  public required List<Resource> Resources { get; init; }
}

/// <summary>Result of <c>resources/templates/list</c>.</summary>
public sealed class ListResourceTemplatesResult : PaginatedResult
{
  /// <summary>The templates.</summary>
  [JsonPropertyName("resourceTemplates")]
  public required List<ResourceTemplate> ResourceTemplates { get; init; }
}

/// <summary>Parameters of <c>resources/read</c>.</summary>
public sealed class ReadResourceRequestParams : InputResponseRequestParams
{
  /// <summary>The resource URI.</summary>
  [JsonPropertyName("uri")]
  public required string Uri { get; init; }
}

/// <summary>Result of <c>resources/read</c>.</summary>
public sealed class ReadResourceResult : CacheableResult
{
  /// <summary>The contents, text or blob.</summary>
  [JsonPropertyName("contents")]
  public required List<ResourceContents> Contents { get; init; }
}

/// <summary>Result of <c>prompts/list</c>.</summary>
public sealed class ListPromptsResult : PaginatedResult
{
  /// <summary>The prompts.</summary>
  [JsonPropertyName("prompts")]
  public required List<Prompt> Prompts { get; init; }
}

/// <summary>Parameters of <c>prompts/get</c>.</summary>
public sealed class GetPromptRequestParams : InputResponseRequestParams
{
  /// <summary>The prompt name.</summary>
  [JsonPropertyName("name")]
  public required string Name { get; init; }

  /// <summary>Argument values, all strings.</summary>
  [JsonPropertyName("arguments")]
  public Dictionary<string, string>? Arguments { get; init; }
}

/// <summary>Result of <c>prompts/get</c>.</summary>
public sealed class GetPromptResult : Result
{
  /// <summary>What the prompt is.</summary>
  [JsonPropertyName("description")]
  public string? Description { get; init; }

  /// <summary>The messages.</summary>
  [JsonPropertyName("messages")]
  public required List<PromptMessage> Messages { get; init; }
}

/// <summary>Result of <c>tools/list</c>.</summary>
public sealed class ListToolsResult : PaginatedResult
{
  /// <summary>The tools, in a stable order.</summary>
  [JsonPropertyName("tools")]
  public required List<Tool> Tools { get; init; }
}

/// <summary>Parameters of <c>tools/call</c>.</summary>
public sealed class CallToolRequestParams : InputResponseRequestParams
{
  /// <summary>The tool name.</summary>
  [JsonPropertyName("name")]
  public required string Name { get; init; }

  /// <summary>The arguments object; <c>default</c> when the call has none.</summary>
  [JsonPropertyName("arguments")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public JsonElement Arguments { get; init; }
}

/// <summary>Result of <c>tools/call</c>. A tool failure is a result with <see cref="IsError"/> set, not a protocol error.</summary>
public sealed class CallToolResult : Result
{
  /// <summary>The content.</summary>
  [JsonPropertyName("content")]
  public required List<ContentBlock> Content { get; init; }

  /// <summary>Structured data matching the tool's <c>outputSchema</c> (2025-06-18 and later).</summary>
  [JsonPropertyName("structuredContent")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public JsonElement StructuredContent { get; init; }

  /// <summary>True when the tool failed; the content then says why.</summary>
  [JsonPropertyName("isError")]
  public bool? IsError { get; init; }

  /// <summary>The text blocks joined with newlines; empty when there are none.</summary>
  [JsonIgnore]
  public string Text
  {
    get
    {
      var text = new System.Text.StringBuilder();
      foreach (var block in Content)
      {
        if (block is TextContent t)
        {
          if (text.Length > 0)
          {
            text.Append('\n');
          }

          text.Append(t.Text);
        }
      }

      return text.ToString();
    }
  }

  /// <summary>The structured content, or <c>default</c> when absent.</summary>
  [JsonIgnore]
  public JsonElement Structured => StructuredContent;

  /// <summary>A successful result with one text block.</summary>
  /// <param name="text">The text.</param>
  public static CallToolResult FromText(string text) => new() { Content = [TextContent.Of(text)] };

  /// <summary>A failed result with one text block.</summary>
  /// <param name="message">What went wrong.</param>
  public static CallToolResult FromError(string message) => new() { Content = [TextContent.Of(message)], IsError = true };
}

/// <summary>Parameters of <c>completion/complete</c>.</summary>
public sealed class CompleteRequestParams : RequestParams
{
  /// <summary>What is being completed.</summary>
  [JsonPropertyName("ref")]
  public required CompletionReference Ref { get; init; }

  /// <summary>The argument.</summary>
  [JsonPropertyName("argument")]
  public required CompleteArgument Argument { get; init; }

  /// <summary>Resolved arguments (2025-06-18 and later).</summary>
  [JsonPropertyName("context")]
  public CompleteContext? Context { get; init; }
}

/// <summary>Result of <c>completion/complete</c>.</summary>
public sealed class CompleteResult : Result
{
  /// <summary>The suggestions.</summary>
  [JsonPropertyName("completion")]
  public required CompletionValues Completion { get; init; }
}

/// <summary>Parameters of <c>sampling/createMessage</c> (deprecated in 2026-07-28).</summary>
public sealed class CreateMessageRequestParams : RequestParams
{
  /// <summary>The conversation.</summary>
  [JsonPropertyName("messages")]
  public required List<SamplingMessage> Messages { get; init; }

  /// <summary>Model preferences.</summary>
  [JsonPropertyName("modelPreferences")]
  public ModelPreferences? ModelPreferences { get; init; }

  /// <summary>A system prompt.</summary>
  [JsonPropertyName("systemPrompt")]
  public string? SystemPrompt { get; init; }

  /// <summary><c>none</c>, <c>thisServer</c> or <c>allServers</c> (soft-deprecated from 2025-11-25).</summary>
  [JsonPropertyName("includeContext")]
  public string? IncludeContext { get; init; }

  /// <summary>Sampling temperature.</summary>
  [JsonPropertyName("temperature")]
  public double? Temperature { get; init; }

  /// <summary>Most tokens to generate.</summary>
  [JsonPropertyName("maxTokens")]
  public required int MaxTokens { get; init; }

  /// <summary>Stop sequences.</summary>
  [JsonPropertyName("stopSequences")]
  public List<string>? StopSequences { get; init; }

  /// <summary>Provider-specific metadata.</summary>
  [JsonPropertyName("metadata")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public JsonElement Metadata { get; init; }

  /// <summary>Tools the model may use (2025-11-25 and later).</summary>
  [JsonPropertyName("tools")]
  public List<Tool>? Tools { get; init; }

  /// <summary>How it may use them (2025-11-25 and later).</summary>
  [JsonPropertyName("toolChoice")]
  public ToolChoice? ToolChoice { get; init; }
}

/// <summary>Result of <c>sampling/createMessage</c>.</summary>
public sealed class CreateMessageResult : Result
{
  /// <summary><c>assistant</c>.</summary>
  [JsonPropertyName("role")]
  public required string Role { get; init; }

  /// <summary>The generated content.</summary>
  [JsonPropertyName("content")]
  [JsonConverter(typeof(ContentBlockListConverter))]
  public required List<ContentBlock> Content { get; init; }

  /// <summary>The model that answered.</summary>
  [JsonPropertyName("model")]
  public required string Model { get; init; }

  /// <summary><c>endTurn</c>, <c>stopSequence</c>, <c>maxTokens</c>, <c>toolUse</c> or another string.</summary>
  [JsonPropertyName("stopReason")]
  public string? StopReason { get; init; }
}

/// <summary>Parameters of <c>roots/list</c> (deprecated in 2026-07-28).</summary>
public sealed class ListRootsRequestParams : RequestParams;

/// <summary>Result of <c>roots/list</c>.</summary>
public sealed class ListRootsResult : Result
{
  /// <summary>The roots.</summary>
  [JsonPropertyName("roots")]
  public required List<Root> Roots { get; init; }
}

/// <summary>
/// Parameters of <c>elicitation/create</c>. One record for both modes: <see cref="Mode"/> <c>form</c> (the default when absent)
/// uses <see cref="RequestedSchema"/>; <c>url</c> uses <see cref="Url"/> and <see cref="ElicitationId"/> (2025-11-25 and later).
/// </summary>
public sealed class ElicitRequestParams : RequestParams
{
  /// <summary><c>form</c> or <c>url</c>; absent means form.</summary>
  [JsonPropertyName("mode")]
  public string? Mode { get; init; }

  /// <summary>What to ask the user.</summary>
  [JsonPropertyName("message")]
  public required string Message { get; init; }

  /// <summary>Form mode: a flat object schema of primitive properties.</summary>
  [JsonPropertyName("requestedSchema")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public JsonElement RequestedSchema { get; init; }

  /// <summary>URL mode: where the user goes.</summary>
  [JsonPropertyName("url")]
  public string? Url { get; init; }

  /// <summary>URL mode (2025-11-25): correlates <c>notifications/elicitation/complete</c>.</summary>
  [JsonPropertyName("elicitationId")]
  public string? ElicitationId { get; init; }
}

/// <summary>Result of <c>elicitation/create</c>.</summary>
public sealed class ElicitResult : Result
{
  /// <summary><c>accept</c>, <c>decline</c> or <c>cancel</c>.</summary>
  [JsonPropertyName("action")]
  public required string Action { get; init; }

  /// <summary>The submitted values when accepted (form mode).</summary>
  [JsonPropertyName("content")]
  public Dictionary<string, JsonElement>? Content { get; init; }
}
