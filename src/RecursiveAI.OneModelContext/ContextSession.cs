using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;

namespace RecursiveAI.OneModelContext;

/// <summary>
/// One peer of a <see cref="ContextServer"/> that talks in whole JSON-RPC messages rather than over a stream: a browser tab's message
/// port, a WebSocket handler, a test. It remembers what the peer's handshake settled (the legacy <c>initialize</c> negotiation), so use
/// one session per peer and the same session for all of that peer's messages. Modern requests carry everything they need and leave it unchanged.
/// </summary>
/// <example>
/// <code>
/// var page = server.OpenSession();
/// string? reply = await page.HandleAsync(messageFromThePage);
/// if (reply is not null) SendToPage(reply);
/// </code>
/// </example>
public sealed class ContextSession
{
  private readonly ContextServer _server;
  private readonly ServerSessionState _state = new();
  private readonly IServerSink? _sink;
  private readonly ClaimsPrincipal? _principal;

  internal ContextSession(ContextServer server, IServerSink? sink, ClaimsPrincipal? principal)
  {
    _server = server;
    _sink = sink;
    _principal = principal;
  }

  /// <summary><see cref="ProtocolEra.Legacy"/> once the peer has sent <c>initialize</c>; null otherwise, because modern requests are stateless and leave the session unchanged.</summary>
  public ProtocolEra? Era => _state.Era;

  /// <summary>Legacy: the revision <c>initialize</c> negotiated, or null.</summary>
  public string? NegotiatedVersion => _state.NegotiatedVersion;

  /// <summary>Handles one JSON-RPC message (a batch where the negotiated revision allows it) and returns the reply.</summary>
  /// <param name="message">The message text.</param>
  /// <param name="cancellationToken">Cancels the handler.</param>
  /// <returns>The reply text, or null for a notification or a response. Bad input never throws: it returns a JSON-RPC error.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
  public ValueTask<string?> HandleAsync(string message, CancellationToken cancellationToken = default) =>
    _server.HandleMessageAsync(message, _state, _sink, _principal, cancellationToken);
}

public sealed partial class ContextServer
{
  /// <summary>Starts a session for a peer that talks in whole messages; see <see cref="ContextSession"/>.</summary>
  /// <param name="sink">
  /// Where notifications (list changes, progress) and, on legacy sessions, server-initiated requests go. <c>null</c> drops notifications and
  /// refuses server-initiated requests.
  /// </param>
  /// <param name="principal">The authenticated caller, if the host knows one.</param>
  public ContextSession OpenSession(IServerSink? sink = null, ClaimsPrincipal? principal = null) => new(this, sink, principal);
}
