using System;
using System.Collections.Generic;

namespace RecursiveAI.OneModelContext.Hosting;

/// <summary>
/// Settings of a <see cref="StreamableHttpEndpoint"/>. Every limit is a default you can change; the defaults suit a server that listens
/// on a loopback address.
/// </summary>
public sealed class StreamableHttpOptions
{
  /// <summary>
  /// Makes the endpoint an OAuth resource server: it serves the protected resource metadata, requires a valid bearer token on every
  /// request (401 with a <c>WWW-Authenticate</c> challenge otherwise), and enforces per-tool, per-resource and per-prompt
  /// <c>RequiredScopes</c> with 403 <c>insufficient_scope</c>. A caller the host already authenticated (<see cref="Http.IHttpExchange.User"/>,
  /// for example by the ASP.NET Core authentication handler) is not asked for a token again, but its scopes are still checked.
  /// Default null: the endpoint does not authenticate.
  /// </summary>
  public Auth.ResourceServer? ResourceServer { get; init; }

  /// <summary>
  /// Origins allowed in addition to loopback origins, written as the browser sends them (<c>https://app.example.com</c>, no trailing
  /// slash), compared case-insensitively and in full. <c>*</c> allows every origin, which switches off the defence against DNS
  /// rebinding; use it only behind your own checks. Default: none.
  /// </summary>
  public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

  /// <summary>
  /// Whether an <c>Origin</c> whose host is <c>localhost</c> or a loopback address (any port) is allowed. A request with no
  /// <c>Origin</c> is not a browser request and is always allowed. Default true.
  /// </summary>
  public bool AllowLoopbackOrigins { get; init; } = true;

  /// <summary>
  /// Host names (<c>example.com</c> or <c>example.com:8080</c>) the <c>Host</c> header may carry in addition to loopback names, compared
  /// case-insensitively. A name without a port matches any port. <c>*</c> allows every host. A same-origin request after a DNS rebinding
  /// attack carries no <c>Origin</c>, so the host check is what stops it. Default: none (loopback names only).
  /// </summary>
  public IReadOnlyList<string> AllowedHosts { get; init; } = [];

  /// <summary>Whether the <c>Host</c> header is checked at all. Default true; turn it off only when a proxy in front already enforces it.</summary>
  public bool ValidateHost { get; init; } = true;

  /// <summary>Largest request body accepted, in bytes. A larger body gets 413. Default 8 MiB.</summary>
  public int MaxRequestBodyBytes { get; init; } = 8 * 1024 * 1024;

  /// <summary>How long reading one request body may take. A slower sender gets 408. Default 30 seconds.</summary>
  public TimeSpan BodyReadTimeout { get; init; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// How long one write to a response stream may block before the stream is dropped. A client that stops reading cannot hold a handler
  /// forever. Default 30 seconds.
  /// </summary>
  public TimeSpan SseWriteTimeout { get; init; } = TimeSpan.FromSeconds(30);

  /// <summary>How often an idle event stream gets a comment line so that intermediaries keep it open. Default 15 seconds; zero turns it off.</summary>
  public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(15);

  /// <summary>Most legacy sessions held at once. More initialize requests get 503. Default 1000.</summary>
  public int MaxSessions { get; init; } = 1000;

  /// <summary>How long a legacy session may go without a request or an open stream before it is dropped. Default 30 minutes.</summary>
  public TimeSpan SessionIdleTimeout { get; init; } = TimeSpan.FromMinutes(30);

  /// <summary>
  /// Most events a legacy stream keeps for <c>Last-Event-ID</c> replay. Older events are dropped, so a client that was away longer than
  /// this many events misses some. Default 256.
  /// </summary>
  public int MaxReplayEvents { get; init; } = 256;

  /// <summary>Most bytes of event data one legacy stream keeps for replay. Default 4 MiB.</summary>
  public int MaxReplayBytes { get; init; } = 4 * 1024 * 1024;

  /// <summary>Most event streams one legacy session keeps for resumption. The oldest finished streams go first. Default 16.</summary>
  public int MaxStreamsPerSession { get; init; } = 16;
}
