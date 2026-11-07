using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;

namespace RecursiveAI.OneModelContext;

/// <summary>An elicitation answer with the submitted values read into <typeparamref name="T"/>.</summary>
/// <typeparam name="T">The type of the form values.</typeparam>
/// <param name="Action"><c>accept</c>, <c>decline</c> or <c>cancel</c>.</param>
/// <param name="Content">The values when accepted, otherwise null.</param>
public sealed record ElicitResult<T>(string Action, T? Content)
{
  /// <summary>True when the user accepted.</summary>
  public bool Accepted => Action == "accept";
}

/// <summary>
/// What a handler sees of the request it is serving: cancellation, who is calling, how to report progress, and how to ask the
/// client for input. The same code runs on both eras.
/// </summary>
/// <remarks>
/// Asking for input: on a legacy connection the ask is a real await on a server-initiated request. On a modern connection a handler that
/// asks is run again from the start when the client retries with the answer; the ask then returns it. Code before an ask must be safe to
/// repeat, and anything that must not be recomputed goes in <see cref="State"/>, which travels in the signed <c>requestState</c>.
/// </remarks>
public sealed class RequestContext
{
  private readonly IServerSink _sink;
  private readonly IInputChannel _input;

  internal RequestContext(
    ContextServer server,
    string method,
    string? name,
    PeerContext peer,
    IServerSink sink,
    IInputChannel input,
    ClaimsPrincipal? principal,
    CancellationToken cancellationToken)
  {
    Server = server;
    Method = method;
    Name = name;
    Peer = peer;
    _sink = sink;
    _input = input;
    Principal = principal;
    CancellationToken = cancellationToken;
    State = peer.RestoredState is null
      ? new Dictionary<string, string>(StringComparer.Ordinal)
      : new Dictionary<string, string>(peer.RestoredState, StringComparer.Ordinal);
  }

  /// <summary>The server handling the request.</summary>
  public ContextServer Server { get; }

  /// <summary>The JSON-RPC method.</summary>
  public string Method { get; }

  /// <summary>The tool or prompt name or resource URI, when the request addresses one.</summary>
  public string? Name { get; }

  /// <summary>Cancelled when the client cancels the request or the connection closes. Pass it to everything that waits.</summary>
  public CancellationToken CancellationToken { get; }

  /// <summary>The era of the connection. Handlers rarely need it.</summary>
  public ProtocolEra Era => Peer.Era;

  /// <summary>The protocol revision in force.</summary>
  public string ProtocolVersion => Peer.ProtocolVersion;

  /// <summary>The client's self-reported name and version; not authenticated.</summary>
  public Implementation? ClientInfo => Peer.ClientInfo;

  /// <summary>What the client supports.</summary>
  public ClientCapabilities ClientCapabilities => Peer.ClientCapabilities;

  /// <summary>The authenticated caller, supplied by the host that accepted the request; null on stdio and unauthenticated hosts.</summary>
  public ClaimsPrincipal? Principal { get; }

  /// <summary>
  /// Values that must survive a modern input round trip without being recomputed. They are sent to the client as protected
  /// <c>requestState</c> (signed, not readable by contract) and restored on the retry. Keep them small; legacy connections never lose them.
  /// </summary>
  public IDictionary<string, string> State { get; }

  /// <summary>What the codec learned about the peer.</summary>
  internal PeerContext Peer { get; }

  /// <summary>Reports progress to a client that asked for it with a progress token; does nothing otherwise.</summary>
  /// <param name="progress">Work done so far; must increase with each call.</param>
  /// <param name="total">Total work, if known.</param>
  /// <param name="message">A short status.</param>
  public ValueTask ReportProgressAsync(double progress, double? total = null, string? message = null)
  {
    if (Peer.ProgressToken is not { } token)
    {
      return ValueTask.CompletedTask;
    }

    var parameters = new ProgressNotificationParams { ProgressToken = token, Progress = progress, Total = total, Message = message };
    var element = JsonSerializer.SerializeToElement(parameters, WireJsonContext.Default.ProgressNotificationParams);
    return _sink.SendNotificationAsync(new JsonRpcNotification(Methods.NotificationsProgress, element), CancellationToken);
  }

  /// <summary>Asks the user a yes or no question through the client.</summary>
  /// <param name="message">The question.</param>
  /// <returns>True when the user answered yes.</returns>
  /// <exception cref="MissingCapabilityException">The client cannot elicit.</exception>
  public async ValueTask<bool> ConfirmAsync(string message)
  {
    ArgumentNullException.ThrowIfNull(message);
    using var schema = JsonDocument.Parse("""{"type":"object","properties":{"confirm":{"type":"boolean","title":"Confirm","default":false}},"required":["confirm"]}""");
    var result = await ElicitAsync(message, schema.RootElement).ConfigureAwait(false);
    return result.Action == "accept"
      && result.Content is { } values
      && values.TryGetValue("confirm", out var confirmed)
      && confirmed.ValueKind == JsonValueKind.True;
  }

  /// <summary>Asks the user for values described by a flat form schema.</summary>
  /// <param name="message">What to ask.</param>
  /// <param name="requestedSchema">An object schema of primitive properties.</param>
  /// <exception cref="MissingCapabilityException">The client cannot elicit.</exception>
  public async ValueTask<ElicitResult> ElicitAsync(string message, JsonElement requestedSchema)
  {
    ArgumentNullException.ThrowIfNull(message);
    if (ClientCapabilities.Elicitation is null)
    {
      throw new MissingCapabilityException(
        "The client does not support elicitation.",
        new ClientCapabilities { Elicitation = new ElicitationCapability() });
    }

    var parameters = new ElicitRequestParams { Message = message, RequestedSchema = requestedSchema };
    var element = JsonSerializer.SerializeToElement(parameters, WireJsonContext.Default.ElicitRequestParams);
    var answer = await _input.RequestInputAsync(Methods.ElicitationCreate, element, CancellationToken).ConfigureAwait(false);
    try
    {
      return answer.Deserialize(WireJsonContext.Default.ElicitResult)
        ?? throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "The elicitation answer is empty.");
    }
    catch (JsonException ex)
    {
      throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "The elicitation answer is not valid.", default, ex);
    }
  }

  /// <summary>Asks the user for values and reads them into <typeparamref name="T"/>.</summary>
  /// <typeparam name="T">The form's value type.</typeparam>
  /// <param name="message">What to ask.</param>
  /// <param name="requestedSchema">The form schema describing <typeparamref name="T"/> (the generator emits it for generated tools).</param>
  /// <param name="typeInfo">Source-generated metadata for <typeparamref name="T"/>.</param>
  public async ValueTask<ElicitResult<T>> ElicitAsync<T>(string message, JsonElement requestedSchema, JsonTypeInfo<T> typeInfo)
  {
    ArgumentNullException.ThrowIfNull(typeInfo);
    var result = await ElicitAsync(message, requestedSchema).ConfigureAwait(false);
    if (result.Action != "accept" || result.Content is null)
    {
      return new ElicitResult<T>(result.Action, default);
    }

    try
    {
      var content = JsonSerializer.SerializeToElement(result.Content, WireJsonContext.Default.DictionaryStringJsonElement).Deserialize(typeInfo);
      return new ElicitResult<T>(result.Action, content);
    }
    catch (JsonException ex)
    {
      throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "The elicitation answer does not match the expected form.", default, ex);
    }
  }

  /// <summary>
  /// Sends a log message to the client. On modern connections it is sent only when the request asked for a level at or below
  /// <paramref name="level"/>; on legacy connections it follows <c>logging/setLevel</c>.
  /// </summary>
  /// <param name="level">One of <see cref="LoggingLevels"/>.</param>
  /// <param name="message">The text.</param>
  /// <param name="logger">The logger name.</param>
  public ValueTask LogAsync(string level, string message, string? logger = null)
  {
    ArgumentNullException.ThrowIfNull(level);
    ArgumentNullException.ThrowIfNull(message);
    if (!LogLevelFilter.Passes(Peer.LogLevel, level))
    {
      return ValueTask.CompletedTask;
    }

    var data = JsonSerializer.SerializeToElement(message, WireJsonContext.Default.String);
    var parameters = new LoggingMessageNotificationParams { Level = level, Logger = logger, Data = data };
    var element = JsonSerializer.SerializeToElement(parameters, WireJsonContext.Default.LoggingMessageNotificationParams);
    return _sink.SendNotificationAsync(new JsonRpcNotification(Methods.NotificationsMessage, element), CancellationToken);
  }
}

/// <summary>Orders RFC 5424 log levels.</summary>
internal static class LogLevelFilter
{
  private static readonly string[] Order =
  [
    LoggingLevels.Debug, LoggingLevels.Info, LoggingLevels.Notice, LoggingLevels.Warning,
    LoggingLevels.Error, LoggingLevels.Critical, LoggingLevels.Alert, LoggingLevels.Emergency,
  ];

  /// <summary>True when a message at <paramref name="level"/> should be sent to a peer that wants <paramref name="minimum"/> and above. No minimum means send nothing.</summary>
  internal static bool Passes(string? minimum, string level)
  {
    if (minimum is null)
    {
      return false;
    }

    var min = Array.IndexOf(Order, minimum);
    var at = Array.IndexOf(Order, level);
    return min >= 0 && at >= min;
  }
}
