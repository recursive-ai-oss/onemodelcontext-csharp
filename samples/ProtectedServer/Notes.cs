using RecursiveAI.OneModelContext;

namespace ProtectedServer;

public partial class Notes
{
  /// <summary>Names the caller, as the token validator saw them.</summary>
  [Tool(ReadOnly = true)]
  public string WhoAmI(RequestContext ctx) => ctx.Principal?.FindFirst("sub")?.Value ?? "nobody";

  /// <summary>Adds two whole numbers.</summary>
  /// <param name="a">The first number.</param>
  /// <param name="b">The second number.</param>
  [Tool(ReadOnly = true)]
  public int Add(int a, int b) => a + b;
}
