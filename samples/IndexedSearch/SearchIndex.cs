namespace IndexedSearch;

/// <summary>One search result.</summary>
/// <param name="Title">The document's title.</param>
/// <param name="Score">How many query words the document contains.</param>
public sealed record Hit(string Title, int Score);

/// <summary>
/// A small in-memory index, standing in for whatever a real application already has. The point of the sample is the tool class
/// beside it, not the ranking.
/// </summary>
public sealed class SearchIndex(IReadOnlyDictionary<string, string> documents)
{
  /// <summary>Documents that contain at least one of the query's words, best first, ties by title.</summary>
  /// <param name="query">Words to look for; case is ignored.</param>
  /// <param name="limit">The most hits to return.</param>
  public Hit[] Search(string query, int limit)
  {
    var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return
    [
      .. documents
        .Select(d => new Hit(d.Key, words.Count(w => d.Value.Contains(w, StringComparison.OrdinalIgnoreCase))))
        .Where(h => h.Score > 0)
        .OrderByDescending(h => h.Score)
        .ThenBy(h => h.Title, StringComparer.Ordinal)
        .Take(limit),
    ];
  }

  /// <summary>A few documents to search.</summary>
  public static SearchIndex Sample() => new(new Dictionary<string, string>
  {
    ["Brewing"] = "Water, coffee beans and patience make a good cup.",
    ["Espresso"] = "A small, strong coffee pulled under pressure.",
    ["Tea"] = "Leaves steeped in hot water.",
  });
}
