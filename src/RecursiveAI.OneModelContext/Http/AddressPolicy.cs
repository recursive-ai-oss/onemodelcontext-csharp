using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace RecursiveAI.OneModelContext.Http;

/// <summary>
/// Decides whether an IP address is a public, globally routable unicast address, the only kind a server should fetch a
/// client-supplied URL from (RFC 6890 special-use addresses, and IPv6 forms that embed an IPv4 address, are refused). A host may allow-list
/// specific addresses or networks, such as a loopback test fixture.
/// </summary>
public sealed class AddressPolicy
{
  private readonly List<(IPAddress Network, int Prefix)> _allowed = [];

  /// <summary>Creates a policy that allows public addresses only.</summary>
  public AddressPolicy()
  {
  }

  /// <summary>
  /// Allows an address or network that would otherwise be refused. Use this for hosts you operate, never for user-supplied names.
  /// </summary>
  /// <param name="address">An IP address, or a network in CIDR form such as <c>10.1.0.0/16</c>.</param>
  /// <returns>This policy.</returns>
  /// <exception cref="ArgumentException">The text is not an address or CIDR network.</exception>
  public AddressPolicy Allow(string address)
  {
    ArgumentException.ThrowIfNullOrEmpty(address);
    var slash = address.IndexOf('/', StringComparison.Ordinal);
    var text = slash < 0 ? address : address[..slash];
    if (!IPAddress.TryParse(text, out var ip))
    {
      throw new ArgumentException("Not an IP address or CIDR network.", nameof(address));
    }

    var max = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
    var prefix = max;
    if (slash >= 0 && (!int.TryParse(address.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix > max))
    {
      throw new ArgumentException("The prefix length is not valid.", nameof(address));
    }

    _allowed.Add((ip, prefix));
    return this;
  }

  /// <summary>Whether the address may be fetched from: it is public, or inside an allow-listed network.</summary>
  /// <param name="address">The resolved address.</param>
  public bool IsAllowed(IPAddress address)
  {
    ArgumentNullException.ThrowIfNull(address);
    return IsAllowListed(address) || IsPublic(address);
  }

  /// <summary>Whether the address was allow-listed explicitly.</summary>
  /// <param name="address">The resolved address.</param>
  public bool IsAllowListed(IPAddress address)
  {
    ArgumentNullException.ThrowIfNull(address);
    foreach (var (network, prefix) in _allowed)
    {
      if (Matches(address, network, prefix))
      {
        return true;
      }
    }

    return false;
  }

  /// <summary>
  /// Whether the address is a public unicast address. IPv4-mapped IPv6 addresses (<c>::ffff:a.b.c.d</c>) are judged as the IPv4 address
  /// they carry; 6to4 addresses by their embedded IPv4 address; NAT64, Teredo, documentation, unique-local, link-local, site-local,
  /// multicast and the unspecified and loopback addresses are never public.
  /// </summary>
  /// <param name="address">The address.</param>
  public static bool IsPublic(IPAddress address)
  {
    ArgumentNullException.ThrowIfNull(address);
    if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
    {
      address = address.MapToIPv4();
    }

    // Fixed 16-byte buffer: the size of an IPv6 address, which is the most TryWriteBytes writes.
    Span<byte> b = stackalloc byte[16];
    if (!address.TryWriteBytes(b, out var written))
    {
      return false;
    }

    if (address.AddressFamily == AddressFamily.InterNetwork)
    {
      return IsPublicV4(b[0], b[1], b[2]);
    }

    if (written != 16)
    {
      return false;
    }

    // Only global unicast 2000::/3 can be public; everything else (loopback, unspecified, ULA, link-local, multicast, NAT64 64:ff9b::/96,
    // IPv4-compatible ::/96, discard 100::/64) is outside it (RFC 4291 section 2.4, RFC 6890).
    if ((b[0] & 0xE0) != 0x20)
    {
      return false;
    }

    // 2001::/23 IETF protocol assignments (includes Teredo 2001::/32, ORCHID), 2001:db8::/32 documentation, 3fff::/20 documentation.
    if (b[0] == 0x20 && b[1] == 0x01 && (b[2] & 0xFE) == 0x00)
    {
      return false;
    }

    if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8)
    {
      return false;
    }

    if (b[0] == 0x3F && b[1] == 0xFF && (b[2] & 0xF0) == 0x00)
    {
      return false;
    }

    // 6to4 2002::/16 carries an IPv4 address in bits 16..47 (RFC 3056): a private one makes it a route to a private host.
    if (b[0] == 0x20 && b[1] == 0x02)
    {
      return IsPublicV4(b[2], b[3], b[4]);
    }

    return true;
  }

  // RFC 6890 and the IANA IPv4 special-purpose registry.
  private static bool IsPublicV4(byte a, byte b, byte c) => !(
    a == 0 || a == 10 || a == 127 || a >= 224
    || (a == 100 && b >= 64 && b <= 127)
    || (a == 169 && b == 254)
    || (a == 172 && b >= 16 && b <= 31)
    || (a == 192 && b == 0 && (c == 0 || c == 2))
    || (a == 192 && b == 88 && c == 99)
    || (a == 192 && b == 168)
    || (a == 198 && (b == 18 || b == 19))
    || (a == 198 && b == 51 && c == 100)
    || (a == 203 && b == 0 && c == 113));

  private static bool Matches(IPAddress address, IPAddress network, int prefix)
  {
    if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
    {
      address = address.MapToIPv4();
    }

    if (address.AddressFamily != network.AddressFamily)
    {
      return false;
    }

    // Fixed 16-byte buffers: the size of an IPv6 address.
    Span<byte> a = stackalloc byte[16];
    Span<byte> n = stackalloc byte[16];
    if (!address.TryWriteBytes(a, out var len) || !network.TryWriteBytes(n, out _))
    {
      return false;
    }

    var fullBytes = prefix / 8;
    for (var i = 0; i < fullBytes; i++)
    {
      if (a[i] != n[i])
      {
        return false;
      }
    }

    var rest = prefix % 8;
    if (rest == 0)
    {
      return true;
    }

    var mask = (byte)(0xFF << (8 - rest));
    return fullBytes < len && (a[fullBytes] & mask) == (n[fullBytes] & mask);
  }
}
