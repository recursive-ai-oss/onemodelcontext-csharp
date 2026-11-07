using System;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneModelContext.JsonRpc;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests.JsonRpc;

public class JsonRpcReaderTests
{
  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData("{")]
  [InlineData("not json")]
  [InlineData("{\"jsonrpc\":\"2.0\",")]
  [InlineData("\u0000\u0001")]
  [InlineData("{\"a\":1}}")]
  public void Text_that_is_not_json_is_a_parse_error_with_a_null_id(string text)
  {
    var read = JsonRpcReader.Read(text);

    Assert.Equal(JsonRpcReadKind.Invalid, read.Kind);
    Assert.Equal(JsonRpcErrorCodes.ParseError, read.Error!.Error.Code);
    Assert.False(read.Error.Id.HasValue);
  }

  [Fact]
  public void Invalid_utf8_is_a_parse_error()
  {
    var read = JsonRpcReader.Read(new byte[] { 0x7b, 0xff, 0xfe, 0x7d });

    Assert.Equal(JsonRpcErrorCodes.ParseError, read.Error!.Error.Code);
  }

  [Theory]
  [InlineData("42")]
  [InlineData("\"hello\"")]
  [InlineData("null")]
  [InlineData("true")]
  [InlineData("[]")]
  public void Json_that_is_not_an_object_is_an_invalid_request(string text)
  {
    var read = JsonRpcReader.Read(text);

    Assert.Equal(JsonRpcReadKind.Invalid, read.Kind);
    Assert.Equal(JsonRpcErrorCodes.InvalidRequest, read.Error!.Error.Code);
  }

  [Theory]
  [InlineData("""{"jsonrpc":"2.0","id":1}""")]
  [InlineData("""{"jsonrpc":"2.0","id":1,"method":5}""")]
  [InlineData("""{"id":1,"method":"ping"}""")]
  [InlineData("""{"jsonrpc":"1.0","id":1,"method":"ping"}""")]
  [InlineData("""{"jsonrpc":"2.0","id":null,"method":"ping"}""")]
  [InlineData("""{"jsonrpc":"2.0","id":true,"method":"ping"}""")]
  [InlineData("""{"jsonrpc":"2.0","id":{"a":1},"method":"ping"}""")]
  [InlineData("""{"jsonrpc":"2.0","result":{}}""")]
  [InlineData("""{"jsonrpc":"2.0","id":1,"error":"boom"}""")]
  [InlineData("""{"jsonrpc":"2.0","id":1,"error":{"code":"x","message":"m"}}""")]
  public void Objects_that_are_not_messages_are_invalid_requests(string text)
  {
    var read = JsonRpcReader.Read(text);

    Assert.Equal(JsonRpcReadKind.Invalid, read.Kind);
    Assert.Equal(JsonRpcErrorCodes.InvalidRequest, read.Error!.Error.Code);
  }

  [Fact]
  public void An_invalid_request_with_a_usable_id_echoes_it()
  {
    var read = JsonRpcReader.Read("""{"jsonrpc":"2.0","id":"abc"}""");

    Assert.Equal(JsonRpcId.FromString("abc"), read.Error!.Id);
  }

  [Fact]
  public void A_request_keeps_its_id_method_and_params()
  {
    var read = JsonRpcReader.Read("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"x","_meta":{"k":1}}}""");

    var request = Assert.IsType<JsonRpcRequest>(read.Message);
    Assert.Equal(JsonRpcId.FromNumber(7), request.Id);
    Assert.Equal("tools/call", request.Method);
    Assert.Equal("x", request.Params.GetProperty("name").GetString());
    Assert.Equal(1, request.Params.GetProperty("_meta").GetProperty("k").GetInt32());
  }

  [Theory]
  [InlineData("""{"jsonrpc":"2.0","id":1.0,"method":"a"}""", "1.0", true)]
  [InlineData("""{"jsonrpc":"2.0","id":12345678901234567890,"method":"a"}""", "12345678901234567890", true)]
  [InlineData("""{"jsonrpc":"2.0","id":"1","method":"a"}""", "1", false)]
  [InlineData("""{"jsonrpc":"2.0","id":"","method":"a"}""", "", false)]
  public void Ids_are_kept_exactly_as_written(string text, string expectedText, bool isNumber)
  {
    var request = Assert.IsType<JsonRpcRequest>(JsonRpcReader.Read(text).Message);

    Assert.Equal(expectedText, request.Id.Text);
    Assert.Equal(isNumber, request.Id.IsNumber);

    // The same id comes back byte for byte in a response.
    var json = JsonRpcWriter.ToJson(new JsonRpcResultResponse(request.Id, default));
    Assert.Contains(isNumber ? "\"id\":" + expectedText + "," : "\"id\":\"" + expectedText + "\",", json, StringComparison.Ordinal);
  }

  [Fact]
  public void String_and_number_ids_with_the_same_text_are_different()
  {
    Assert.NotEqual(JsonRpcId.FromString("1"), JsonRpcId.FromNumber(1));
  }

  [Fact]
  public void A_message_without_an_id_is_a_notification()
  {
    var read = JsonRpcReader.Read("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

    var notification = Assert.IsType<JsonRpcNotification>(read.Message);
    Assert.Equal("notifications/initialized", notification.Method);
    Assert.Equal(JsonValueKind.Undefined, notification.Params.ValueKind);
  }

  [Theory]
  [InlineData("""{"jsonrpc":"2.0","id":1,"method":"a","params":5}""")]
  [InlineData("""{"jsonrpc":"2.0","id":1,"method":"a","params":"s"}""")]
  [InlineData("""{"jsonrpc":"2.0","id":1,"method":"a","params":null}""")]
  public void Params_that_are_not_structured_are_treated_as_absent(string text)
  {
    var request = Assert.IsType<JsonRpcRequest>(JsonRpcReader.Read(text).Message);

    Assert.Equal(JsonValueKind.Undefined, request.Params.ValueKind);
  }

  [Fact]
  public void A_success_response_is_read()
  {
    var read = JsonRpcReader.Read("""{"jsonrpc":"2.0","id":3,"result":{"ok":true}}""");

    var response = Assert.IsType<JsonRpcResultResponse>(read.Message);
    Assert.Equal(JsonRpcId.FromNumber(3), response.Id);
    Assert.True(response.Result.GetProperty("ok").GetBoolean());
  }

  [Fact]
  public void An_error_response_with_a_null_id_is_read()
  {
    var read = JsonRpcReader.Read("""{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"Parse error","data":{"x":1}}}""");

    var response = Assert.IsType<JsonRpcErrorResponse>(read.Message);
    Assert.False(response.Id.HasValue);
    Assert.Equal(-32700, response.Error.Code);
    Assert.Equal(1, response.Error.Data.GetProperty("x").GetInt32());
  }

  [Fact]
  public void Nesting_deeper_than_the_limit_is_a_parse_error_not_an_exception()
  {
    var depth = 200;
    var text = new StringBuilder("""{"jsonrpc":"2.0","id":1,"method":"a","params":""");
    text.Append('[', depth).Append(']', depth).Append('}');

    var read = JsonRpcReader.Read(text.ToString());

    Assert.Equal(JsonRpcErrorCodes.ParseError, read.Error!.Error.Code);
  }

  [Fact]
  public void Nesting_within_the_limit_is_accepted()
  {
    var text = new StringBuilder("""{"jsonrpc":"2.0","id":1,"method":"a","params":{"a":""");
    text.Append('[', 40).Append(']', 40).Append("}}");

    Assert.IsType<JsonRpcRequest>(JsonRpcReader.Read(text.ToString()).Message);
  }

  [Fact]
  public void A_message_over_the_size_limit_is_an_invalid_request()
  {
    var options = new JsonRpcReaderOptions { MaxMessageBytes = 64 };
    var text = """{"jsonrpc":"2.0","id":1,"method":"a","params":{"padding":"0123456789012345678901234567890123456789"}}""";

    var read = JsonRpcReader.Read(text, options);

    Assert.Equal(JsonRpcErrorCodes.InvalidRequest, read.Error!.Error.Code);
  }

  [Fact]
  public void A_batch_is_read_into_its_own_shape_with_per_item_errors()
  {
    var read = JsonRpcReader.Read("""[{"jsonrpc":"2.0","id":1,"method":"a"},{"jsonrpc":"2.0","method":"n"},5,{"jsonrpc":"2.0","id":2,"result":{}}]""");

    Assert.Equal(JsonRpcReadKind.Batch, read.Kind);
    Assert.Equal(4, read.Items!.Count);
    Assert.IsType<JsonRpcRequest>(read.Items[0].Message);
    Assert.IsType<JsonRpcNotification>(read.Items[1].Message);
    Assert.Equal(JsonRpcReadKind.Invalid, read.Items[2].Kind);
    Assert.IsType<JsonRpcResultResponse>(read.Items[3].Message);
  }

  [Fact]
  public void An_empty_batch_is_an_invalid_request()
  {
    Assert.Equal(JsonRpcErrorCodes.InvalidRequest, JsonRpcReader.Read("[]").Error!.Error.Code);
  }

  [Fact]
  public void A_batch_over_the_item_limit_is_an_invalid_request()
  {
    var options = new JsonRpcReaderOptions { MaxBatchItems = 2 };

    var read = JsonRpcReader.Read("""[{"jsonrpc":"2.0","method":"a"},{"jsonrpc":"2.0","method":"b"},{"jsonrpc":"2.0","method":"c"}]""", options);

    Assert.Equal(JsonRpcReadKind.Invalid, read.Kind);
  }

  [Fact]
  public void Random_bytes_never_throw()
  {
    var random = new Random(12345);
    var bytes = new byte[300];
    for (var i = 0; i < 2000; i++)
    {
      random.NextBytes(bytes.AsSpan(0, random.Next(1, bytes.Length)));
      var read = JsonRpcReader.Read(bytes.AsMemory(0, random.Next(1, bytes.Length)));
      Assert.NotNull(read);
    }
  }

  [Fact]
  public void Mutated_valid_messages_never_throw()
  {
    var original = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"x","arguments":{"a":[1,2,3]},"_meta":{"progressToken":"t"}}}""");
    var random = new Random(99);
    for (var i = 0; i < 3000; i++)
    {
      var copy = (byte[])original.Clone();
      for (var j = 0; j < 1 + random.Next(4); j++)
      {
        copy[random.Next(copy.Length)] = (byte)random.Next(256);
      }

      Assert.NotNull(JsonRpcReader.Read(copy));
    }
  }
}

public class JsonRpcWriterTests
{
  [Fact]
  public void A_request_round_trips()
  {
    var request = new JsonRpcRequest(JsonRpcId.FromString("a"), "m", Json.Parse("""{"x":[1,2]}"""));

    var read = JsonRpcReader.Read(JsonRpcWriter.ToUtf8Bytes(request));

    var back = Assert.IsType<JsonRpcRequest>(read.Message);
    Assert.Equal(request.Id, back.Id);
    Assert.Equal("m", back.Method);
    Assert.True(Json.SameMeaning(request.Params, back.Params));
  }

  [Fact]
  public void An_error_without_an_id_writes_null()
  {
    var json = JsonRpcWriter.ToJson(new JsonRpcErrorResponse(default, new JsonRpcError(-32700, "Parse error")));

    Assert.Equal("""{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"Parse error"}}""", json);
  }

  [Fact]
  public void A_result_response_without_a_result_writes_an_empty_object()
  {
    var json = JsonRpcWriter.ToJson(new JsonRpcResultResponse(JsonRpcId.FromNumber(1), default));

    Assert.Equal("""{"jsonrpc":"2.0","id":1,"result":{}}""", json);
  }

  [Fact]
  public void A_notification_has_no_id()
  {
    var json = JsonRpcWriter.ToJson(new JsonRpcNotification("notifications/initialized"));

    Assert.Equal("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", json);
  }

  [Fact]
  public void Error_data_is_written()
  {
    var json = JsonRpcWriter.ToJson(new JsonRpcErrorResponse(JsonRpcId.FromNumber(2), new JsonRpcError(-32022, "bad", Json.Parse("""{"supported":["2026-07-28"]}"""))));

    Assert.Contains("\"data\":{\"supported\":[\"2026-07-28\"]}", json, StringComparison.Ordinal);
  }
}
