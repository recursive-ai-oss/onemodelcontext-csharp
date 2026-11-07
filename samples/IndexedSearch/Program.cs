using IndexedSearch;
using RecursiveAI.OneModelContext;

// A search server in one line. The generator wrote SearchTools.AddTo; ServeAsync speaks to the client over standard input and
// output, on whichever protocol era the client opens with. Anything this process prints to standard output would corrupt the
// protocol, so diagnostics go to standard error.
await ModelContext.ServeAsync(new SearchTools(SearchIndex.Sample()));
