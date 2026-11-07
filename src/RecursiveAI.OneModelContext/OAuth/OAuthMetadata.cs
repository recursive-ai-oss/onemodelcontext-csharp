using System;
using System.Collections.Generic;
using System.Text.Json;

namespace RecursiveAI.OneModelContext.OAuth;

/// <summary>OAuth 2.0 Protected Resource Metadata (RFC 9728 section 2), the fields a client acts on.</summary>
public sealed class ResourceMetadata
{
  /// <summary>The resource identifier the document describes (<c>resource</c>). A client must compare it with the URL it requested.</summary>
  public string? Resource { get; init; }

  /// <summary>The authorization servers that can issue tokens for the resource (<c>authorization_servers</c>).</summary>
  public IReadOnlyList<string> AuthorizationServers { get; init; } = [];

  /// <summary>The scopes the resource understands (<c>scopes_supported</c>), or null when it does not say.</summary>
  public IReadOnlyList<string>? ScopesSupported { get; init; }

  /// <summary>Reads a metadata document.</summary>
  /// <param name="utf8Json">The response body.</param>
  /// <returns>The metadata, or null when the body is not a JSON object.</returns>
  public static ResourceMetadata? Parse(ReadOnlySpan<byte> utf8Json)
  {
    using var document = MetadataJson.TryParse(utf8Json);
    if (document is null)
    {
      return null;
    }

    var root = document.RootElement;
    return new ResourceMetadata
    {
      Resource = MetadataJson.GetString(root, "resource"),
      AuthorizationServers = MetadataJson.GetStrings(root, "authorization_servers") ?? [],
      ScopesSupported = MetadataJson.GetStrings(root, "scopes_supported"),
    };
  }
}

/// <summary>Authorization server metadata (RFC 8414 section 2, or OpenID Provider metadata), the fields a client acts on.</summary>
public sealed class AuthorizationServerMetadata
{
  /// <summary>The issuer identifier (<c>issuer</c>).</summary>
  public string? Issuer { get; init; }

  /// <summary>The authorization endpoint.</summary>
  public string? AuthorizationEndpoint { get; init; }

  /// <summary>The token endpoint.</summary>
  public string? TokenEndpoint { get; init; }

  /// <summary>The dynamic client registration endpoint (RFC 7591), if the server has one.</summary>
  public string? RegistrationEndpoint { get; init; }

  /// <summary><c>code_challenge_methods_supported</c>; null when absent, which means the server does not support PKCE.</summary>
  public IReadOnlyList<string>? CodeChallengeMethodsSupported { get; init; }

  /// <summary><c>scopes_supported</c>, or null.</summary>
  public IReadOnlyList<string>? ScopesSupported { get; init; }

  /// <summary><c>token_endpoint_auth_methods_supported</c>, or null (RFC 8414 section 2 then defaults to <c>client_secret_basic</c>).</summary>
  public IReadOnlyList<string>? TokenEndpointAuthMethodsSupported { get; init; }

  /// <summary><c>token_endpoint_auth_signing_alg_values_supported</c>, or null.</summary>
  public IReadOnlyList<string>? TokenEndpointAuthSigningAlgValuesSupported { get; init; }

  /// <summary><c>grant_types_supported</c>, or null.</summary>
  public IReadOnlyList<string>? GrantTypesSupported { get; init; }

  /// <summary><c>authorization_response_iss_parameter_supported</c> (RFC 9207 section 3); false when absent.</summary>
  public bool AuthorizationResponseIssParameterSupported { get; init; }

  /// <summary><c>client_id_metadata_document_supported</c>; false when absent.</summary>
  public bool ClientIdMetadataDocumentSupported { get; init; }

  /// <summary><c>authorization_grant_profiles_supported</c> (the ID-JAG profile is <c>urn:ietf:params:oauth:grant-profile:id-jag</c>), or null.</summary>
  public IReadOnlyList<string>? AuthorizationGrantProfilesSupported { get; init; }

  /// <summary>Reads a metadata document.</summary>
  /// <param name="utf8Json">The response body.</param>
  /// <returns>The metadata, or null when the body is not a JSON object.</returns>
  public static AuthorizationServerMetadata? Parse(ReadOnlySpan<byte> utf8Json)
  {
    using var document = MetadataJson.TryParse(utf8Json);
    if (document is null)
    {
      return null;
    }

    var root = document.RootElement;
    return new AuthorizationServerMetadata
    {
      Issuer = MetadataJson.GetString(root, "issuer"),
      AuthorizationEndpoint = MetadataJson.GetString(root, "authorization_endpoint"),
      TokenEndpoint = MetadataJson.GetString(root, "token_endpoint"),
      RegistrationEndpoint = MetadataJson.GetString(root, "registration_endpoint"),
      CodeChallengeMethodsSupported = MetadataJson.GetStrings(root, "code_challenge_methods_supported"),
      ScopesSupported = MetadataJson.GetStrings(root, "scopes_supported"),
      TokenEndpointAuthMethodsSupported = MetadataJson.GetStrings(root, "token_endpoint_auth_methods_supported"),
      TokenEndpointAuthSigningAlgValuesSupported = MetadataJson.GetStrings(root, "token_endpoint_auth_signing_alg_values_supported"),
      GrantTypesSupported = MetadataJson.GetStrings(root, "grant_types_supported"),
      AuthorizationResponseIssParameterSupported = MetadataJson.GetBool(root, "authorization_response_iss_parameter_supported"),
      ClientIdMetadataDocumentSupported = MetadataJson.GetBool(root, "client_id_metadata_document_supported"),
      AuthorizationGrantProfilesSupported = MetadataJson.GetStrings(root, "authorization_grant_profiles_supported"),
    };
  }
}

internal static class MetadataJson
{
  // A metadata document is small; deeper nesting than this is hostile.
  private const int MaxDepth = 16;

  // Longest array read from a document; the rest is ignored.
  private const int MaxItems = 256;

  internal static JsonDocument? TryParse(ReadOnlySpan<byte> utf8Json)
  {
    try
    {
      var document = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions { MaxDepth = MaxDepth });
      if (document.RootElement.ValueKind == JsonValueKind.Object)
      {
        return document;
      }

      document.Dispose();
      return null;
    }
    catch (JsonException)
    {
      return null;
    }
  }

  internal static string? GetString(JsonElement root, string name) =>
    root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? Text(value) : null;

  // JsonElement.GetString throws on a string that is not valid UTF-8, which a hostile server can send; such a value counts as absent.
  internal static string? Text(JsonElement value)
  {
    try
    {
      return value.GetString();
    }
    catch (InvalidOperationException)
    {
      return null;
    }
  }

  internal static bool GetBool(JsonElement root, string name) =>
    root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

  internal static List<string>? GetStrings(JsonElement root, string name)
  {
    if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
    {
      return null;
    }

    var list = new List<string>();
    foreach (var item in value.EnumerateArray())
    {
      if (item.ValueKind == JsonValueKind.String && Text(item) is { } text && list.Count < MaxItems)
      {
        list.Add(text);
      }
    }

    return list;
  }
}
