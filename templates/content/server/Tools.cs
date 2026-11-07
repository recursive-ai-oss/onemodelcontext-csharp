using RecursiveAI.OneModelContext;

namespace OneModelContextServer;

/// <summary>The tools this server offers. The method names, parameters and doc comments become what a client sees.</summary>
public partial class Tools
{
    /// <summary>Adds two whole numbers.</summary>
    /// <param name="a">The first number.</param>
    /// <param name="b">The second number.</param>
    [Tool(ReadOnly = true)]
    public long Add(long a, long b) => a + b;

    /// <summary>Greets someone by name.</summary>
    /// <param name="name">Who to greet.</param>
    [Tool(ReadOnly = true)]
    public string Greet(string name) => $"Hello, {name}!";
}
