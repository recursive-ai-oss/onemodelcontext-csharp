using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext;

// Calls that exist on legacy connections only: ping, logging level, resource subscriptions, roots changes.
public sealed partial class ContextClient
{
  /// <summary>Checks that the server answers (<c>ping</c>). Legacy revisions only.</summary>
  /// <param name="cancellationToken">Cancels the call.</param>
  /// <exception cref="ContextException">The connection negotiated 2026-07-28, which has no ping.</exception>
  public async ValueTask PingAsync(CancellationToken cancellationToken = default)
  {
    RequireLegacy("ping");
    await CallAsync<EmptyResult>(Methods.Ping, new RequestParams(), cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Asks the server to send log messages at or above <paramref name="level"/> (<c>logging/setLevel</c>). Legacy revisions only.</summary>
  /// <param name="level">One of <see cref="LoggingLevels"/>.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  /// <remarks>Messages arrive through <see cref="NotificationReceived"/> as <c>notifications/message</c>.</remarks>
  /// <exception cref="ContextException">The connection negotiated 2026-07-28; send the level with each request there.</exception>
  [Obsolete(DeprecatedFeatures.Logging, DiagnosticId = DeprecatedFeatures.LoggingId)]
  public async ValueTask SetLoggingLevelAsync(string level, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(level);
    RequireLegacy("logging/setLevel");
    await CallAsync<EmptyResult>(Methods.LoggingSetLevel, new SetLevelRequestParams { Level = level }, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Asks to be told when a resource changes (<c>resources/subscribe</c>); updates arrive through <see cref="NotificationReceived"/>. Legacy revisions only.</summary>
  /// <param name="uri">The resource URI.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  /// <exception cref="ProtocolException">The server has no resources, or refused.</exception>
  public async ValueTask SubscribeResourceAsync(string uri, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(uri);
    RequireLegacy("resources/subscribe");
    await CallAsync<EmptyResult>(Methods.ResourcesSubscribe, new ResourceSubscriptionParams { Uri = uri }, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Ends a subscription made with <see cref="SubscribeResourceAsync"/> (<c>resources/unsubscribe</c>). Legacy revisions only.</summary>
  /// <param name="uri">The resource URI.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  public async ValueTask UnsubscribeResourceAsync(string uri, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(uri);
    RequireLegacy("resources/unsubscribe");
    await CallAsync<EmptyResult>(Methods.ResourcesUnsubscribe, new ResourceSubscriptionParams { Uri = uri }, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Tells the server the client's roots changed (<c>notifications/roots/list_changed</c>). Legacy revisions only; does nothing on modern.</summary>
  /// <param name="cancellationToken">Cancels the send.</param>
  [Obsolete(DeprecatedFeatures.Roots, DiagnosticId = DeprecatedFeatures.RootsId)]
  public ValueTask NotifyRootsListChangedAsync(CancellationToken cancellationToken = default) =>
    Era == ProtocolEra.Legacy ? _rpc.SendNotificationAsync(Methods.NotificationsRootsListChanged, default, cancellationToken) : ValueTask.CompletedTask;

  private void RequireLegacy(string method)
  {
    if (Era != ProtocolEra.Legacy)
    {
      throw new ContextException("'" + method + "' does not exist in protocol revision " + ProtocolVersion + ".");
    }
  }
}
