using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;

namespace RecursiveAI.OneModelContext.Transports;

/// <summary>The Streamable HTTP client transport behind <see cref="ContextTransport.Http"/>.</summary>
internal sealed class HttpClientTransport : ContextTransport
{
  private readonly Uri _endpoint;
  private readonly HttpClient? _client;
  private readonly HttpTransportOptions _options;
  private int _opened;

  internal HttpClientTransport(Uri endpoint, HttpClient? client, HttpTransportOptions options)
  {
    _endpoint = endpoint;
    _client = client;
    _options = options;
  }

  // The era is a property of the server, which is reached by origin (2026-07-28 versioning: cache per origin).
  public override string? ServerKey => _endpoint.GetLeftPart(UriPartial.Authority).ToLowerInvariant();

  public override ValueTask<IJsonRpcChannel> OpenAsync(CancellationToken cancellationToken)
  {
    if (Interlocked.Exchange(ref _opened, 1) != 0)
    {
      throw new InvalidOperationException("The transport was already opened.");
    }

    // Redirects are never followed by a client this library creates: a credential added by the application must not travel to
    // another place. A client the application passes in is used as it is configured.
    var client = _client ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(30) }) { Timeout = Timeout.InfiniteTimeSpan };
    return ValueTask.FromResult<IJsonRpcChannel>(new HttpChannel(_endpoint, client, ownsClient: _client is null, _options));
  }
}
