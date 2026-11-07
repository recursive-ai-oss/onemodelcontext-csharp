using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests.Protocol;

public class RecordRoundTripTests
{
  private static JsonElement RoundTrip<T>(JsonTypeInfo<T> info, string json)
  {
    var value = JsonSerializer.Deserialize(json, info);
    Assert.NotNull(value);
    return JsonSerializer.SerializeToElement(value, info);
  }

  private static void AssertSame<T>(JsonTypeInfo<T> info, string json)
  {
    var back = RoundTrip(info, json);
    Assert.True(Json.SameMeaning(Json.Parse(json), back), "Round trip changed the message.\nin:  " + json + "\nout: " + back.GetRawText());
  }

  private static readonly WireJsonContext C = WireJsonContext.Default;

  [Fact]
  public void Every_request_and_result_keeps_its_meta_and_unknown_fields()
  {
    // Each sample carries members the record does not model, at the top level and inside _meta, and each must survive.
    AssertSame(C.CallToolRequestParams, """{"name":"t","arguments":{"a":1,"b":[true,null]},"inputResponses":{"k":{"action":"accept"}},"requestState":"abc","_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"c","version":"1","futureField":1},"io.modelcontextprotocol/clientCapabilities":{"elicitation":{"form":{}},"extensions":{"io.example/x":{"a":1}},"futureCap":{}},"progressToken":"p1","traceparent":"00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01","com.example/vendor":{"x":1}},"vendorTopLevel":{"z":2}}""");
    AssertSame(C.PaginatedRequestParams, """{"cursor":"c","_meta":{"progressToken":7,"x":1},"extra":1}""");
    AssertSame(C.ReadResourceRequestParams, """{"uri":"file:///a","_meta":{"x":1},"extra":[1]}""");
    AssertSame(C.GetPromptRequestParams, """{"name":"p","arguments":{"a":"b"},"_meta":{"x":1},"extra":1}""");
    AssertSame(C.CompleteRequestParams, """{"ref":{"type":"ref/prompt","name":"p","extra":1},"argument":{"name":"a","value":"v"},"context":{"arguments":{"x":"y"}},"_meta":{"x":1}}""");
    AssertSame(C.SubscriptionsListenRequestParams, """{"notifications":{"toolsListChanged":true,"resourceSubscriptions":["a","b"],"future":1},"_meta":{"x":1}}""");
    AssertSame(C.InitializeRequestParams, """{"protocolVersion":"2025-11-25","capabilities":{"roots":{"listChanged":true},"sampling":{"tools":{}},"elicitation":{"form":{},"url":{}},"tasks":{"requests":{}},"experimental":{"a":{"b":1}}},"clientInfo":{"name":"c","title":"C","version":"1","description":"d","websiteUrl":"https://example.com","icons":[{"src":"https://example.com/i.png","sizes":["48x48"],"theme":"dark"}]},"_meta":{"x":1}}""");
    AssertSame(C.ListToolsResult, """{"resultType":"complete","tools":[{"name":"t","title":"T","description":"d","inputSchema":{"type":"object","properties":{"a":{"type":"string"}},"$schema":"https://json-schema.org/draft/2020-12/schema"},"outputSchema":{"type":"object"},"annotations":{"readOnlyHint":true,"destructiveHint":false,"title":"x","future":1},"icons":[{"src":"data:image/png;base64,AA=="}],"_meta":{"x":1},"execution":{"taskSupport":"optional"}}],"nextCursor":"n","ttlMs":60000,"cacheScope":"private","_meta":{"io.modelcontextprotocol/serverInfo":{"name":"s","version":"2"},"y":2},"extra":1}""");
    AssertSame(C.ListResourcesResult, """{"resources":[{"uri":"file:///a","name":"a","mimeType":"text/plain","size":12,"annotations":{"audience":["user"],"priority":0.5,"lastModified":"2026-01-01T00:00:00Z"},"_meta":{"x":1},"extra":1}],"ttlMs":0,"cacheScope":"public"}""");
    AssertSame(C.ListResourceTemplatesResult, """{"resourceTemplates":[{"uriTemplate":"file:///{p}","name":"t","extra":1}],"nextCursor":"x"}""");
    AssertSame(C.ListPromptsResult, """{"prompts":[{"name":"p","description":"d","arguments":[{"name":"a","required":true,"extra":1}],"extra":1}]}""");
    AssertSame(C.GetPromptResult, """{"description":"d","messages":[{"role":"user","content":{"type":"text","text":"hi","annotations":{"priority":1}}},{"role":"assistant","content":{"type":"image","data":"AA==","mimeType":"image/png"}}],"_meta":{"x":1}}""");
    AssertSame(C.ReadResourceResult, """{"contents":[{"uri":"file:///a","mimeType":"text/plain","text":"hello","_meta":{"x":1}},{"uri":"file:///b","blob":"AAEC","extra":1}],"ttlMs":1000,"cacheScope":"private"}""");
    AssertSame(C.CompleteResult, """{"completion":{"values":["a","b"],"total":10,"hasMore":true,"extra":1},"_meta":{"x":1}}""");
    AssertSame(C.DiscoverResult, """{"resultType":"complete","supportedVersions":["2026-07-28","2025-11-25"],"capabilities":{"tools":{"listChanged":true},"resources":{"subscribe":false,"listChanged":true},"prompts":{},"logging":{},"completions":{},"extensions":{"io.modelcontextprotocol/ui":{}}},"instructions":"i","ttlMs":3600000,"cacheScope":"public","_meta":{"io.modelcontextprotocol/serverInfo":{"name":"s","version":"1"}}}""");
    AssertSame(C.InitializeResult, """{"protocolVersion":"2025-06-18","capabilities":{"tools":{"listChanged":false},"logging":{},"experimental":{"x":{}}},"serverInfo":{"name":"s","version":"1"},"instructions":"i","_meta":{"x":1}}""");
    AssertSame(C.InputRequiredResult, """{"resultType":"input_required","inputRequests":{"a":{"method":"elicitation/create","params":{"message":"m","requestedSchema":{"type":"object","properties":{}}}},"b":{"method":"roots/list"}},"requestState":"opaque","_meta":{"x":1}}""");
    AssertSame(C.CallToolResult, """{"resultType":"complete","content":[{"type":"text","text":"t"},{"type":"audio","data":"AA==","mimeType":"audio/wav"},{"type":"resource_link","uri":"file:///x","name":"x","size":3},{"type":"resource","resource":{"uri":"file:///r","text":"body"}}],"structuredContent":{"n":1},"isError":false,"_meta":{"x":1},"extra":1}""");
    AssertSame(C.CreateMessageRequestParams, """{"messages":[{"role":"user","content":{"type":"text","text":"q"}}],"modelPreferences":{"hints":[{"name":"m"}],"costPriority":0.1},"systemPrompt":"s","includeContext":"none","temperature":0.5,"maxTokens":100,"stopSequences":["x"],"metadata":{"a":1},"tools":[{"name":"t","inputSchema":{"type":"object"}}],"toolChoice":{"mode":"auto"},"extra":1}""");
    AssertSame(C.CreateMessageResult, """{"role":"assistant","content":[{"type":"text","text":"a"},{"type":"tool_use","id":"u1","name":"t","input":{"a":1}}],"model":"m","stopReason":"toolUse","_meta":{"x":1}}""");
    AssertSame(C.ListRootsResult, """{"roots":[{"uri":"file:///w","name":"w","_meta":{"x":1}}]}""");
    AssertSame(C.ElicitRequestParams, """{"mode":"form","message":"m","requestedSchema":{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]},"_meta":{"x":1}}""");
    AssertSame(C.ElicitRequestParams, """{"mode":"url","message":"m","url":"https://example.com/auth","elicitationId":"e1"}""");
    AssertSame(C.ElicitResult, """{"action":"accept","content":{"a":"x","b":2,"c":true,"d":["p","q"]},"_meta":{"x":1}}""");
    AssertSame(C.ProgressNotificationParams, """{"progressToken":"t","progress":1.5,"total":10,"message":"half","_meta":{"io.modelcontextprotocol/subscriptionId":3},"extra":1}""");
    AssertSame(C.CancelledNotificationParams, """{"requestId":"r","reason":"x","extra":1}""");
    AssertSame(C.LoggingMessageNotificationParams, """{"level":"info","logger":"l","data":{"a":[1,2]},"extra":1}""");
    AssertSame(C.ResourceUpdatedNotificationParams, """{"uri":"file:///a","extra":1}""");
    AssertSame(C.ElicitationCompleteNotificationParams, """{"elicitationId":"e","extra":1}""");
    AssertSame(C.SetLevelRequestParams, """{"level":"debug"}""");
    AssertSame(C.ResourceSubscriptionParams, """{"uri":"file:///a"}""");
    AssertSame(C.SubscriptionsAcknowledgedNotificationParams, """{"notifications":{"toolsListChanged":true}}""");
    AssertSame(C.UnsupportedProtocolVersionData, """{"supported":["2026-07-28"],"requested":"1999-01-01"}""");
    AssertSame(C.MissingRequiredClientCapabilityData, """{"requiredCapabilities":{"elicitation":{"form":{}}}}""");
  }

  [Fact]
  public void Trace_context_and_vendor_keys_in_meta_survive()
  {
    var result = JsonSerializer.Deserialize("""{"content":[],"_meta":{"traceparent":"00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01","tracestate":"a=b","baggage":"k=v","io.example/key":[1,2]}}""", C.CallToolResult)!;

    Assert.Equal("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01", result.Meta!.TraceParent);
    Assert.Equal("a=b", result.Meta.TraceState);
    Assert.Equal("k=v", result.Meta.Baggage);
    Assert.Equal(2, result.Meta.AdditionalProperties!["io.example/key"].GetArrayLength());
  }

  [Fact]
  public void An_absent_result_type_reads_as_complete()
  {
    var legacy = JsonSerializer.Deserialize("""{"content":[{"type":"text","text":"x"}]}""", C.CallToolResult)!;
    var modern = JsonSerializer.Deserialize("""{"resultType":"input_required"}""", C.InputRequiredResult)!;

    Assert.Null(legacy.ResultType);
    Assert.Equal("complete", legacy.EffectiveResultType);
    Assert.Equal("input_required", modern.EffectiveResultType);
    Assert.DoesNotContain("resultType", JsonSerializer.Serialize(legacy, C.CallToolResult), StringComparison.Ordinal);
  }

  [Fact]
  public void An_unknown_content_type_is_kept_and_written_back()
  {
    var json = """{"content":[{"type":"hologram","payload":{"a":1},"_meta":{"x":1}},{"type":"text","text":"t"}]}""";

    var result = JsonSerializer.Deserialize(json, C.CallToolResult)!;

    var unknown = Assert.IsType<UnknownContent>(result.Content[0]);
    Assert.Equal("hologram", unknown.Type);
    Assert.IsType<TextContent>(result.Content[1]);
    Assert.True(Json.SameMeaning(Json.Parse(json), JsonSerializer.SerializeToElement(result, C.CallToolResult)));
  }

  [Fact]
  public void A_content_block_without_a_type_is_unknown_not_an_exception()
  {
    var result = JsonSerializer.Deserialize("""{"content":[{"text":"no type"},5]}""", C.CallToolResult)!;

    Assert.All(result.Content, block => Assert.IsType<UnknownContent>(block));
  }

  [Fact]
  public void Resource_contents_are_text_or_blob_by_their_members()
  {
    var result = JsonSerializer.Deserialize("""{"contents":[{"uri":"a","text":"t"},{"uri":"b","blob":"AA=="}]}""", C.ReadResourceResult)!;

    Assert.IsType<TextResourceContents>(result.Contents[0]);
    Assert.IsType<BlobResourceContents>(result.Contents[1]);
  }

  [Fact]
  public void A_single_sampling_block_is_written_as_an_object_and_many_as_an_array()
  {
    var one = new SamplingMessage { Role = "user", Content = [TextContent.Of("a")] };
    var two = new SamplingMessage { Role = "user", Content = [TextContent.Of("a"), TextContent.Of("b")] };

    Assert.Equal(JsonValueKind.Object, JsonSerializer.SerializeToElement(one, C.SamplingMessage).GetProperty("content").ValueKind);
    Assert.Equal(JsonValueKind.Array, JsonSerializer.SerializeToElement(two, C.SamplingMessage).GetProperty("content").ValueKind);
    Assert.Single(JsonSerializer.Deserialize("""{"role":"user","content":{"type":"text","text":"x"}}""", C.SamplingMessage)!.Content);
    Assert.Equal(2, JsonSerializer.Deserialize("""{"role":"user","content":[{"type":"text","text":"x"},{"type":"text","text":"y"}]}""", C.SamplingMessage)!.Content.Count);
  }

  [Fact]
  public void Unset_dynamic_members_are_not_written_and_json_null_is_kept()
  {
    var withoutStructured = JsonSerializer.SerializeToElement(CallToolResult.FromText("x"), C.CallToolResult);
    var withNull = JsonSerializer.Deserialize("""{"content":[],"structuredContent":null}""", C.CallToolResult)!;

    Assert.False(withoutStructured.TryGetProperty("structuredContent", out _));
    Assert.Equal(JsonValueKind.Null, withNull.StructuredContent.ValueKind);
    Assert.True(JsonSerializer.SerializeToElement(withNull, C.CallToolResult).TryGetProperty("structuredContent", out _));
  }

  [Fact]
  public void Missing_required_members_are_a_json_exception_for_the_codec_to_map()
  {
    Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"arguments":{}}""", C.CallToolRequestParams));
    Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"content":[{"type":"text"}]}""", C.CallToolResult));
  }

  [Fact]
  public void Ids_in_params_keep_their_kind()
  {
    var str = JsonSerializer.Deserialize("""{"requestId":"abc"}""", C.CancelledNotificationParams)!;
    var num = JsonSerializer.Deserialize("""{"requestId":42}""", C.CancelledNotificationParams)!;

    Assert.Equal(JsonRpcId.FromString("abc"), str.RequestId);
    Assert.Equal(JsonRpcId.FromNumber(42), num.RequestId);
    Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"requestId":true}""", C.CancelledNotificationParams));
  }

  [Fact]
  public void The_call_tool_result_helpers_read_text_and_errors()
  {
    var result = new CallToolResult { Content = [TextContent.Of("a"), new ImageContent { Data = "AA==", MimeType = "image/png" }, TextContent.Of("b")] };

    Assert.Equal("a\nb", result.Text);
    Assert.True(CallToolResult.FromError("bad").IsError);
    Assert.Equal("bad", CallToolResult.FromError("bad").Text);
  }
}

public class ProtocolConstantsTests
{
  [Fact]
  public void Error_codes_match_the_2026_07_28_schema()
  {
    Assert.Equal(-32020, ProtocolErrorCodes.HeaderMismatch);
    Assert.Equal(-32021, ProtocolErrorCodes.MissingRequiredClientCapability);
    Assert.Equal(-32022, ProtocolErrorCodes.UnsupportedProtocolVersion);
    Assert.Equal(-32700, JsonRpcErrorCodes.ParseError);
    Assert.Equal(-32600, JsonRpcErrorCodes.InvalidRequest);
    Assert.Equal(-32601, JsonRpcErrorCodes.MethodNotFound);
    Assert.Equal(-32602, JsonRpcErrorCodes.InvalidParams);
    Assert.Equal(-32603, JsonRpcErrorCodes.InternalError);
  }

  [Fact]
  public void Resource_not_found_depends_on_the_era()
  {
    Assert.Equal(-32602, ProtocolErrorCodes.ResourceNotFound(ProtocolEra.Modern));
    Assert.Equal(-32002, ProtocolErrorCodes.ResourceNotFound(ProtocolEra.Legacy));
  }

  [Fact]
  public void The_default_supported_set_is_newest_first_and_has_no_draft()
  {
    Assert.Equal(["2026-07-28", "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"], ProtocolVersions.Supported);
    Assert.Equal(ProtocolEra.Modern, ProtocolVersions.EraOf("2026-07-28"));
    Assert.All(ProtocolVersions.SupportedLegacy, v => Assert.Equal(ProtocolEra.Legacy, ProtocolVersions.EraOf(v)));
    Assert.Throws<ArgumentException>(() => ProtocolVersions.EraOf("DRAFT-2026-v1"));
    Assert.True(ProtocolVersions.AllowsBatches("2025-03-26"));
    Assert.False(ProtocolVersions.AllowsBatches("2025-06-18"));
  }

  [Fact]
  public void Wire_names_are_the_reserved_meta_keys_and_headers()
  {
    Assert.Equal("io.modelcontextprotocol/protocolVersion", WireNames.MetaProtocolVersion);
    Assert.Equal("Mcp-Method", WireNames.HeaderMethod);
    Assert.Equal("Mcp-Name", WireNames.HeaderName);
    Assert.Equal("Mcp-Session-Id", WireNames.HeaderSessionId);
  }

  [Fact]
  public void The_version_error_names_the_eras_on_both_sides()
  {
    var error = new ProtocolVersionException(["2026-07-28", "2025-11-25", "2024-11-05"], ["2025-03-26"], "2026-07-28");

    Assert.Contains("modern 2026-07-28", error.Message, StringComparison.Ordinal);
    Assert.Contains("legacy 2025-11-25, 2024-11-05", error.Message, StringComparison.Ordinal);
    Assert.Contains("the peer speaks legacy 2025-03-26", error.Message, StringComparison.Ordinal);
    Assert.Equal(-32022, error.Code);
    Assert.Equal("2026-07-28", error.ErrorData.GetProperty("requested").GetString());
  }

  [Fact]
  public void Errors_from_the_peer_map_to_their_exception_types()
  {
    var version = ProtocolException.FromError(new JsonRpcError(-32022, "x", Json.Parse("""{"supported":["2025-11-25"],"requested":"2026-07-28"}""")));
    var header = ProtocolException.FromError(new JsonRpcError(-32020, "mismatch"));
    var capability = ProtocolException.FromError(new JsonRpcError(-32021, "missing", Json.Parse("""{"requiredCapabilities":{"elicitation":{}}}""")));
    var other = ProtocolException.FromError(new JsonRpcError(-32602, "bad"));

    Assert.Equal(["2025-11-25"], Assert.IsType<ProtocolVersionException>(version).Remote);
    Assert.IsType<HeaderMismatchException>(header);
    Assert.NotNull(Assert.IsType<MissingCapabilityException>(capability).Required.Elicitation);
    Assert.Equal(typeof(ProtocolException), other.GetType());
  }
}
