using System;
using System.IO;
using System.Text.Json;

namespace RecursiveAI.OneModelContext.JsonRpc;

/// <summary>The peer answered a request with a JSON-RPC error response.</summary>
public sealed class JsonRpcRemoteException : Exception
{
  /// <summary>Creates the exception.</summary>
  /// <param name="error">The error the peer sent.</param>
  public JsonRpcRemoteException(JsonRpcError error)
    : base(error.Message)
  {
    Error = error;
  }

  /// <summary>The error the peer sent.</summary>
  public JsonRpcError Error { get; }

  /// <summary>The error code.</summary>
  public int Code => Error.Code;

  /// <summary>The error's <c>data</c> member, or <c>default</c> when it had none.</summary>
  public JsonElement ErrorData => Error.Data;
}

/// <summary>The connection ended before a response arrived, or a send was attempted on a closed connection.</summary>
public sealed class JsonRpcClosedException : IOException
{
  /// <summary>Creates the exception.</summary>
  /// <param name="message">What ended the connection.</param>
  /// <param name="inner">The cause, when there is one.</param>
  public JsonRpcClosedException(string message, Exception? inner = null)
    : base(message, inner)
  {
  }
}
