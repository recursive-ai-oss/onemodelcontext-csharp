using System;
using System.Buffers;
using System.Text.Json;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Wire.Modern;

/// <summary>
/// Adds the 2026-07-28 envelope members to a message that is already serialized, without touching the record it came from (a handler may
/// return the same result object many times) and without losing any member the record does not model.
/// </summary>
internal static class ModernEnvelope
{
  /// <summary>
  /// Turns a serialized result into its wire form: <c>resultType</c> (<c>complete</c> unless set), <c>_meta</c> with the server's
  /// <c>serverInfo</c> (unless the handler set one), and for cacheable results <c>ttlMs</c> and <c>cacheScope</c> (unless set).
  /// </summary>
  internal static JsonElement WrapResult(JsonElement serialized, Implementation serverInfo, bool cacheable, long ttlMs, string cacheScope)
  {
    var buffer = new ArrayBufferWriter<byte>(512);
    using (var writer = new Utf8JsonWriter(buffer))
    {
      var resultType = serialized.TryGetProperty("resultType", out var existingType) && existingType.ValueKind == JsonValueKind.String
        ? existingType.GetString()
        : null;
      var complete = resultType is null or WireNames.ResultTypeComplete;
      writer.WriteStartObject();
      if (resultType is null)
      {
        writer.WriteString("resultType", WireNames.ResultTypeComplete);
      }

      var wroteMeta = false;
      var hasTtl = false;
      var hasScope = false;
      foreach (var property in serialized.EnumerateObject())
      {
        if (property.NameEquals("_meta") && property.Value.ValueKind == JsonValueKind.Object)
        {
          wroteMeta = true;
          writer.WritePropertyName("_meta");
          writer.WriteStartObject();
          var hasInfo = false;
          foreach (var meta in property.Value.EnumerateObject())
          {
            hasInfo |= meta.NameEquals(WireNames.MetaServerInfo);
            meta.WriteTo(writer);
          }

          if (!hasInfo)
          {
            WriteServerInfo(writer, serverInfo);
          }

          writer.WriteEndObject();
          continue;
        }

        hasTtl |= property.NameEquals("ttlMs");
        hasScope |= property.NameEquals("cacheScope");
        property.WriteTo(writer);
      }

      if (!wroteMeta)
      {
        writer.WriteStartObject("_meta");
        WriteServerInfo(writer, serverInfo);
        writer.WriteEndObject();
      }

      // 2026-07-28 server/utilities/caching: a complete cacheable result MUST carry hints; an input_required result carries none.
      if (cacheable && complete)
      {
        if (!hasTtl)
        {
          writer.WriteNumber("ttlMs", ttlMs);
        }

        if (!hasScope)
        {
          writer.WriteString("cacheScope", cacheScope);
        }
      }

      writer.WriteEndObject();
    }

    return JsonDocument.Parse(buffer.WrittenMemory).RootElement;
  }

  /// <summary>
  /// Turns serialized request params into their wire form: the version, client identity and capabilities go into <c>_meta</c>
  /// next to whatever the caller already put there (a progress token, trace context, extension keys).
  /// </summary>
  internal static JsonElement WrapParams(JsonElement serialized, string version, Implementation clientInfo, ClientCapabilities capabilities)
  {
    var buffer = new ArrayBufferWriter<byte>(512);
    using (var writer = new Utf8JsonWriter(buffer))
    {
      writer.WriteStartObject();
      writer.WritePropertyName("_meta");
      writer.WriteStartObject();
      var hasTrace = false;
      var hasTraceState = false;
      if (serialized.ValueKind == JsonValueKind.Object && serialized.TryGetProperty("_meta", out var existing) && existing.ValueKind == JsonValueKind.Object)
      {
        foreach (var meta in existing.EnumerateObject())
        {
          if (meta.NameEquals(WireNames.MetaProtocolVersion) || meta.NameEquals(WireNames.MetaClientInfo) || meta.NameEquals(WireNames.MetaClientCapabilities))
          {
            continue;
          }

          hasTrace |= meta.NameEquals("traceparent");
          hasTraceState |= meta.NameEquals("tracestate");
          meta.WriteTo(writer);
        }
      }

      writer.WriteString(WireNames.MetaProtocolVersion, version);
      writer.WritePropertyName(WireNames.MetaClientInfo);
      JsonSerializer.Serialize(writer, clientInfo, WireJsonContext.Default.Implementation);
      writer.WritePropertyName(WireNames.MetaClientCapabilities);
      JsonSerializer.Serialize(writer, capabilities, WireJsonContext.Default.ClientCapabilities);

      // SEP-414: carry the caller's trace context so the server's spans join the client's trace.
      if (!hasTrace && Diagnostics.TraceContext.TryGetCurrent(out var traceParent, out var traceState))
      {
        writer.WriteString("traceparent", traceParent);
        if (traceState is not null && !hasTraceState)
        {
          writer.WriteString("tracestate", traceState);
        }
      }

      writer.WriteEndObject();
      if (serialized.ValueKind == JsonValueKind.Object)
      {
        foreach (var property in serialized.EnumerateObject())
        {
          if (!property.NameEquals("_meta"))
          {
            property.WriteTo(writer);
          }
        }
      }

      writer.WriteEndObject();
    }

    return JsonDocument.Parse(buffer.WrittenMemory).RootElement;
  }

  private static void WriteServerInfo(Utf8JsonWriter writer, Implementation serverInfo)
  {
    writer.WritePropertyName(WireNames.MetaServerInfo);
    JsonSerializer.Serialize(writer, serverInfo, WireJsonContext.Default.Implementation);
  }
}
