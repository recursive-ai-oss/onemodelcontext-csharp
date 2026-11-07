using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;
using RecursiveAI.OneModelContext.Wire;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

public class NegotiationTests
{
  /// <summary>Wraps a codec to count and steer negotiation.</summary>
  private sealed class ScriptedCodec(ProtocolEra era, Func<CancellationToken, Task<bool>> accepts) : IClientCodec
  {
    private readonly TestClientCodec _inner = new(era, era == ProtocolEra.Modern ? ProtocolVersions.V20260728 : ProtocolVersions.V20251125);
    private int _attempts;

    public int Attempts => Volatile.Read(ref _attempts);

    public ProtocolEra Era => era;

    public async ValueTask<ClientSession?> NegotiateAsync(JsonRpcConnection connection, ClientHello hello, CancellationToken cancellationToken)
    {
      Interlocked.Increment(ref _attempts);
      return await accepts(cancellationToken).ConfigureAwait(false) ? await _inner.NegotiateAsync(connection, hello, cancellationToken).ConfigureAwait(false) : null;
    }

    public JsonElement EncodeParams(ClientSession session, ClientHello hello, string method, RequestParams? parameters) => _inner.EncodeParams(session, hello, method, parameters);

    public Result DecodeResult(ClientSession session, string method, JsonElement result) => _inner.DecodeResult(session, method, result);
  }

  /// <summary>A transport that names its server, over an in-memory pipe whose far end is never read.</summary>
  private sealed class NamedTransport(string? key) : ContextTransport
  {
    private readonly ContextTransport _inner = Stream(PipeEnd.CreatePair().A);

    public override string? ServerKey => key;

    public override ValueTask<IJsonRpcChannel> OpenAsync(CancellationToken cancellationToken) => _inner.OpenAsync(cancellationToken);
  }

  private static Task<bool> Yes(CancellationToken _) => Task.FromResult(true);

  private static Task<bool> No(CancellationToken _) => Task.FromResult(false);

  private static async Task<ContextClient> ConnectAsync(string? key, ContextClientOptions options)
  {
    await using var transport = new NamedTransport(key);
    return await ContextClient.ConnectAsync(transport, options, TestContext.Current.CancellationToken);
  }

  [Fact]
  public async Task Auto_tries_modern_first_and_falls_back_to_legacy()
  {
    var modern = new ScriptedCodec(ProtocolEra.Modern, No);
    var legacy = new ScriptedCodec(ProtocolEra.Legacy, Yes);

    await using var client = await ConnectAsync(null, new ContextClientOptions { Codecs = [modern, legacy] });

    Assert.Equal(ProtocolEra.Legacy, client.Era);
    Assert.Equal(1, modern.Attempts);
    Assert.Equal(1, legacy.Attempts);
  }

  [Fact]
  public async Task A_modern_server_stays_modern_without_trying_legacy()
  {
    var modern = new ScriptedCodec(ProtocolEra.Modern, Yes);
    var legacy = new ScriptedCodec(ProtocolEra.Legacy, Yes);

    await using var client = await ConnectAsync(null, new ContextClientOptions { Codecs = [modern, legacy] });

    Assert.Equal(ProtocolEra.Modern, client.Era);
    Assert.Equal(0, legacy.Attempts);
  }

  [Fact]
  public async Task The_era_is_remembered_per_server_and_the_next_connect_skips_the_probe()
  {
    var cache = new EraCache();
    var modern = new ScriptedCodec(ProtocolEra.Modern, No);
    var legacy = new ScriptedCodec(ProtocolEra.Legacy, Yes);
    var options = new ContextClientOptions { Codecs = [modern, legacy], EraCache = cache };

    await using var first = await ConnectAsync("process:server-a", options);
    await using var second = await ConnectAsync("process:server-a", options);

    Assert.Equal(ProtocolEra.Legacy, second.Era);
    Assert.Equal(1, modern.Attempts);
    Assert.Equal(2, legacy.Attempts);
    Assert.True(cache.TryGet("process:server-a", out var era));
    Assert.Equal(ProtocolEra.Legacy, era);
  }

  [Fact]
  public async Task A_different_server_is_probed_afresh()
  {
    var cache = new EraCache();
    var modern = new ScriptedCodec(ProtocolEra.Modern, No);
    var legacy = new ScriptedCodec(ProtocolEra.Legacy, Yes);
    var options = new ContextClientOptions { Codecs = [modern, legacy], EraCache = cache };

    await using var a = await ConnectAsync("process:a", options);
    await using var b = await ConnectAsync("process:b", options);

    Assert.Equal(2, modern.Attempts);
  }

  [Fact]
  public async Task A_remembered_era_that_stops_working_falls_back_and_is_corrected()
  {
    var cache = new EraCache();
    cache.Remember("process:upgraded", ProtocolEra.Legacy);
    var modern = new ScriptedCodec(ProtocolEra.Modern, Yes);
    var legacy = new ScriptedCodec(ProtocolEra.Legacy, No);

    await using var client = await ConnectAsync("process:upgraded", new ContextClientOptions { Codecs = [modern, legacy], EraCache = cache });

    Assert.Equal(ProtocolEra.Modern, client.Era);
    Assert.Equal(1, legacy.Attempts);
    Assert.True(cache.TryGet("process:upgraded", out var era));
    Assert.Equal(ProtocolEra.Modern, era);
  }

  [Fact]
  public async Task The_key_in_the_options_overrides_the_transports_and_adds_one_when_it_has_none()
  {
    var cache = new EraCache();
    var modern = new ScriptedCodec(ProtocolEra.Modern, No);
    var legacy = new ScriptedCodec(ProtocolEra.Legacy, Yes);
    var options = new ContextClientOptions { Codecs = [modern, legacy], EraCache = cache, ServerKey = "mine" };

    await using var first = await ConnectAsync(null, options);
    await using var second = await ConnectAsync("something else", options);

    Assert.Equal(1, modern.Attempts);
    Assert.Equal(1, cache.Count);
  }

  [Fact]
  public async Task A_transport_with_no_key_remembers_nothing()
  {
    var cache = new EraCache();
    var modern = new ScriptedCodec(ProtocolEra.Modern, No);
    var legacy = new ScriptedCodec(ProtocolEra.Legacy, Yes);
    var options = new ContextClientOptions { Codecs = [modern, legacy], EraCache = cache };

    await using var first = await ConnectAsync(null, options);
    await using var second = await ConnectAsync(null, options);

    Assert.Equal(0, cache.Count);
    Assert.Equal(2, modern.Attempts);
  }

  [Fact]
  public async Task Caching_can_be_turned_off()
  {
    var modern = new ScriptedCodec(ProtocolEra.Modern, No);
    var legacy = new ScriptedCodec(ProtocolEra.Legacy, Yes);
    var options = new ContextClientOptions { Codecs = [modern, legacy], EraCache = null };

    await using var first = await ConnectAsync("process:x", options);
    await using var second = await ConnectAsync("process:x", options);

    Assert.Equal(2, modern.Attempts);
  }

  [Fact]
  public async Task Forcing_an_era_uses_only_that_codec_and_ignores_the_cache()
  {
    var cache = new EraCache();
    cache.Remember("process:y", ProtocolEra.Modern);
    var modern = new ScriptedCodec(ProtocolEra.Modern, Yes);
    var legacy = new ScriptedCodec(ProtocolEra.Legacy, Yes);

    await using var client = await ConnectAsync("process:y", new ContextClientOptions { Codecs = [modern, legacy], EraCache = cache, Era = EraPreference.Legacy });

    Assert.Equal(ProtocolEra.Legacy, client.Era);
    Assert.Equal(0, modern.Attempts);
    Assert.True(cache.TryGet("process:y", out var era));
    Assert.Equal(ProtocolEra.Modern, era);
  }

  [Fact]
  public async Task Forcing_an_era_the_server_does_not_speak_fails_with_the_version_error()
  {
    var modern = new ScriptedCodec(ProtocolEra.Modern, No);

    var error = await Assert.ThrowsAsync<ProtocolVersionException>(() => ConnectAsync(null, new ContextClientOptions { Codecs = [modern, new ScriptedCodec(ProtocolEra.Legacy, Yes)], Era = EraPreference.Modern }));

    Assert.Contains("modern", error.Message, StringComparison.Ordinal);
    Assert.Contains("legacy", error.Message, StringComparison.Ordinal);
    Assert.Equal(ProtocolErrorCodes.UnsupportedProtocolVersion, error.Code);
  }

  [Fact]
  public async Task A_server_that_answers_neither_era_fails_naming_what_the_client_speaks()
  {
    var options = new ContextClientOptions { Codecs = [new ScriptedCodec(ProtocolEra.Modern, No), new ScriptedCodec(ProtocolEra.Legacy, No)], EraCache = new EraCache() };

    var error = await Assert.ThrowsAsync<ProtocolVersionException>(() => ConnectAsync("process:none", options));

    Assert.Contains(ProtocolVersions.V20260728, error.Message, StringComparison.Ordinal);
    Assert.Contains(ProtocolVersions.V20241105, error.Message, StringComparison.Ordinal);
    Assert.False(options.EraCache!.TryGet("process:none", out _));
  }

  [Fact]
  public async Task A_codec_that_reports_the_server_speaks_its_era_but_no_common_version_ends_negotiation()
  {
    var legacy = new ScriptedCodec(ProtocolEra.Legacy, Yes);
    var picky = new ThrowingCodec();

    var error = await Assert.ThrowsAsync<ProtocolVersionException>(() => ConnectAsync(null, new ContextClientOptions { Codecs = [picky, legacy] }));

    Assert.Equal(0, legacy.Attempts);
    Assert.Equal(["2026-07-28"], error.Remote);
  }

  [Fact]
  public async Task A_silent_server_is_treated_as_legacy_after_the_probe_timeout()
  {
    var clock = new ManualTimeProvider();
    var silent = new ScriptedCodec(ProtocolEra.Modern, async ct =>
    {
      await Task.Delay(Timeout.Infinite, ct);
      return true;
    });
    var legacy = new ScriptedCodec(ProtocolEra.Legacy, Yes);
    var connecting = ConnectAsync(null, new ContextClientOptions { Codecs = [silent, legacy], TimeProvider = clock, ProbeTimeout = TimeSpan.FromSeconds(5) });
    await Task.Delay(100, TestContext.Current.CancellationToken);
    Assert.False(connecting.IsCompleted);

    clock.Advance(TimeSpan.FromSeconds(6));
    await using var client = await connecting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    Assert.Equal(ProtocolEra.Legacy, client.Era);
  }

  [Fact]
  public async Task The_probe_timeout_does_not_cut_short_the_last_era()
  {
    var clock = new ManualTimeProvider();
    var slow = new ScriptedCodec(ProtocolEra.Legacy, async ct =>
    {
      await Task.Delay(TimeSpan.FromSeconds(30), clock, ct);
      return true;
    });
    var connecting = ConnectAsync(null, new ContextClientOptions { Codecs = [slow], TimeProvider = clock, ProbeTimeout = TimeSpan.FromSeconds(5), ConnectTimeout = TimeSpan.FromMinutes(5) });
    await Task.Delay(100, TestContext.Current.CancellationToken);

    clock.Advance(TimeSpan.FromSeconds(10));
    await Task.Delay(100, TestContext.Current.CancellationToken);
    Assert.False(connecting.IsCompleted);
    clock.Advance(TimeSpan.FromSeconds(25));

    await using var client = await connecting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    Assert.Equal(ProtocolEra.Legacy, client.Era);
  }

  [Fact]
  public async Task The_callers_cancellation_during_a_probe_is_not_mistaken_for_silence()
  {
    var silent = new ScriptedCodec(ProtocolEra.Modern, async ct =>
    {
      await Task.Delay(Timeout.Infinite, ct);
      return true;
    });
    using var cts = new CancellationTokenSource();
    await using var transport = new NamedTransport(null);
    var connecting = ContextClient.ConnectAsync(transport, new ContextClientOptions { Codecs = [silent, new ScriptedCodec(ProtocolEra.Legacy, Yes)] }, cts.Token).AsTask();
    await Task.Delay(100, TestContext.Current.CancellationToken);

    await cts.CancelAsync();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
  }

  [Theory]
  [InlineData("node server.js", new[] { "node", "server.js" })]
  [InlineData("  npx   -y  pkg@1.2.3  ", new[] { "npx", "-y", "pkg@1.2.3" })]
  [InlineData("run --name \"a b\" --x 'c d'", new[] { "run", "--name", "a b", "--x", "c d" })]
  [InlineData("run \"say \\\"hi\\\"\"", new[] { "run", "say \"hi\"" })]
  [InlineData("C:\\tools\\server.exe --flag", new[] { "C:\\tools\\server.exe", "--flag" })]
  [InlineData("run \"\"", new[] { "run", "" })]
  public void A_command_line_splits_into_words(string line, string[] expected)
  {
    Assert.Equal(expected, ContextTransport.SplitCommandLine(line));
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData("run \"unclosed")]
  public void A_bad_command_line_is_refused(string line)
  {
    Assert.Throws<ArgumentException>(() => ContextTransport.SplitCommandLine(line));
  }

  private sealed class ThrowingCodec : IClientCodec
  {
    public ProtocolEra Era => ProtocolEra.Modern;

    public ValueTask<ClientSession?> NegotiateAsync(JsonRpcConnection connection, ClientHello hello, CancellationToken cancellationToken) =>
      throw new ProtocolVersionException(hello.SupportedVersions, ["2026-07-28"], "2099-01-01");

    public JsonElement EncodeParams(ClientSession session, ClientHello hello, string method, RequestParams? parameters) => throw new NotSupportedException();

    public Result DecodeResult(ClientSession session, string method, JsonElement result) => throw new NotSupportedException();
  }
}
