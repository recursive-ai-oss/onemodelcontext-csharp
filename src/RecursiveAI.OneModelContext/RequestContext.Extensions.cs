using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;

namespace RecursiveAI.OneModelContext;

// What the caller declared about extensions. Handlers use it to degrade: the same handler serves peers with and without an extension.
public sealed partial class RequestContext
{
  /// <summary>
  /// True when the caller declared the extension (on 2026-07-28 in this request's <c>_meta</c>, earlier in <c>initialize</c>) and, if this
  /// server registered it, the extension works on the revision in force. Use it to decide whether to add what only a supporting caller
  /// understands; a caller without it gets core behaviour.
  /// </summary>
  /// <param name="extensionId">The extension identifier.</param>
  public bool SupportsExtension(string extensionId) => TryGetExtensionSettings(extensionId, out _);

  /// <summary>Reads the settings the caller declared for an extension.</summary>
  /// <param name="extensionId">The extension identifier.</param>
  /// <param name="settings">The caller's settings object, as sent. Unknown members are kept.</param>
  /// <returns>True when <see cref="SupportsExtension"/> would be true.</returns>
  public bool TryGetExtensionSettings(string extensionId, out JsonElement settings)
  {
    ArgumentNullException.ThrowIfNull(extensionId);
    settings = default;
    if (ClientCapabilities.Extensions is not { } declared || !declared.TryGetValue(extensionId, out var found))
    {
      return false;
    }

    if (Server.Extensions.TryGet(extensionId, out var registration) && !registration.SupportsRevision(ProtocolVersion))
    {
      return false;
    }

    settings = found;
    return true;
  }

  /// <summary>Reads the settings the caller declared for an extension into a record.</summary>
  /// <typeparam name="T">The settings type.</typeparam>
  /// <param name="extensionId">The extension identifier.</param>
  /// <param name="typeInfo">Source-generated metadata for <typeparamref name="T"/>.</param>
  /// <returns>The settings, or <c>null</c> when the caller did not declare the extension or sent settings that do not fit <typeparamref name="T"/>.</returns>
  public T? GetExtensionSettings<T>(string extensionId, JsonTypeInfo<T> typeInfo)
    where T : class
  {
    ArgumentNullException.ThrowIfNull(typeInfo);
    if (!TryGetExtensionSettings(extensionId, out var settings))
    {
      return null;
    }

    try
    {
      return settings.Deserialize(typeInfo);
    }
    catch (JsonException)
    {
      // A peer's settings are input like any other: ones that do not fit are the same as none.
      return null;
    }
  }

  /// <summary>
  /// Sends a notification an extension defines to the caller, on the stream of this request. It is sent only when the caller declared the
  /// extension, so a caller without it never sees a message it cannot understand.
  /// </summary>
  /// <param name="extensionId">The extension the notification belongs to.</param>
  /// <param name="method">The notification method.</param>
  /// <param name="parameters">The params object, or <c>default</c> for none.</param>
  /// <returns>True when it was sent.</returns>
  public async ValueTask<bool> NotifyExtensionAsync(string extensionId, string method, JsonElement parameters = default)
  {
    ArgumentException.ThrowIfNullOrEmpty(method);
    if (!SupportsExtension(extensionId))
    {
      return false;
    }

    if (parameters.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
    {
      throw new ArgumentException("The params of a notification are a JSON object.", nameof(parameters));
    }

    await _sink.SendNotificationAsync(new JsonRpcNotification(method, parameters), CancellationToken).ConfigureAwait(false);
    return true;
  }
}
