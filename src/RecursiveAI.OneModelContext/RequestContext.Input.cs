using System;
using System.Text.Json;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext;

// Asking the client for more than a form: URL-mode elicitation, sampling and roots. Each is a real awaited request on a legacy
// connection and follows the multi round-trip rule on a modern one.
public sealed partial class RequestContext
{
  /// <summary>Sends the user to a web page to finish something that must not pass through the client (2025-11-25 URL-mode elicitation).</summary>
  /// <param name="message">Why the user is being sent there.</param>
  /// <param name="url">The page to open.</param>
  /// <param name="elicitationId">An id you choose; later pass it to <see cref="NotifyElicitationCompleteAsync"/> when the page's flow finishes.</param>
  /// <returns>The user's consent to open the page: <c>accept</c>, <c>decline</c> or <c>cancel</c>. Accepting does not mean the flow finished.</returns>
  /// <exception cref="MissingCapabilityException">The client did not declare URL-mode elicitation.</exception>
  public async ValueTask<ElicitResult> ElicitUrlAsync(string message, string url, string elicitationId)
  {
    ArgumentNullException.ThrowIfNull(message);
    ArgumentNullException.ThrowIfNull(url);
    ArgumentNullException.ThrowIfNull(elicitationId);
    var parameters = new ElicitRequestParams { Mode = "url", Message = message, Url = url, ElicitationId = elicitationId };
    var element = JsonSerializer.SerializeToElement(parameters, WireJsonContext.Default.ElicitRequestParams);
    var answer = await _input.RequestInputAsync(Methods.ElicitationCreate, element, CancellationToken).ConfigureAwait(false);
    return Decode(answer, WireJsonContext.Default.ElicitResult, "elicitation");
  }

  /// <summary>
  /// Tells the client that the out-of-band flow started by <see cref="ElicitUrlAsync"/> finished (<c>notifications/elicitation/complete</c>).
  /// Does nothing unless the connection negotiated 2025-11-25 and the client declared URL-mode elicitation.
  /// </summary>
  /// <param name="elicitationId">The id given to <see cref="ElicitUrlAsync"/>.</param>
  public ValueTask NotifyElicitationCompleteAsync(string elicitationId)
  {
    ArgumentNullException.ThrowIfNull(elicitationId);
    if (ProtocolVersion != ProtocolVersions.V20251125 || ClientCapabilities.Elicitation?.Url.ValueKind is null or JsonValueKind.Undefined)
    {
      return ValueTask.CompletedTask;
    }

    var element = JsonSerializer.SerializeToElement(new ElicitationCompleteNotificationParams { ElicitationId = elicitationId }, WireJsonContext.Default.ElicitationCompleteNotificationParams);
    return _sink.SendNotificationAsync(new JsonRpcNotification(Methods.NotificationsElicitationComplete, element), CancellationToken);
  }

  private static T Decode<T>(JsonElement answer, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, string what)
    where T : class
  {
    try
    {
      return answer.Deserialize(info) ?? throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "The " + what + " answer is empty.");
    }
    catch (JsonException ex)
    {
      throw new ProtocolException(JsonRpcErrorCodes.InvalidParams, "The " + what + " answer is not valid.", default, ex);
    }
  }
}
