using RecursiveAI.OneModelContext;

namespace AspNetCoreServer;

// The generator writes Greeter.AddTo from these methods: the doc comments become descriptions and the parameters become the input schema.
public partial class Greeter
{
  /// <summary>Says hello.</summary>
  /// <param name="name">Who to greet.</param>
  [Tool(ReadOnly = true)]
  public string Greet(string name) => $"Hello, {name}!";

  /// <summary>Adds two whole numbers.</summary>
  /// <param name="a">The first number.</param>
  /// <param name="b">The second number.</param>
  [Tool(ReadOnly = true)]
  public int Add(int a, int b) => a + b;
}
