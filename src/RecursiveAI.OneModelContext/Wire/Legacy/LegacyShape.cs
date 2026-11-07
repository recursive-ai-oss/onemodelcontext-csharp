using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Wire.Legacy;

/// <summary>
/// Turns a result written in the modern shape into the shape of one legacy revision: drops the fields the revision does not define so a
/// strict client never sees them, and downgrades content blocks it cannot render. Fields the library does not model are never touched.
/// </summary>
internal static class LegacyShape
{
  // The revisions in age order; a field "since" a revision is dropped for any older one.
  internal static int Rank(string version) => version switch
  {
    ProtocolVersions.V20241105 => 0,
    ProtocolVersions.V20250326 => 1,
    ProtocolVersions.V20250618 => 2,
    _ => 3,
  };

  internal static bool IsLegacy(string? version) => version is ProtocolVersions.V20251125 or ProtocolVersions.V20250618 or ProtocolVersions.V20250326 or ProtocolVersions.V20241105;

  internal static bool IsLogLevel(string? level) =>
    level is LoggingLevels.Debug or LoggingLevels.Info or LoggingLevels.Notice or LoggingLevels.Warning
      or LoggingLevels.Error or LoggingLevels.Critical or LoggingLevels.Alert or LoggingLevels.Emergency;

  /// <summary>Rewrites a result for <paramref name="version"/>.</summary>
  internal static JsonElement Result(string version, JsonElement result)
  {
    if (result.ValueKind != JsonValueKind.Object || JsonNode.Parse(result.GetRawText()) is not JsonObject root)
    {
      return result;
    }

    var rank = Rank(version);

    // 2026-07-28 basic/versioning: resultType, ttlMs and cacheScope do not exist before it; the server info moved into _meta.
    root.Remove("resultType");
    root.Remove("ttlMs");
    root.Remove("cacheScope");
    if (root["_meta"] is JsonObject meta)
    {
      meta.Remove(WireNames.MetaServerInfo);
      if (meta.Count == 0)
      {
        root.Remove("_meta");
      }
    }

    if (root["serverInfo"] is JsonObject serverInfo)
    {
      Implementation(serverInfo, rank);
    }

    if (root["capabilities"] is JsonObject capabilities)
    {
      Capabilities(capabilities, rank);
    }

    ForEachObject(root["tools"], tool =>
    {
      Titled(tool, rank);
      if (rank < 3)
      {
        tool.Remove("icons");
        tool.Remove("execution"); // 2025-11-25 task support
      }

      if (rank < 2)
      {
        tool.Remove("outputSchema");
      }

      if (rank < 1)
      {
        tool.Remove("annotations"); // tool annotations arrived with 2025-03-26
      }
    });
    ForEachObject(root["resources"], r => Titled(r, rank));
    ForEachObject(root["resourceTemplates"], r => Titled(r, rank));
    ForEachObject(root["prompts"], prompt =>
    {
      Titled(prompt, rank);
      ForEachObject(prompt["arguments"], argument =>
      {
        if (rank < 2)
        {
          argument.Remove("title");
        }
      });
    });

    if (rank < 2)
    {
      root.Remove("structuredContent"); // 2025-06-18
    }

    ForEachObject(root["content"], block => Block(block, rank));
    ForEachObject(root["messages"], message =>
    {
      if (message["content"] is JsonObject block)
      {
        Block(block, rank);
      }
    });

    return JsonDocument.Parse(root.ToJsonString()).RootElement.Clone();
  }

  /// <summary>Rewrites the capabilities a server declares for an older revision.</summary>
  internal static void Capabilities(JsonObject capabilities, int rank)
  {
    if (rank < 1)
    {
      capabilities.Remove("completions"); // 2025-03-26
    }

    if (rank < 3)
    {
      capabilities.Remove("tasks"); // 2025-11-25, experimental
    }
  }

  private static void Implementation(JsonObject info, int rank)
  {
    if (rank < 2)
    {
      info.Remove("title"); // 2025-06-18
    }

    if (rank < 3)
    {
      info.Remove("icons");
      info.Remove("websiteUrl");
      info.Remove("description");
    }
  }

  private static void Titled(JsonObject definition, int rank)
  {
    if (rank < 2)
    {
      definition.Remove("title"); // 2025-06-18
    }

    if (rank < 3)
    {
      definition.Remove("icons"); // 2025-11-25
    }
  }

  private static void Block(JsonObject block, int rank)
  {
    var type = block["type"]?.GetValue<string>();
    if (type == "resource_link" && rank < 2)
    {
      // 2025-06-18 added resource links; earlier clients get the pointer as text.
      var uri = block["uri"]?.GetValue<string>() ?? "";
      block.Clear();
      block["type"] = "text";
      block["text"] = uri;
    }
    else if (type == "audio" && rank < 1)
    {
      // 2025-03-26 added audio.
      block.Clear();
      block["type"] = "text";
      block["text"] = "[audio content omitted: this protocol revision has no audio]";
    }
  }

  private static void ForEachObject(JsonNode? array, Action<JsonObject> action)
  {
    if (array is not JsonArray items)
    {
      return;
    }

    foreach (var item in items)
    {
      if (item is JsonObject o)
      {
        action(o);
      }
    }
  }
}
