using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests.Generated;

/// <summary>The schemas the generator writes, row by row of the signature table, and the invokers it emits.</summary>
public class GeneratedToolTests
{
  private static readonly string Int32Range = """{"type":"integer","minimum":-2147483648,"maximum":2147483647}""";

  private static ContextServer NewServer()
  {
    var server = new ContextServer("generated", "1.0", new ContextServerOptions { Codecs = [new TestServerCodec(ProtocolEra.Modern, ProtocolVersions.V20260728)] });
    new SignatureTools().AddTo(server);
    new NotesTools().AddTo(server);
    new CounterTools().AddTo(server);
    return server;
  }

  private sealed class Session : IAsyncDisposable
  {
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;

    private Session(ContextClient client, Task serving)
    {
      Client = client;
      _serving = serving;
    }

    public ContextClient Client { get; }

    public static async Task<Session> StartAsync(ContextServer server)
    {
      var (a, b) = PipeEnd.CreatePair();
      var serving = server.RunAsync(ContextTransport.Stream(a), TestContext.Current.CancellationToken);
      var client = await ContextClient.ConnectAsync(
        ContextTransport.Stream(b),
        new ContextClientOptions { Codecs = [new TestClientCodec(ProtocolEra.Modern, ProtocolVersions.V20260728)] },
        TestContext.Current.CancellationToken);
      return new Session(client, serving);
    }

    public async ValueTask DisposeAsync()
    {
      await Client.DisposeAsync();
      await _stop.CancelAsync();
      await _serving.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public async Task<CallToolResult> CallAsync(string name, string arguments = "{}") =>
      await Client.CallToolAsync(name, JsonDocument.Parse(arguments).RootElement, TestContext.Current.CancellationToken);
  }

  private static string SchemaOf(ContextServer server, string tool)
  {
    Assert.True(server.Tools.TryGet(tool, out var definition, out _), "tool " + tool);
    return definition.InputSchema.GetRawText();
  }

  private static string Object(string properties, string? required = null) =>
    "{\"type\":\"object\",\"properties\":{" + properties + "}" + (required is null ? string.Empty : ",\"required\":[" + required + "]") + ",\"additionalProperties\":false}";

  [Fact]
  public void Scalars_follow_the_table()
  {
    Assert.Equal(
      Object(
        """
        "flag":{"type":"boolean"},"a":{"type":"integer","minimum":-128,"maximum":127},"b":{"type":"integer","minimum":0,"maximum":255},"c":{"type":"integer","minimum":-32768,"maximum":32767},"d":{"type":"integer","minimum":0,"maximum":65535},"e":{"type":"integer","minimum":-2147483648,"maximum":2147483647},"f":{"type":"integer","minimum":0,"maximum":4294967295},"g":{"type":"integer","minimum":-9223372036854775808,"maximum":9223372036854775807},"h":{"type":"integer","minimum":0,"maximum":18446744073709551615},"i":{"type":"number"},"j":{"type":"number"},"k":{"type":"number"},"s":{"type":"string"},"ch":{"type":"string","minLength":1,"maxLength":1}
        """,
        "\"flag\",\"a\",\"b\",\"c\",\"d\",\"e\",\"f\",\"g\",\"h\",\"i\",\"j\",\"k\",\"s\",\"ch\""),
      SchemaOf(NewServer(), "scalars"));
  }

  [Fact]
  public void Formats_follow_the_table()
  {
    Assert.Equal(
      Object(
        """
        "id":{"type":"string","format":"uuid"},"at":{"type":"string","format":"date-time"},"offset":{"type":"string","format":"date-time"},"day":{"type":"string","format":"date"},"time":{"type":"string","format":"time"},"span":{"type":"string","pattern":"^-?(\\d+\\.)?\\d{2}:\\d{2}:\\d{2}(\\.\\d{1,7})?$"},"uri":{"type":"string","format":"uri"},"data":{"type":"string","contentEncoding":"base64"}
        """,
        "\"id\",\"at\",\"offset\",\"day\",\"time\",\"span\",\"uri\",\"data\""),
      SchemaOf(NewServer(), "formats"));
  }

  [Fact]
  public void Enums_list_member_names_and_nullable_enums_accept_null()
  {
    Assert.Equal(
      Object(
        """
        "color":{"type":"string","enum":["Red","Green","Blue"]},"maybe":{"type":["string","null"],"enum":["Red","Green","Blue",null]},"fallback":{"type":"string","enum":["Red","Green","Blue"],"default":"Green"}
        """,
        "\"color\",\"maybe\""),
      SchemaOf(NewServer(), "enums"));
  }

  [Fact]
  public void A_nullable_type_without_a_default_is_required_and_accepts_null()
  {
    Assert.Equal(
      Object(
        "\"count\":{\"type\":[\"integer\",\"null\"],\"minimum\":-2147483648,\"maximum\":2147483647},\"text\":{\"type\":[\"string\",\"null\"]},\"plain\":{\"type\":\"string\"}",
        "\"count\",\"text\",\"plain\""),
      SchemaOf(NewServer(), "nullables"));
  }

  [Fact]
  public void Defaults_are_written_for_scalars_and_a_null_default_writes_nothing()
  {
    Assert.Equal(
      Object(
        """
        "s":{"type":"string","default":"x"},"n":{"type":"integer","minimum":-2147483648,"maximum":2147483647,"default":5},"b":{"type":"boolean","default":true},"c":{"type":"string","minLength":1,"maxLength":1,"default":"q"},"d":{"type":"number","default":1.5},"none":{"type":["string","null"]},"big":{"type":"integer","minimum":-9223372036854775808,"maximum":9223372036854775807,"default":9000000000}
        """),
      SchemaOf(NewServer(), "defaults"));
  }

  [Fact]
  public void Collections_and_dictionaries_follow_the_table()
  {
    Assert.Equal(
      Object(
        "\"a\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"b\":{\"type\":\"array\",\"items\":" + Int32Range + "},\"c\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"d\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"uniqueItems\":true},\"e\":{\"type\":\"object\",\"additionalProperties\":" + Int32Range + "}",
        "\"a\",\"b\",\"c\",\"d\",\"e\""),
      SchemaOf(NewServer(), "collections"));
  }

  private const string OrderLineSchema =
    """{"type":"object","properties":{"sku":{"type":"string","description":"Stock code."},"quantity":{"type":"integer","description":"How many.","minimum":-2147483648,"maximum":2147483647},"note":{"type":["string","null"],"description":"Free text."}},"required":["sku","quantity"],"additionalProperties":false}""";

  private const string OrderSchema =
    """{"type":"object","properties":{"id":{"type":"string","description":"The order id."},"lines":{"type":"array","description":"What was bought.","items":""" + OrderLineSchema + """},"priority":{"type":"string","description":"How urgent.","enum":["Red","Green","Blue"]},"when":{"type":["string","null"],"format":"date-time"}},"required":["id","lines"],"additionalProperties":false}""";

  private const string SettingsSchema =
    """{"type":"object","properties":{"name":{"type":"string"},"level":{"type":"integer","minimum":-2147483648,"maximum":2147483647},"ratio":{"type":"number"}},"required":["name","level"],"additionalProperties":false}""";

  [Fact]
  public void Records_and_classes_become_closed_objects_with_docs_renames_and_required_rules()
  {
    Assert.Equal(
      Object(
        "\"order\":" + OrderSchema + ",\"lines\":{\"type\":\"array\",\"items\":" + OrderLineSchema + "},\"settings\":" + SettingsSchema,
        "\"order\",\"lines\",\"settings\""),
      SchemaOf(NewServer(), "objects"));
  }

  [Fact]
  public void Validation_attributes_and_description_attributes_map_to_keywords()
  {
    Assert.Equal(
      Object(
        """
        "count":{"type":"integer","minimum":1,"maximum":10},"name":{"type":"string","minLength":2,"maxLength":20},"tags":{"type":"array","items":{"type":"string"},"minItems":1,"maxItems":5},"slug":{"type":"string","pattern":"^[a-z]+$"},"mail":{"type":"string","format":"email"},"link":{"type":"string","format":"uri"},"described":{"type":"string","description":"Explicit wins."}
        """,
        "\"count\",\"name\",\"tags\",\"slug\",\"mail\",\"link\",\"described\""),
      SchemaOf(NewServer(), "annotated"));
  }

  [Fact]
  public async Task Names_descriptions_docs_and_annotations_are_listed()
  {
    await using var session = await Session.StartAsync(NewServer());
    var tools = (await session.Client.ListToolsAsync(TestContext.Current.CancellationToken)).ToDictionary(t => t.Name);

    var add = tools["add"];
    Assert.Equal("Adds two numbers.", add.Description);
    Assert.Equal("Sum", add.Title);
    Assert.True(add.Annotations!.ReadOnlyHint);
    Assert.True(add.Annotations.IdempotentHint);
    Assert.Null(add.Annotations.DestructiveHint);
    Assert.Equal(
      Object(
        "\"a\":{\"type\":\"integer\",\"description\":\"The first b addend, see int.\",\"minimum\":-2147483648,\"maximum\":2147483647},\"b\":{\"type\":\"integer\",\"description\":\"The second.\",\"minimum\":-2147483648,\"maximum\":2147483647}",
        "\"a\",\"b\""),
      add.InputSchema.GetRawText().Replace("\\u0027", "'", StringComparison.Ordinal));

    var danger = tools["danger.zone"];
    Assert.Equal("Does harm.", danger.Description);
    Assert.True(danger.Annotations!.DestructiveHint);
    Assert.False(danger.Annotations.OpenWorldHint);
    Assert.Equal("""{"type":"object","properties":{},"additionalProperties":false}""", danger.InputSchema.GetRawText());

    Assert.Contains("http_request", tools.Keys);
    Assert.Contains("make_receipt", tools.Keys);
  }

  [Fact]
  public async Task Listing_order_is_deterministic()
  {
    await using var session = await Session.StartAsync(NewServer());
    var first = (await session.Client.ListToolsAsync(TestContext.Current.CancellationToken)).Select(t => t.Name).ToList();
    var second = (await session.Client.ListToolsAsync(TestContext.Current.CancellationToken)).Select(t => t.Name).ToList();
    Assert.Equal(first, second);
    Assert.Equal(first.OrderBy(n => n, StringComparer.Ordinal), first);
  }

  [Fact]
  public async Task Arguments_bind_and_the_tool_runs()
  {
    await using var session = await Session.StartAsync(NewServer());
    var result = await session.CallAsync("add", """{"a":2,"b":3}""");
    Assert.NotEqual(true, result.IsError);
    Assert.Equal("5", result.Text);
  }

  [Fact]
  public async Task Defaults_fill_missing_optional_arguments()
  {
    await using var session = await Session.StartAsync(NewServer());
    Assert.Equal("x5Trueq1.5null9000000000", (await session.CallAsync("defaults")).Text);
    Assert.Equal("yy5Trueq1.5null9000000000", (await session.CallAsync("defaults", """{"s":"yy"}""")).Text);
  }

  [Theory]
  [InlineData("add", """{"a":1}""", "Argument 'b' is required.")]
  [InlineData("add", """{"a":1,"b":2,"z":3}""", "Unknown argument 'z'.")]
  [InlineData("add", """{"a":"x","b":2}""", "Argument 'a' must be an integer.")]
  [InlineData("add", """{"a":1.5,"b":2}""", "Argument 'a' must be an integer.")]
  [InlineData("add", """{"a":3000000000,"b":2}""", "Argument 'a' must be an integer.")]
  [InlineData("add", """{"a":null,"b":2}""", "Argument 'a' must be an integer.")]
  [InlineData("scalars", """{"flag":true,"a":300,"b":1,"c":1,"d":1,"e":1,"f":1,"g":1,"h":1,"i":1,"j":1,"k":1,"s":"s","ch":"c"}""", "Argument 'a' must be an integer.")]
  [InlineData("scalars", """{"flag":true,"a":1,"b":1,"c":1,"d":1,"e":1,"f":1,"g":1,"h":1,"i":1,"j":1,"k":1,"s":"s","ch":"cc"}""", "Argument 'ch' must be a single character.")]
  [InlineData("enums", """{"color":"blue","maybe":null}""", "Argument 'color' must be one of Red, Green, Blue.")]
  [InlineData("enums", """{"color":"Red"}""", "Argument 'maybe' is required.")]
  [InlineData("nullables", """{"count":null,"text":null,"plain":null}""", "Argument 'plain' must be a string.")]
  [InlineData("formats", """{"id":"nope","at":"2020-01-01T00:00:00Z","offset":"2020-01-01T00:00:00Z","day":"2020-01-01","time":"10:00:00","span":"01:00:00","uri":"http://x/","data":"AAAA"}""", "Argument 'id' must be a UUID string.")]
  [InlineData("objects", """{"order":{"id":"1","lines":[{"sku":"a","quantity":"many"}]},"lines":[],"settings":{"name":"n","level":1}}""", "Argument 'order.lines[0].quantity' is not valid.")]
  public async Task A_bad_argument_is_an_error_result_that_names_it(string tool, string arguments, string message)
  {
    await using var session = await Session.StartAsync(NewServer());
    var result = await session.CallAsync(tool, arguments);
    Assert.True(result.IsError);
    Assert.Equal(message, result.Text);
  }

  [Fact]
  public async Task Enums_nullables_and_nested_values_bind()
  {
    await using var session = await Session.StartAsync(NewServer());
    Assert.Equal("Blue-Green", (await session.CallAsync("enums", """{"color":"Blue","maybe":null}""")).Text);
    Assert.Equal("RedBlueRed", (await session.CallAsync("enums", """{"color":"Red","maybe":"Blue","fallback":"Red"}""")).Text);
    Assert.NotEqual(true, (await session.CallAsync("nullables", """{"count":null,"text":null,"plain":"p"}""")).IsError);
    var objects = await session.CallAsync("objects", """{"order":{"id":"o1","lines":[{"sku":"a","quantity":1}],"priority":"Blue"},"lines":[{"sku":"b","quantity":2},{"sku":"c","quantity":3}],"settings":{"name":"cfg","level":2}}""");
    Assert.Equal("o12cfg", objects.Text);
  }

  [Fact]
  public async Task RequestContext_and_CancellationToken_are_injected()
  {
    await using var session = await Session.StartAsync(NewServer());
    Assert.Equal("tools/callTrue", (await session.CallAsync("danger.zone")).Text);
  }

  [Fact]
  public async Task Records_come_back_as_structured_content_with_an_output_schema()
  {
    await using var session = await Session.StartAsync(NewServer());
    var listed = (await session.Client.ListToolsAsync(TestContext.Current.CancellationToken)).ToDictionary(t => t.Name);
    Assert.Equal(
      """{"type":"object","properties":{"id":{"type":"string"},"total":{"type":"integer","minimum":-2147483648,"maximum":2147483647}},"required":["id","total"],"additionalProperties":false}""",
      listed["make_receipt"].OutputSchema.GetRawText());
    Assert.Equal(JsonValueKind.Undefined, listed["make_receipts"].OutputSchema.ValueKind);
    Assert.Equal(JsonValueKind.Undefined, listed["add"].OutputSchema.ValueKind);

    var result = await session.CallAsync("make_receipt", """{"id":"r1","total":7}""");
    Assert.Equal("""{"id":"r1","total":7}""", result.Text);
    Assert.Equal("r1", result.Structured.GetProperty("id").GetString());
    Assert.Equal(7, result.Structured.GetProperty("total").GetInt32());

    var many = await session.CallAsync("make_receipts", """{"count":2}""");
    Assert.Equal("""[{"id":"r0","total":0},{"id":"r1","total":1}]""", many.Text);
    Assert.Equal(JsonValueKind.Undefined, many.Structured.ValueKind);
  }

  private const string ReceiptOutputSchema =
    """{"type":"object","properties":{"id":{"type":"string"},"total":{"type":"integer","minimum":-2147483648,"maximum":2147483647}},"required":["id","total"],"additionalProperties":false}""";

  [Fact]
  public async Task A_nullable_object_return_keeps_an_object_output_schema_and_null_is_content_only()
  {
    var server = NewServer();
    await using var session = await Session.StartAsync(server);
    var listed = (await session.Client.ListToolsAsync(TestContext.Current.CancellationToken)).ToDictionary(t => t.Name);
    Assert.Equal(ReceiptOutputSchema, listed["find_receipt"].OutputSchema.GetRawText());

    var none = await session.CallAsync("find_receipt", """{"id":"none"}""");
    Assert.Equal("null", none.Text);
    Assert.Equal(JsonValueKind.Undefined, none.Structured.ValueKind);
    var found = await session.CallAsync("find_receipt", """{"id":"a"}""");
    Assert.Equal("a", found.Structured.GetProperty("id").GetString());
  }

  [Fact]
  public async Task Legacy_clients_see_object_output_schemas()
  {
    var server = new ContextServer("generated", "1.0");
    new SignatureTools().AddTo(server);
    var (a, b) = PipeEnd.CreatePair();
    var serving = server.RunAsync(ContextTransport.Stream(a), TestContext.Current.CancellationToken);
    await using var client = await ContextClient.ConnectAsync(
      ContextTransport.Stream(b),
      new ContextClientOptions { Era = EraPreference.Legacy },
      TestContext.Current.CancellationToken);
    Assert.Equal(ProtocolEra.Legacy, client.Era);
    var listed = (await client.ListToolsAsync(TestContext.Current.CancellationToken)).ToDictionary(t => t.Name);
    Assert.Equal(ReceiptOutputSchema, listed["find_receipt"].OutputSchema.GetRawText());
    Assert.Equal(ReceiptOutputSchema, listed["make_receipt"].OutputSchema.GetRawText());
    Assert.Equal(JsonValueKind.Undefined, listed["make_receipts"].OutputSchema.ValueKind);
    Assert.False(serving.IsFaulted);
  }

  [Fact]
  public void Schema_text_keeps_apostrophes_and_angle_brackets_readable_on_the_wire()
  {
    var server = NewServer();
    Assert.True(server.Tools.TryGet("quote", out var definition, out _));
    var wire = global::RecursiveAI.OneModelContext.JsonRpc.JsonRpcWriter.ToJson(new global::RecursiveAI.OneModelContext.JsonRpc.JsonRpcResultResponse(global::RecursiveAI.OneModelContext.JsonRpc.JsonRpcId.FromNumber(1), JsonSerializer.SerializeToElement(definition.ToTool(), WireJsonContext.Default.Tool)));
    Assert.Contains("Don't <panic> & carry on.", wire, StringComparison.Ordinal);
    Assert.DoesNotContain('\n', wire);
  }

  [Fact]
  public async Task Returns_map_to_content()
  {
    await using var session = await Session.StartAsync(NewServer());
    Assert.Equal("16", (await session.CallAsync("square", """{"n":4}""")).Text);
    Assert.Empty((await session.CallAsync("nothing")).Content);
    Assert.Empty((await session.CallAsync("later")).Content);
    Assert.Equal("block", (await session.CallAsync("block")).Text);
    var blocks = await session.CallAsync("blocks");
    Assert.Equal(2, blocks.Content.Count);
    Assert.IsType<ImageContent>(blocks.Content[1]);
    Assert.Equal("""{"a":1}""", (await session.CallAsync("node")).Text);
    Assert.Equal("Blue", (await session.CallAsync("pick")).Text);
    Assert.Equal("null", (await session.CallAsync("nothing2")).Text);
  }

  [Fact]
  public async Task A_tool_exception_is_an_error_result_and_only_ToolException_text_is_sent()
  {
    await using var session = await Session.StartAsync(NewServer());
    var fails = await session.CallAsync("fails");
    Assert.True(fails.IsError);
    Assert.Equal("Out of cheese.", fails.Text);
    var throws = await session.CallAsync("throws");
    Assert.True(throws.IsError);
    Assert.DoesNotContain("secret detail", throws.Text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Instance_methods_use_the_registered_instance()
  {
    var server = NewServer();
    var counter = new CounterTools();
    counter.AddTo(server);
    await using var session = await Session.StartAsync(server);
    Assert.Equal("1", (await session.CallAsync("increment")).Text);
    Assert.Equal("6", (await session.CallAsync("increment", """{"by":5}""")).Text);
  }

  [Fact]
  public async Task Resources_and_templates_read()
  {
    await using var session = await Session.StartAsync(NewServer());
    var resources = await session.Client.ListResourcesAsync(TestContext.Current.CancellationToken);
    var readme = Assert.Single(resources);
    Assert.Equal("notes://readme", readme.Uri);
    Assert.Equal("text/markdown", readme.MimeType);
    Assert.Equal("Readme", readme.Title);
    Assert.Equal("The readme.", readme.Description);

    var templates = (await session.Client.ListResourceTemplatesAsync(TestContext.Current.CancellationToken)).Select(t => t.UriTemplate).Order(StringComparer.Ordinal).ToList();
    Assert.Equal(["blob://{+path}", "notes://{id}", "num://{n}"], templates);

    var text = Assert.IsType<TextResourceContents>((await session.Client.ReadResourceAsync("notes://readme", TestContext.Current.CancellationToken)).Contents[0]);
    Assert.Equal("# hello", text.Text);
    Assert.Equal("text/markdown", text.MimeType);
    var note = Assert.IsType<TextResourceContents>((await session.Client.ReadResourceAsync("notes://abc", TestContext.Current.CancellationToken)).Contents[0]);
    Assert.Equal("note abcresources/read", note.Text);
    var blob = Assert.IsType<BlobResourceContents>((await session.Client.ReadResourceAsync("blob://a/b/c", TestContext.Current.CancellationToken)).Contents[0]);
    Assert.Equal("a/b/c", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(blob.Blob)));
    var number = Assert.IsType<TextResourceContents>((await session.Client.ReadResourceAsync("num://4", TestContext.Current.CancellationToken)).Contents[0]);
    Assert.Equal("8", number.Text);
    var bad = await Assert.ThrowsAsync<ProtocolException>(async () => await session.Client.ReadResourceAsync("num://four", TestContext.Current.CancellationToken));
    Assert.Equal(-32602, bad.Code);
  }

  [Fact]
  public async Task Prompts_list_arguments_and_build_messages()
  {
    await using var session = await Session.StartAsync(NewServer());
    var prompts = (await session.Client.ListPromptsAsync(TestContext.Current.CancellationToken)).ToDictionary(p => p.Name);
    var greeting = prompts["greeting"];
    Assert.Equal("Greets someone.", greeting.Description);
    Assert.Equal(["name", "tone"], greeting.Arguments!.Select(a => a.Name));
    Assert.True(greeting.Arguments![0].Required);
    Assert.Equal("Who.", greeting.Arguments[0].Description);
    Assert.NotEqual(true, greeting.Arguments[1].Required);

    var built = await session.Client.GetPromptAsync("greeting", new Dictionary<string, string> { ["name"] = "Ada" }, TestContext.Current.CancellationToken);
    Assert.Equal("Say hello to Ada in a warm tone.", Assert.IsType<TextContent>(Assert.Single(built.Messages).Content).Text);
    var two = await session.Client.GetPromptAsync("two-step", new Dictionary<string, string> { ["topic"] = "x" }, TestContext.Current.CancellationToken);
    Assert.Equal(2, two.Messages.Count);
    var missing = await Assert.ThrowsAsync<ProtocolException>(async () => await session.Client.GetPromptAsync("greeting", null, TestContext.Current.CancellationToken));
    Assert.Equal(-32602, missing.Code);
  }
}

/// <summary>The reflection quick path writes the same schemas as the generator for the same signatures.</summary>
public class ReflectionParityTests
{
  [Fact]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Trimming", "IL2026", Justification = "The test exercises the reflection path on purpose.")]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("AOT", "IL3050", Justification = "The test exercises the reflection path on purpose.")]
  public void The_quick_path_and_the_generator_agree()
  {
    var server = new ContextServer("parity", "1.0");
    new SignatureTools().AddTo(server);

    server
      .Tool("q_scalars", (bool flag, sbyte a, byte b, short c, ushort d, int e, uint f, long g, ulong h, float i, double j, decimal k, string s, char ch) => "ok")
      .Tool("q_formats", (Guid id, DateTime at, DateTimeOffset offset, DateOnly day, TimeOnly time, TimeSpan span, Uri uri, byte[] data) => "ok")
      .Tool("q_enums", (Color color, Color? maybe, Color fallback = Color.Green) => "ok")
      .Tool("q_nullables", (int? count, string? text, string plain) => "ok")
      .Tool("q_defaults", (string s = "x", int n = 5, bool b = true, char c = 'q', double d = 1.5, string? none = null, long big = 9000000000L) => "ok")
      .Tool("q_collections", (string[] a, List<int> b, IReadOnlyList<string> c, HashSet<string> d, Dictionary<string, int> e) => "ok")
      .Tool("q_config", (Settings settings) => "ok")
      .Tool("q_annotated", (
        [Range(1, 10)] int count,
        [StringLength(20, MinimumLength = 2)] string name,
        [MinLength(1)][MaxLength(5)] string[] tags,
        [RegularExpression("^[a-z]+$")] string slug,
        [EmailAddress] string mail,
        [Url] string link,
        [Description("Explicit wins.")] string described) => "ok");

    foreach (var (generated, quick) in new[] { ("scalars", "q_scalars"), ("formats", "q_formats"), ("enums", "q_enums"), ("nullables", "q_nullables"), ("defaults", "q_defaults"), ("collections", "q_collections"), ("annotated", "q_annotated") })
    {
      Assert.True(server.Tools.TryGet(generated, out var g, out _));
      Assert.True(server.Tools.TryGet(quick, out var q, out _));
      Assert.Equal(g.InputSchema.GetRawText(), q.InputSchema.GetRawText());
    }

    Assert.True(server.Tools.TryGet("q_config", out var config, out _));
    Assert.Equal("""{"type":"object","properties":{"settings":""" + """{"type":"object","properties":{"name":{"type":"string"},"level":{"type":"integer","minimum":-2147483648,"maximum":2147483647},"ratio":{"type":"number"}},"required":["name","level"],"additionalProperties":false}""" + """},"required":["settings"],"additionalProperties":false}""", config.InputSchema.GetRawText());
  }
}
