using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecursiveAI.OneModelContext.Protocol;

/// <summary>
/// The source-generated serialization context for every protocol record. Use its type infos for all message encoding and decoding;
/// nothing in the library serializes through reflection.
/// </summary>
/// <remarks>
/// Properties that are null are not written. Members a record does not model are kept (<see cref="WireObject.AdditionalProperties"/>) and
/// written back. A <see cref="JsonElement"/> property that was never set is not written; a JSON <c>null</c> read into one is kept.
/// </remarks>
[JsonSourceGenerationOptions(
  DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  Converters = [typeof(ContentBlockConverter), typeof(ResourceContentsConverter)],
  GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(MetaObject))]
[JsonSerializable(typeof(Implementation))]
[JsonSerializable(typeof(ClientCapabilities))]
[JsonSerializable(typeof(ServerCapabilities))]
[JsonSerializable(typeof(EmptyResult))]
[JsonSerializable(typeof(RequestParams))]
[JsonSerializable(typeof(PaginatedRequestParams))]
[JsonSerializable(typeof(InputResponseRequestParams))]
[JsonSerializable(typeof(InitializeRequestParams))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(DiscoverResult))]
[JsonSerializable(typeof(UnsupportedProtocolVersionData))]
[JsonSerializable(typeof(MissingRequiredClientCapabilityData))]
[JsonSerializable(typeof(InputRequiredResult))]
[JsonSerializable(typeof(CancelledNotificationParams))]
[JsonSerializable(typeof(ProgressNotificationParams))]
[JsonSerializable(typeof(LoggingMessageNotificationParams))]
[JsonSerializable(typeof(SetLevelRequestParams))]
[JsonSerializable(typeof(ResourceSubscriptionParams))]
[JsonSerializable(typeof(ResourceUpdatedNotificationParams))]
[JsonSerializable(typeof(SubscriptionsListenRequestParams))]
[JsonSerializable(typeof(SubscriptionsAcknowledgedNotificationParams))]
[JsonSerializable(typeof(SubscriptionsListenResult))]
[JsonSerializable(typeof(ElicitationCompleteNotificationParams))]
[JsonSerializable(typeof(ListResourcesResult))]
[JsonSerializable(typeof(ListResourceTemplatesResult))]
[JsonSerializable(typeof(ReadResourceRequestParams))]
[JsonSerializable(typeof(ReadResourceResult))]
[JsonSerializable(typeof(ListPromptsResult))]
[JsonSerializable(typeof(GetPromptRequestParams))]
[JsonSerializable(typeof(GetPromptResult))]
[JsonSerializable(typeof(ListToolsResult))]
[JsonSerializable(typeof(CallToolRequestParams))]
[JsonSerializable(typeof(CallToolResult))]
[JsonSerializable(typeof(CompleteRequestParams))]
[JsonSerializable(typeof(CompleteResult))]
[JsonSerializable(typeof(CreateMessageRequestParams))]
[JsonSerializable(typeof(CreateMessageResult))]
[JsonSerializable(typeof(ListRootsRequestParams))]
[JsonSerializable(typeof(ListRootsResult))]
[JsonSerializable(typeof(ElicitRequestParams))]
[JsonSerializable(typeof(ElicitResult))]
[JsonSerializable(typeof(Tool))]
[JsonSerializable(typeof(Resource))]
[JsonSerializable(typeof(ResourceTemplate))]
[JsonSerializable(typeof(Prompt))]
[JsonSerializable(typeof(PromptMessage))]
[JsonSerializable(typeof(SamplingMessage))]
[JsonSerializable(typeof(SubscriptionFilter))]
[JsonSerializable(typeof(InputRequest))]
[JsonSerializable(typeof(Annotations))]
[JsonSerializable(typeof(ContentBlock))]
[JsonSerializable(typeof(TextContent))]
[JsonSerializable(typeof(ImageContent))]
[JsonSerializable(typeof(AudioContent))]
[JsonSerializable(typeof(ResourceLink))]
[JsonSerializable(typeof(EmbeddedResource))]
[JsonSerializable(typeof(ToolUseContent))]
[JsonSerializable(typeof(ToolResultContent))]
[JsonSerializable(typeof(ResourceContents))]
[JsonSerializable(typeof(TextResourceContents))]
[JsonSerializable(typeof(BlobResourceContents))]
public sealed partial class WireJsonContext : JsonSerializerContext;
