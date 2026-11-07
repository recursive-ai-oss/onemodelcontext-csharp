using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.Protocol;

namespace Explorer;

/// <summary>
/// Answers a server's questions by asking the person at the terminal. A real host shows a dialog instead; the shape is the same:
/// the server sends a message, the host collects an answer and returns it. Plug the two methods into <see cref="ContextClientOptions"/>.
/// </summary>
/// <param name="input">Where answers come from.</param>
/// <param name="output">Where questions are shown.</param>
public sealed class ConsoleElicitation(TextReader input, TextWriter output)
{
  /// <summary>A yes/no question (<see cref="ContextClientOptions.OnConfirm"/>): the library has already recognised the form.</summary>
  public ValueTask<bool> ConfirmAsync(string message, CancellationToken cancellationToken)
  {
    output.Write($"{message} [y/N]: ");
    return ValueTask.FromResult(input.ReadLine()?.Trim().StartsWith('y') == true);
  }

  /// <summary>Any other question (<see cref="ContextClientOptions.OnElicit"/>): a form of primitive fields.</summary>
  public ValueTask<ElicitResult> AskAsync(ElicitRequestParams request, CancellationToken cancellationToken)
  {
    if (request.Mode == "url")
    {
      // A form-only terminal host cannot send the user to a page.
      return ValueTask.FromResult(ElicitResult.Decline());
    }

    output.WriteLine(request.Message);
    var values = new Dictionary<string, JsonElement>();
    if (request.RequestedSchema.ValueKind == JsonValueKind.Object && request.RequestedSchema.TryGetProperty("properties", out var properties))
    {
      foreach (var property in properties.EnumerateObject())
      {
        var type = property.Value.TryGetProperty("type", out var t) ? t.GetString() : "string";
        output.Write($"  {property.Name} ({type}){(type == "boolean" ? " [y/n]" : "")}: ");
        var line = input.ReadLine();
        if (line is null || line.Trim().Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
          return ValueTask.FromResult(ElicitResult.Cancel());
        }

        values[property.Name] = type switch
        {
          "boolean" => JsonSerializer.SerializeToElement(line.Trim().StartsWith('y') || line.Trim().StartsWith('Y'), SampleJson.Default.Boolean),
          "integer" or "number" when double.TryParse(line, CultureInfo.InvariantCulture, out var number) => JsonSerializer.SerializeToElement(number, SampleJson.Default.Double),
          _ => JsonSerializer.SerializeToElement(line, SampleJson.Default.String),
        };
      }
    }

    return ValueTask.FromResult(ElicitResult.Accept(values));
  }
}

[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(string))]
internal sealed partial class SampleJson : JsonSerializerContext
{
}
