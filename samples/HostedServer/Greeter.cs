using RecursiveAI.OneModelContext;

namespace HostedServer;

/// <summary>Keeps the greetings the server has handed out. A service from the container.</summary>
public sealed class GreetingBook
{
  private int _count;

  /// <summary>Counts one more greeting.</summary>
  public int Next() => Interlocked.Increment(ref _count);
}

/// <summary>Greets people.</summary>
public partial class Greeter(GreetingBook book)
{
  /// <summary>Greets someone by name.</summary>
  /// <param name="name">Who to greet.</param>
  [Tool(ReadOnly = true)]
  public string Greet(string name) => $"Hello, {name}! (greeting #{book.Next()})";
}
