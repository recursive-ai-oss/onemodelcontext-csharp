using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Extensions.Tasks;

/// <summary>The params of <c>tasks/update</c>.</summary>
internal sealed class TaskUpdateParams
{
  [JsonPropertyName("taskId")]
  public required string TaskId { get; init; }

  [JsonPropertyName("inputResponses")]
  public required Dictionary<string, JsonElement> InputResponses { get; init; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TaskUpdateParams))]
[JsonSerializable(typeof(Dictionary<string, InputRequest>))]
internal sealed partial class TasksJsonContext : JsonSerializerContext;
