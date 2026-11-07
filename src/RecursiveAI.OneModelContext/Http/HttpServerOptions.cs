using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace RecursiveAI.OneModelContext.Http;

/// <summary>
/// Settings of an <see cref="HttpServer"/>. Every limit is a default you can change. The defaults suit a server that listens on a
/// loopback address for local tools; a server reachable from other machines should also set <see cref="Certificate"/> or
/// <see cref="TrustedProxies"/>.
/// </summary>
public sealed class HttpServerOptions
{
  /// <summary>
  /// The address to listen on. Default: the IPv4 loopback address. Any other address is an explicit choice and publishes a
  /// <see cref="Diagnostics.DiagnosticKind.Warning"/> unless <see cref="Certificate"/> or <see cref="TrustedProxies"/> is set.
  /// </summary>
  public IPAddress Address { get; init; } = IPAddress.Loopback;

  /// <summary>The TCP port. Default 0, which picks a free port; read it back from <see cref="HttpServer.LocalEndPoint"/>.</summary>
  public int Port { get; init; }

  /// <summary>The server certificate. When set, every connection is TLS (HTTP/1.1 only). Default null: plain HTTP.</summary>
  public X509Certificate2? Certificate { get; init; }

  /// <summary>The TLS versions accepted. Default <see cref="SslProtocols.None"/>, which lets the operating system choose its secure defaults.</summary>
  public SslProtocols SslProtocols { get; init; } = SslProtocols.None;

  /// <summary>
  /// Addresses of reverse proxies whose <c>X-Forwarded-For</c>, <c>X-Forwarded-Proto</c> and <c>X-Forwarded-Host</c> headers are
  /// believed. For any other peer those headers (and <c>Forwarded</c>) are removed from the request, so a forged value never reaches
  /// a handler. For a trusted peer the client address is the first address from the right in <c>X-Forwarded-For</c> that is not
  /// itself a trusted proxy. Default: none.
  /// </summary>
  public IReadOnlyList<IPAddress> TrustedProxies { get; init; } = [];

  /// <summary>Most connections served at once. Further connections wait in the operating system's queue. Default 256.</summary>
  public int MaxConnections { get; init; } = 256;

  /// <summary>
  /// Most connections one client address may hold at once, so a single peer cannot take every slot with idle sockets. A further
  /// connection from that address is closed at once. Counted per IP address, with IPv4-mapped IPv6 addresses folded in. Default 64.
  /// Behind a reverse proxy every client shares the proxy's address: raise it to <see cref="MaxConnections"/> there.
  /// </summary>
  public int MaxConnectionsPerAddress { get; init; } = 64;

  /// <summary>Longest request line (method, target and version) in bytes. A longer one gets 414. Default 8 KiB.</summary>
  public int MaxRequestLineBytes { get; init; } = 8 * 1024;

  /// <summary>Largest request head (request line and all headers) in bytes, at least 1 KiB. A larger one gets 431. Default 32 KiB.</summary>
  public int MaxHeaderBytes { get; init; } = 32 * 1024;

  /// <summary>Most header fields in one request. More get 431. Default 100.</summary>
  public int MaxHeaderCount { get; init; } = 100;

  /// <summary>
  /// Largest request body in bytes. A larger declared <c>Content-Length</c> gets 413 before anything is read; a chunked body is cut
  /// off at the limit and the connection closed. Default 8 MiB.
  /// </summary>
  public long MaxRequestBodyBytes { get; init; } = 8 * 1024 * 1024;

  /// <summary>
  /// How long a new connection may stay silent before its first request byte arrives, then it is closed without a word. Shorter than
  /// <see cref="IdleTimeout"/> because a client that opens a connection sends its request at once. Default 10 seconds.
  /// </summary>
  public TimeSpan FirstByteTimeout { get; init; } = TimeSpan.FromSeconds(10);

  /// <summary>How long a keep-alive connection may wait for the first byte of the next request before it is closed. Default 60 seconds.</summary>
  public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

  /// <summary>
  /// How long a request head may take to arrive in full, counted from its first byte (and the same for the TLS handshake). A slower
  /// sender gets 408 and is closed. This is the defence against slowloris. Default 10 seconds.
  /// </summary>
  public TimeSpan HeaderReadTimeout { get; init; } = TimeSpan.FromSeconds(10);

  /// <summary>How long a request body may take to arrive in full, counted from the end of its head. A slower sender is closed. Default 30 seconds.</summary>
  public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

  /// <summary>How long one write to the peer may block before the connection is dropped. A client that stops reading cannot hold a handler. Default 30 seconds.</summary>
  public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(30);

  /// <summary>How long <see cref="HttpServer.DisposeAsync"/> waits for connections to finish after cancelling their requests. Default 5 seconds.</summary>
  public TimeSpan ShutdownGracePeriod { get; init; } = TimeSpan.FromSeconds(5);
}
