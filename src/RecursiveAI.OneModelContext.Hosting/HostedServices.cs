using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using RecursiveAI.OneModelContext.Auth;

namespace RecursiveAI.OneModelContext.Hosting;

/// <summary>Serves a <see cref="ContextServer"/> over one transport while the host runs, and asks the host to stop when the transport closes.</summary>
internal sealed class ContextTransportHostedService(ContextServer server, Func<ContextTransport> transport, IHostApplicationLifetime? lifetime) : BackgroundService
{
  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    // Let the host finish starting its other services before the first read.
    await Task.Yield();
    try
    {
      await server.RunAsync(transport(), stoppingToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
      // The host is stopping.
      return;
    }

    // The client hung up (stdin closed): a stdio server has nothing left to do.
    if (!stoppingToken.IsCancellationRequested)
    {
      lifetime?.StopApplication();
    }
  }
}

/// <summary>
/// Hosts the built-in HTTP listener for a <see cref="ContextServer"/>: loopback only, the listener's default limits, started and stopped
/// with the host. Added by <see cref="ContextServerBuilder.UseListener"/>.
/// </summary>
public sealed class ContextListener : BackgroundService
{
  private readonly ContextServer _server;
  private readonly int _port;
  private readonly string _path;
  private readonly ResourceServer? _resourceServer;
  private readonly TaskCompletionSource<Uri> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

  internal ContextListener(ContextServer server, int port, string path, ResourceServer? resourceServer)
  {
    _server = server;
    _port = port;
    _path = path;
    _resourceServer = resourceServer;
  }

  /// <summary>Completes with the endpoint's full address (scheme, loopback host, bound port and path) once the listener accepts connections; faults if it cannot bind.</summary>
  public Task<Uri> Started => _started.Task;

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    await Task.Yield();
    try
    {
      await ModelContext.ServeHttpAsync(_server, _port, _path, address => _started.TrySetResult(address), _resourceServer, stoppingToken).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      _started.TrySetException(ex);
      throw;
    }
    finally
    {
      _started.TrySetCanceled(CancellationToken.None);
    }
  }
}
