using OneModelContextServer;
using RecursiveAI.OneModelContext;

//#if (isHttp)
// Streamable HTTP on http://127.0.0.1:3001/mcp. The built-in listener binds loopback only and checks the Origin header.
await ModelContext.ServeHttpAsync(new Tools(), port: 3001);
//#else
// Standard input and output carry the protocol, so write diagnostics to standard error only.
await ModelContext.ServeAsync(new Tools());
//#endif
