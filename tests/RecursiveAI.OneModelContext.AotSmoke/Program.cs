using System;
using System.IO;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext;
using System.Security.Cryptography;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.OAuth;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;

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

// The OAuth client roots its code here so the publish checks it: a challenge, a loopback receiver, a client built with a private-key
// grant and a file token store, and a token request that finds nothing stored (no network is used).
var challenges = BearerChallenge.Parse(["Bearer resource_metadata=\"https://example.com/.well-known/oauth-protected-resource\", scope=\"read\""]);
if (challenges.Count != 1 || challenges[0].Scope != "read")
{
  Console.Error.WriteLine("challenge not parsed");
  return 1;
}

using (var receiver = new LoopbackRedirectReceiver((_, _) => ValueTask.CompletedTask))
using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
using (var oauth = new OAuthClient(new OAuthClientOptions(new Uri("https://example.com/mcp"))
{
  UserAgent = receiver,
  ClientCredentials = new ClientCredentialsGrant("smoke", key) { ExpectedIssuer = "https://auth.example" },
  Store = new MemoryTokenStore(),
}))
{
  if (!receiver.RedirectUri.IsLoopback || await oauth.GetAccessTokenAsync() is not null)
  {
    Console.Error.WriteLine("oauth client not usable");
    return 1;
  }
}

// A modern (2026-07-28) round trip: discover, a tool call, and an input round trip whose requestState is sealed with AES-GCM.
var toClient = new Pipe();
var toServer = new Pipe();
var server = new ContextServer("smoke", "1", new ContextServerOptions { EncryptRequestState = true });
server.Tools.Add(new ToolDefinition("echo", "Echoes.", JsonDocument.Parse("""{"type":"object"}""").RootElement), (_, _) => ValueTask.FromResult(CallToolResult.FromText("hi")));
server.Tools.Add(new ToolDefinition("delete", "Asks first.", JsonDocument.Parse("""{"type":"object"}""").RootElement), async (_, ctx) => CallToolResult.FromText(await ctx.ConfirmAsync("Delete?") ? "deleted" : "kept"));
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var serving = server.RunAsync(ContextTransport.Stream(new Duplex(toServer.Reader.AsStream(), toClient.Writer.AsStream())), cts.Token);
await using (var client = await ContextClient.ConnectAsync(
  ContextTransport.Stream(new Duplex(toClient.Reader.AsStream(), toServer.Writer.AsStream())),
  new ContextClientOptions { Capabilities = new ClientCapabilities { Elicitation = new ElicitationCapability() }, RequestHandler = new Yes() },
  cts.Token))
{
  var tools = await client.ListToolsAsync(cts.Token);
  var echoed = await client.CallToolAsync("echo", cancellationToken: cts.Token);
  var deleted = await client.CallToolAsync("delete", cancellationToken: cts.Token);
  if (client.Era != ProtocolEra.Modern || tools.Count != 2 || echoed.Text != "hi" || deleted.Text != "deleted")
  {
    Console.Error.WriteLine("modern round trip failed: " + client.Era + " " + tools.Count + " " + echoed.Text + " " + deleted.Text);
    return 1;
  }
}

await serving.WaitAsync(TimeSpan.FromSeconds(10));

// The legacy handshake and a version-shaped result, which rewrite JSON through JsonNode.
var legacyServer = new RecursiveAI.OneModelContext.ContextServer("smoke", "1");
legacyServer.Tools.Add(
  new RecursiveAI.OneModelContext.ToolDefinition("t", "d", JsonDocument.Parse("{\"type\":\"object\"}").RootElement) { Title = "T" },
  (_, _) => System.Threading.Tasks.ValueTask.FromResult(CallToolResult.FromText("x")));
var session = new RecursiveAI.OneModelContext.Wire.ServerSessionState();
await legacyServer.HandleMessageAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"c","version":"1"}}}""", session);
var listed = await legacyServer.HandleMessageAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", session);
if (listed is null || listed.Contains("\"title\"", StringComparison.Ordinal) || !listed.Contains("\"name\":\"t\"", StringComparison.Ordinal))
{
  Console.Error.WriteLine("legacy result not shaped: " + listed);
  return 1;
}

// The experimental extensions: tasks on a file store, skills with verification, and a server card over the listener.
if (await ExtensionsSmoke.RunAsync() is { } extensionsFailure)
{
  Console.Error.WriteLine(extensionsFailure);
  return 1;
}

// The authorization server: flow, file store, key set validation and the address guard.
if (await AuthorizationServerSmoke.RunAsync() is { } failure)
{
  Console.Error.WriteLine(failure);
  return 1;
}

// A Streamable HTTP client round trip over a loopback socket: the library's HTTP client against a few lines of HTTP/1.1 that hand each
// POST body to a server's message-level entry. It covers era negotiation, JSON responses and the refused listening stream.
using (var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
{
  listener.Start();
  var httpServer = new ContextServer("smoke", "1");
  httpServer.Tools.Add(new ToolDefinition("echo", "Echoes.", JsonDocument.Parse("""{"type":"object"}""").RootElement), (_, _) => ValueTask.FromResult(CallToolResult.FromText("over http")));
  using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
  _ = Task.Run(async () =>
  {
    try
    {
      while (true)
      {
        var socket = await listener.AcceptSocketAsync(stop.Token);
        _ = Task.Run(() => HttpLoop.ServeAsync(socket, httpServer, stop.Token));
      }
    }
    catch (Exception)
    {
      // Shutting down.
    }
  });

  var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
  await using var http = await ModelContext.ConnectAsync(new Uri("http://127.0.0.1:" + port + "/mcp"), cancellationToken: stop.Token);
  var httpTools = await http.ListToolsAsync(stop.Token);
  var httpEcho = await http.CallToolAsync("echo", cancellationToken: stop.Token);
  if (httpTools.Count != 1 || httpEcho.Text != "over http")
  {
    Console.Error.WriteLine("http round trip failed: " + httpTools.Count + " " + httpEcho.Text);
    return 1;
  }

  stop.Cancel();
}

Console.WriteLine("ok " + response);
return 0;

internal sealed class Duplex(Stream input, Stream output) : Stream
{
  public override bool CanRead => true;
  public override bool CanSeek => false;
  public override bool CanWrite => true;
  public override long Length => throw new NotSupportedException();
  public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
  public override void Flush() => output.Flush();
  public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);
  public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
  public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => input.ReadAsync(buffer, cancellationToken);
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
  public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => output.WriteAsync(buffer, cancellationToken);

  protected override void Dispose(bool disposing)
  {
    if (disposing)
    {
      output.Dispose();
      input.Dispose();
    }

    base.Dispose(disposing);
  }
}

internal sealed class Yes : IClientRequestHandler
{
  public ValueTask<Result> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken) =>
    ValueTask.FromResult<Result>(new ElicitResult { Action = "accept", Content = new() { ["confirm"] = JsonDocument.Parse("true").RootElement } });
}


// Just enough HTTP/1.1 for the smoke: POST bodies go to the server, anything else is refused with 405.
internal static class HttpLoop
{
  internal static async Task ServeAsync(System.Net.Sockets.Socket socket, ContextServer server, CancellationToken cancellationToken)
  {
    using (socket)
    {
      using var stream = new System.Net.Sockets.NetworkStream(socket, ownsSocket: false);
      var buffer = new System.Collections.Generic.List<byte>();
      var one = new byte[1];
      while (!cancellationToken.IsCancellationRequested)
      {
        buffer.Clear();
        while (!EndsWithBlankLine(buffer))
        {
          if (await stream.ReadAsync(one, cancellationToken) == 0)
          {
            return;
          }

          buffer.Add(one[0]);
        }

        var head = System.Text.Encoding.ASCII.GetString(buffer.ToArray());
        var length = 0;
        foreach (var line in head.Split("\r\n"))
        {
          if (line.StartsWith("content-length:", StringComparison.OrdinalIgnoreCase))
          {
            length = int.Parse(line.AsSpan(15).Trim(), System.Globalization.CultureInfo.InvariantCulture);
          }
        }

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken);
        if (!head.StartsWith("POST ", StringComparison.Ordinal))
        {
          await stream.WriteAsync("HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), cancellationToken);
          continue;
        }

        var reply = await server.HandleMessageAsync(System.Text.Encoding.UTF8.GetString(body), cancellationToken: cancellationToken);
        if (reply is null)
        {
          await stream.WriteAsync("HTTP/1.1 202 Accepted\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), cancellationToken);
          continue;
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(reply);
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\n\r\n"), cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
      }
    }
  }

  private static bool EndsWithBlankLine(System.Collections.Generic.List<byte> b) =>
    b.Count >= 4 && b[^4] == '\r' && b[^3] == '\n' && b[^2] == '\r' && b[^1] == '\n';
}
