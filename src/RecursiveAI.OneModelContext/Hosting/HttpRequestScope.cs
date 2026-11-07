using System;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Http;

namespace RecursiveAI.OneModelContext.Hosting;

/// <summary>
/// The HTTP request a handler is running for, available to code that runs inside <see cref="StreamableHttpEndpoint"/>. It lets a handler
/// use features of the transport that the protocol model does not have, such as ending the response stream early.
/// </summary>
public sealed class HttpRequestScope
{
  private static readonly AsyncLocal<HttpRequestScope?> s_current = new();
  private readonly Func<TimeSpan, CancellationToken, ValueTask> _closeConnection;

  internal HttpRequestScope(IHttpExchange exchange, Func<TimeSpan, CancellationToken, ValueTask> closeConnection)
  {
    Exchange = exchange;
    _closeConnection = closeConnection;
  }

  /// <summary>The scope of the request being handled on this logical thread of execution, or null outside a handler or on another transport.</summary>
  public static HttpRequestScope? Current => s_current.Value;

  /// <summary>The HTTP exchange. A handler may read its headers; it must not write to the response or read the body.</summary>
  public IHttpExchange Exchange { get; }

  /// <summary>
  /// Ends the HTTP connection that carries this request's event stream without ending the request: the client is told when to reconnect
  /// (<c>retry</c>) and then polls with <c>Last-Event-ID</c> to collect the rest (2025-11-25 Streamable HTTP, "Sending Messages to the
  /// Server"). It has an effect only on legacy sessions, whose streams are resumable; on a modern request it does nothing, because
  /// closing a 2026-07-28 response stream cancels the request.
  /// </summary>
  /// <param name="retryAfter">How long the client should wait before it reconnects.</param>
  /// <param name="cancellationToken">Cancels the call.</param>
  public ValueTask CloseSseConnectionAsync(TimeSpan retryAfter, CancellationToken cancellationToken = default) =>
    _closeConnection(retryAfter, cancellationToken);

  internal static IDisposable Enter(HttpRequestScope scope)
  {
    var previous = s_current.Value;
    s_current.Value = scope;
    return new Restore(previous);
  }

  private sealed class Restore(HttpRequestScope? previous) : IDisposable
  {
    public void Dispose() => s_current.Value = previous;
  }
}
