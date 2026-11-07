using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Tests.Generated;

public enum Color
{
  Red,
  Green,
  Blue,
}

/// <summary>An order line.</summary>
/// <param name="Sku">Stock code.</param>
/// <param name="Quantity">How many.</param>
/// <param name="Note">Free text.</param>
public sealed record OrderLine(string Sku, int Quantity, string? Note = null);

/// <summary>An order.</summary>
/// <param name="Id">The order id.</param>
/// <param name="Lines">What was bought.</param>
public sealed record OrderRecord(string Id, List<OrderLine> Lines)
{
  /// <summary>How urgent.</summary>
  public Color Priority { get; init; }

  [JsonIgnore]
  public string Hidden { get; init; } = string.Empty;

  [JsonPropertyName("when")]
  public DateTimeOffset? Placed { get; init; }
}

public sealed class Settings
{
  public required string Name { get; init; }

  [JsonRequired]
  public int Level { get; init; }

  public double Ratio { get; init; }
}

public sealed record Receipt(string Id, int Total);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(OrderRecord))]
[JsonSerializable(typeof(OrderLine))]
[JsonSerializable(typeof(List<OrderLine>))]
[JsonSerializable(typeof(HashSet<string>))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(Receipt))]
[JsonSerializable(typeof(List<Receipt>))]
public sealed partial class FixtureJson : JsonSerializerContext
{
}

/// <summary>Tools whose signatures cover the rows of the signature table.</summary>
public partial class SignatureTools
{
  [Tool]
  public string Scalars(bool flag, sbyte a, byte b, short c, ushort d, int e, uint f, long g, ulong h, float i, double j, decimal k, string s, char ch) => "ok";

  [Tool]
  public string Formats(Guid id, DateTime at, DateTimeOffset offset, DateOnly day, TimeOnly time, TimeSpan span, Uri uri, byte[] data) => "ok";

  [Tool]
  public string Enums(Color color, Color? maybe, Color fallback = Color.Green) => color.ToString() + (maybe?.ToString() ?? "-") + fallback;

  [Tool]
  public string Nullables(int? count, string? text, string plain) => "ok";

  [Tool]
  public string Defaults(string s = "x", int n = 5, bool b = true, char c = 'q', double d = 1.5, string? none = null, long big = 9000000000L) => s + n + b + c + d + (none ?? "null") + big;

  [Tool]
  public string Collections(string[] a, List<int> b, IReadOnlyList<string> c, HashSet<string> d, Dictionary<string, int> e) => "ok";

  [Tool]
  public string Objects(OrderRecord order, List<OrderLine> lines, Settings settings) => order.Id + lines.Count + settings.Name;

  [Tool]
  public string Annotated(
    [Range(1, 10)] int count,
    [StringLength(20, MinimumLength = 2)] string name,
    [MinLength(1)][MaxLength(5)] string[] tags,
    [RegularExpression("^[a-z]+$")] string slug,
    [EmailAddress] string mail,
    [Url] string link,
    [Description("Explicit wins.")] string described) => "ok";

  /// <summary>Adds two numbers.</summary>
  /// <param name="a">The first <paramref name="b"/> addend, see <see cref="int"/>.</param>
  /// <param name="b">The second.</param>
  [Tool(ReadOnly = true, Idempotent = true, Title = "Sum")]
  public int Add(int a, int b) => a + b;

  [Tool(Name = "danger.zone", Destructive = true, OpenWorld = false, Description = "Does harm.")]
  public async Task<string> DeleteEverythingAsync(RequestContext context, CancellationToken cancellationToken)
  {
    await Task.Yield();
    return context.Method + cancellationToken.CanBeCanceled;
  }

  [Tool]
  public static Receipt MakeReceipt(string id, int total) => new(id, total);

  [Tool]
  public static List<Receipt> MakeReceipts(int count)
  {
    var list = new List<Receipt>();
    for (var i = 0; i < count; i++)
    {
      list.Add(new Receipt("r" + i, i));
    }

    return list;
  }

  [Tool]
  public static ValueTask<int> Square(int n) => ValueTask.FromResult(n * n);

  [Tool]
  public static void Nothing()
  {
  }

  [Tool]
  public static Task Later() => Task.CompletedTask;

  [Tool]
  public static ContentBlock Block() => TextContent.Of("block");

  [Tool]
  public static List<ContentBlock> Blocks() => [TextContent.Of("one"), new ImageContent { Data = "AAAA", MimeType = "image/png" }];

  [Tool]
  public static JsonNode Node() => new JsonObject { ["a"] = 1 };

  [Tool]
  public static Color Pick() => Color.Blue;

  [Tool]
  public static string? Nothing2() => null;

  [Tool]
  public static string Fails() => throw new ToolException("Out of cheese.");

  [Tool]
  public static string Throws() => throw new InvalidOperationException("secret detail");

  [Tool]
  public static string HTTPRequest() => "ok";
}

/// <summary>Resources and prompts.</summary>
public partial class NotesTools
{
  /// <summary>The readme.</summary>
  [Resource("notes://readme", MimeType = "text/markdown", Title = "Readme")]
  public static string Readme() => "# hello";

  [Resource("notes://{id}")]
  public static string Note(string id, RequestContext context) => "note " + id + context.Method;

  [Resource("blob://{+path}", Name = "blob")]
  public static async Task<byte[]> BlobAsync(string path)
  {
    await Task.Yield();
    return System.Text.Encoding.UTF8.GetBytes(path);
  }

  [Resource("num://{n}")]
  public static string Number(int n, double scale = 2) => (n * scale).ToString(System.Globalization.CultureInfo.InvariantCulture);

  /// <summary>Greets someone.</summary>
  /// <param name="name">Who.</param>
  /// <param name="tone">How.</param>
  [Prompt]
  public static string Greeting(string name, string tone = "warm") => $"Say hello to {name} in a {tone} tone.";

  [Prompt("two-step")]
  public static List<PromptMessage> TwoStep(string topic) =>
  [
    new PromptMessage { Role = "user", Content = TextContent.Of("Explain " + topic) },
    new PromptMessage { Role = "assistant", Content = TextContent.Of("Sure.") },
  ];
}

/// <summary>A server-level instance class with state, to prove instance methods work.</summary>
public partial class CounterTools
{
  private int _count;

  [Tool]
  public int Increment(int by = 1) => Interlocked.Add(ref _count, by);
}
