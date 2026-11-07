using QuickPath;
using RecursiveAI.OneModelContext;

// Serves the lambda-based server over standard input and output.
await ModelContext.ServeAsync(QuickServer.Create());
