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

  /// <summary>
  /// The same as <see cref="WrapParams(JsonElement, string, Implementation, ClientCapabilities)"/> for params that are already UTF-8 JSON, with
  /// the identity members serialized once (<paramref name="identity"/>, see <see cref="IdentityMembers"/>). Params without their own
  /// <c>_meta</c> (the usual case) are not parsed or copied member by member: the envelope is written in front of their bytes.
  /// </summary>
  internal static JsonElement WrapSerializedParams(ReadOnlySpan<byte> serialized, string version, Implementation clientInfo, ClientCapabilities capabilities, ReadOnlySpan<byte> identity)
  {
    // Params with their own _meta, and calls made inside a trace (the trace context joins _meta), take the general path.
    if (HasTopLevel(serialized, "_meta"u8, default) || serialized.Length < 2 || serialized[0] != (byte)'{' || Diagnostics.TraceContext.TryGetCurrent(out _, out _))
    {
      return WrapParams(serialized.IsEmpty ? default : JsonDocument.Parse(serialized.ToArray()).RootElement, version, clientInfo, capabilities);
    }

    var buffer = new ArrayBufferWriter<byte>(identity.Length + serialized.Length + 96);
    buffer.Write("{\"_meta\":{"u8);
    buffer.Write(identity);
    buffer.Write("}"u8);
    if (serialized.Length > 2)
    {
      buffer.Write(","u8);
    }

    buffer.Write(serialized[1..]);
    return JsonDocument.Parse(buffer.WrittenMemory).RootElement;
  }

  /// <summary>
  /// The same as <see cref="WrapResult"/> for a result that is already UTF-8 JSON and not cacheable. A result without its own
  /// <c>resultType</c> or <c>_meta</c> (the usual case) is not parsed member by member: the envelope is written in front of its bytes.
  /// </summary>
  internal static JsonElement WrapSerializedResult(ReadOnlySpan<byte> serialized, Implementation serverInfo, bool cacheable, long ttlMs, string cacheScope, ReadOnlySpan<byte> serverInfoMember)
  {
    if (cacheable || serialized.Length < 2 || serialized[0] != (byte)'{' || HasTopLevel(serialized, "resultType"u8, "_meta"u8))
    {
      return WrapResult(JsonDocument.Parse(serialized.ToArray()).RootElement, serverInfo, cacheable, ttlMs, cacheScope);
    }

    var buffer = new ArrayBufferWriter<byte>(serverInfoMember.Length + serialized.Length + 64);
    buffer.Write("{\"resultType\":\"complete\",\"_meta\":{"u8);
    buffer.Write(serverInfoMember);
    buffer.Write("}"u8);
    if (serialized.Length > 2)
    {
      buffer.Write(","u8);
    }

    buffer.Write(serialized[1..]);
    return JsonDocument.Parse(buffer.WrittenMemory).RootElement;
  }

  /// <summary>The <c>serverInfo</c> member of a result's <c>_meta</c>, as UTF-8 JSON.</summary>
  internal static byte[] ServerInfoMember(Implementation serverInfo)
  {
    var buffer = new ArrayBufferWriter<byte>(128);
    using (var writer = new Utf8JsonWriter(buffer))
    {
      writer.WriteStartObject();
      WriteServerInfo(writer, serverInfo);
      writer.WriteEndObject();
    }

    return buffer.WrittenSpan[1..^1].ToArray();
  }

  /// <summary>The members every request carries in <c>_meta</c>: version, client identity and capabilities, as UTF-8 JSON members.</summary>
  internal static byte[] IdentityMembers(string version, Implementation clientInfo, ClientCapabilities capabilities)
  {
    var buffer = new ArrayBufferWriter<byte>(256);
    using (var writer = new Utf8JsonWriter(buffer))
    {
      writer.WriteStartObject();
      writer.WriteString(WireNames.MetaProtocolVersion, version);
      writer.WritePropertyName(WireNames.MetaClientInfo);
      JsonSerializer.Serialize(writer, clientInfo, WireJsonContext.Default.Implementation);
      writer.WritePropertyName(WireNames.MetaClientCapabilities);
      JsonSerializer.Serialize(writer, capabilities, WireJsonContext.Default.ClientCapabilities);
      writer.WriteEndObject();
    }

    // Strip the object's braces: the members go inside the _meta object being written.
    return buffer.WrittenSpan[1..^1].ToArray();
  }

  private static bool HasTopLevel(ReadOnlySpan<byte> json, ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
  {
    var reader = new Utf8JsonReader(json);
    if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
    {
      return true;
    }

    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
    {
      if (reader.ValueTextEquals(first) || (!second.IsEmpty && reader.ValueTextEquals(second)))
      {
        return true;
      }

      reader.Read();
      if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
      {
        reader.Skip();
      }
    }

    return false;
  }

  private static void WriteServerInfo(Utf8JsonWriter writer, Implementation serverInfo)
  {
    writer.WritePropertyName(WireNames.MetaServerInfo);
    JsonSerializer.Serialize(writer, serverInfo, WireJsonContext.Default.Implementation);
  }
}
