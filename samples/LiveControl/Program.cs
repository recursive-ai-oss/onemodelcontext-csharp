using System.Net.Sockets;
using LiveControl;
using RecursiveAI.OneModelContext;

// A tiny "application" with a clock, and a live-control endpoint on a local socket. The demo connects to its own endpoint the way
// an agent would, lists the operations, calls one, adds another while connected, and shows the list-changed notification arrive.
var ticks = 0;
using var host = new LiveControlHost("live-control", "1.0.0", "Inspect and step the running application.");
host.Add(new Operation("info", "Reports the current tick.", """{"type":"object","properties":{}}""", false,
  _ => Task.FromResult(new OperationResult($$"""{"tick":{{ticks}}}"""))));
host.Add(new Operation("step", "Advances the application by some ticks.",
  """{"type":"object","properties":{"count":{"type":"integer","minimum":1,"description":"How many ticks."}},"required":["count"]}""", true,
  args =>
  {
    using var doc = System.Text.Json.JsonDocument.Parse(args ?? "{}");
    ticks += doc.RootElement.TryGetProperty("count", out var n) ? n.GetInt32() : 1;
    return Task.FromResult(new OperationResult($$"""{"tick":{{ticks}}}"""));
  }));

var path = Path.Combine(Path.GetTempPath(), $"live-control-{Environment.ProcessId}.sock");
using var listener = LiveControlHost.Listen(path);
using var stop = new CancellationTokenSource();
var serving = host.ServeAsync(listener, stop.Token);

using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
// Pinned to the legacy era only for the push below: 2026-07-28 delivers list changes on a subscriptions/listen stream, which this
// client does not open yet. A modern client sees the new tool the next time it lists, so the rest of the demo works on either era.
await using var client = await ModelContext.ConnectAsync(ContextTransport.Stream(new NetworkStream(socket, ownsSocket: true)), EraPreference.Legacy);
Console.WriteLine($"connected on {client.ProtocolVersion} ({client.Era})");

var changed = new TaskCompletionSource();
client.NotificationReceived += n =>
{
  if (n.Method == "notifications/tools/list_changed")
  {
    changed.TrySetResult();
  }
};

Console.WriteLine("tools: " + string.Join(", ", (await client.ListToolsAsync()).Select(t => t.Name)));
var step = await client.CallToolAsync("step", System.Text.Json.JsonDocument.Parse("""{"count":3}""").RootElement);
Console.WriteLine("step -> " + step.Text);

host.Add(new Operation("reset", "Sets the tick back to zero.", """{"type":"object","properties":{}}""", true,
  _ =>
  {
    ticks = 0;
    return Task.FromResult(new OperationResult($$"""{"tick":{{ticks}}}"""));
  }));
await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
client.InvalidateCache();
Console.WriteLine("tools: " + string.Join(", ", (await client.ListToolsAsync()).Select(t => t.Name)));

stop.Cancel();
await serving;
File.Delete(path);
