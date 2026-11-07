using Microsoft.Extensions.AI;

namespace ChatTools;

/// <summary>
/// A stand-in for a real model: asks for the first tool once, then answers with what the tool returned. Swap in any <see cref="IChatClient"/>.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
  public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
  {
    var results = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();
    if (results.Count > 0)
    {
      return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"The tool said: {results[^1].Result}")));
    }

    var tool = options?.Tools?.OfType<AIFunctionDeclaration>().First() ?? throw new InvalidOperationException("No tools were offered.");
    var call = new FunctionCallContent("call-1", tool.Name, new Dictionary<string, object?> { ["city"] = "Lisbon" });
    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
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
