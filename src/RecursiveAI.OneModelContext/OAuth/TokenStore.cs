using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.OAuth;

/// <summary>
/// What a client remembers about one authorization: who issued it, the client credentials used there, and the tokens. The same shape
/// holds a client registration (no tokens) and a token set. <see cref="ToString"/> never prints a secret.
/// </summary>
public sealed class TokenRecord
{
  /// <summary>The issuer identifier of the authorization server that issued everything in this record. Credentials are never used at another issuer.</summary>
  public string? Issuer { get; init; }

  /// <summary>The client identifier at that issuer.</summary>
  public string? ClientId { get; init; }

  /// <summary>The client secret registered at that issuer, if the client is confidential. Treated like a refresh token: kept only in the store.</summary>
  public string? ClientSecret { get; init; }

  /// <summary>The access token.</summary>
  public string? AccessToken { get; init; }

  /// <summary>The refresh token, if the server issued one.</summary>
  public string? RefreshToken { get; init; }

  /// <summary>When the access token expires, if the server said.</summary>
  public DateTimeOffset? ExpiresAt { get; init; }

  /// <summary>The resource indicator (RFC 8707) the tokens were requested for, as the resource published it.</summary>
  public string? Resource { get; init; }

  /// <summary>The scope set last requested for this authorization, space-separated. Step-up unions new challenges into it.</summary>
  public string? Scope { get; init; }

  /// <summary>Whether the client was registered dynamically (so the record may be discarded if the server rejects the client).</summary>
  public bool Dynamic { get; init; }

  /// <inheritdoc />
  public override string ToString() =>
    $"TokenRecord(issuer: {Issuer}, client: {ClientId}, access token: {(AccessToken is null ? "none" : "present")}, refresh token: {(RefreshToken is null ? "none" : "present")})";

  internal void Write(Utf8JsonWriter writer)
  {
    writer.WriteStartObject();
    WriteIf(writer, "issuer", Issuer);
    WriteIf(writer, "client_id", ClientId);
    WriteIf(writer, "client_secret", ClientSecret);
    WriteIf(writer, "access_token", AccessToken);
    WriteIf(writer, "refresh_token", RefreshToken);
    if (ExpiresAt is { } at)
    {
      writer.WriteString("expires_at", at.ToString("O", CultureInfo.InvariantCulture));
    }

    WriteIf(writer, "resource", Resource);
    WriteIf(writer, "scope", Scope);
    if (Dynamic)
    {
      writer.WriteBoolean("dynamic", true);
    }

    writer.WriteEndObject();
  }

  internal static TokenRecord Read(JsonElement element)
  {
    DateTimeOffset? expires = null;
    if (element.TryGetProperty("expires_at", out var at) && at.ValueKind == JsonValueKind.String
      && DateTimeOffset.TryParse(MetadataJson.Text(at), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
    {
      expires = parsed;
    }

    return new TokenRecord
    {
      Issuer = MetadataJson.GetString(element, "issuer"),
      ClientId = MetadataJson.GetString(element, "client_id"),
      ClientSecret = MetadataJson.GetString(element, "client_secret"),
      AccessToken = MetadataJson.GetString(element, "access_token"),
      RefreshToken = MetadataJson.GetString(element, "refresh_token"),
      ExpiresAt = expires,
      Resource = MetadataJson.GetString(element, "resource"),
      Scope = MetadataJson.GetString(element, "scope"),
      Dynamic = MetadataJson.GetBool(element, "dynamic"),
    };
  }

  private static void WriteIf(Utf8JsonWriter writer, string name, string? value)
  {
    if (value is not null)
    {
      writer.WriteString(name, value);
    }
  }
}

/// <summary>Keeps tokens and client registrations between requests, and between runs when the store is durable.</summary>
public interface ITokenStore
{
  /// <summary>Reads a record.</summary>
  /// <param name="key">The record key. Keys are opaque strings chosen by the client; a store must treat them as data.</param>
  /// <param name="cancellationToken">Cancels the read.</param>
  /// <returns>The record, or null when there is none.</returns>
  ValueTask<TokenRecord?> GetAsync(string key, CancellationToken cancellationToken);

  /// <summary>Writes a record, replacing any under the key.</summary>
  /// <param name="key">The record key.</param>
  /// <param name="record">The record.</param>
  /// <param name="cancellationToken">Cancels the write.</param>
  ValueTask SetAsync(string key, TokenRecord record, CancellationToken cancellationToken);

  /// <summary>Deletes a record; absent keys are not an error.</summary>
  /// <param name="key">The record key.</param>
  /// <param name="cancellationToken">Cancels the delete.</param>
  ValueTask RemoveAsync(string key, CancellationToken cancellationToken);
}

/// <summary>Seals the bytes of a durable token store, for example with an operating-system key or a host secret. Optional.</summary>
public interface ITokenProtector
{
  /// <summary>Encrypts bytes for storage.</summary>
  /// <param name="plain">The plain text.</param>
  byte[] Protect(byte[] plain);

  /// <summary>Decrypts bytes read from storage.</summary>
  /// <param name="protectedBytes">What <see cref="Protect"/> returned.</param>
  /// <returns>The plain text.</returns>
  /// <exception cref="System.Security.Cryptography.CryptographicException">The bytes cannot be decrypted. The store then treats itself as empty.</exception>
  byte[] Unprotect(byte[] protectedBytes);
}

/// <summary>A token store that lives in this process only. The default.</summary>
public sealed class MemoryTokenStore : ITokenStore
{
  private readonly Dictionary<string, TokenRecord> _records = new(StringComparer.Ordinal);
  private readonly object _lock = new();

  /// <inheritdoc />
  public ValueTask<TokenRecord?> GetAsync(string key, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(key);
    lock (_lock)
    {
      return ValueTask.FromResult(_records.GetValueOrDefault(key));
    }
  }

  /// <inheritdoc />
  public ValueTask SetAsync(string key, TokenRecord record, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(key);
    ArgumentNullException.ThrowIfNull(record);
    lock (_lock)
    {
      _records[key] = record;
    }

    return ValueTask.CompletedTask;
  }

  /// <inheritdoc />
  public ValueTask RemoveAsync(string key, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(key);
    lock (_lock)
    {
      _records.Remove(key);
    }

    return ValueTask.CompletedTask;
  }
}

/// <summary>
/// A token store in one file. On Unix the file is created with mode 0600 (owner read and write) before any secret is written, in a
/// directory created with mode 0700; on Windows it takes the access rules of the user's profile directory, where it should live. The
/// file is replaced atomically, so a crash never leaves half a store. With an <see cref="ITokenProtector"/>, the file holds only protected
/// bytes. Several processes sharing one file is not supported: the last writer wins.
/// </summary>
public sealed class FileTokenStore : ITokenStore, IDisposable
{
  // A store this large is not ours; reading it whole would only be a way to exhaust memory.
  private const int MaxFileBytes = 4 * 1024 * 1024;

  private readonly string _path;
  private readonly ITokenProtector? _protector;
  private readonly SemaphoreSlim _gate = new(1, 1);

  /// <summary>Creates a store.</summary>
  /// <param name="path">The file. Its directory is created when missing. Choose a location only the user can read.</param>
  /// <param name="protector">Seals the file contents; null stores them as JSON protected by file permissions alone.</param>
  public FileTokenStore(string path, ITokenProtector? protector = null)
  {
    ArgumentException.ThrowIfNullOrEmpty(path);
    _path = Path.GetFullPath(path);
    _protector = protector;
  }

  /// <summary>Releases the store.</summary>
  public void Dispose() => _gate.Dispose();

  /// <inheritdoc />
  public async ValueTask<TokenRecord?> GetAsync(string key, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(key);
    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      return (await LoadAsync(cancellationToken).ConfigureAwait(false)).GetValueOrDefault(key);
    }
    finally
    {
      _gate.Release();
    }
  }

  /// <inheritdoc />
  public async ValueTask SetAsync(string key, TokenRecord record, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(key);
    ArgumentNullException.ThrowIfNull(record);
    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      var all = await LoadAsync(cancellationToken).ConfigureAwait(false);
      all[key] = record;
      await SaveAsync(all, cancellationToken).ConfigureAwait(false);
    }
    finally
    {
      _gate.Release();
    }
  }

  /// <inheritdoc />
  public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(key);
    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      var all = await LoadAsync(cancellationToken).ConfigureAwait(false);
      if (all.Remove(key))
      {
        await SaveAsync(all, cancellationToken).ConfigureAwait(false);
      }
    }
    finally
    {
      _gate.Release();
    }
  }

  private async Task<Dictionary<string, TokenRecord>> LoadAsync(CancellationToken cancellationToken)
  {
    var result = new Dictionary<string, TokenRecord>(StringComparer.Ordinal);
    if (!File.Exists(_path))
    {
      return result;
    }

    byte[] bytes;
    await using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, useAsync: true))
    {
      if (stream.Length > MaxFileBytes)
      {
        return result;
      }

      bytes = new byte[stream.Length];
      await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    try
    {
      if (_protector is not null)
      {
        bytes = _protector.Unprotect(bytes);
      }

      using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
      if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Object)
      {
        foreach (var entry in entries.EnumerateObject())
        {
          if (entry.Value.ValueKind == JsonValueKind.Object)
          {
            result[entry.Name] = TokenRecord.Read(entry.Value);
          }
        }
      }
    }
    catch (Exception ex) when (ex is JsonException or System.Security.Cryptography.CryptographicException)
    {
      // An unreadable store is an empty store: the user signs in again. The content is never put in a message.
      result.Clear();
    }

    return result;
  }

  private async Task SaveAsync(Dictionary<string, TokenRecord> all, CancellationToken cancellationToken)
  {
    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer))
    {
      writer.WriteStartObject();
      writer.WriteNumber("version", 1);
      writer.WritePropertyName("entries");
      writer.WriteStartObject();
      foreach (var (key, record) in all)
      {
        writer.WritePropertyName(key);
        record.Write(writer);
      }

      writer.WriteEndObject();
      writer.WriteEndObject();
    }

    var bytes = buffer.ToArray();
    if (_protector is not null)
    {
      bytes = _protector.Protect(bytes);
    }

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

    // The temporary file is created with its final permissions, so the secrets are never readable by anyone else, even for an instant.
    var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
    if (!OperatingSystem.IsWindows())
    {
      options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    }

    try
    {
      await using (var stream = new FileStream(temporary, options))
      {
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
      }

      File.Move(temporary, _path, overwrite: true);
    }
    catch
    {
      try
      {
        File.Delete(temporary);
      }
      catch (IOException)
      {
        // Nothing more to do for a temporary file that cannot be removed.
      }

      throw;
    }
  }
}
