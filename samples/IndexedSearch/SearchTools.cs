using System.Text.Json.Serialization;
using RecursiveAI.OneModelContext;

namespace IndexedSearch;

// The generator writes a schema for every record a tool returns from a serializer context that lists it. That keeps the server
// free of reflection, which is what lets it publish with NativeAOT.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Hit[]))]
public sealed partial class SearchJson : JsonSerializerContext
{
}

// The tool itself. The doc comments become the descriptions a model reads; the parameter list becomes the input schema.
public partial class SearchTools(SearchIndex index)
{
  /// <summary>Searches the index.</summary>
  /// <param name="query">What to look for.</param>
  /// <param name="limit">Most results to return.</param>
  [Tool(ReadOnly = true)]
  public Hit[] Search(string query, int limit = 10) => index.Search(query, limit);
}
