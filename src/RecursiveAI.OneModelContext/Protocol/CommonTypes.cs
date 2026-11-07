using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using RecursiveAI.OneModelContext.JsonRpc;

namespace RecursiveAI.OneModelContext.Protocol;

/// <summary>
/// Base of every message record. Members the record does not model are kept in <see cref="AdditionalProperties"/> and written back,
/// so a newer peer's fields survive a decode and re-encode.
/// </summary>
public abstract class WireObject
{
  /// <summary>Fields the record has no property for, by name.</summary>
  [JsonExtensionData]
  public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// A <c>_meta</c> object. The reserved keys the protocol defines are typed; every other key (trace context, extension keys,
/// vendor keys) stays in <see cref="WireObject.AdditionalProperties"/>.
/// </summary>
public sealed class MetaObject : WireObject
{
  /// <summary>The progress token a request asks to be reported against (all revisions).</summary>
  [JsonPropertyName("progressToken")]
  public JsonRpcId? ProgressToken { get; init; }

  /// <summary>2026-07-28 requests: the protocol version.</summary>
  [JsonPropertyName(WireNames.MetaProtocolVersion)]
  public string? ProtocolVersion { get; init; }

  /// <summary>2026-07-28 requests: who is calling.</summary>
  [JsonPropertyName(WireNames.MetaClientInfo)]
  public Implementation? ClientInfo { get; init; }

  /// <summary>2026-07-28 requests: what the client supports.</summary>
  [JsonPropertyName(WireNames.MetaClientCapabilities)]
  public ClientCapabilities? ClientCapabilities { get; init; }

  /// <summary>2026-07-28 requests: the minimum log level the client wants delivered.</summary>
  [JsonPropertyName(WireNames.MetaLogLevel)]
  public string? LogLevel { get; init; }

  /// <summary>2026-07-28 notifications: the <c>subscriptions/listen</c> request the notification belongs to.</summary>
  [JsonPropertyName(WireNames.MetaSubscriptionId)]
  public JsonRpcId? SubscriptionId { get; init; }

  /// <summary>2026-07-28 results: who answered.</summary>
  [JsonPropertyName(WireNames.MetaServerInfo)]
  public Implementation? ServerInfo { get; init; }

  /// <summary>W3C Trace Context <c>traceparent</c> carried in <c>_meta</c>.</summary>
  [JsonPropertyName("traceparent")]
  public string? TraceParent { get; init; }

  /// <summary>W3C Trace Context <c>tracestate</c> carried in <c>_meta</c>.</summary>
  [JsonPropertyName("tracestate")]
  public string? TraceState { get; init; }

  /// <summary>W3C Baggage carried in <c>_meta</c>.</summary>
  [JsonPropertyName("baggage")]
  public string? Baggage { get; init; }
}

/// <summary>Base of request and notification parameters.</summary>
public class RequestParams : WireObject
{
  /// <summary>The <c>_meta</c> object: required with version and capabilities on 2026-07-28, optional with a progress token earlier.</summary>
  [JsonPropertyName("_meta")]
  public MetaObject? Meta { get; init; }
}

/// <summary>Base of every result.</summary>
public class Result : WireObject
{
  /// <summary>The <c>_meta</c> object; on 2026-07-28 it carries the server info.</summary>
  [JsonPropertyName("_meta")]
  public MetaObject? Meta { get; init; }

  /// <summary>
  /// 2026-07-28: <c>complete</c> or <c>input_required</c> (other strings are reserved for extensions). Absent on legacy results.
  /// Use <see cref="EffectiveResultType"/> to read it.
  /// </summary>
  [JsonPropertyName("resultType")]
  public string? ResultType { get; init; }

  /// <summary>The result type with absence read as <c>complete</c> (2026-07-28 basic/versioning: a legacy server sends no <c>resultType</c>).</summary>
  [JsonIgnore]
  public string EffectiveResultType => ResultType ?? WireNames.ResultTypeComplete;
}

/// <summary>A result that carries nothing but the common members.</summary>
public sealed class EmptyResult : Result;

/// <summary>A result a client may cache (2026-07-28 <c>CacheableResult</c>).</summary>
public class CacheableResult : Result
{
  /// <summary>How long the result stays fresh, in milliseconds. Absent on legacy results.</summary>
  [JsonPropertyName("ttlMs")]
  public long? TtlMs { get; init; }

  /// <summary>Who may cache it: <c>public</c> or <c>private</c>. Absent on legacy results.</summary>
  [JsonPropertyName("cacheScope")]
  public string? CacheScope { get; init; }
}

/// <summary>A cacheable result with a pagination cursor.</summary>
public class PaginatedResult : CacheableResult
{
  /// <summary>Opaque cursor for the next page; absent on the last page.</summary>
  [JsonPropertyName("nextCursor")]
  public string? NextCursor { get; init; }
}

/// <summary>Parameters of a paginated list request.</summary>
public class PaginatedRequestParams : RequestParams
{
  /// <summary>The cursor from the previous page's <c>nextCursor</c>.</summary>
  [JsonPropertyName("cursor")]
  public string? Cursor { get; init; }
}

/// <summary>2026-07-28 request parameters that can answer an earlier <c>input_required</c> result.</summary>
public class InputResponseRequestParams : RequestParams
{
  /// <summary>The client's answers to the server's input requests, by the key the server used.</summary>
  [JsonPropertyName("inputResponses")]
  public Dictionary<string, JsonElement>? InputResponses { get; init; }

  /// <summary>The opaque state the server sent with its <c>input_required</c> result, to be returned unchanged.</summary>
  [JsonPropertyName("requestState")]
  public string? RequestState { get; init; }
}

/// <summary>An icon a client may show for a server, tool, resource or prompt.</summary>
public sealed class Icon : WireObject
{
  /// <summary>An <c>https:</c> or <c>data:</c> URI of the image.</summary>
  [JsonPropertyName("src")]
  public required string Source { get; init; }

  /// <summary>The image media type.</summary>
  [JsonPropertyName("mimeType")]
  public string? MimeType { get; init; }

  /// <summary>Sizes like <c>48x48</c>, or <c>any</c>.</summary>
  [JsonPropertyName("sizes")]
  public List<string>? Sizes { get; init; }

  /// <summary><c>light</c> or <c>dark</c>.</summary>
  [JsonPropertyName("theme")]
  public string? Theme { get; init; }
}

/// <summary>Name and version of a client or server (<c>Implementation</c> in the schema).</summary>
public sealed class Implementation : WireObject
{
  /// <summary>The programmatic name.</summary>
  [JsonPropertyName("name")]
  public required string Name { get; init; }

  /// <summary>A display name.</summary>
  [JsonPropertyName("title")]
  public string? Title { get; init; }

  /// <summary>The version.</summary>
  [JsonPropertyName("version")]
  public required string Version { get; init; }

  /// <summary>A short description (2025-11-25 and later).</summary>
  [JsonPropertyName("description")]
  public string? Description { get; init; }

  /// <summary>A home page (2025-11-25 and later).</summary>
  [JsonPropertyName("websiteUrl")]
  public string? WebsiteUrl { get; init; }

  /// <summary>Icons (2025-11-25 and later).</summary>
  [JsonPropertyName("icons")]
  public List<Icon>? Icons { get; init; }
}

/// <summary>What a client supports.</summary>
public sealed class ClientCapabilities : WireObject
{
  /// <summary>Non-standard capabilities.</summary>
  [JsonPropertyName("experimental")]
  public Dictionary<string, JsonElement>? Experimental { get; init; }

  /// <summary>The client can list roots (deprecated in 2026-07-28).</summary>
  [JsonPropertyName("roots")]
  public RootsCapability? Roots { get; init; }

  /// <summary>The client can answer sampling requests (deprecated in 2026-07-28).</summary>
  [JsonPropertyName("sampling")]
  public SamplingCapability? Sampling { get; init; }

  /// <summary>The client can answer elicitation requests.</summary>
  [JsonPropertyName("elicitation")]
  public ElicitationCapability? Elicitation { get; init; }

  /// <summary>The 2025-11-25 experimental task capability, kept raw.</summary>
  [JsonPropertyName("tasks")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public JsonElement Tasks { get; init; }

  /// <summary>Extensions by identifier, with their settings.</summary>
  [JsonPropertyName("extensions")]
  public Dictionary<string, JsonElement>? Extensions { get; init; }
}

/// <summary>The <c>roots</c> client capability.</summary>
public sealed class RootsCapability : WireObject
{
  /// <summary>The client sends <c>notifications/roots/list_changed</c> (legacy).</summary>
  [JsonPropertyName("listChanged")]
  public bool? ListChanged { get; init; }
}

/// <summary>The <c>sampling</c> client capability.</summary>
public sealed class SamplingCapability : WireObject
{
  /// <summary>The client honours <c>includeContext</c> other than <c>none</c>.</summary>
  [JsonPropertyName("context")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public JsonElement Context { get; init; }

  /// <summary>The client supports tools in sampling requests.</summary>
  [JsonPropertyName("tools")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public JsonElement Tools { get; init; }
}

/// <summary>The <c>elicitation</c> client capability. An empty object means form mode only (2025-06-18).</summary>
public sealed class ElicitationCapability : WireObject
{
  /// <summary>Form-mode elicitation.</summary>
  [JsonPropertyName("form")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public JsonElement Form { get; init; }

  /// <summary>URL-mode elicitation.</summary>
  [JsonPropertyName("url")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public JsonElement Url { get; init; }
}

/// <summary>What a server supports.</summary>
public sealed class ServerCapabilities : WireObject
{
  /// <summary>Non-standard capabilities.</summary>
  [JsonPropertyName("experimental")]
  public Dictionary<string, JsonElement>? Experimental { get; init; }

  /// <summary>The server sends log messages (deprecated in 2026-07-28).</summary>
  [JsonPropertyName("logging")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public JsonElement Logging { get; init; }

  /// <summary>The server supports <c>completion/complete</c>.</summary>
  [JsonPropertyName("completions")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public JsonElement Completions { get; init; }

  /// <summary>The server offers prompts.</summary>
  [JsonPropertyName("prompts")]
  public PromptsCapability? Prompts { get; init; }

  /// <summary>The server offers resources.</summary>
  [JsonPropertyName("resources")]
  public ResourcesCapability? Resources { get; init; }

  /// <summary>The server offers tools.</summary>
  [JsonPropertyName("tools")]
  public ToolsCapability? Tools { get; init; }

  /// <summary>The 2025-11-25 experimental task capability, kept raw.</summary>
  [JsonPropertyName("tasks")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public JsonElement Tasks { get; init; }

  /// <summary>Extensions by identifier, with their settings.</summary>
  [JsonPropertyName("extensions")]
  public Dictionary<string, JsonElement>? Extensions { get; init; }
}

/// <summary>The <c>prompts</c> server capability.</summary>
public sealed class PromptsCapability : WireObject
{
  /// <summary>The server announces list changes.</summary>
  [JsonPropertyName("listChanged")]
  public bool? ListChanged { get; init; }
}

/// <summary>The <c>resources</c> server capability.</summary>
public sealed class ResourcesCapability : WireObject
{
  /// <summary>The server supports subscriptions to single resources.</summary>
  [JsonPropertyName("subscribe")]
  public bool? Subscribe { get; init; }

  /// <summary>The server announces list changes.</summary>
  [JsonPropertyName("listChanged")]
  public bool? ListChanged { get; init; }
}

/// <summary>The <c>tools</c> server capability.</summary>
public sealed class ToolsCapability : WireObject
{
  /// <summary>The server announces list changes.</summary>
  [JsonPropertyName("listChanged")]
  public bool? ListChanged { get; init; }
}

/// <summary>Hints about who a piece of content is for and how much it matters.</summary>
public sealed class Annotations : WireObject
{
  /// <summary>The intended readers: <c>user</c>, <c>assistant</c>.</summary>
  [JsonPropertyName("audience")]
  public List<string>? Audience { get; init; }

  /// <summary>0 (least) to 1 (most important).</summary>
  [JsonPropertyName("priority")]
  public double? Priority { get; init; }

  /// <summary>ISO 8601 time of the last change.</summary>
  [JsonPropertyName("lastModified")]
  public string? LastModified { get; init; }
}
