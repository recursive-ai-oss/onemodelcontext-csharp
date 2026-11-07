using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using ProtectedServer;
using RecursiveAI.OneModelContext;
using Xunit;

namespace RecursiveAI.OneModelContext.SamplesTests;

/// <summary>The protected sample on a real loopback listener: the 401 challenge, a rejected token and an authorized call on each era.</summary>
public sealed class ProtectedServerTests : IAsyncLifetime
{
  private const string Token = "sample_0123456789abcdef0123456789abcdef";
  private const int Port = 49510;
  private static readonly Uri Endpoint = new($"http://127.0.0.1:{Port}/mcp");
  private WebApplication? _app;

  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  public async ValueTask InitializeAsync()
  {
    _app = Site.Build(["--urls", $"http://127.0.0.1:{Port}"], Token);
    await _app.StartAsync(Ct);
  }

  public async ValueTask DisposeAsync()
  {
    if (_app is not null)
    {
      await _app.StopAsync(Ct);
      await _app.DisposeAsync();
    }
  }

  [Fact]
  public async Task Without_a_token_the_endpoint_answers_401_and_points_at_the_resource_metadata()
  {
    using var http = new HttpClient();
    using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", Encoding.UTF8, "application/json") };
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

    using var response = await http.SendAsync(request, Ct);

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    Assert.Equal($"Bearer resource_metadata=\"http://127.0.0.1:{Port}/.well-known/oauth-protected-resource/mcp\"", string.Join(", ", response.Headers.WwwAuthenticate));

    using var metadata = await http.GetAsync($"http://127.0.0.1:{Port}/.well-known/oauth-protected-resource/mcp", Ct);
    Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
    Assert.Contains("https://login.example.com", await metadata.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
  }

  [Theory]
  [MemberData(nameof(Eras.Both), MemberType = typeof(Eras))]
  public async Task A_client_without_a_valid_token_cannot_connect(EraPreference era)
  {
    using var http = new HttpClient { DefaultRequestHeaders = { Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token") } };

    var failure = await Assert.ThrowsAsync<AuthorizationException>(async () =>
      await ContextClient.ConnectAsync(ContextTransport.Http(Endpoint, http), new ContextClientOptions { Era = era }, Ct));

    Assert.Equal(401, failure.StatusCode);
    Assert.DoesNotContain("not-the-token", failure.Message, StringComparison.Ordinal);
  }

  [Theory]
  [MemberData(nameof(Eras.Both), MemberType = typeof(Eras))]
  public async Task A_client_with_the_token_calls_a_tool_and_the_server_sees_the_principal(EraPreference era)
  {
    using var http = new HttpClient { DefaultRequestHeaders = { Authorization = new AuthenticationHeaderValue("Bearer", Token) } };
    await using var client = await ContextClient.ConnectAsync(ContextTransport.Http(Endpoint, http), new ContextClientOptions { Era = era }, Ct);

    var result = await client.CallToolAsync("who_am_i", cancellationToken: Ct);

    Assert.NotEqual(true, result.IsError);
    Assert.Equal("alice", result.Text);
  }
}
