using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RecursiveAI.OneModelContext.Protocol;
using AIText = Microsoft.Extensions.AI.TextContent;
using McpText = RecursiveAI.OneModelContext.Protocol.TextContent;

namespace RecursiveAI.OneModelContext.Extensions.AI;

/// <summary>
/// Answers a server's <c>sampling/createMessage</c> requests with any <see cref="IChatClient"/>.
/// </summary>
/// <remarks>
/// Sampling is deprecated in the 2026-07-28 revision (<c>OMC0002</c>) but still negotiated with servers on earlier revisions, so this
/// adapter implements it and suppresses the warning inside its own code. Using the handler as
/// <c>ContextClientOptions.OnSample</c> raises the warning at your call site, which is the intended signal.
/// The server's <c>tools</c> and <c>toolChoice</c> are not passed on: the chat client answers with text, images or audio only.
/// </remarks>
public static class ChatClientSampling
{
  /// <summary>Creates the handler for <c>ContextClientOptions.OnSample</c> (or a <c>ModelContext.ConnectAsync</c> overload).</summary>
  /// <param name="chatClient">The chat client that produces the model's reply. It is not disposed.</param>
  /// <returns>A handler that maps the request to chat messages and the reply back. Image or audio blocks with malformed base64 are left out of the prompt.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="chatClient"/> is null.</exception>
  public static Func<CreateMessageRequestParams, CancellationToken, ValueTask<CreateMessageResult>> CreateHandler(IChatClient chatClient)
  {
    ArgumentNullException.ThrowIfNull(chatClient);
    return async (request, cancellationToken) =>
    {
      var messages = new List<ChatMessage>(request.Messages.Count);
      foreach (var message in request.Messages)
      {
        var role = string.Equals(message.Role, "assistant", StringComparison.Ordinal) ? ChatRole.Assistant : ChatRole.User;
        var contents = new List<AIContent>();
        foreach (var block in message.Content)
        {
          switch (block)
          {
            case McpText text:
              contents.Add(new AIText(text.Text));
              break;
            case ImageContent image:
              if (TryDecode(image.Data, out var imageBytes))
              {
                contents.Add(new DataContent(imageBytes, image.MimeType));
              }


              break;
            case AudioContent audio:
              if (TryDecode(audio.Data, out var audioBytes))
              {
                contents.Add(new DataContent(audioBytes, audio.MimeType));
              }


              break;
          }
        }

        messages.Add(new ChatMessage(role, contents));
      }

      var options = new ChatOptions
      {
        Instructions = request.SystemPrompt,
        MaxOutputTokens = request.MaxTokens,
        Temperature = request.Temperature is { } t ? (float)t : null,
        StopSequences = request.StopSequences,
      };

      var response = await chatClient.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

      var reply = new List<ContentBlock>();
      foreach (var message in response.Messages)
      {
        foreach (var content in message.Contents)
        {
          switch (content)
          {
            case AIText text:
              reply.Add(McpText.Of(text.Text));
              break;
            case DataContent data when data.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
              reply.Add(new ImageContent { Data = Convert.ToBase64String(data.Data.Span), MimeType = data.MediaType });
              break;
            case DataContent data when data.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase):
              reply.Add(new AudioContent { Data = Convert.ToBase64String(data.Data.Span), MimeType = data.MediaType });
              break;
          }
        }
      }

      if (reply.Count == 0)
      {
        reply.Add(McpText.Of(string.Empty));
      }

      return new CreateMessageResult
      {
        Role = "assistant",
        Content = reply,
        Model = response.ModelId ?? "unknown",
        StopReason = response.FinishReason == ChatFinishReason.Length ? "maxTokens" : response.FinishReason == ChatFinishReason.Stop ? "endTurn" : null,
      };
    };
  }

  private static bool TryDecode(string base64, out byte[] bytes)
  {
    try
    {
      bytes = Convert.FromBase64String(base64);
      return true;
    }
    catch (FormatException)
    {
      bytes = [];
      return false;
    }
  }
}
