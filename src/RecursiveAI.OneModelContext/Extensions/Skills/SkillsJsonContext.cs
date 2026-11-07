using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RecursiveAI.OneModelContext.Extensions.Skills;

[JsonSerializable(typeof(Dictionary<string, bool>))]
internal sealed partial class SkillsJsonContext : JsonSerializerContext;
