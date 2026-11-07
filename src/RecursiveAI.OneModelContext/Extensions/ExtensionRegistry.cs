using System;
using System.Collections.Generic;
using System.Text.Json;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;

namespace RecursiveAI.OneModelContext.Extensions;

/// <summary>
/// The extensions a server offers, by identifier. Empty by default, so a server advertises no extension until the host adds one. Safe to
/// change while the server runs; connections see the change in the capabilities of the next negotiation.
/// </summary>
public sealed class ExtensionRegistry
{
  private readonly object _gate = new();
  private ExtensionRegistration[] _entries = [];

  internal ExtensionRegistry()
  {
  }

  /// <summary>How many extensions are registered.</summary>
  public int Count => _entries.Length;

  /// <summary>A snapshot of the registrations, in the order they were added.</summary>
  public IReadOnlyList<ExtensionRegistration> Registrations => Array.AsReadOnly(_entries);

  /// <summary>Registers an extension, replacing one with the same identifier.</summary>
  /// <param name="registration">The registration; it can no longer be given new methods afterwards.</param>
  /// <exception cref="ArgumentException">A method of the registration is already taken by another extension.</exception>
  public void Add(ExtensionRegistration registration)
  {
    ArgumentNullException.ThrowIfNull(registration);
    lock (_gate)
    {
      var list = new List<ExtensionRegistration>(_entries);
      list.RemoveAll(e => e.Id == registration.Id);
      foreach (var other in list)
      {
        foreach (var method in registration.RequestMethods)
        {
          if (other.TryGetRequest(method, out _))
          {
            throw new ArgumentException("The request '" + method + "' belongs to extension '" + other.Id + "'.", nameof(registration));
          }
        }

        foreach (var method in registration.NotificationMethods)
        {
          if (other.TryGetNotification(method, out _))
          {
            throw new ArgumentException("The notification '" + method + "' belongs to extension '" + other.Id + "'.", nameof(registration));
          }
        }
      }

      registration.Freeze();
      list.Add(registration);
      _entries = [.. list];
    }
  }

  /// <summary>Removes an extension.</summary>
  /// <param name="id">The identifier.</param>
  /// <returns>True when it was registered.</returns>
  public bool Remove(string id)
  {
    ArgumentNullException.ThrowIfNull(id);
    lock (_gate)
    {
      var list = new List<ExtensionRegistration>(_entries);
      if (list.RemoveAll(e => e.Id == id) == 0)
      {
        return false;
      }

      _entries = [.. list];
      return true;
    }
  }

  /// <summary>Finds a registration.</summary>
  /// <param name="id">The identifier.</param>
  /// <param name="registration">The registration.</param>
  public bool TryGet(string id, out ExtensionRegistration registration)
  {
    foreach (var entry in _entries)
    {
      if (entry.Id == id)
      {
        registration = entry;
        return true;
      }
    }

    registration = null!;
    return false;
  }

  internal bool HasToolAdapters
  {
    get
    {
      foreach (var entry in _entries)
      {
        if (entry.ToolAdapter is not null)
        {
          return true;
        }
      }

      return false;
    }
  }

  /// <summary>What the server advertises on a connection that negotiated <paramref name="protocolVersion"/>: the registered extensions that work on it (all of them when null).</summary>
  internal Dictionary<string, JsonElement>? AdvertisedFor(string? protocolVersion)
  {
    Dictionary<string, JsonElement>? advertised = null;
    foreach (var entry in _entries)
    {
      if (protocolVersion is null || entry.SupportsRevision(protocolVersion))
      {
        (advertised ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal))[entry.Id] = entry.Settings;
      }
    }

    return advertised;
  }

  internal bool TryGetRequest(string method, out ExtensionRegistration registration, out ExtensionRequestHandler handler)
  {
    foreach (var entry in _entries)
    {
      if (entry.TryGetRequest(method, out handler))
      {
        registration = entry;
        return true;
      }
    }

    registration = null!;
    handler = null!;
    return false;
  }

  internal bool TryGetNotification(string method, out ExtensionRegistration registration, out ExtensionNotificationHandler handler)
  {
    foreach (var entry in _entries)
    {
      if (entry.TryGetNotification(method, out handler))
      {
        registration = entry;
        return true;
      }
    }

    registration = null!;
    handler = null!;
    return false;
  }

  /// <summary>The first required extension the peer did not declare, or null.</summary>
  internal ExtensionRegistration? FirstMissingRequired(PeerContext peer)
  {
    foreach (var entry in _entries)
    {
      if (entry.Required && entry.SupportsRevision(peer.ProtocolVersion) && !Declares(peer.ClientCapabilities, entry.Id))
      {
        return entry;
      }
    }

    return null;
  }

  internal static bool Declares(ClientCapabilities capabilities, string id) =>
    capabilities.Extensions is { } declared && declared.ContainsKey(id);

  /// <summary>Applies every registered tool adapter in registration order; null when one hides the tool.</summary>
  internal Tool? AdaptTool(Tool tool, RequestContext context)
  {
    Tool? current = tool;
    foreach (var entry in _entries)
    {
      if (entry.ToolAdapter is { } adapter)
      {
        current = adapter(current, context);
        if (current is null)
        {
          return null;
        }
      }
    }

    return current;
  }
}
