using System;
using System.Collections.Generic;
using System.Text.Json;

namespace RecursiveAI.OneModelContext.JsonRpc;

/// <summary>Limits applied while reading a JSON-RPC message.</summary>
public sealed class JsonRpcReaderOptions
{
  /// <summary>Deepest JSON nesting accepted, counting the message object itself. Default 64.</summary>
  public int MaxDepth { get; init; } = 64;

  /// <summary>Largest message accepted, in bytes. Default 8 MiB (<see cref="NewlineChannel.DefaultMaxLineBytes"/>).</summary>
  public int MaxMessageBytes { get; init; } = NewlineChannel.DefaultMaxLineBytes;

  /// <summary>Most elements accepted in one batch array. Default 100.</summary>
  public int MaxBatchItems { get; init; } = 100;

  internal static JsonRpcReaderOptions Default { get; } = new();
}

/// <summary>What <see cref="JsonRpcReader.Read(ReadOnlyMemory{byte}, JsonRpcReaderOptions?)"/> found.</summary>
public enum JsonRpcReadKind
{
  /// <summary>One well-formed message (request, notification or response).</summary>
  Message,

  /// <summary>A JSON array of items; each item is a <see cref="Message"/> or <see cref="Invalid"/>.</summary>
  Batch,

  /// <summary>Not a valid message; <see cref="JsonRpcReadResult.Error"/> is the response to send.</summary>
  Invalid,
}

/// <summary>The outcome of reading one JSON-RPC message. Reading never throws on bad input.</summary>
public sealed class JsonRpcReadResult
{
  private JsonRpcReadResult(JsonRpcReadKind kind, JsonRpcMessage? message, IReadOnlyList<JsonRpcReadResult>? items, JsonRpcErrorResponse? error)
  {
    Kind = kind;
    Message = message;
    Items = items;
    Error = error;
  }

  /// <summary>What was read.</summary>
  public JsonRpcReadKind Kind { get; }

  /// <summary>The message, when <see cref="Kind"/> is <see cref="JsonRpcReadKind.Message"/>.</summary>
  public JsonRpcMessage? Message { get; }

  /// <summary>The batch items in order, when <see cref="Kind"/> is <see cref="JsonRpcReadKind.Batch"/>.</summary>
  public IReadOnlyList<JsonRpcReadResult>? Items { get; }

  /// <summary>The error response to send, when <see cref="Kind"/> is <see cref="JsonRpcReadKind.Invalid"/>.</summary>
  public JsonRpcErrorResponse? Error { get; }

  internal static JsonRpcReadResult Of(JsonRpcMessage message) => new(JsonRpcReadKind.Message, message, null, null);

  internal static JsonRpcReadResult OfBatch(IReadOnlyList<JsonRpcReadResult> items) => new(JsonRpcReadKind.Batch, null, items, null);

  internal static JsonRpcReadResult Fail(JsonRpcId id, int code, string message) =>
    new(JsonRpcReadKind.Invalid, null, null, new JsonRpcErrorResponse(id, new JsonRpcError(code, message)));
}

/// <summary>
/// Reads JSON-RPC 2.0 messages from UTF-8 bytes. It never throws on bad input; it classifies the way the protocol needs:
/// -32700 with a null id for text that is not JSON, -32600 for JSON that is not a message.
/// </summary>
public static class JsonRpcReader
{
  /// <summary>Reads one message or batch.</summary>
  /// <param name="utf8">The message bytes, without a trailing newline.</param>
  /// <param name="options">Limits; <c>null</c> for the defaults.</param>
  /// <returns>The classified result. Parameters and results are cloned, so they outlive <paramref name="utf8"/>.</returns>
  public static JsonRpcReadResult Read(ReadOnlyMemory<byte> utf8, JsonRpcReaderOptions? options = null)
  {
    options ??= JsonRpcReaderOptions.Default;
    if (utf8.Length > options.MaxMessageBytes)
    {
      return JsonRpcReadResult.Fail(default, JsonRpcErrorCodes.InvalidRequest, "Invalid request: the message is larger than the limit.");
    }

    JsonDocument doc;
    try
    {
      doc = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = options.MaxDepth });
    }
    catch (JsonException)
    {
      // A message that is not JSON, or nests deeper than MaxDepth. JSON-RPC 2.0 §5.1: parse error, id null.
      return JsonRpcReadResult.Fail(default, JsonRpcErrorCodes.ParseError, "Parse error: the message is not valid JSON.");
    }

    using (doc)
    {
      try
      {
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
          return ReadBatch(root, options);
        }

        return ReadOne(root);
      }
      catch (InvalidOperationException)
      {
        // The parser checks string contents lazily: bytes that are not valid UTF-8 surface when a string is read. That is not JSON either.
        return JsonRpcReadResult.Fail(default, JsonRpcErrorCodes.ParseError, "Parse error: the message is not valid JSON.");
      }
    }
  }

  /// <summary>Reads one message from a string. Convenience for hosts that hold text.</summary>
  /// <param name="message">One JSON-RPC message.</param>
  /// <param name="options">Limits; <c>null</c> for the defaults.</param>
  public static JsonRpcReadResult Read(string message, JsonRpcReaderOptions? options = null)
  {
    ArgumentNullException.ThrowIfNull(message);
    return Read(System.Text.Encoding.UTF8.GetBytes(message), options);
  }

  private static JsonRpcReadResult ReadBatch(JsonElement array, JsonRpcReaderOptions options)
  {
    var length = array.GetArrayLength();
    if (length == 0)
    {
      // JSON-RPC 2.0 §6: an empty array is an invalid request.
      return JsonRpcReadResult.Fail(default, JsonRpcErrorCodes.InvalidRequest, "Invalid request: an empty batch.");
    }

    if (length > options.MaxBatchItems)
    {
      return JsonRpcReadResult.Fail(default, JsonRpcErrorCodes.InvalidRequest, "Invalid request: the batch has too many messages.");
    }

    var items = new List<JsonRpcReadResult>(length);
    foreach (var item in array.EnumerateArray())
    {
      items.Add(ReadOne(item));
    }

    return JsonRpcReadResult.OfBatch(items);
  }

  private static JsonRpcReadResult ReadOne(JsonElement root)
  {
    if (root.ValueKind != JsonValueKind.Object)
    {
      return JsonRpcReadResult.Fail(default, JsonRpcErrorCodes.InvalidRequest, "Invalid request: expected a JSON-RPC object.");
    }

    // An id of the wrong type (null, boolean, object, array) is an invalid request; MCP forbids null ids.
    var hasIdProperty = root.TryGetProperty("id", out var idElement);
    var id = default(JsonRpcId);
    if (hasIdProperty && !JsonRpcId.TryFromElement(idElement, out id))
    {
      // An error response may carry "id": null, so only requests and success responses reject it below.
      if (idElement.ValueKind != JsonValueKind.Null || root.TryGetProperty("method", out _) || root.TryGetProperty("result", out _))
      {
        return JsonRpcReadResult.Fail(default, JsonRpcErrorCodes.InvalidRequest, "Invalid request: the id must be a string or a number.");
      }
    }

    if (!root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
    {
      return JsonRpcReadResult.Fail(id, JsonRpcErrorCodes.InvalidRequest, "Invalid request: \"jsonrpc\" must be \"2.0\".");
    }

    if (root.TryGetProperty("method", out var methodElement))
    {
      if (methodElement.ValueKind != JsonValueKind.String)
      {
        return JsonRpcReadResult.Fail(id, JsonRpcErrorCodes.InvalidRequest, "Invalid request: the method must be a string.");
      }

      var method = methodElement.GetString()!;
      var parameters = default(JsonElement);
      if (root.TryGetProperty("params", out var p))
      {
        // The protocol uses by-name params only; anything else is treated as absent, as the seed did.
        if (p.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
          parameters = p.Clone();
        }
      }

      return JsonRpcReadResult.Of(id.HasValue ? new JsonRpcRequest(id, method, parameters) : new JsonRpcNotification(method, parameters));
    }

    // A response to something we sent.
    if (root.TryGetProperty("result", out var result))
    {
      return id.HasValue
        ? JsonRpcReadResult.Of(new JsonRpcResultResponse(id, result.Clone()))
        : JsonRpcReadResult.Fail(default, JsonRpcErrorCodes.InvalidRequest, "Invalid request: a response needs an id.");
    }

    if (root.TryGetProperty("error", out var error))
    {
      if (error.ValueKind == JsonValueKind.Object
        && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var codeValue)
        && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
      {
        var data = error.TryGetProperty("data", out var d) ? d.Clone() : default;
        return JsonRpcReadResult.Of(new JsonRpcErrorResponse(id, new JsonRpcError(codeValue, message.GetString()!, data)));
      }

      return JsonRpcReadResult.Fail(id, JsonRpcErrorCodes.InvalidRequest, "Invalid request: malformed error object.");
    }

    return JsonRpcReadResult.Fail(id, JsonRpcErrorCodes.InvalidRequest, "Invalid request: no method.");
  }
}
