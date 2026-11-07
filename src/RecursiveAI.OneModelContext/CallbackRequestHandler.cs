using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;

namespace RecursiveAI.OneModelContext;

/// <summary>
/// Adapts the typed callbacks on <see cref="ContextClientOptions"/> to the raw <see cref="IClientRequestHandler"/> the codecs call. A
/// request no callback covers goes to <see cref="ContextClientOptions.RequestHandler"/>, and without one it is method-not-found.
/// </summary>
internal sealed class CallbackRequestHandler(ContextClientOptions options) : IClientRequestHandler
{
  /// <summary>The handler to use for these options, or null when the application supplied nothing.</summary>
  internal static IClientRequestHandler? For(ContextClientOptions options) =>
    options.OnElicit is null && options.OnConfirm is null && options.OnSample is null && options.OnListRoots is null
      ? options.RequestHandler
      : new CallbackRequestHandler(options);

  /// <summary>The capabilities to announce: those the application listed, plus one for each callback it set.</summary>
  internal static ClientCapabilities Declare(ContextClientOptions options)
  {
    var listed = options.Capabilities;
    var elicits = options.OnElicit is not null || options.OnConfirm is not null;
    if ((!elicits || listed.Elicitation is not null) && (options.OnSample is null || listed.Sampling is not null) && (options.OnListRoots is null || listed.Roots is not null) && options.Extensions.Count == 0)
    {
      return listed;
    }

    // Extensions the host opted in to are added to any it listed by hand; the options win for an identifier given twice.
    var extensions = listed.Extensions;
    if (options.Extensions.Count > 0)
    {
      extensions = listed.Extensions is null ? new Dictionary<string, JsonElement>(StringComparer.Ordinal) : new Dictionary<string, JsonElement>(listed.Extensions, StringComparer.Ordinal);
      foreach (var extension in options.Extensions)
      {
        extensions[extension.Id] = extension.Settings;
      }
    }

    return new ClientCapabilities
    {
      Experimental = listed.Experimental,
      Roots = listed.Roots ?? (options.OnListRoots is null ? null : new RootsCapability()),
      Sampling = listed.Sampling ?? (options.OnSample is null ? null : new SamplingCapability()),
      Elicitation = listed.Elicitation ?? (elicits ? new ElicitationCapability() : null),
      Tasks = listed.Tasks,
      Extensions = extensions,
      AdditionalProperties = listed.AdditionalProperties,
    };
  }

  public async ValueTask<Result> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
  {
    switch (method)
    {
      case Methods.ElicitationCreate when options.OnElicit is not null || options.OnConfirm is not null:
        var question = parameters.Deserialize(WireJsonContext.Default.ElicitRequestParams)
          ?? throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "elicitation/create needs parameters.");
        if (question.IsConfirmation && options.OnConfirm is { } confirm)
        {
          return ElicitResult.Confirmation(await confirm(question.Message, cancellationToken).ConfigureAwait(false));
        }

        // No UI for a general form: say so by declining rather than failing the tool call.
        return options.OnElicit is { } elicit ? await elicit(question, cancellationToken).ConfigureAwait(false) : ElicitResult.Decline();
      case Methods.SamplingCreateMessage when options.OnSample is { } sample:
        var request = parameters.Deserialize(WireJsonContext.Default.CreateMessageRequestParams)
          ?? throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "sampling/createMessage needs parameters.");
        return await sample(request, cancellationToken).ConfigureAwait(false);
      case Methods.RootsList when options.OnListRoots is { } roots:
        return await roots(cancellationToken).ConfigureAwait(false);
      default:
        return options.RequestHandler is { } fallback
          ? await fallback.HandleAsync(method, parameters, cancellationToken).ConfigureAwait(false)
          : throw new ProtocolException(JsonRpcErrorCodes.MethodNotFound, "Method not found: " + method + ".");
    }
  }
}
