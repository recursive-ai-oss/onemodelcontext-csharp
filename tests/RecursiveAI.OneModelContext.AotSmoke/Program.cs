using System;
using System.Text.Json;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;

// Publishes with NativeAOT in CI and locally (-p:SmokeMode=aot) and exits non-zero when a round trip fails.
// It exercises the pieces every program built on the library touches: JSON-RPC reading and writing, and the
// source-generated protocol records.
var read = JsonRpcReader.Read("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hi"},"_meta":{"x":1}}}""");
if (read.Message is not JsonRpcRequest request || request.Id != JsonRpcId.FromNumber(7))
{
  Console.Error.WriteLine("request not read");
  return 1;
}

var call = request.Params.Deserialize(WireJsonContext.Default.CallToolRequestParams);
if (call is null || call.Name != "echo" || call.Meta?.AdditionalProperties?["x"].GetInt32() != 1)
{
  Console.Error.WriteLine("params not decoded");
  return 1;
}

var result = new CallToolResult { Content = [TextContent.Of("hi"), new ImageContent { Data = "AAAA", MimeType = "image/png" }] };
var element = JsonSerializer.SerializeToElement(result, WireJsonContext.Default.CallToolResult);
var response = JsonRpcWriter.ToJson(new JsonRpcResultResponse(request.Id, element));
var back = JsonRpcReader.Read(response).Message as JsonRpcResultResponse;
var decoded = back?.Result.Deserialize(WireJsonContext.Default.CallToolResult);
if (decoded is null || decoded.Text != "hi" || decoded.Content.Count != 2 || decoded.Content[1] is not ImageContent)
{
  Console.Error.WriteLine("result not round-tripped: " + response);
  return 1;
}

// The legacy handshake and a version-shaped result, which rewrite JSON through JsonNode.
var server = new RecursiveAI.OneModelContext.ContextServer("smoke", "1");
server.Tools.Add(
  new RecursiveAI.OneModelContext.ToolDefinition("t", "d", JsonDocument.Parse("{\"type\":\"object\"}").RootElement) { Title = "T" },
  (_, _) => System.Threading.Tasks.ValueTask.FromResult(CallToolResult.FromText("x")));
var session = new RecursiveAI.OneModelContext.Wire.ServerSessionState();
await server.HandleMessageAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"c","version":"1"}}}""", session);
var listed = await server.HandleMessageAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", session);
if (listed is null || listed.Contains("\"title\"", StringComparison.Ordinal) || !listed.Contains("\"name\":\"t\"", StringComparison.Ordinal))
{
  Console.Error.WriteLine("legacy result not shaped: " + listed);
  return 1;
}

Console.WriteLine("ok " + response);
return 0;
