using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext;

/// <summary>
/// Settings of the Streamable HTTP client transport (<see cref="ContextTransport.Http"/>). Every limit is a default you can change.
/// </summary>
public sealed class HttpTransportOptions
{
  /// <summary>
  /// Headers sent with every request to the server, such as an API key header the server expects. Never put credentials in the URL.
  /// A value is sent to the server's origin only; the client never follows a redirect on its own. Default: none.
  /// </summary>
  public IReadOnlyDictionary<string, string>? Headers { get; init; }

  /// <summary>
  /// Called with every request just before it is sent (POST, GET and DELETE), so a credential can be added, for example an
  /// <c>Authorization</c> bearer token. The request goes to the server's origin only. The library never logs the headers of a request or
  /// puts them in an exception message. Default: none.
  /// </summary>
  public Func<HttpRequestMessage, CancellationToken, ValueTask>? AuthorizeRequest { get; init; }

  /// <summary>
  /// Fall back to the deprecated HTTP+SSE transport (2024-11-05) when the server answers a first POST with 400, 404 or 405 and no
  /// JSON-RPC error, by opening the URL as an event stream and waiting for the <c>endpoint</c> event (2025-03-26 transports,
  /// "Backwards compatibility"). Default <c>true</c>.
  /// </summary>
  public bool AllowSseFallback { get; init; } = true;

  /// <summary>
  /// Open the standalone GET event stream on a legacy session (2025-03-26 to 2025-11-25) for messages the server starts, such as
  /// sampling requests and list changes. Default <c>true</c>. A server that answers 405 has none, and the client stops asking.
  /// </summary>
  public bool OpenListeningStream { get; init; } = true;

  /// <summary>The largest single event, in bytes, of an event stream. A larger one ends that stream. Default 8 MiB.</summary>
  public int MaxEventBytes { get; init; } = 8 * 1024 * 1024;

  /// <summary>The largest <c>application/json</c> response body accepted, in bytes. A larger one fails that request. Default 16 MiB.</summary>
  public int MaxResponseBodyBytes { get; init; } = 16 * 1024 * 1024;

  /// <summary>
  /// How long an event stream may stay completely silent (not even a comment line) before the client treats it as broken. Servers should
  /// send keep-alive comments more often than this. Default 5 minutes.
  /// </summary>
  public TimeSpan StreamIdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

  /// <summary>
  /// Most times a legacy event stream is resumed with <c>Last-Event-ID</c> in a row, without an event arriving in between, before the
  /// client gives up. Default 5.
  /// </summary>
  public int MaxResumeAttempts { get; init; } = 5;

  /// <summary>How long to wait before resuming a stream when the server sent no <c>retry</c> value. Default 1 second.</summary>
  public TimeSpan DefaultRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

  /// <summary>The longest wait before resuming a stream, whatever <c>retry</c> the server asks for. Default 30 seconds.</summary>
  public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// How many times a 2026-07-28 request is sent again, as a new request with a new id, when its response stream breaks before the
  /// response arrived. The revision has no resumption; closing a stream cancels the request. A request that is not safe to repeat should
  /// carry its own idempotency key. Default 2; zero turns it off.
  /// </summary>
  public int MaxReissues { get; init; } = 2;

  /// <summary>How long waiting for the <c>endpoint</c> event of the HTTP+SSE fallback may take. Default 10 seconds.</summary>
  public TimeSpan SseFallbackTimeout { get; init; } = TimeSpan.FromSeconds(10);

  /// <summary>How long closing a legacy session (the DELETE request) may take. Default 5 seconds.</summary>
  public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);

  /// <summary>
  /// Most received messages waiting to be read. When the application falls behind, reading from the network pauses instead of
  /// growing memory. Default 256.
  /// </summary>
  public int MaxQueuedMessages { get; init; } = 256;

  /// <summary>The clock for waits and timeouts. Default <see cref="TimeProvider.System"/>.</summary>
  public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
