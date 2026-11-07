using RecursiveAI.OneModelContext;
using RecursiveAI.OneModelContext.Extensions.Apps;
using RecursiveAI.OneModelContext.Protocol;

namespace AppsServer;

/// <summary>A weather tool with a view. Hosts that render MCP Apps show the view; every other client gets the text.</summary>
public static class WeatherApp
{
  public const string ViewUri = "ui://weather/forecast";

  private const string Html = """
    <!DOCTYPE html>
    <html><body>
      <h1 id="city"></h1><p id="temp"></p>
      <script>
        // The host sends the tool result as a ui/notifications/tool-result message once the view has initialized.
        window.addEventListener('message', e => {
          const r = e.data && e.data.params && e.data.params.structuredContent;
          if (e.data && e.data.method === 'ui/notifications/tool-result' && r) {
            document.getElementById('city').textContent = r.city;
            document.getElementById('temp').textContent = r.celsius + ' °C';
          }
        });
        window.parent.postMessage({ jsonrpc: '2.0', id: 1, method: 'ui/initialize', params: { protocolVersion: '2026-01-26', clientInfo: { name: 'weather-view', version: '1' }, appCapabilities: {} } }, '*');
      </script>
    </body></html>
    """;

  private static readonly System.Text.Json.JsonElement Schema = System.Text.Json.JsonDocument.Parse("""
    {"type":"object","properties":{"city":{"type":"string","description":"The city."}},"required":["city"]}
    """).RootElement;

  public static ContextServer Create()
  {
    var server = new ContextServer("weather", "1.0.0");

    // The view: HTML with the origins it may talk to. This one needs none, which is the secure default.
    server.AddUiResource(ViewUri, "forecast_view", Html, new UiResourceMeta { PrefersBorder = true }, "Shows a forecast.");

    // The tool: its text answer is the fallback every caller gets; the view is added for hosts that render Apps.
    server.AddAppTool(
      new ToolDefinition("get_forecast", "Gets the forecast for a city.", Schema) { Annotations = new ToolAnnotations { ReadOnlyHint = true } },
      ViewUri,
      (arguments, context) =>
      {
        var city = arguments.GetProperty("city").GetString() ?? "nowhere";
        var celsius = 18 + city.Length % 7;
        var text = $"{city}: {celsius} °C, partly cloudy.";
        var structured = System.Text.Json.JsonDocument.Parse($"{{\"city\":{System.Text.Json.JsonSerializer.Serialize(city, AppJson.Default.String)},\"celsius\":{celsius}}}").RootElement;
        return ValueTask.FromResult(new CallToolResult
        {
          // A caller that renders the view gets a short text; one that does not gets the whole answer.
          Content = [TextContent.Of(context.SupportsApps() ? $"Forecast for {city} is shown." : text)],
          StructuredContent = structured,
        });
      });
    return server;
  }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class AppJson : System.Text.Json.Serialization.JsonSerializerContext;
