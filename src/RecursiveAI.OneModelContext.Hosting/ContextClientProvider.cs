using System;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.Hosting;

/// <summary>
/// Gives an app one shared <see cref="ContextClient"/> to a server, connected on first use and reconnected if the connection has closed.
/// Registered by <see cref="ContextHostingExtensions.AddContextClient(Microsoft.Extensions.DependencyInjection.IServiceCollection, Func{IServiceProvider, ContextTransport}, Func{IServiceProvider, ContextClientOptions}?)"/>;
/// the container disposes it, which closes the connection.
/// </summary>
public sealed class ContextClientProvider : IAsyncDisposable
{
  private readonly Func<ContextTransport> _transport;
  private readonly ContextClientOptions _options;
  private readonly SemaphoreSlim _gate = new(1, 1);
  private ContextClient? _client;
  private bool _disposed;

  internal ContextClientProvider(Func<ContextTransport> transport, ContextClientOptions options)
  {
    _transport = transport;
    _options = options;
  }

  /// <summary>Returns the connected client, connecting (and negotiating the protocol era) the first time and after a closed connection.</summary>
  /// <param name="cancellationToken">Cancels the wait or the connection attempt.</param>
  /// <returns>The shared client. Do not dispose it; the provider owns it.</returns>
  /// <exception cref="ObjectDisposedException">The provider was disposed.</exception>
  /// <exception cref="TransportException">The server could not be reached or negotiated with.</exception>
  public async ValueTask<ContextClient> GetClientAsync(CancellationToken cancellationToken = default)
  {
    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      ObjectDisposedException.ThrowIf(_disposed, this);
      if (_client is { } existing && !existing.Completion.IsCompleted)
      {
        return existing;
      }

      if (_client is not null)
      {
        await _client.DisposeAsync().ConfigureAwait(false);
        _client = null;
      }

      _client = await ContextClient.ConnectAsync(_transport(), _options, cancellationToken).ConfigureAwait(false);
      return _client;
    }
    finally
    {
      _gate.Release();
    }
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync()
  {
    await _gate.WaitAsync().ConfigureAwait(false);
    try
    {
      _disposed = true;
      if (_client is not null)
      {
        await _client.DisposeAsync().ConfigureAwait(false);
        _client = null;
      }
    }
    finally
    {
      _gate.Release();
    }
  }
}
