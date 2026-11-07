using System;
using System.Collections.Generic;
using System.Text.Json;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext;

/// <summary>
/// Base of every exception this library throws on purpose. Messages never contain tokens, secrets or request bodies.
/// </summary>
public class ContextException : Exception
{
  /// <summary>Creates the exception.</summary>
  /// <param name="message">What went wrong.</param>
  /// <param name="inner">The cause, when there is one.</param>
  public ContextException(string message, Exception? inner = null)
    : base(message, inner)
  {
  }
}

/// <summary>
/// The peer answered with a JSON-RPC error, or this side is about to answer with one. It carries the code and the error's <c>data</c>.
/// Throw it from a handler to send that exact error. A tool that fails is not a protocol error: throw <see cref="ToolException"/>.
/// </summary>
public class ProtocolException : ContextException
{
  /// <summary>Creates the exception.</summary>
  /// <param name="code">The JSON-RPC error code.</param>
  /// <param name="message">The error message sent to the peer.</param>
  /// <param name="data">The error's <c>data</c> member, or <c>default</c> for none.</param>
  /// <param name="inner">The cause, when there is one.</param>
  public ProtocolException(int code, string message, JsonElement data = default, Exception? inner = null)
    : base(message, inner)
  {
    Code = code;
    ErrorData = data;
  }

  /// <summary>The JSON-RPC error code.</summary>
  public int Code { get; }

  /// <summary>The error's <c>data</c> member, or <c>default</c> when it had none.</summary>
  public JsonElement ErrorData { get; }

  /// <summary>The JSON-RPC error object for this exception.</summary>
  public JsonRpcError ToError() => new(Code, Message, ErrorData);

  /// <summary>Wraps an error the peer sent, choosing the most specific exception type for its code.</summary>
  /// <param name="error">The error object from the peer.</param>
  public static ProtocolException FromError(JsonRpcError error) => error.Code switch
  {
    ProtocolErrorCodes.UnsupportedProtocolVersion => ProtocolVersionException.FromData(error),
    ProtocolErrorCodes.HeaderMismatch => new HeaderMismatchException(error.Message),
    ProtocolErrorCodes.MissingRequiredClientCapability => MissingCapabilityException.FromData(error),
    _ => new ProtocolException(error.Code, error.Message, error.Data),
  };
}

/// <summary>
/// No protocol revision is common to both sides (-32022). The message names the revisions and eras on both sides.
/// </summary>
public sealed class ProtocolVersionException : ProtocolException
{
  /// <summary>Creates the exception and composes a message naming both sides.</summary>
  /// <param name="local">The revisions this side speaks.</param>
  /// <param name="remote">The revisions the peer speaks; empty when the peer did not say.</param>
  /// <param name="requested">The revision that was asked for, when there was one.</param>
  public ProtocolVersionException(IReadOnlyList<string> local, IReadOnlyList<string> remote, string? requested = null)
    : base(ProtocolErrorCodes.UnsupportedProtocolVersion, Describe(local, remote, requested), BuildData(remote, requested))
  {
    Local = local;
    Remote = remote;
    Requested = requested;
  }

  /// <summary>The revisions this side speaks.</summary>
  public IReadOnlyList<string> Local { get; }

  /// <summary>The revisions the peer speaks.</summary>
  public IReadOnlyList<string> Remote { get; }

  /// <summary>The revision that was asked for.</summary>
  public string? Requested { get; }

  internal static ProtocolVersionException FromData(JsonRpcError error)
  {
    var remote = new List<string>();
    string? requested = null;
    if (error.Data.ValueKind == JsonValueKind.Object)
    {
      if (error.Data.TryGetProperty("supported", out var supported) && supported.ValueKind == JsonValueKind.Array)
      {
        foreach (var item in supported.EnumerateArray())
        {
          if (item.ValueKind == JsonValueKind.String)
          {
            remote.Add(item.GetString()!);
          }
        }
      }

      if (error.Data.TryGetProperty("requested", out var req) && req.ValueKind == JsonValueKind.String)
      {
        requested = req.GetString();
      }
    }

    return new ProtocolVersionException(ProtocolVersions.Supported, remote, requested);
  }

  private static string Describe(IReadOnlyList<string> local, IReadOnlyList<string> remote, string? requested)
  {
    var text = "No common protocol version. This side speaks " + Eras(local) + "; the peer speaks " + (remote.Count == 0 ? "no version it named" : Eras(remote)) + ".";
    return requested is null ? text : text + " Requested: " + requested + ".";
  }

  private static string Eras(IReadOnlyList<string> versions)
  {
    var modern = new List<string>();
    var legacy = new List<string>();
    foreach (var version in versions)
    {
      (ProtocolVersions.IsKnown(version) && ProtocolVersions.EraOf(version) == ProtocolEra.Modern ? modern : legacy).Add(version);
    }

    var parts = new List<string>();
    if (modern.Count > 0)
    {
      parts.Add("modern " + string.Join(", ", modern));
    }

    if (legacy.Count > 0)
    {
      parts.Add("legacy " + string.Join(", ", legacy));
    }

    return parts.Count == 0 ? "no versions" : string.Join(" and ", parts);
  }

  private static JsonElement BuildData(IReadOnlyList<string> supported, string? requested)
  {
    var data = new UnsupportedProtocolVersionData { Supported = [.. supported], Requested = requested };
    return JsonSerializer.SerializeToElement(data, WireJsonContext.Default.UnsupportedProtocolVersionData);
  }
}

/// <summary>The HTTP headers and the request body disagree, or required headers are missing (-32020).</summary>
public sealed class HeaderMismatchException : ProtocolException
{
  /// <summary>Creates the exception.</summary>
  /// <param name="message">Which header disagreed and how (without the header's value if it could be sensitive).</param>
  public HeaderMismatchException(string message)
    : base(ProtocolErrorCodes.HeaderMismatch, message)
  {
  }
}

/// <summary>The client lacks a capability the request needs (-32021).</summary>
public sealed class MissingCapabilityException : ProtocolException
{
  /// <summary>Creates the exception.</summary>
  /// <param name="message">What is missing.</param>
  /// <param name="required">The capabilities the request needs.</param>
  public MissingCapabilityException(string message, ClientCapabilities required)
    : base(ProtocolErrorCodes.MissingRequiredClientCapability, message, JsonSerializer.SerializeToElement(new MissingRequiredClientCapabilityData { RequiredCapabilities = required }, WireJsonContext.Default.MissingRequiredClientCapabilityData))
  {
    Required = required;
  }

  /// <summary>The capabilities the request needs.</summary>
  public ClientCapabilities Required { get; }

  internal static MissingCapabilityException FromData(JsonRpcError error)
  {
    ClientCapabilities? required = null;
    if (error.Data.ValueKind == JsonValueKind.Object)
    {
      try
      {
        required = error.Data.Deserialize(WireJsonContext.Default.MissingRequiredClientCapabilityData)?.RequiredCapabilities;
      }
      catch (JsonException)
      {
        // A malformed error body still becomes the exception; only the capability detail is lost.
      }
    }

    return new MissingCapabilityException(error.Message, required ?? new ClientCapabilities());
  }
}

/// <summary>The transport failed: the process exited, the stream closed or a message was too large.</summary>
public sealed class TransportException : ContextException
{
  /// <summary>Creates the exception.</summary>
  /// <param name="message">What failed.</param>
  /// <param name="inner">The cause, when there is one.</param>
  public TransportException(string message, Exception? inner = null)
    : base(message, inner)
  {
  }
}

/// <summary>
/// A tool failed. Throw it from a tool handler to return a result with <c>isError</c> set and the message as text, which the model
/// can read and react to. It is not a JSON-RPC error; use <see cref="ProtocolException"/> for a malformed call.
/// </summary>
public sealed class ToolException : ContextException
{
  /// <summary>Creates the exception.</summary>
  /// <param name="message">What went wrong, written for a model to read.</param>
  /// <param name="inner">The cause; its text is never sent.</param>
  public ToolException(string message, Exception? inner = null)
    : base(message, inner)
  {
  }
}

/// <summary>
/// Thrown inside a handler when it asks for input that is not available yet, so the attempt ends with an <c>input_required</c> result
/// (2026-07-28). Handlers do not catch it. A handler that builds one itself owns the integrity of any <c>requestState</c> it puts in the
/// result; the asks made through <see cref="RequestContext"/> are protected by the server.
/// </summary>
public sealed class InputRequiredException : ContextException
{
  private readonly Func<InputRequiredResult>? _factory;
  private InputRequiredResult? _result;

  /// <summary>Creates the exception.</summary>
  /// <param name="result">The result that asks the client for input.</param>
  public InputRequiredException(InputRequiredResult result)
    : base("The request needs input from the client.")
  {
    _result = result;
  }

  // The asks of one attempt are collected until the handler is done with it, so the result is built when the server asks for it.
  internal InputRequiredException(Func<InputRequiredResult> factory)
    : base("The request needs input from the client.")
  {
    _factory = factory;
  }

  /// <summary>The result to send instead of the handler's.</summary>
  public InputRequiredResult Result => _result ??= _factory!();

  /// <summary>True when the server built the result from the asks the handler made; its <c>requestState</c> is then added and protected by the server.</summary>
  internal bool IsServerBuilt => _factory is not null;
}
