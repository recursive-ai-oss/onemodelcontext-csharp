using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Extensions;
using RecursiveAI.OneModelContext.Extensions.Apps;
using RecursiveAI.OneModelContext.Protocol;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>MCP Apps (io.modelcontextprotocol/ui) on both eras.</summary>
public class AppsTests
{
  private const string Dashboard = "ui://weather/dashboard";
  private static readonly JsonElement ObjectSchema = Json.Parse("""{"type":"object"}""");

  public static IEnumerable<object[]> Revisions() =>
    [[EraPreference.Modern, ProtocolVersions.V20260728], [EraPreference.Legacy, ProtocolVersions.V20251125], [EraPreference.Legacy, ProtocolVersions.V20250326]];

  private static ContextServer AppsServer()
  {
    var server = new ContextServer("weather", "1.0");
    server.AddUiResource(
      Dashboard,
      "dashboard",
      "<!DOCTYPE html><html><body>weather</body></html>",
      new UiResourceMeta
      {
        Csp = new UiResourceCsp { ConnectDomains = ["https://api.example.com"], ResourceDomains = ["https://cdn.example.com"] },
        Permissions = new UiPermissions { Geolocation = new UiPermissionRequest() },
        PrefersBorder = true,
      },
      "Interactive weather view.");
    server.AddAppTool(
      new ToolDefinition("get_weather", "Gets the weather.", ObjectSchema),
      Dashboard,
      (_, ctx) => ValueTask.FromResult(new CallToolResult { Content = [], StructuredContent = Json.Parse("""{"temp":21}""") }),
      fallbackText: "21 degrees and sunny.");
    server.AddAppTool(
      new ToolDefinition("refresh", "Refreshes the dashboard.", ObjectSchema),
      Dashboard,
      (_, _) => ValueTask.FromResult(CallToolResult.FromText("refreshed")),
      [UiVisibility.App]);
    server.Tools.Add(new ToolDefinition("plain", "No UI.", ObjectSchema), (_, _) => ValueTask.FromResult(CallToolResult.FromText("plain")));
    return server;
  }

  private static async Task<(ContextClient Client, Task Serving)> ConnectAsync(ContextServer server, EraPreference era, string version, bool withApps)
  {
    var (a, b) = PipeEnd.CreatePair();
    var serving = server.RunAsync(ContextTransport.Stream(a), TestContext.Current.CancellationToken);
    var client = await ContextClient.ConnectAsync(
      ContextTransport.Stream(b),
      new ContextClientOptions { Era = era, SupportedVersions = [version], EraCache = null, Extensions = withApps ? [AppsExtension.Client()] : [] },
      TestContext.Current.CancellationToken);
    return (client, serving);
  }

  [Theory]
  [MemberData(nameof(Revisions))]
  public async Task A_client_with_Apps_sees_the_UI_metadata_and_every_tool(EraPreference era, string version)
  {
    var (client, _) = await ConnectAsync(AppsServer(), era, version, withApps: true);
    await using var __client = client;

    var tools = await client.ListToolsAsync(TestContext.Current.CancellationToken);
    var weather = tools.Single(t => t.Name == "get_weather");
    var refresh = tools.Single(t => t.Name == "refresh");

    Assert.Equal(["get_weather", "plain", "refresh"], tools.Select(t => t.Name).Order(StringComparer.Ordinal));
    Assert.Equal(Dashboard, weather.GetUi()!.ResourceUri);
    Assert.True(weather.GetUi()!.VisibleToModel);
    Assert.Equal(["app"], refresh.GetUi()!.Visibility);
    Assert.False(refresh.GetUi()!.VisibleToModel);
    Assert.Null(tools.Single(t => t.Name == "plain").GetUi());
    Assert.True(client.ServerSupportsExtension(AppsExtension.Id));
  }

  [Theory]
  [MemberData(nameof(Revisions))]
  public async Task A_client_without_Apps_gets_an_ordinary_text_tool(EraPreference era, string version)
  {
    var (client, _) = await ConnectAsync(AppsServer(), era, version, withApps: false);
    await using var __ = client;

    var tools = await client.ListToolsAsync(TestContext.Current.CancellationToken);
    var result = await client.CallToolAsync("get_weather", cancellationToken: TestContext.Current.CancellationToken);
    var hidden = await Assert.ThrowsAsync<ProtocolException>(async () => await client.CallToolAsync("refresh", cancellationToken: TestContext.Current.CancellationToken));

    Assert.Equal(["get_weather", "plain"], tools.Select(t => t.Name).Order(StringComparer.Ordinal));
    var weather = tools.Single(t => t.Name == "get_weather");
    Assert.Null(weather.Meta?.AdditionalProperties?.GetValueOrDefault("ui"));
    Assert.Null(weather.GetUi());
    Assert.Equal("21 degrees and sunny.", result.Text);
    Assert.Equal(-32602, hidden.Code);
  }

  [Theory]
  [MemberData(nameof(Revisions))]
  public async Task A_client_that_cannot_render_the_view_type_is_treated_as_without_Apps(EraPreference era, string version)
  {
    var server = AppsServer();
    var (a, b) = PipeEnd.CreatePair();
    _ = server.RunAsync(ContextTransport.Stream(a), TestContext.Current.CancellationToken);
    await using var client = await ContextClient.ConnectAsync(
      ContextTransport.Stream(b),
      new ContextClientOptions { Era = era, SupportedVersions = [version], EraCache = null, Extensions = [AppsExtension.Client(["text/uri-list"])] },
      TestContext.Current.CancellationToken);

    var tools = await client.ListToolsAsync(TestContext.Current.CancellationToken);

    Assert.All(tools, t => Assert.Null(t.GetUi()));
    Assert.DoesNotContain(tools, t => t.Name == "refresh");
  }

  [Theory]
  [MemberData(nameof(Revisions))]
  public async Task The_UI_resource_is_read_with_its_security_metadata(EraPreference era, string version)
  {
    var (client, _) = await ConnectAsync(AppsServer(), era, version, withApps: true);
    await using var __ = client;

    var resource = await client.ReadUiResourceAsync(Dashboard, TestContext.Current.CancellationToken);

    Assert.Equal(AppsExtension.MimeType, resource.MimeType);
    Assert.Contains("weather", resource.Html, StringComparison.Ordinal);
    Assert.Equal(["https://api.example.com"], resource.Meta!.Csp!.ConnectDomains);
    Assert.NotNull(resource.Meta.Permissions!.Geolocation);
    Assert.Null(resource.Meta.Permissions.Camera);
    Assert.True(resource.Meta.PrefersBorder);
    Assert.Equal("geolocation", UiContentSecurityPolicy.BuildAllowAttribute(resource.Meta.Permissions));
  }

  [Fact]
  public async Task Tool_calls_return_text_content_for_a_client_with_Apps_too()
  {
    var (client, _) = await ConnectAsync(AppsServer(), EraPreference.Modern, ProtocolVersions.V20260728, withApps: true);
    await using var __ = client;

    var result = await client.CallToolAsync("get_weather", cancellationToken: TestContext.Current.CancellationToken);
    var refreshed = await client.CallToolAsync("refresh", cancellationToken: TestContext.Current.CancellationToken);

    // The specification asks for meaningful content whether or not a UI is shown.
    Assert.Equal("21 degrees and sunny.", result.Text);
    Assert.Equal(21, result.StructuredContent.GetProperty("temp").GetInt32());
    Assert.Equal("refreshed", refreshed.Text);
  }

  [Fact]
  public async Task A_handler_can_tell_whether_the_caller_renders_views()
  {
    var server = new ContextServer("w", "1");
    server.AddUiResource("ui://w/v", "v", "<html></html>");
    server.AddAppTool(new ToolDefinition("show", "Shows.", ObjectSchema), "ui://w/v", (_, ctx) => ValueTask.FromResult(CallToolResult.FromText(ctx.SupportsApps() ? "see the view" : "full text answer")));

    var (withApps, _) = await ConnectAsync(server, EraPreference.Modern, ProtocolVersions.V20260728, withApps: true);
    var (without, _) = await ConnectAsync(server, EraPreference.Legacy, ProtocolVersions.V20251125, withApps: false);
    await using var a = withApps;
    await using var b = without;

    Assert.Equal("see the view", (await withApps.CallToolAsync("show", cancellationToken: TestContext.Current.CancellationToken)).Text);
    Assert.Equal("full text answer", (await without.CallToolAsync("show", cancellationToken: TestContext.Current.CancellationToken)).Text);
  }

  [Fact]
  public async Task Lists_are_not_shared_between_callers_even_when_the_server_says_public()
  {
    var server = new ContextServer("w", "1", new ContextServerOptions { ListCacheScope = "public", ListCacheTtl = TimeSpan.FromMinutes(1) });
    server.AddUiResource("ui://w/v", "v", "<html></html>");
    server.AddAppTool(new ToolDefinition("show", "Shows.", ObjectSchema), "ui://w/v", (_, _) => ValueTask.FromResult(CallToolResult.FromText("x")));
    var (client, _) = await ConnectAsync(server, EraPreference.Modern, ProtocolVersions.V20260728, withApps: true);
    await using var __ = client;

    var page = await client.ListToolsPageAsync(null, TestContext.Current.CancellationToken);

    Assert.Equal("private", page.CacheScope);
  }

  [Fact]
  public void A_server_refuses_a_UI_resource_or_tool_that_is_not_ui_scheme_or_has_a_bad_visibility()
  {
    var server = new ContextServer("w", "1");
    var tool = new ToolDefinition("t", "d", ObjectSchema);

    Assert.Throws<ArgumentException>(() => server.AddUiResource("https://example.com/x", "x", "<html/>"));
    Assert.Throws<ArgumentException>(() => server.AddUiResource("ui://", "x", "<html/>"));
    Assert.Throws<ArgumentException>(() => server.AddAppTool(tool, "file:///etc/passwd", (_, _) => default));
    Assert.Throws<ArgumentException>(() => server.AddAppTool(tool, "ui://w/v", (_, _) => default, ["admin"]));
    Assert.Throws<ArgumentException>(() => server.AddAppTool(tool, "ui://w/v", (_, _) => default, []));
  }

  [Fact]
  public void A_tool_pointing_at_a_non_ui_resource_has_no_UI_for_a_host()
  {
    var hostile = new Tool
    {
      Name = "t",
      InputSchema = ObjectSchema,
      Meta = new MetaObject { AdditionalProperties = new Dictionary<string, JsonElement> { ["ui"] = Json.Parse("""{"resourceUri":"https://evil.example/x"}""") } },
    };
    var flat = new Tool
    {
      Name = "t",
      InputSchema = ObjectSchema,
      Meta = new MetaObject { AdditionalProperties = new Dictionary<string, JsonElement> { ["ui/resourceUri"] = Json.Parse("\"ui://old/view\"") } },
    };
    var malformed = new Tool
    {
      Name = "t",
      InputSchema = ObjectSchema,
      Meta = new MetaObject { AdditionalProperties = new Dictionary<string, JsonElement> { ["ui"] = Json.Parse("""{"visibility":"app"}""") } },
    };

    Assert.Null(hostile.GetUi());
    Assert.Equal("ui://old/view", flat.GetUi()!.ResourceUri);
    Assert.Null(malformed.GetUi());
  }

  [Fact]
  public async Task A_server_that_returns_something_else_than_a_view_is_refused()
  {
    var server = new ContextServer("w", "1");
    server.Resources.Add(new ResourceDefinition("ui://w/wrong", "wrong"), (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, MimeType = "text/plain", Text = "x" }] }));
    server.Resources.Add(new ResourceDefinition("ui://w/blob", "blob"), (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [new BlobResourceContents { Uri = uri, MimeType = AppsExtension.MimeType, Blob = "!!not base64" }] }));
    server.Resources.Add(new ResourceDefinition("ui://w/b64", "b64"), (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [new BlobResourceContents { Uri = uri, MimeType = "text/html; profile=mcp-app", Blob = Convert.ToBase64String("<p>hi</p>"u8) }] }));
    var (client, _) = await ConnectAsync(server, EraPreference.Modern, ProtocolVersions.V20260728, withApps: true);
    await using var __ = client;

    await Assert.ThrowsAsync<ContextException>(async () => await client.ReadUiResourceAsync("ui://w/wrong", TestContext.Current.CancellationToken));
    await Assert.ThrowsAsync<ContextException>(async () => await client.ReadUiResourceAsync("ui://w/blob", TestContext.Current.CancellationToken));
    await Assert.ThrowsAsync<ArgumentException>(async () => await client.ReadUiResourceAsync("https://example.com/x", TestContext.Current.CancellationToken));
    Assert.Equal("<p>hi</p>", (await client.ReadUiResourceAsync("ui://w/b64", TestContext.Current.CancellationToken)).Html);
  }

  [Fact]
  public void The_content_security_policy_allows_only_declared_origins_and_drops_injection()
  {
    var none = UiContentSecurityPolicy.Build(null);
    var declared = UiContentSecurityPolicy.Build(new UiResourceMeta
    {
      Csp = new UiResourceCsp
      {
        ConnectDomains = ["https://api.example.com", "https://x.example; script-src *", "https://y.example,https://z", "https://q.example'", "https://ok.example:8443/path"],
        ResourceDomains = ["https://*.cdn.example"],
        FrameDomains = ["https://www.youtube.com"],
        BaseUriDomains = ["https://base.example"],
      },
    });
    var emptyCsp = UiContentSecurityPolicy.Build(new UiResourceMeta { Csp = new UiResourceCsp() });

    Assert.Equal(UiContentSecurityPolicy.RestrictiveDefault, none);
    Assert.Contains("connect-src 'self' https://api.example.com https://ok.example:8443/path;", declared, StringComparison.Ordinal);
    Assert.DoesNotContain("script-src *", declared, StringComparison.Ordinal);
    Assert.DoesNotContain("x.example", declared, StringComparison.Ordinal);
    Assert.DoesNotContain("https://z", declared, StringComparison.Ordinal);
    Assert.Contains("script-src 'self' 'unsafe-inline' https://*.cdn.example;", declared, StringComparison.Ordinal);
    Assert.Contains("frame-src https://www.youtube.com;", declared, StringComparison.Ordinal);
    Assert.Contains("base-uri https://base.example", declared, StringComparison.Ordinal);
    Assert.Contains("frame-src 'none'; object-src 'none'; base-uri 'self'", emptyCsp, StringComparison.Ordinal);
    Assert.Contains("connect-src 'self';", emptyCsp, StringComparison.Ordinal);
  }

  [Fact]
  public void View_host_messages_round_trip_and_keep_members_they_do_not_know()
  {
    const string json = """
      {"protocolVersion":"2026-01-26","hostCapabilities":{"openLinks":{},"serverTools":{"listChanged":true},"sandbox":{"csp":{"connectDomains":["https://a"]}},"future":1},
       "hostInfo":{"name":"host","version":"1"},
       "hostContext":{"theme":"dark","displayMode":"inline","availableDisplayModes":["inline","fullscreen"],"containerDimensions":{"width":400,"maxHeight":600},
         "styles":{"variables":{"--color-text-primary":"light-dark(#171717, #fafafa)"},"css":{"fonts":"@font-face {}"}},
         "toolInfo":{"id":5,"tool":{"name":"t","inputSchema":{"type":"object"}}},"safeAreaInsets":{"top":1,"right":2,"bottom":3,"left":4},"surprise":"yes"}}
      """;

    var result = JsonSerializer.Deserialize(json, AppsJsonContext.Default.UiInitializeResult)!;
    var again = JsonSerializer.SerializeToElement(result, AppsJsonContext.Default.UiInitializeResult);

    Assert.Equal("dark", result.HostContext!.Theme);
    Assert.Equal(400, result.HostContext.ContainerDimensions!.Width);
    Assert.Null(result.HostContext.ContainerDimensions.Height);
    Assert.Equal("t", result.HostContext.ToolInfo!.Tool!.Name);
    Assert.True(result.HostCapabilities!.ServerTools!.ListChanged);
    Assert.NotNull(result.HostCapabilities.OpenLinks);
    Assert.Equal(1, again.GetProperty("hostCapabilities").GetProperty("future").GetInt32());
    Assert.Equal("yes", again.GetProperty("hostContext").GetProperty("surprise").GetString());
    Assert.Equal(4, again.GetProperty("hostContext").GetProperty("safeAreaInsets").GetProperty("left").GetDouble());
  }

  [Fact]
  public void View_requests_and_notifications_have_the_specified_shapes()
  {
    var initialize = JsonSerializer.Deserialize("""{"protocolVersion":"2026-01-26","clientInfo":{"name":"v","version":"1"},"appCapabilities":{"availableDisplayModes":["inline","pip"],"tools":{"listChanged":true}}}""", AppsJsonContext.Default.UiInitializeRequestParams)!;
    var message = JsonSerializer.Deserialize("""{"role":"user","content":{"type":"text","text":"hello"}}""", AppsJsonContext.Default.UiMessageParams)!;
    var context = JsonSerializer.Deserialize("""{"content":[{"type":"text","text":"a"}],"structuredContent":{"k":1}}""", AppsJsonContext.Default.UiUpdateModelContextParams)!;
    var input = JsonSerializer.Deserialize("""{"arguments":{"location":"SF"}}""", AppsJsonContext.Default.UiToolInputParams)!;
    var ready = JsonSerializer.Deserialize("""{"html":"<p/>","csp":{"frameDomains":["https://f"]},"permissions":{"camera":{}}}""", AppsJsonContext.Default.UiSandboxResourceReadyParams)!;
    var result = JsonSerializer.Deserialize("""{"content":[{"type":"text","text":"ok"}],"structuredContent":{"t":1}}""", AppsJsonContext.Default.CallToolResult)!;

    Assert.Equal(["inline", "pip"], initialize.AppCapabilities!.AvailableDisplayModes);
    Assert.Equal("hello", Assert.IsType<TextContent>(message.Content).Text);
    Assert.Equal(1, context.StructuredContent.GetProperty("k").GetInt32());
    Assert.Equal("SF", input.Arguments.GetProperty("location").GetString());
    Assert.NotNull(ready.Permissions!.Camera);
    Assert.Equal("ok", result.Text);
    Assert.True(UiMethods.IsSandboxReserved(UiMethods.SandboxResourceReady));
    Assert.False(UiMethods.IsSandboxReserved(UiMethods.Initialized));
    Assert.Equal("{}", JsonSerializer.Serialize(new UiToolInputParams(), AppsJsonContext.Default.UiToolInputParams));
  }

  [Fact]
  public async Task A_generated_tool_can_name_its_UI_resource_and_visibility()
  {
    var server = new ContextServer("gen", "1");
    new GeneratedAppTools().AddTo(server);
    server.AddUiResource("ui://gen/chart", "chart", "<html></html>");
    var (withApps, _) = await ConnectAsync(server, EraPreference.Modern, ProtocolVersions.V20260728, withApps: true);
    var (without, _) = await ConnectAsync(server, EraPreference.Legacy, ProtocolVersions.V20251125, withApps: false);
    await using var a = withApps;
    await using var b = without;

    var tools = await withApps.ListToolsAsync(TestContext.Current.CancellationToken);
    var plain = await without.ListToolsAsync(TestContext.Current.CancellationToken);

    Assert.Equal("ui://gen/chart", tools.Single(t => t.Name == "chart").GetUi()!.ResourceUri);
    Assert.Equal(["app"], tools.Single(t => t.Name == "chart_data").GetUi()!.Visibility);
    Assert.Equal(["chart"], plain.Select(t => t.Name));
    Assert.Null(plain[0].GetUi());
    Assert.Equal("drawn", (await without.CallToolAsync("chart", cancellationToken: TestContext.Current.CancellationToken)).Text);
  }
}

public sealed partial class GeneratedAppTools
{
  /// <summary>Draws the chart.</summary>
  [Tool(UiResource = "ui://gen/chart")]
  public static string Chart() => "drawn";

  /// <summary>Feeds the chart.</summary>
  [Tool(UiResource = "ui://gen/chart", UiVisibility = new[] { "app" })]
  public static string ChartData() => "data";
}
