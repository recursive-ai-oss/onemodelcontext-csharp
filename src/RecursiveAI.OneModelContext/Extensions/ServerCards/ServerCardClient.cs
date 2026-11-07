using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Http;

namespace RecursiveAI.OneModelContext.Extensions.ServerCards;

/// <summary>A Server Card could not be fetched or is not a Server Card. The message never includes the response body.</summary>
[Experimental(ExperimentalIds.ServerCards)]
public sealed class ServerCardException : ContextException
{
  internal ServerCardException(string message, Exception? inner = null)
    : base(message, inner)
  {
  }
}

/// <summary>What a fetch of a Server Card returned.</summary>
[Experimental(ExperimentalIds.ServerCards)]
public sealed class ServerCardFetch
{
  internal ServerCardFetch(Uri url, ServerCard? card, string? eTag, bool notModified, TimeSpan? cacheLifetime)
  {
    Url = url;
    Card = card;
    ETag = eTag;
    NotModified = notModified;
    CacheLifetime = cacheLifetime;
  }

  /// <summary>Where the card was fetched from.</summary>
  public Uri Url { get; }

  /// <summary>The card, or <c>null</c> when <see cref="NotModified"/> (the copy the caller sent the validator for is current).</summary>
  public ServerCard? Card { get; }

  /// <summary>The validator to send next time, or <c>null</c> when the server sent none.</summary>
  public string? ETag { get; }

  /// <summary>True when the server answered 304 to the validator the caller sent.</summary>
  public bool NotModified { get; }

  /// <summary>How long the response may be reused without asking, from <c>Cache-Control</c>; <c>null</c> when the server gave no lifetime.</summary>
  public TimeSpan? CacheLifetime { get; }
}

/// <summary>A card an AI Catalog advertises.</summary>
[Experimental(ExperimentalIds.ServerCards)]
public sealed class DiscoveredServerCard
{
  internal DiscoveredServerCard(string identifier, Uri? url, ServerCard card)
  {
    Identifier = identifier;
    Url = url;
    Card = card;
  }

  /// <summary>The catalog's identifier for the server, for example <c>urn:air:example.com:mcp:weather</c>.</summary>
  public string Identifier { get; }

  /// <summary>Where the card was fetched from, or <c>null</c> for a card the catalog carried inline.</summary>
  public Uri? Url { get; }

  /// <summary>The card. It is advisory: compare it with what the server says once connected.</summary>
  public ServerCard Card { get; }
}

/// <summary>
/// Fetches Server Cards and the AI Catalog that lists them. Every fetch goes through a <see cref="GuardedHttpFetcher"/>: only public
/// addresses (unless the caller allow-listed more), no redirects, no credentials, a capped body and a time limit, because the URLs
/// come from remote parties.
/// </summary>
/// <remarks>
/// A card is read before connecting and nothing guarantees it is true. Never use it for security or access decisions, and prefer the values the
/// live connection reports when the two disagree (Server Cards, "Consistency with Runtime Behavior").
/// </remarks>
[Experimental(ExperimentalIds.ServerCards)]
public sealed class ServerCardClient : IDisposable
{
  /// <summary>Most entries read from one AI Catalog.</summary>
  public const int MaxCatalogEntries = 100;

  /// <summary>The path of the catalog on a domain (Server Cards, "Well-Known URI").</summary>
  public const string CatalogPath = "/.well-known/ai-catalog.json";

  private const string CatalogMediaType = "application/ai-catalog+json";

  private readonly GuardedHttpFetcher _fetcher;
  private readonly bool _ownsFetcher;

  /// <summary>Creates a client.</summary>
  /// <param name="fetcher">
  /// The fetcher to use, which sets the address policy, the size limit and the timeout. <c>null</c> creates one that allows 256 KiB bodies
  /// from public addresses over https; the caller keeps ownership of a fetcher it passes.
  /// </param>
  public ServerCardClient(GuardedHttpFetcher? fetcher = null)
  {
    _ownsFetcher = fetcher is null;
    _fetcher = fetcher ?? new GuardedHttpFetcher(new GuardedFetchOptions { MaxBytes = 256 * 1024 });
  }

  /// <summary>Fetches one Server Card.</summary>
  /// <param name="url">The card's URL, for example <c>https://example.com/mcp/server-card</c> for a server at <c>https://example.com/mcp</c>.</param>
  /// <param name="previousETag">The validator from an earlier fetch, so an unchanged card answers 304 instead of being sent again; <c>null</c> for none.</param>
  /// <param name="cancellationToken">Cancels the fetch.</param>
  /// <returns>The card, or a not-modified answer.</returns>
  /// <exception cref="ServerCardException">The fetch was refused or failed, the response is not a Server Card, or it breaks the schema's required fields.</exception>
  public async Task<ServerCardFetch> FetchAsync(Uri url, string? previousETag = null, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(url);

    // Server Cards, "Hosted Server Card Location": ask for the card's media type.
    var result = await _fetcher.GetAsync(url, ServerCard.MediaType, previousETag, cancellationToken).ConfigureAwait(false);
    if (result.NotModified)
    {
      return new ServerCardFetch(url, null, result.ETag ?? previousETag, notModified: true, result.CacheLifetime);
    }

    if (!result.Succeeded)
    {
      throw new ServerCardException("The Server Card could not be fetched: " + result.Failure);
    }

    // The server echoes the negotiated type; JSON is tolerated for hosts that only know application/json.
    if (result.ContentType is not (ServerCard.MediaType or "application/json"))
    {
      throw new ServerCardException("The response is not a Server Card: its media type is " + (result.ContentType ?? "missing") + ".");
    }

    return new ServerCardFetch(url, Parse(result.Body), result.ETag, notModified: false, result.CacheLifetime);
  }

  /// <summary>
  /// Finds the servers a domain advertises: fetches its AI Catalog at <c>/.well-known/ai-catalog.json</c>, keeps the entries of the Server Card
  /// type and reads each card from its <c>url</c> or inline <c>data</c>.
  /// </summary>
  /// <param name="origin">The domain, as a URL such as <c>https://example.com</c>; only its scheme and authority are used.</param>
  /// <param name="cancellationToken">Cancels the fetches.</param>
  /// <returns>The cards found, at most <see cref="MaxCatalogEntries"/>. An entry whose card cannot be fetched or read is skipped; a catalog that is missing or invalid is an error.</returns>
  /// <exception cref="ServerCardException">The catalog could not be fetched or is not a catalog.</exception>
  public async Task<IReadOnlyList<DiscoveredServerCard>> DiscoverAsync(Uri origin, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(origin);
    var catalogUrl = new Uri(origin.GetLeftPart(UriPartial.Authority) + CatalogPath);
    var catalog = await _fetcher.GetAsync(catalogUrl, CatalogMediaType + ", application/json", null, cancellationToken).ConfigureAwait(false);
    if (!catalog.Succeeded)
    {
      throw new ServerCardException("The AI Catalog could not be fetched: " + catalog.Failure);
    }

    JsonDocument document;
    try
    {
      document = JsonDocument.Parse(catalog.Body, new JsonDocumentOptions { MaxDepth = 16 });
    }
    catch (JsonException ex)
    {
      throw new ServerCardException("The AI Catalog is not valid JSON.", ex);
    }

    using (document)
    {
      if (document.RootElement.ValueKind != JsonValueKind.Object
        || !document.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
      {
        throw new ServerCardException("The AI Catalog has no entries array.");
      }

      var found = new List<DiscoveredServerCard>();
      var seen = 0;
      foreach (var entry in entries.EnumerateArray())
      {
        if (++seen > MaxCatalogEntries)
        {
          break;
        }

        if (entry.ValueKind != JsonValueKind.Object
          || !entry.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != ServerCard.MediaType)
        {
          continue;
        }

        var identifier = entry.TryGetProperty("identifier", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        if (string.IsNullOrEmpty(identifier))
        {
          continue;
        }

        try
        {
          // AI Catalog entries carry exactly one of url and data.
          var hasUrl = entry.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String;
          var hasData = entry.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object;
          if (hasUrl == hasData)
          {
            continue;
          }

          if (hasData)
          {
            found.Add(new DiscoveredServerCard(identifier, null, Parse(data.GetRawText())));
          }
          else if (Uri.TryCreate(catalogUrl, url.GetString(), out var cardUrl))
          {
            found.Add(new DiscoveredServerCard(identifier, cardUrl, (await FetchAsync(cardUrl, null, cancellationToken).ConfigureAwait(false)).Card!));
          }
        }
        catch (ServerCardException)
        {
          // One bad entry does not hide the others.
        }
      }

      return found;
    }
  }

  /// <inheritdoc />
  public void Dispose()
  {
    if (_ownsFetcher)
    {
      _fetcher.Dispose();
    }
  }

  private static ServerCard Parse(ReadOnlySpan<byte> json)
  {
    try
    {
      var card = JsonSerializer.Deserialize(json, ServerCardsJsonContext.Default.ServerCard);
      return card ?? throw new ServerCardException("The response is not a Server Card.");
    }
    catch (JsonException ex)
    {
      throw new ServerCardException("The response is not a Server Card: a required field is missing or has the wrong type.", ex);
    }
  }

  private static ServerCard Parse(string json)
  {
    try
    {
      var card = JsonSerializer.Deserialize(json, ServerCardsJsonContext.Default.ServerCard);
      return card ?? throw new ServerCardException("The entry is not a Server Card.");
    }
    catch (JsonException ex)
    {
      throw new ServerCardException("The entry is not a Server Card: a required field is missing or has the wrong type.", ex);
    }
  }
}
