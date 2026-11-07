using System;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext;

public static partial class ModelContext
{
  /// <summary>Connects to a Streamable HTTP server and negotiates the protocol era (or uses the one you pin).</summary>
  /// <param name="url">The server's URL, <c>http</c> or <c>https</c>. See <see cref="ContextTransport.Http"/> for what the transport does and for adding credentials.</param>
  /// <param name="era">Pin the era when a server is known to need it; <see cref="EraPreference.Auto"/> negotiates and remembers the answer per origin.</param>
  /// <param name="cancellationToken">Cancels the connection attempt.</param>
  /// <returns>The connected client; dispose it to disconnect.</returns>
  /// <exception cref="ArgumentException"><paramref name="url"/> is not an absolute http or https URL.</exception>
  /// <exception cref="TransportException">The server cannot be reached or refused the connection.</exception>
  /// <exception cref="ProtocolVersionException">The server and client share no protocol revision.</exception>
  public static Task<ContextClient> ConnectAsync(Uri url, EraPreference era = EraPreference.Auto, CancellationToken cancellationToken = default) =>
    ConnectAsync(ContextTransport.Http(url), era, cancellationToken);
}
