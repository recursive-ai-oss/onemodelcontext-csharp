using System.Globalization;
using System.Text.Json;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;

namespace Explorer;

/// <summary>
/// Answers a server's questions by asking the person at the terminal. A real host shows a dialog instead; the shape is the same:
/// the server sends a message and a flat form schema, the host collects values and returns accept, decline or cancel.
/// </summary>
/// <param name="input">Where answers come from.</param>
/// <param name="output">Where questions are shown.</param>
public sealed class ConsoleElicitation(TextReader input, TextWriter output) : IClientRequestHandler
{
  /// <summary>The client capabilities to connect with. A server only asks for what the client says it can show.</summary>
  public static ClientCapabilities Capabilities { get; } = new() { Elicitation = new ElicitationCapability() };

  /// <inheritdoc />
  public ValueTask<Result> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
  {
    if (method != "elicitation/create")
    {
      // Sampling and roots are deprecated and this client does not offer them; method-not-found is the polite answer.
      throw new ProtocolException(-32601, $"This client does not answer {method}.");
    }

    output.WriteLine(parameters.GetProperty("message").GetString());
    var values = new Dictionary<string, JsonElement>();
    if (parameters.TryGetProperty("requestedSchema", out var schema) && schema.TryGetProperty("properties", out var properties))
    {
      foreach (var property in properties.EnumerateObject())
      {
        var type = property.Value.TryGetProperty("type", out var t) ? t.GetString() : "string";
        output.Write($"  {property.Name} ({type}){(type == "boolean" ? " [y/n]" : "")}: ");
        var line = input.ReadLine();
        if (line is null || line.Trim().Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
          return ValueTask.FromResult<Result>(new ElicitResult { Action = "cancel" });
        }

        values[property.Name] = type switch
        {
          "boolean" => JsonSerializer.SerializeToElement(line.Trim().StartsWith('y') || line.Trim().StartsWith('Y'), SampleJson.Default.Boolean),
          "integer" or "number" when double.TryParse(line, CultureInfo.InvariantCulture, out var number) => JsonSerializer.SerializeToElement(number, SampleJson.Default.Double),
          _ => JsonSerializer.SerializeToElement(line, SampleJson.Default.String),
        };
      }
    }

    return ValueTask.FromResult<Result>(new ElicitResult { Action = "accept", Content = values });
  }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(bool))]
[System.Text.Json.Serialization.JsonSerializable(typeof(double))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class SampleJson : System.Text.Json.Serialization.JsonSerializerContext
{
}
