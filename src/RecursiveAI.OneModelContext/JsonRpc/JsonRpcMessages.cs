using System;
using System.Text.Json;

namespace RecursiveAI.OneModelContext.JsonRpc;

/// <summary>Error codes of JSON-RPC 2.0 §5.1 and the one implementation-defined code the protocol layer needs.</summary>
public static class JsonRpcErrorCodes
{
  /// <summary>Invalid JSON was received (-32700).</summary>
  public const int ParseError = -32700;

  /// <summary>The JSON sent is not a valid request object (-32600).</summary>
  public const int InvalidRequest = -32600;

  /// <summary>The method does not exist or is not available (-32601).</summary>
  public const int MethodNotFound = -32601;

  /// <summary>Invalid method parameters (-32602).</summary>
  public const int InvalidParams = -32602;

  /// <summary>Internal error, used for faults in a handler (-32603).</summary>
  public const int InternalError = -32603;

  /// <summary>The receiver has too many requests in flight (-32000, implementation-defined range).</summary>
  public const int ServerBusy = -32000;
}

/// <summary>The error object of a JSON-RPC error response (JSON-RPC 2.0 §5.1).</summary>
/// <param name="Code">The error code.</param>
/// <param name="Message">A short description.</param>
/// <param name="Data">Optional structured detail; <c>default</c> when absent.</param>
public readonly record struct JsonRpcError(int Code, string Message, JsonElement Data = default);

/// <summary>A JSON-RPC 2.0 message. The base layer carries no protocol types: params and results are raw JSON.</summary>
public abstract class JsonRpcMessage
{
  private protected JsonRpcMessage()
  {
  }
}

/// <summary>A request: it has an id and expects a response.</summary>
public sealed class JsonRpcRequest : JsonRpcMessage
{
  /// <summary>Creates a request.</summary>
  /// <param name="id">The id; must have a value.</param>
  /// <param name="method">The method name.</param>
  /// <param name="parameters">The params value, or <c>default</c> for none. Cloned by the reader; the writer does not copy it.</param>
  public JsonRpcRequest(JsonRpcId id, string method, JsonElement parameters = default)
  {
    ArgumentNullException.ThrowIfNull(method);
    if (!id.HasValue)
    {
      throw new ArgumentException("A request needs an id.", nameof(id));
    }

    Id = id;
    Method = method;
    Params = parameters;
  }

  /// <summary>The id.</summary>
  public JsonRpcId Id { get; }

  /// <summary>The method name.</summary>
  public string Method { get; }

  /// <summary>The params, or <c>default</c> (kind <see cref="JsonValueKind.Undefined"/>) when absent.</summary>
  public JsonElement Params { get; }
}

/// <summary>A notification: a request without an id; no response is sent.</summary>
public sealed class JsonRpcNotification : JsonRpcMessage
{
  /// <summary>Creates a notification.</summary>
  /// <param name="method">The method name.</param>
  /// <param name="parameters">The params value, or <c>default</c> for none.</param>
  public JsonRpcNotification(string method, JsonElement parameters = default)
  {
    ArgumentNullException.ThrowIfNull(method);
    Method = method;
    Params = parameters;
  }

  /// <summary>The method name.</summary>
  public string Method { get; }

  /// <summary>The params, or <c>default</c> when absent.</summary>
  public JsonElement Params { get; }
}

/// <summary>A success response.</summary>
public sealed class JsonRpcResultResponse : JsonRpcMessage
{
  /// <summary>Creates a success response.</summary>
  /// <param name="id">The id of the request being answered.</param>
  /// <param name="result">The result value.</param>
  public JsonRpcResultResponse(JsonRpcId id, JsonElement result)
  {
    Id = id;
    Result = result;
  }

  /// <summary>The id of the request being answered.</summary>
  public JsonRpcId Id { get; }

  /// <summary>The result value.</summary>
  public JsonElement Result { get; }
}

/// <summary>An error response. The id is absent when the request could not be read far enough to find one.</summary>
public sealed class JsonRpcErrorResponse : JsonRpcMessage
{
  /// <summary>Creates an error response.</summary>
  /// <param name="id">The id of the failed request, or <c>default</c> to write <c>null</c> (JSON-RPC 2.0 §5).</param>
  /// <param name="error">The error.</param>
  public JsonRpcErrorResponse(JsonRpcId id, JsonRpcError error)
  {
    Id = id;
    Error = error;
  }

  /// <summary>The id of the failed request, or <c>default</c> when unknown.</summary>
  public JsonRpcId Id { get; }

  /// <summary>The error.</summary>
  public JsonRpcError Error { get; }
}
