using System;
using System.Collections.Concurrent;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext;

/// <summary>
/// Remembers which protocol era each server spoke, so that a client that connects again goes straight to it instead of probing. The era is a
/// property of the server, not of one request (2026-07-28 versioning, "Backward Compatibility"): clients cache it for the life of the
/// server process (stdio) or origin (HTTP) and probe again if the assumption later fails.
/// </summary>
public interface IEraCache
{
  /// <summary>Looks up the era remembered for a server.</summary>
  /// <param name="serverKey">The key from <see cref="ContextTransport.ServerKey"/>.</param>
  /// <param name="era">The remembered era.</param>
  bool TryGet(string serverKey, out ProtocolEra era);

  /// <summary>Remembers the era a server spoke.</summary>
  /// <param name="serverKey">The key from <see cref="ContextTransport.ServerKey"/>.</param>
  /// <param name="era">The era that worked.</param>
  void Remember(string serverKey, ProtocolEra era);

  /// <summary>Forgets a server.</summary>
  /// <param name="serverKey">The key from <see cref="ContextTransport.ServerKey"/>.</param>
  void Forget(string serverKey);
}

/// <summary>An <see cref="IEraCache"/> in memory. <see cref="Shared"/> is the default of every client in the process.</summary>
public sealed class EraCache : IEraCache
{
  private readonly ConcurrentDictionary<string, ProtocolEra> _eras = new(StringComparer.Ordinal);

  /// <summary>The cache clients use unless their options say otherwise. It lives as long as the process.</summary>
  public static EraCache Shared { get; } = new();

  /// <summary>How many servers are remembered.</summary>
  public int Count => _eras.Count;

  /// <inheritdoc />
  public bool TryGet(string serverKey, out ProtocolEra era)
  {
    ArgumentNullException.ThrowIfNull(serverKey);
    return _eras.TryGetValue(serverKey, out era);
  }

  /// <inheritdoc />
  public void Remember(string serverKey, ProtocolEra era)
  {
    ArgumentNullException.ThrowIfNull(serverKey);
    _eras[serverKey] = era;
  }

  /// <inheritdoc />
  public void Forget(string serverKey)
  {
    ArgumentNullException.ThrowIfNull(serverKey);
    _eras.TryRemove(serverKey, out _);
  }

  /// <summary>Forgets every server.</summary>
  public void Clear() => _eras.Clear();
}
