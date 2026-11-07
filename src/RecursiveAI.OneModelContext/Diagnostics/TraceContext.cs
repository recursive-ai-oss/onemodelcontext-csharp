using System;
using System.Diagnostics;
using System.Text.Json;

namespace RecursiveAI.OneModelContext.Diagnostics;

/// <summary>
/// Trace context in <c>_meta</c> (SEP-414, 2026-07-28 basic: <c>traceparent</c>, <c>tracestate</c> and <c>baggage</c> follow W3C Trace
/// Context). The client writes the current activity; the server makes it the parent of its request span.
/// </summary>
internal static class TraceContext
{
  /// <summary>The current activity as W3C headers, when there is one.</summary>
  internal static bool TryGetCurrent(out string traceParent, out string? traceState)
  {
    var current = Activity.Current;
    if (current is null || current.IdFormat != ActivityIdFormat.W3C || current.Id is null)
    {
      traceParent = string.Empty;
      traceState = null;
      return false;
    }

    traceParent = current.Id;
    traceState = current.TraceStateString;
    return true;
  }

  /// <summary>Reads the parent from a request's params. Malformed values are ignored: a bad header never fails a request.</summary>
  internal static bool TryRead(JsonElement parameters, out ActivityContext context)
  {
    context = default;
    if (parameters.ValueKind != JsonValueKind.Object
      || !parameters.TryGetProperty("_meta", out var meta)
      || meta.ValueKind != JsonValueKind.Object
      || !meta.TryGetProperty("traceparent", out var parent)
      || parent.ValueKind != JsonValueKind.String)
    {
      return false;
    }

    string? state = null;
    if (meta.TryGetProperty("tracestate", out var stateElement) && stateElement.ValueKind == JsonValueKind.String)
    {
      state = stateElement.GetString();
    }

    return ActivityContext.TryParse(parent.GetString(), state, isRemote: true, out context);
  }
}
