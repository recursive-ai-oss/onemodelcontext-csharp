using System.IO.Pipelines;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RecursiveAI.OneModelContext;

namespace RecursiveAI.OneModelContext.AdapterTests;

internal static class Eras
{
  public static IEnumerable<object[]> Both() => [[EraPreference.Modern], [EraPreference.Legacy]];
}

/// <summary>Two in-memory pipes: one end for the server's transport, one for the client's.</summary>
internal sealed class Wire
{
  private readonly Pipe _toServer = new();
  private readonly Pipe _toClient = new();

  public ContextTransport Server() => ContextTransport.Stream(_toServer.Reader.AsStream(), _toClient.Writer.AsStream());

  public ContextTransport Client() => ContextTransport.Stream(_toClient.Reader.AsStream(), _toServer.Writer.AsStream());
}

/// <summary>A chat client whose reply is a function of the request, recording what it was asked.</summary>
internal sealed class FakeChatClient(Func<IList<ChatMessage>, ChatOptions?, ChatResponse> reply) : IChatClient
{
  public List<(IList<ChatMessage> Messages, ChatOptions? Options)> Requests { get; } = [];

  public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
  {
    var list = messages.ToList();
    Requests.Add((list, options));
    return Task.FromResult(reply(list, options));
  }

  public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
  {
    var response = await GetResponseAsync(messages, options, cancellationToken);
    foreach (var update in response.ToChatResponseUpdates())
    {
      yield return update;
    }
  }

  public object? GetService(Type serviceType, object? serviceKey = null) => null;

  public void Dispose()
  {
  }
}

/// <summary>Collects everything logged.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
  public List<string> Lines { get; } = [];

  public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);

  public void Dispose()
  {
  }

  private sealed class Capture(CapturingLoggerProvider owner, string category) : ILogger
  {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
      var line = $"{category} {logLevel}: {formatter(state, exception)}" + (exception is null ? string.Empty : " | " + exception);
      lock (owner.Lines)
      {
        owner.Lines.Add(line);
      }
    }
  }
}
