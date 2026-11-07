using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace RecursiveAI.OneModelContext.Extensions.ServerCards;

/// <summary>The document a server publishes: its bytes and the validator clients use to ask whether it changed.</summary>
internal sealed class ServerCardDocument(byte[] json, string eTag)
{
  internal byte[] Json { get; } = json;

  internal string ETag { get; } = eTag;
}

/// <summary>Publishing a Server Card from a <see cref="ContextServer"/>.</summary>
/// <remarks>
/// The HTTP hosts serve the card at <c>GET &lt;endpoint&gt;/server-card</c> (the location Server Cards reserve) as
/// <c>application/mcp-server-card+json</c>, with the CORS headers browser clients need, <c>Cache-Control: public, max-age=3600</c> and an
/// <c>ETag</c> (a matching <c>If-None-Match</c> gets 304). The card is public: it needs no token and is answered before the
/// <c>Origin</c> check, because browsers must be able to read it. Put no credentials, internal addresses or other private details in it.
/// </remarks>
[Experimental(ExperimentalIds.ServerCards)]
public static class ServerCardsServerExtensions
{
  private static readonly ConditionalWeakTable<ContextServer, ServerCardDocument> Documents = new();

  /// <summary>Publishes the card. Calling it again replaces the card.</summary>
  /// <param name="server">The server.</param>
  /// <param name="card">The card; see <see cref="CreateServerCard"/> for one that follows the server's own values.</param>
  /// <returns>The server.</returns>
  /// <exception cref="ArgumentException">The card breaks the schema: <c>$schema</c>, the shape of <c>name</c>, a length, a remote's type or URL.</exception>
  public static ContextServer UseServerCard(this ContextServer server, ServerCard card)
  {
    ArgumentNullException.ThrowIfNull(server);
    ArgumentNullException.ThrowIfNull(card);
    Validate(card);
    var json = JsonSerializer.SerializeToUtf8Bytes(card, ServerCardsJsonContext.Default.ServerCard);

    // The entity tag is a strong validator over the exact bytes served (RFC 9110 section 8.8.3).
    var eTag = "\"" + Convert.ToHexStringLower(SHA256.HashData(json).AsSpan(0, 16)) + "\"";
    Documents.AddOrUpdate(server, new ServerCardDocument(json, eTag));
    return server;
  }

  /// <summary>
  /// Starts a card from what the server already knows: its version (the card's <c>version</c> must equal <c>serverInfo.version</c>), and the
  /// protocol revisions it speaks, which become the remote's <c>supportedProtocolVersions</c>.
  /// </summary>
  /// <param name="server">The server.</param>
  /// <param name="name">The server's reverse-DNS name, for example <c>com.example/weather</c>.</param>
  /// <param name="description">What the server does, up to 100 characters.</param>
  /// <param name="url">The Streamable HTTP endpoint clients connect to, for example <c>https://example.com/mcp</c>.</param>
  /// <returns>The card; copy it with a collection initializer of your own to add a title, icons or a repository before passing it to <see cref="UseServerCard"/>.</returns>
  public static ServerCard CreateServerCard(this ContextServer server, string name, string description, string url)
  {
    ArgumentNullException.ThrowIfNull(server);
    return new ServerCard
    {
      Name = name,
      Version = server.Info.Version,
      Description = description,
      Title = server.Info.Title,
      Remotes = [new ServerCardRemote { Type = "streamable-http", Url = url, SupportedProtocolVersions = [.. server.SupportedVersions] }],
    };
  }

  internal static bool TryGetDocument(ContextServer server, [NotNullWhen(true)] out ServerCardDocument? document) =>
    Documents.TryGetValue(server, out document);

  // Server Cards schema v1: patterns and lengths from the schema; ranges in version are refused.
  private static void Validate(ServerCard card)
  {
    if (card.Schema != ServerCard.SchemaUrl)
    {
      throw new ArgumentException("$schema must be " + ServerCard.SchemaUrl + ".", nameof(card));
    }

    if (card.Name.Length is < 3 or > 200 || !IsReverseDnsName(card.Name))
    {
      throw new ArgumentException("name is 3 to 200 characters of the form namespace/name, for example com.example/weather.", nameof(card));
    }

    if (card.Version.Length is 0 or > 255 || card.Version.AsSpan().ContainsAny("^~><*") || card.Version.EndsWith(".x", StringComparison.Ordinal))
    {
      throw new ArgumentException("version is a version string of up to 255 characters, not a range.", nameof(card));
    }

    if (card.Description.Length is 0 or > 100 || card.Title is { Length: 0 or > 100 })
    {
      throw new ArgumentException("description and title are 1 to 100 characters.", nameof(card));
    }

    foreach (var remote in card.Remotes ?? [])
    {
      if (remote.Type is not ("streamable-http" or "sse"))
      {
        throw new ArgumentException("A remote's type is streamable-http or sse.", nameof(card));
      }

      if (!(remote.Url.StartsWith("http://", StringComparison.Ordinal) || remote.Url.StartsWith("https://", StringComparison.Ordinal) || remote.Url.StartsWith('{'))
        || remote.Url.AsSpan().ContainsAny(" \t\r\n"))
      {
        throw new ArgumentException("A remote's url starts with http://, https:// or a {placeholder} and has no spaces.", nameof(card));
      }
    }
  }

  private static bool IsReverseDnsName(string name)
  {
    var slash = name.IndexOf('/', StringComparison.Ordinal);
    if (slash <= 0 || slash == name.Length - 1 || name.IndexOf('/', slash + 1) >= 0)
    {
      return false;
    }

    for (var i = 0; i < name.Length; i++)
    {
      if (i == slash)
      {
        continue;
      }

      var c = name[i];
      var letter = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
      if (!(letter || c is '.' or '-' || (i > slash && c == '_')))
      {
        return false;
      }
    }

    return true;
  }
}
