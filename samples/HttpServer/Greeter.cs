using RecursiveAI.OneModelContext;

namespace HttpServer;

/// <summary>A small server for trying the HTTP listener.</summary>
public partial class Greeter
{
  /// <summary>Says hello.</summary>
  /// <param name="name">Who to greet.</param>
  /// <param name="times">How many greetings to return.</param>
  [Tool(ReadOnly = true)]
  public static string[] Greet(string name, int times = 1) => [.. Enumerable.Repeat($"Hello, {name}!", Math.Clamp(times, 1, 10))];

  /// <summary>Adds two numbers.</summary>
  /// <param name="a">The first number.</param>
  /// <param name="b">The second number.</param>
  [Tool(ReadOnly = true, Idempotent = true)]
  public static double Add(double a, double b) => a + b;
}
