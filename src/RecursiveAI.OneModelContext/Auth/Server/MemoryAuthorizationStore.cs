using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.Auth.Server;

internal sealed class StoreState
{
  public Dictionary<string, RegisteredClient> Clients { get; set; } = [];

  public Dictionary<string, AuthorizationCodeGrant> Codes { get; set; } = [];

  public Dictionary<string, RefreshTokenGrant> RefreshTokens { get; set; } = [];

  public Dictionary<string, DateTimeOffset> Revocations { get; set; } = [];

  public Dictionary<string, DateTimeOffset> Assertions { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StoreState))]
internal sealed partial class AuthorizationStoreJson : JsonSerializerContext;

/// <summary>
/// An <see cref="IAuthorizationStore"/> in process memory. State is lost when the process ends and is not shared between instances, so
/// it suits tests, single-process hosts that re-register their clients at start, and a base for durable stores. Every method is atomic
/// under one lock.
/// </summary>
public class MemoryAuthorizationStore : IAuthorizationStore
{
  private readonly object _gate = new();
  private StoreState _state = new();

  /// <summary>Creates an empty store.</summary>
  public MemoryAuthorizationStore()
  {
  }

  /// <summary>Called inside the lock after every change, with the full state. The file store writes it out.</summary>
  private protected virtual void Persist(StoreState state)
  {
  }

  private protected void Load(StoreState state)
  {
    lock (_gate)
    {
      _state = state;
    }
  }

  /// <inheritdoc />
  public ValueTask<RegisteredClient?> FindClientAsync(string clientId, CancellationToken cancellationToken)
  {
    lock (_gate)
    {
      return ValueTask.FromResult(_state.Clients.GetValueOrDefault(clientId));
    }
  }

  /// <inheritdoc />
  public ValueTask SaveClientAsync(RegisteredClient client, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(client);
    lock (_gate)
    {
      _state.Clients[client.ClientId] = client;
      Persist(_state);
    }

    return ValueTask.CompletedTask;
  }

  /// <inheritdoc />
  public ValueTask SaveCodeAsync(AuthorizationCodeGrant grant, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(grant);
    lock (_gate)
    {
      _state.Codes[grant.CodeHash] = grant;
      Persist(_state);
    }

    return ValueTask.CompletedTask;
  }

  /// <inheritdoc />
  public ValueTask<CodeRedemption> RedeemCodeAsync(string codeHash, DateTimeOffset now, CancellationToken cancellationToken)
  {
    lock (_gate)
    {
      if (!_state.Codes.TryGetValue(codeHash, out var grant))
      {
        return ValueTask.FromResult(new CodeRedemption(RedemptionStatus.Unknown, null));
      }

      if (grant.Redeemed)
      {
        return ValueTask.FromResult(new CodeRedemption(RedemptionStatus.AlreadyRedeemed, grant));
      }

      grant.Redeemed = true;
      Persist(_state);
      return ValueTask.FromResult(new CodeRedemption(grant.ExpiresAt <= now ? RedemptionStatus.Expired : RedemptionStatus.Redeemed, grant));
    }
  }

  /// <inheritdoc />
  public ValueTask SaveRefreshTokenAsync(RefreshTokenGrant token, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(token);
    lock (_gate)
    {
      _state.RefreshTokens[token.TokenHash] = token;
      Persist(_state);
    }

    return ValueTask.CompletedTask;
  }

  /// <inheritdoc />
  public ValueTask<RefreshRedemption> RedeemRefreshTokenAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
  {
    lock (_gate)
    {
      if (!_state.RefreshTokens.TryGetValue(tokenHash, out var token))
      {
        return ValueTask.FromResult(new RefreshRedemption(RedemptionStatus.Unknown, null));
      }

      if (token.Used)
      {
        return ValueTask.FromResult(new RefreshRedemption(RedemptionStatus.AlreadyRedeemed, token));
      }

      token.Used = true;
      Persist(_state);
      return ValueTask.FromResult(new RefreshRedemption(token.ExpiresAt <= now || token.FamilyExpiresAt <= now ? RedemptionStatus.Expired : RedemptionStatus.Redeemed, token));
    }
  }

  /// <inheritdoc />
  public ValueTask<RefreshTokenGrant?> FindRefreshTokenAsync(string tokenHash, CancellationToken cancellationToken)
  {
    lock (_gate)
    {
      return ValueTask.FromResult(_state.RefreshTokens.GetValueOrDefault(tokenHash));
    }
  }

  /// <inheritdoc />
  public ValueTask RevokeAsync(string key, DateTimeOffset expiresAt, CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrEmpty(key);
    lock (_gate)
    {
      _state.Revocations[key] = _state.Revocations.TryGetValue(key, out var existing) && existing > expiresAt ? existing : expiresAt;
      Persist(_state);
    }

    return ValueTask.CompletedTask;
  }

  /// <inheritdoc />
  public ValueTask<bool> IsRevokedAsync(string key, CancellationToken cancellationToken)
  {
    lock (_gate)
    {
      return ValueTask.FromResult(_state.Revocations.ContainsKey(key));
    }
  }

  /// <inheritdoc />
  public ValueTask<bool> TryRecordAssertionAsync(string key, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken cancellationToken)
  {
    lock (_gate)
    {
      if (_state.Assertions.TryGetValue(key, out var until) && until > now)
      {
        return ValueTask.FromResult(false);
      }

      _state.Assertions[key] = expiresAt;
      Persist(_state);
      return ValueTask.FromResult(true);
    }
  }

  /// <inheritdoc />
  public ValueTask PurgeAsync(DateTimeOffset now, CancellationToken cancellationToken)
  {
    lock (_gate)
    {
      var before = _state.Codes.Count + _state.RefreshTokens.Count + _state.Revocations.Count + _state.Assertions.Count;
      Remove(_state.Codes, g => (g.Redeemed && g.FamilyExpiresAt > g.ExpiresAt ? g.FamilyExpiresAt : g.ExpiresAt) <= now);
      Remove(_state.RefreshTokens, t => t.FamilyExpiresAt <= now);
      Remove(_state.Revocations, e => e <= now);
      Remove(_state.Assertions, e => e <= now);
      if (_state.Codes.Count + _state.RefreshTokens.Count + _state.Revocations.Count + _state.Assertions.Count != before)
      {
        Persist(_state);
      }
    }

    return ValueTask.CompletedTask;
  }

  private static void Remove<T>(Dictionary<string, T> map, Func<T, bool> expired)
  {
    List<string>? gone = null;
    foreach (var (key, value) in map)
    {
      if (expired(value))
      {
        (gone ??= []).Add(key);
      }
    }

    if (gone is not null)
    {
      foreach (var key in gone)
      {
        map.Remove(key);
      }
    }
  }
}

/// <summary>
/// An <see cref="IAuthorizationStore"/> kept in one JSON file, for a single process that must survive restarts. The file is written
/// to a temporary file and moved into place, so a crash leaves the old or the new state and never half of one; on Unix it is created
/// readable and writable by its owner only, and so is a directory this class creates. It holds hashes of codes, tokens and client
/// secrets, never the secrets themselves. Two processes sharing the file are not supported; use a database-backed store for that.
/// </summary>
public sealed class FileAuthorizationStore : MemoryAuthorizationStore
{
  private readonly string _path;

  /// <summary>Opens or creates the store.</summary>
  /// <param name="path">The file. Its directory is created if missing.</param>
  /// <exception cref="InvalidDataException">The file exists but is not a store written by this class. It is never overwritten in that case.</exception>
  /// <exception cref="IOException">The file or directory cannot be read or created.</exception>
  public FileAuthorizationStore(string path)
  {
    ArgumentException.ThrowIfNullOrEmpty(path);
    _path = Path.GetFullPath(path);
    var directory = Path.GetDirectoryName(_path)!;
    if (!Directory.Exists(directory))
    {
      if (OperatingSystem.IsWindows())
      {
        Directory.CreateDirectory(directory);
      }
      else
      {
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
      }
    }

    if (File.Exists(_path))
    {
      try
      {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Load(JsonSerializer.Deserialize(stream, AuthorizationStoreJson.Default.StoreState) ?? throw new InvalidDataException("The authorization store file is empty."));
      }
      catch (JsonException ex)
      {
        throw new InvalidDataException("The file is not an authorization store.", ex);
      }
    }
  }

  private protected override void Persist(StoreState state)
  {
    var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
    if (!OperatingSystem.IsWindows())
    {
      options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    }

    try
    {
      using (var stream = new FileStream(temp, options))
      {
        JsonSerializer.Serialize(stream, state, AuthorizationStoreJson.Default.StoreState);
        stream.Flush(flushToDisk: true);
      }

      // This store is single-process and serialized by the store gate; there are no concurrent writers.
      if (File.Exists(_path))
      {
        File.Replace(temp, _path, destinationBackupFileName: null);
      }
      else
      {
        File.Move(temp, _path);
      }
    }
    finally
    {
      if (File.Exists(temp))
      {
        File.Delete(temp);
      }
    }
  }
}
