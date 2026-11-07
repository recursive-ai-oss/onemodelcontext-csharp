using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Wire.Modern;

/// <summary>An input channel that can say which asks are still waiting for an answer.</summary>
internal interface IPendingInput
{
  /// <summary>The asks made in this attempt that the client has not answered, as an <c>input_required</c> result without state.</summary>
  bool TryTakePending(out InputRequiredResult result);
}

/// <summary>
/// The 2026-07-28 way to ask the client for something in the middle of a request (basic/patterns/mrtr). An ask is answered from
/// <c>inputResponses</c> when the retry carries the answer; otherwise it is remembered, and the attempt ends with <c>input_required</c>.
/// </summary>
/// <remarks>
/// Asks are keyed <c>input-1</c>, <c>input-2</c>… in the order the handler makes them. The handler runs again from the start on a retry, so
/// the same asks come in the same order and find their answers. An ask that fails returns a faulted task instead of throwing, so a handler
/// that starts several asks together (<c>Task.WhenAll</c>) puts all of them in one round.
/// </remarks>
internal sealed class ModernInputChannel(PeerContext peer) : IInputChannel, IPendingInput
{
  private readonly object _gate = new();
  private readonly List<KeyValuePair<string, InputRequest>> _pending = [];
  private int _sequence;

  public ValueTask<JsonElement> RequestInputAsync(string method, JsonElement parameters, CancellationToken cancellationToken, string? key = null)
  {
    // 2026-07-28 mrtr, server requirement 7: never ask for what the client has not declared.
    var missing = method switch
    {
      Methods.ElicitationCreate when peer.ClientCapabilities.Elicitation is null => new ClientCapabilities { Elicitation = new ElicitationCapability() },
      Methods.SamplingCreateMessage when peer.ClientCapabilities.Sampling is null => new ClientCapabilities { Sampling = new SamplingCapability() },
      Methods.RootsList when peer.ClientCapabilities.Roots is null => new ClientCapabilities { Roots = new RootsCapability() },
      _ => null,
    };

    if (missing is not null)
    {
      return ValueTask.FromException<JsonElement>(new MissingCapabilityException("The client did not declare the capability this request needs.", missing));
    }

    // A handler-chosen key must be the same on every attempt; the numbered default is stable because the handler asks in the same order.
    key ??= "input-" + Interlocked.Increment(ref _sequence).ToString(CultureInfo.InvariantCulture);
    if (peer.InputResponses is { } answers && answers.TryGetValue(key, out var answer))
    {
      return ValueTask.FromResult(answer);
    }

    lock (_gate)
    {
      _pending.Add(new KeyValuePair<string, InputRequest>(key, new InputRequest { Method = method, Params = parameters }));
    }

    return ValueTask.FromException<JsonElement>(new InputRequiredException(BuildPending));
  }

  public bool TryTakePending(out InputRequiredResult result)
  {
    lock (_gate)
    {
      if (_pending.Count == 0)
      {
        result = null!;
        return false;
      }
    }

    result = BuildPending();
    return true;
  }

  private InputRequiredResult BuildPending()
  {
    var requests = new Dictionary<string, InputRequest>(StringComparer.Ordinal);
    lock (_gate)
    {
      foreach (var (key, request) in _pending)
      {
        requests[key] = request;
      }
    }

    return new InputRequiredResult { ResultType = WireNames.ResultTypeInputRequired, InputRequests = requests };
  }
}
