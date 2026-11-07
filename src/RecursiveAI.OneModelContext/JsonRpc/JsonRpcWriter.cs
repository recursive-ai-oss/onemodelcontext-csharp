using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;

namespace RecursiveAI.OneModelContext.JsonRpc;

/// <summary>Writes JSON-RPC 2.0 messages as UTF-8 JSON.</summary>
public static class JsonRpcWriter
{
  /// <summary>Writes one message to a buffer writer, without a trailing newline.</summary>
  /// <param name="output">Where the bytes go.</param>
  /// <param name="message">The message.</param>
  public static void Write(IBufferWriter<byte> output, JsonRpcMessage message)
  {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(message);
    using var writer = new Utf8JsonWriter(output);
    Write(writer, message);
  }

  /// <summary>Writes a batch (a JSON array of messages) to a buffer writer.</summary>
  /// <param name="output">Where the bytes go.</param>
  /// <param name="messages">The messages, in order.</param>
  public static void WriteBatch(IBufferWriter<byte> output, IReadOnlyList<JsonRpcMessage> messages)
  {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(messages);
    using var writer = new Utf8JsonWriter(output);
    writer.WriteStartArray();
    foreach (var message in messages)
    {
      Write(writer, message);
    }

    writer.WriteEndArray();
  }

  /// <summary>Writes one message to a JSON writer.</summary>
  /// <param name="writer">The writer.</param>
  /// <param name="message">The message.</param>
  public static void Write(Utf8JsonWriter writer, JsonRpcMessage message)
  {
    ArgumentNullException.ThrowIfNull(writer);
    ArgumentNullException.ThrowIfNull(message);
    writer.WriteStartObject();
    writer.WriteString("jsonrpc"u8, "2.0"u8);
    switch (message)
    {
      case JsonRpcRequest request:
        writer.WritePropertyName("id"u8);
        request.Id.WriteTo(writer);
        writer.WriteString("method"u8, request.Method);
        WriteElement(writer, "params"u8, request.Params);
        break;
      case JsonRpcNotification notification:
        writer.WriteString("method"u8, notification.Method);
        WriteElement(writer, "params"u8, notification.Params);
        break;
      case JsonRpcResultResponse result:
        writer.WritePropertyName("id"u8);
        result.Id.WriteTo(writer);
        if (result.Result.ValueKind == JsonValueKind.Undefined)
        {
          // A success response must carry a result; an absent one means an empty result.
          writer.WriteStartObject("result"u8);
          writer.WriteEndObject();
        }
        else
        {
          WriteElement(writer, "result"u8, result.Result);
        }

        break;
      case JsonRpcErrorResponse error:
        writer.WritePropertyName("id"u8);
        error.Id.WriteTo(writer); // null when the id is unknown
        writer.WriteStartObject("error"u8);
        writer.WriteNumber("code"u8, error.Error.Code);
        writer.WriteString("message"u8, error.Error.Message);
        WriteElement(writer, "data"u8, error.Error.Data);
        writer.WriteEndObject();
        break;
      default:
        throw new ArgumentException("Unknown message type.", nameof(message));
    }

    writer.WriteEndObject();
  }

  /// <summary>Serializes one message to a new byte array.</summary>
  /// <param name="message">The message.</param>
  public static byte[] ToUtf8Bytes(JsonRpcMessage message)
  {
    var buffer = new ArrayBufferWriter<byte>(256);
    Write(buffer, message);
    return buffer.WrittenSpan.ToArray();
  }

  /// <summary>Serializes one message to a string.</summary>
  /// <param name="message">The message.</param>
  public static string ToJson(JsonRpcMessage message) => System.Text.Encoding.UTF8.GetString(ToUtf8Bytes(message));

  private static void WriteElement(Utf8JsonWriter writer, ReadOnlySpan<byte> name, JsonElement value)
  {
    if (value.ValueKind == JsonValueKind.Undefined)
    {
      return;
    }

    writer.WritePropertyName(name);
    value.WriteTo(writer);
  }
}
