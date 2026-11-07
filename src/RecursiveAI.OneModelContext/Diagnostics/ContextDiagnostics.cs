using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Threading;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Diagnostics;

/// <summary>How a request ended, for the audit hook and for metrics.</summary>
public enum RequestOutcome
{
  /// <summary>A normal result.</summary>
  Success,

  /// <summary>A tool result with <c>isError</c> set (a tool failure, not a protocol error).</summary>
  ToolError,

  /// <summary>A JSON-RPC error response.</summary>
  ProtocolError,

  /// <summary>An <c>input_required</c> result: the client has to answer and call again.</summary>
  InputRequired,

  /// <summary>The request was cancelled and no response was sent.</summary>
  Cancelled,

  /// <summary>The handler threw an unexpected exception.</summary>
  Failed,
}

/// <summary>One audited request. It never contains arguments, results or tokens.</summary>
/// <param name="Principal">The authenticated caller the host supplied, or null (stdio, unauthenticated).</param>
/// <param name="ClientInfo">The client's self-reported name and version, or null if it sent none. Not authenticated.</param>
/// <param name="Method">The JSON-RPC method.</param>
/// <param name="Name">The tool, prompt or resource name or URI the request addressed, or null.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="ErrorCode">The JSON-RPC error code for <see cref="RequestOutcome.ProtocolError"/>, otherwise null.</param>
/// <param name="Era">The era of the codec that handled it, or null when none was selected.</param>
/// <param name="Duration">How long the server spent.</param>
public sealed record AuditRecord(
  ClaimsPrincipal? Principal,
  Implementation? ClientInfo,
  string Method,
  string? Name,
  RequestOutcome Outcome,
  int? ErrorCode,
  ProtocolEra? Era,
  TimeSpan Duration);

/// <summary>What a <see cref="DiagnosticEvent"/> reports.</summary>
public enum DiagnosticKind
{
  /// <summary>A handler threw an unexpected exception.</summary>
  HandlerFault,

  /// <summary>A message could not be read and was answered with an error.</summary>
  InvalidMessage,

  /// <summary>A codec was selected for a request.</summary>
  CodecSelected,

  /// <summary>The audit callback threw.</summary>
  AuditFault,

  /// <summary>An informational note from a protocol layer.</summary>
  Note,
}

/// <summary>An event for hosts that do not use OpenTelemetry. It never carries tokens or message bodies.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Message">A short description.</param>
/// <param name="Method">The JSON-RPC method, if one applies.</param>
/// <param name="Exception">The exception, if one applies.</param>
public sealed record DiagnosticEvent(DiagnosticKind Kind, string Message, string? Method = null, Exception? Exception = null);

/// <summary>
/// The library's telemetry. <see cref="Source"/> and <see cref="Meter"/> are named <c>RecursiveAI.OneModelContext</c>; they cost close
/// to nothing while nothing listens. <see cref="Subscribe"/> gives hosts without OpenTelemetry the same events as plain callbacks.
/// </summary>
public static class ContextDiagnostics
{
  /// <summary>The name of the activity source and the meter.</summary>
  public const string SourceName = "RecursiveAI.OneModelContext";

  private static readonly object Gate = new();
  private static Action<DiagnosticEvent>[] s_subscribers = [];

  /// <summary>Activities for requests (server spans and client spans).</summary>
  public static ActivitySource Source { get; } = new(SourceName);

  /// <summary>Instruments.</summary>
  public static Meter Meter { get; } = new(SourceName);

  /// <summary>Server-side request duration in seconds, tagged with method, era, outcome and error type.</summary>
  internal static Histogram<double> ServerDuration { get; } = Meter.CreateHistogram<double>("mcp.server.operation.duration", "s", "Duration of requests handled by the server.");

  /// <summary>Client-side request duration in seconds.</summary>
  internal static Histogram<double> ClientDuration { get; } = Meter.CreateHistogram<double>("mcp.client.operation.duration", "s", "Duration of requests made by the client.");

  /// <summary>Registers a callback for diagnostic events. Dispose the result to stop. Callbacks must not throw or block.</summary>
  /// <param name="callback">Receives each event on the thread that raised it.</param>
  public static IDisposable Subscribe(Action<DiagnosticEvent> callback)
  {
    ArgumentNullException.ThrowIfNull(callback);
    lock (Gate)
    {
      s_subscribers = [.. s_subscribers, callback];
    }

    return new Subscription(callback);
  }

  internal static bool HasSubscribers => Volatile.Read(ref s_subscribers).Length != 0;

  internal static void Publish(DiagnosticKind kind, string message, string? method = null, Exception? exception = null)
  {
    var subscribers = Volatile.Read(ref s_subscribers);
    if (subscribers.Length == 0)
    {
      return;
    }

    var diagnosticEvent = new DiagnosticEvent(kind, message, method, exception);
    foreach (var subscriber in subscribers)
    {
      try
      {
        subscriber(diagnosticEvent);
      }
      catch (Exception)
      {
        // A faulty subscriber must not break the library.
      }
    }
  }

  private sealed class Subscription(Action<DiagnosticEvent> callback) : IDisposable
  {
    private int _disposed;

    public void Dispose()
    {
      if (Interlocked.Exchange(ref _disposed, 1) != 0)
      {
        return;
      }

      lock (Gate)
      {
        var list = new System.Collections.Generic.List<Action<DiagnosticEvent>>(s_subscribers);
        list.Remove(callback);
        s_subscribers = [.. list];
      }
    }
  }
}
