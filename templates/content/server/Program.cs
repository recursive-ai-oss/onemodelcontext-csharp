using OneModelContextServer;
using RecursiveAI.OneModelContext;

//#if (isHttp)
// Streamable HTTP on http://127.0.0.1:3001/mcp, or on the port given as the first argument. The built-in listener binds
// loopback only and checks the Origin and Host headers.
await ModelContext.ServeHttpAsync(new Tools(), port: args.Length > 0 ? int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 3001);
//#else
// Standard input and output carry the protocol, so write diagnostics to standard error only.
await ModelContext.ServeAsync(new Tools());
//#endif
