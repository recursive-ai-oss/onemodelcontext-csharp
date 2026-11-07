using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Extensions.ServerCards;

/// <summary>
/// A Server Card: a small JSON document that describes one remote server (identity, version, where to connect) so a client can find out
/// about it before connecting. Cards do not list tools, resources or prompts; those come from the protocol at runtime.
/// </summary>
/// <remarks>
/// Server Cards (Beta), schema <c>v1</c>. A card is advisory: it is read before any connection and may be stale or wrong, so a client
/// checks it against what the server says once connected and never uses it for security decisions. Fields this library does not model
/// are kept in <see cref="WireObject.AdditionalProperties"/>.
/// </remarks>
[Experimental(ExperimentalIds.ServerCards)]
public sealed class ServerCard : WireObject
{
  /// <summary>The only value <c>$schema</c> may have in schema <c>v1</c>.</summary>
  public const string SchemaUrl = "https://static.modelcontextprotocol.io/schemas/v1/server-card.schema.json";

  /// <summary>The media type of a Server Card.</summary>
  public const string MediaType = "application/mcp-server-card+json";

  /// <summary>The schema URI the card conforms to; <see cref="SchemaUrl"/>.</summary>
  [JsonPropertyName("$schema")]
  public string Schema { get; init; } = SchemaUrl;

  /// <summary>The server's name in reverse-DNS form with exactly one slash, for example <c>com.example/weather</c> (3 to 200 characters).</summary>
  [JsonPropertyName("name")]
  public required string Name { get; init; }

  /// <summary>The server's version, up to 255 characters; semantic versioning is advised and ranges are refused. Equals <c>serverInfo.version</c>.</summary>
  [JsonPropertyName("version")]
  public required string Version { get; init; }

  /// <summary>What the server does, 1 to 100 characters.</summary>
  [JsonPropertyName("description")]
  public required string Description { get; init; }

  /// <summary>A display name, 1 to 100 characters.</summary>
  [JsonPropertyName("title")]
  public string? Title { get; init; }

  /// <summary>A link to the server's home page or documentation.</summary>
  [JsonPropertyName("websiteUrl")]
  public string? WebsiteUrl { get; init; }

  /// <summary>Where the source code is, for transparency.</summary>
  [JsonPropertyName("repository")]
  public ServerCardRepository? Repository { get; init; }

  /// <summary>Icons a client may show.</summary>
  [JsonPropertyName("icons")]
  public List<Icon>? Icons { get; init; }

  /// <summary>How to reach the server over HTTP.</summary>
  [JsonPropertyName("remotes")]
  public List<ServerCardRemote>? Remotes { get; init; }

  /// <summary>Extension metadata under reverse-DNS keys.</summary>
  [JsonPropertyName("_meta")]
  public Dictionary<string, JsonElement>? Meta { get; init; }
}

/// <summary>Where the source of a server lives.</summary>
[Experimental(ExperimentalIds.ServerCards)]
public sealed class ServerCardRepository : WireObject
{
  /// <summary>The repository URL.</summary>
  [JsonPropertyName("url")]
  public required string Url { get; init; }

  /// <summary>The hosting service, for example <c>github</c>.</summary>
  [JsonPropertyName("source")]
  public required string Source { get; init; }

  /// <summary>A clean relative path to the server inside a larger repository.</summary>
  [JsonPropertyName("subfolder")]
  public string? Subfolder { get; init; }

  /// <summary>The hosting service's identifier for the repository.</summary>
  [JsonPropertyName("id")]
  public string? Id { get; init; }
}

/// <summary>One HTTP endpoint of the server.</summary>
[Experimental(ExperimentalIds.ServerCards)]
public sealed class ServerCardRemote : WireObject
{
  /// <summary><c>streamable-http</c> or <c>sse</c>.</summary>
  [JsonPropertyName("type")]
  public required string Type { get; init; }

  /// <summary>The endpoint URL, which may contain <c>{variable}</c> placeholders filled from <see cref="Variables"/>. Starts with <c>http://</c>, <c>https://</c> or a placeholder.</summary>
  [JsonPropertyName("url")]
  public required string Url { get; init; }

  /// <summary>Headers the endpoint needs or accepts.</summary>
  [JsonPropertyName("headers")]
  public List<ServerCardKeyValueInput>? Headers { get; init; }

  /// <summary>Settings that fill <c>{placeholders}</c> in <see cref="Url"/>.</summary>
  [JsonPropertyName("variables")]
  public Dictionary<string, ServerCardInput>? Variables { get; init; }

  /// <summary>The protocol revisions this endpoint speaks, so a client can pick one before connecting.</summary>
  [JsonPropertyName("supportedProtocolVersions")]
  public List<string>? SupportedProtocolVersions { get; init; }
}

/// <summary>A value a user supplies or a card presets, for URL variables and header values.</summary>
[Experimental(ExperimentalIds.ServerCards)]
public class ServerCardInput : WireObject
{
  /// <summary>What the input is, for the user.</summary>
  [JsonPropertyName("description")]
  public string? Description { get; init; }

  /// <summary>Whether the connection needs it.</summary>
  [JsonPropertyName("isRequired")]
  public bool? IsRequired { get; init; }

  /// <summary>Whether the value is a secret a client handles carefully.</summary>
  [JsonPropertyName("isSecret")]
  public bool? IsSecret { get; init; }

  /// <summary><c>string</c>, <c>number</c>, <c>boolean</c> or <c>filepath</c>.</summary>
  [JsonPropertyName("format")]
  public string? Format { get; init; }

  /// <summary>The default value.</summary>
  [JsonPropertyName("default")]
  public string? Default { get; init; }

  /// <summary>An example shown while the user types.</summary>
  [JsonPropertyName("placeholder")]
  public string? Placeholder { get; init; }

  /// <summary>A preset value the user cannot change; <c>{placeholders}</c> in it are filled from the variables.</summary>
  [JsonPropertyName("value")]
  public string? Value { get; init; }

  /// <summary>The values the user may choose from.</summary>
  [JsonPropertyName("choices")]
  public List<string>? Choices { get; init; }
}

/// <summary>A named input, used for an HTTP header.</summary>
[Experimental(ExperimentalIds.ServerCards)]
public sealed class ServerCardKeyValueInput : ServerCardInput
{
  /// <summary>The header name.</summary>
  [JsonPropertyName("name")]
  public required string Name { get; init; }

  /// <summary>Variables that fill <c>{placeholders}</c> in <see cref="ServerCardInput.Value"/>.</summary>
  [JsonPropertyName("variables")]
  public Dictionary<string, ServerCardInput>? Variables { get; init; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ServerCard))]
internal sealed partial class ServerCardsJsonContext : JsonSerializerContext;
