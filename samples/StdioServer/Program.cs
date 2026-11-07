using RecursiveAI.OneModelContext;
using StdioServer;

// A server in one line: the generator wrote Notebook.AddTo, and ServeAsync speaks to the client over standard input and output.
await ModelContext.ServeAsync(new Notebook());
