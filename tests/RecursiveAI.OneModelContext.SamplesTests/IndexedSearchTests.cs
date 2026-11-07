using System.Text.Json;
using IndexedSearch;
using RecursiveAI.OneModelContext;
using Xunit;

namespace RecursiveAI.OneModelContext.SamplesTests;

public class IndexedSearchTests
{
  [Theory]
  [MemberData(nameof(Eras.Both), MemberType = typeof(Eras))]
  public async Task SearchRanksByMatchingWords(EraPreference era)
  {
    var server = new ContextServer("indexed-search", "1.0.0");
    new SearchTools(SearchIndex.Sample()).AddTo(server);
    await using var pair = await Pair.ConnectAsync(server, new ContextClientOptions { Era = era });

    var tools = await pair.Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
    var search = Assert.Single(tools);
    Assert.Equal("search", search.Name);
    Assert.Equal("What to look for.", search.InputSchema.GetProperty("properties").GetProperty("query").GetProperty("description").GetString());
    Assert.Equal(["query"], search.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));

    var result = await pair.Client.CallToolAsync("search", JsonDocument.Parse("""{"query":"coffee water"}""").RootElement, cancellationToken: TestContext.Current.CancellationToken);
    Assert.NotEqual(true, result.IsError);
    using var hits = JsonDocument.Parse(result.Text);
    Assert.Equal(["Brewing", "Espresso", "Tea"], hits.RootElement.EnumerateArray().Select(h => h.GetProperty("title").GetString()));
    Assert.Equal(2, hits.RootElement[0].GetProperty("score").GetInt32());
  }
}
