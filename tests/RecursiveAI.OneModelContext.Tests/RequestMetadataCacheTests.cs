using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire.Modern;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

public class RequestMetadataCacheTests
{
  private const string Metadata = """
    {
      "io.modelcontextprotocol/protocolVersion":"2026-07-28",
      "io.modelcontextprotocol/clientInfo":{"name":"test","version":"1","title":"title","description":"description","websiteUrl":"https://example.com","unknown":1,
        "icons":[null,{"src":"https://example.com/icon","sizes":["48x48"],"mimeType":"image/png","theme":"light","future":2}]},
      "io.modelcontextprotocol/clientCapabilities":{
        "experimental":{"test":{}},"extensions":{"test/ext":{}},"tasks":{},"future":3,
        "roots":{"listChanged":true,"future":4},"sampling":{"context":{},"tools":{},"future":5},"elicitation":{"form":{},"url":{},"future":6}},
      "io.modelcontextprotocol/serverInfo":{"name":"server","version":"2"},
      "io.modelcontextprotocol/logLevel":"info","progressToken":"p","io.modelcontextprotocol/subscriptionId":7,
      "traceparent":"trace","tracestate":"state","baggage":"bag","future/meta":{"nested":7}
    }
    """;

  [Fact]
  public void Hits_preserve_every_field_and_do_not_share_mutable_records()
  {
    var cache = new RequestMetadataCache();
    var element = Json.Parse(Metadata);
    var expected = JsonSerializer.Serialize(element.Deserialize(WireJsonContext.Default.MetaObject), WireJsonContext.Default.MetaObject);
    for (var i = 0; i < 3; i++)
    {
      var meta = cache.Read(element);
      Assert.Equal(expected, JsonSerializer.Serialize(meta, WireJsonContext.Default.MetaObject));
      meta.AdditionalProperties!.Clear();
      meta.ClientInfo!.AdditionalProperties!.Clear();
      meta.ClientInfo.Icons![1].Sizes!.Clear();
      meta.ClientInfo.Icons[1].AdditionalProperties!.Clear();
      meta.ClientInfo.Icons.Clear();
      meta.ServerInfo!.AdditionalProperties = new Dictionary<string, JsonElement>();
      var capabilities = meta.ClientCapabilities!;
      capabilities.Extensions!.Clear();
      capabilities.Experimental!.Clear();
      capabilities.AdditionalProperties!.Clear();
      capabilities.Roots!.AdditionalProperties!.Clear();
      capabilities.Sampling!.AdditionalProperties!.Clear();
      capabilities.Elicitation!.AdditionalProperties!.Clear();
    }
  }

  [Fact]
  public async Task Concurrent_clients_and_changed_capabilities_use_the_exact_metadata()
  {
    var cache = new RequestMetadataCache();
    var work = new List<Task>();
    for (var client = 0; client < 8; client++)
    {
      var id = client;
      work.Add(Task.Run(() =>
      {
        for (var i = 0; i < 100; i++)
        {
          var enabled = i % 2 == 0;
          var json = "{\"io.modelcontextprotocol/clientCapabilities\":{\"extensions\":{\"test/ext\":" + (enabled ? "true" : "false") + "}},\"client\":" + id + "}";
          var meta = cache.Read(Json.Parse(json));
          Assert.Equal(enabled, meta.ClientCapabilities!.Extensions!["test/ext"].GetBoolean());
          Assert.Equal(id, meta.AdditionalProperties!["client"].GetInt32());
          meta.ClientCapabilities.Extensions.Clear();
        }
      }, TestContext.Current.CancellationToken));
    }

    await Task.WhenAll(work);
  }

  [Fact]
  public void Invalid_UTF8_cannot_match_a_cached_replacement_character()
  {
    var cache = new RequestMetadataCache();
    cache.Read(Json.Parse("{\"io.modelcontextprotocol/clientInfo\":{\"name\":\"\uFFFD\",\"version\":\"1\"}}"));
    var prefix = System.Text.Encoding.UTF8.GetBytes("{\"io.modelcontextprotocol/clientInfo\":{\"name\":\"");
    var suffix = "\",\"version\":\"1\"}}"u8;
    var bytes = new byte[prefix.Length + 1 + suffix.Length];
    prefix.CopyTo(bytes, 0);
    bytes[prefix.Length] = 0xFF;
    suffix.CopyTo(bytes.AsSpan(prefix.Length + 1));
    using var invalid = JsonDocument.Parse(bytes);
    Assert.NotNull(Record.Exception(() => invalid.RootElement.Deserialize(WireJsonContext.Default.MetaObject)));
    Assert.NotNull(Record.Exception(() => cache.Read(invalid.RootElement)));
  }

  [Theory]
  [InlineData(4096)]
  [InlineData(16384)]
  public void Large_metadata_and_unknown_fields_still_decode(int length)
  {
    var cache = new RequestMetadataCache();
    var json = Json.Parse("{\"vendor\":\"" + new string('x', length) + "\"}");
    for (var i = 0; i < 2; i++)
    {
      Assert.Equal(length, cache.Read(json).AdditionalProperties!["vendor"].GetString()!.Length);
    }
  }

  [Theory]
  [InlineData("{\"progressToken\":true}")]
  [InlineData("{\"io.modelcontextprotocol/clientInfo\":{}}")]
  [InlineData("{\"io.modelcontextprotocol/clientCapabilities\":[]}")]
  public void Malformed_metadata_is_not_a_cached_success(string json)
  {
    var cache = new RequestMetadataCache();
    cache.Read(Json.Parse("{}"));
    for (var i = 0; i < 2; i++)
    {
      Assert.Throws<JsonException>(() => cache.Read(Json.Parse(json)));
    }
  }
}
