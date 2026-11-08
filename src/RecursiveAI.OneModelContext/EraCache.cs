using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext;

/// <summary>
/// Remembers which protocol era each server spoke, so that a client that connects again goes straight to it instead of probing. The era is a
/// property of the server, not of one request (2026-07-28 versioning, "Backward Compatibility"): clients cache it for the life of the
/// server process (stdio) or origin (HTTP) and probe again if the assumption later fails. Implementations may expire legacy entries so
/// modern support can be rediscovered; <see cref="EraCache"/> does so after five minutes by default.
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
  private sealed record Entry(ProtocolEra Era, long RememberedAt);
  private readonly ConcurrentDictionary<string, Entry> _eras = new(StringComparer.Ordinal);
  private readonly TimeProvider _time;
  private readonly TimeSpan _legacyTimeToLive;

  /// <summary>Creates an in-memory cache. Modern choices last until forgotten; legacy choices expire so upgraded servers are probed again.</summary>
  /// <param name="timeProvider">Monotonic clock for expiration; null uses <see cref="TimeProvider.System"/>.</param>
  /// <param name="legacyTimeToLive">Legacy entry lifetime from a definitive probe, not from a cache hit. Default five minutes; zero means always re-probe. Negative values throw <see cref="ArgumentOutOfRangeException"/>.</param>
  public EraCache(TimeProvider? timeProvider = null, TimeSpan? legacyTimeToLive = null)
  {
    _time = timeProvider ?? TimeProvider.System;
    _legacyTimeToLive = legacyTimeToLive ?? TimeSpan.FromMinutes(5);
    ArgumentOutOfRangeException.ThrowIfLessThan(_legacyTimeToLive, TimeSpan.Zero);
  }

  /// <summary>The cache clients use unless their options say otherwise. It lives as long as the process.</summary>
  public static EraCache Shared { get; } = new();

  /// <summary>How many entries are held, including expired entries not yet looked up.</summary>
  public int Count => _eras.Count;

  /// <inheritdoc />
  public bool TryGet(string serverKey, out ProtocolEra era)
  {
    ArgumentNullException.ThrowIfNull(serverKey);
    era = default;
    if (!_eras.TryGetValue(serverKey, out var entry))
    {
      return false;
    }
    if (entry.Era == ProtocolEra.Legacy && _time.GetElapsedTime(entry.RememberedAt) >= _legacyTimeToLive)
    {
      _eras.TryRemove(new KeyValuePair<string, Entry>(serverKey, entry));
      return false;
    }
    era = entry.Era;
    return true;
  }

  /// <inheritdoc />
  public void Remember(string serverKey, ProtocolEra era)
  {
    ArgumentNullException.ThrowIfNull(serverKey);
    _eras[serverKey] = new Entry(era, _time.GetTimestamp());
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
