using System;
using System.Collections.Generic;
using System.Text;

namespace RecursiveAI.OneModelContext.Extensions.Apps;

/// <summary>
/// Builds the Content Security Policy and Permission Policy a host applies to a view from its declared metadata (ext-apps 2026-01-26,
/// "Content Security Policy Enforcement"). A host that renders views uses it so that only the origins the server declared are allowed.
/// </summary>
/// <remarks>
/// The declared origins are a server's input: an entry that could change the policy's structure (a space, <c>;</c>, <c>,</c>, a quote, a
/// backslash or a control character) is left out rather than written, so a server cannot smuggle a directive in through a domain.
/// </remarks>
public static class UiContentSecurityPolicy
{
  /// <summary>The policy for a view that declared no <c>csp</c> at all (specification, "Restrictive Default").</summary>
  public const string RestrictiveDefault =
    "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; media-src 'self' data:; connect-src 'none'";

  /// <summary>Builds the <c>Content-Security-Policy</c> value.</summary>
  /// <param name="meta">The resource's <c>_meta.ui</c>; <c>null</c> or without <c>csp</c> gives <see cref="RestrictiveDefault"/>.</param>
  public static string Build(UiResourceMeta? meta)
  {
    if (meta?.Csp is not { } csp)
    {
      return RestrictiveDefault;
    }

    var resources = Join(csp.ResourceDomains);
    var connect = Join(csp.ConnectDomains);
    var frames = Join(csp.FrameDomains);
    var bases = Join(csp.BaseUriDomains);
    var builder = new StringBuilder();
    builder.Append("default-src 'none'; ");
    builder.Append("script-src 'self' 'unsafe-inline'").Append(resources).Append("; ");
    builder.Append("style-src 'self' 'unsafe-inline'").Append(resources).Append("; ");
    builder.Append("connect-src 'self'").Append(connect).Append("; ");
    builder.Append("img-src 'self' data:").Append(resources).Append("; ");
    builder.Append("font-src 'self'").Append(resources).Append("; ");
    builder.Append("media-src 'self' data:").Append(resources).Append("; ");
    builder.Append("frame-src").Append(frames.Length == 0 ? " 'none'" : frames).Append("; ");
    builder.Append("object-src 'none'; ");
    builder.Append("base-uri").Append(bases.Length == 0 ? " 'self'" : bases);
    return builder.ToString();
  }

  /// <summary>Builds the iframe <c>allow</c> attribute for the permissions a view asked for. Granting any of them is the host's decision.</summary>
  /// <param name="permissions">The requested permissions, or <c>null</c>.</param>
  public static string BuildAllowAttribute(UiPermissions? permissions)
  {
    if (permissions is null)
    {
      return string.Empty;
    }

    var allowed = new List<string>(4);
    if (permissions.Camera is not null)
    {
      allowed.Add("camera");
    }

    if (permissions.Microphone is not null)
    {
      allowed.Add("microphone");
    }

    if (permissions.Geolocation is not null)
    {
      allowed.Add("geolocation");
    }

    if (permissions.ClipboardWrite is not null)
    {
      allowed.Add("clipboard-write");
    }

    return string.Join(' ', allowed);
  }

  /// <summary>True when an origin entry is safe to write into a policy.</summary>
  /// <param name="source">An entry such as <c>https://api.example.com</c> or <c>https://*.example.com</c>.</param>
  public static bool IsSafeSource(string? source)
  {
    if (string.IsNullOrEmpty(source))
    {
      return false;
    }

    foreach (var c in source)
    {
      if (c <= ' ' || c == 0x7f || c is ';' or ',' or '\'' or '"' or '\\')
      {
        return false;
      }
    }

    return true;
  }

  private static string Join(List<string>? sources)
  {
    if (sources is null)
    {
      return string.Empty;
    }

    var builder = new StringBuilder();
    foreach (var source in sources)
    {
      if (IsSafeSource(source))
      {
        builder.Append(' ').Append(source);
      }
    }

    return builder.ToString();
  }
}
