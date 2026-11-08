using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Wire.Modern;

/// <summary>
/// The client half of revision 2026-07-28: no handshake, a <c>server/discover</c> probe to learn what the server speaks, and the
/// version, identity and capabilities repeated in every request's <c>_meta</c>.
/// </summary>
internal sealed class ModernClientCodec : IClientCodec
{
  public ProtocolEra Era => ProtocolEra.Modern;

  public async ValueTask<ClientSession?> NegotiateAsync(JsonRpcConnection connection, ClientHello hello, CancellationToken cancellationToken)
  {
    var wanted = NewestModern(hello.SupportedVersions);
    if (wanted is null)
    {
      return null;
    }

    JsonElement raw;
    var retried = false;
    while (true)
    {
      var parameters = ModernEnvelope.WrapParams(default, wanted, hello.ClientInfo, hello.Capabilities);
      try
      {
        raw = await connection.SendRequestAsync(Methods.ServerDiscover, parameters, cancellationToken).ConfigureAwait(false);
        break;
      }
      catch (JsonRpcRemoteException remote)
      {
        Diagnostics.ContextDiagnostics.Publish(Diagnostics.DiagnosticKind.Note, "Modern probe returned error " + remote.Code.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".", "negotiation");
        // basic/versioning, "Protocol Version Negotiation": on -32022 the client SHOULD pick a version from the server's list that it also
        // speaks and ask again, once; a second refusal is final.
        if (!retried && remote.Code == ProtocolErrorCodes.UnsupportedProtocolVersion
          && FirstCommonModern(hello.SupportedVersions, ProtocolVersionException.FromData(remote.Error).Remote) is { } agreed)
        {
          retried = true;
          wanted = agreed;
          continue;
        }

        return HandleProbeError(remote.Error, hello, wanted);
      }
    }

    DiscoverResult discover;
    try
    {
      discover = raw.Deserialize(WireJsonContext.Default.DiscoverResult) ?? throw new JsonException("null result");
    }
    catch (JsonException)
    {
      hello.ProbeInconclusive = true;
      Diagnostics.ContextDiagnostics.Publish(Diagnostics.DiagnosticKind.Note, "Modern probe result could not be decoded.", "negotiation");
      // Something answered server/discover with a body that is not a DiscoverResult: not a modern server.
      return null;
    }

    hello.ServerVersionsSeen.AddRange(discover.SupportedVersions);
    var common = FirstCommonModern(hello.SupportedVersions, discover.SupportedVersions);
    if (common is null)
    {
      hello.ProbeInconclusive = discover.SupportedVersions.Count == 0;
      Diagnostics.ContextDiagnostics.Publish(Diagnostics.DiagnosticKind.Note, "Modern probe advertised no compatible modern revision.", "negotiation");
      // A server that lists only legacy revisions is a legacy server; one that lists a newer modern revision than ours is a version problem.
      return HasModern(discover.SupportedVersions)
        ? throw new ProtocolVersionException(hello.SupportedVersions, discover.SupportedVersions, wanted)
        : null;
    }

    return new ClientSession(this, common, discover.Meta?.ServerInfo ?? new Implementation { Name = "unknown", Version = "0" }, discover.Capabilities)
    {
      Instructions = discover.Instructions,
      ServerVersions = discover.SupportedVersions,
    };
  }

  // The identity members of _meta are the same for every request of a client, so they are serialized once per client and version.
  private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ClientHello, IdentityCache> Identities = new();

  private sealed record IdentityCache(string Version, byte[] Members);

  public JsonElement EncodeParams(ClientSession session, ClientHello hello, string method, RequestParams? parameters)
  {
    if (!Identities.TryGetValue(hello, out var identity) || identity.Version != session.ProtocolVersion)
    {
      identity = new IdentityCache(session.ProtocolVersion, ModernEnvelope.IdentityMembers(session.ProtocolVersion, hello.ClientInfo, hello.Capabilities));
      Identities.AddOrUpdate(hello, identity);
    }

    if (parameters is null)
    {
      return ModernEnvelope.WrapSerializedParams("{}"u8, session.ProtocolVersion, hello.ClientInfo, hello.Capabilities, identity.Members);
    }

    var info = WireJsonContext.Default.GetTypeInfo(parameters.GetType())
      ?? throw new InvalidOperationException("The parameter type " + parameters.GetType().Name + " is not a protocol record.");
    var bytes = new System.Buffers.ArrayBufferWriter<byte>(256);
    using (var writer = new Utf8JsonWriter(bytes))
    {
      JsonSerializer.Serialize(writer, parameters, info);
    }

    return ModernEnvelope.WrapSerializedParams(bytes.WrittenSpan, session.ProtocolVersion, hello.ClientInfo, hello.Capabilities, identity.Members);
  }

  // 2026-07-28 basic/versioning: an absent resultType is read as complete, which ResultTypes.Decode does by treating every result
  // that is not input_required as final.
  public Result DecodeResult(ClientSession session, string method, JsonElement result) => ResultTypes.Decode(method, result);

  private static ClientSession? HandleProbeError(JsonRpcError error, ClientHello hello, string wanted)
  {
    if (error.Code != ProtocolErrorCodes.UnsupportedProtocolVersion)
    {
      // Fallback can keep this connection usable, but only a definitive rejection may teach the era cache.
      var rejectedHttp = error.Code == Transports.HttpChannel.HttpErrorCode && error.Data.ValueKind == JsonValueKind.Object
        && error.Data.TryGetProperty("transport", out var transport) && transport.ValueKind == JsonValueKind.True
        && error.Data.TryGetProperty("httpStatus", out var status) && status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var code) && code is 400 or 404 or 405;
      hello.ProbeInconclusive = error.Code != JsonRpcErrorCodes.MethodNotFound && !rejectedHttp;
      return null;
    }

    // -32022 is a recognised modern error: the server is modern, so the question is which version to retry with.
    var failure = ProtocolVersionException.FromData(error);
    hello.ServerVersionsSeen.AddRange(failure.Remote);
    if (FirstCommonModern(hello.SupportedVersions, failure.Remote) is not null)
    {
      // The server named a revision we speak that it also accepts; this client has one modern revision, so asking again would repeat itself.
      throw new ProtocolVersionException(hello.SupportedVersions, failure.Remote, wanted);
    }

    foreach (var version in failure.Remote)
    {
      if (ProtocolVersions.IsKnown(version) && ProtocolVersions.EraOf(version) == ProtocolEra.Legacy && Contains(hello.SupportedVersions, version))
      {
        // The server only offers legacy revisions we also speak: let the legacy codec negotiate.
        return null;
      }
    }

    throw new ProtocolVersionException(hello.SupportedVersions, failure.Remote, wanted);
  }

  private static string? NewestModern(IReadOnlyList<string> versions)
  {
    foreach (var version in versions)
    {
      if (ProtocolVersions.IsKnown(version) && ProtocolVersions.EraOf(version) == ProtocolEra.Modern)
      {
        return version;
      }
    }

    return null;
  }

  private static string? FirstCommonModern(IReadOnlyList<string> ours, IReadOnlyList<string> theirs)
  {
    foreach (var version in ours)
    {
      if (ProtocolVersions.IsKnown(version) && ProtocolVersions.EraOf(version) == ProtocolEra.Modern && Contains(theirs, version))
      {
        return version;
      }
    }

    return null;
  }

  private static bool HasModern(IReadOnlyList<string> versions)
  {
    foreach (var version in versions)
    {
      if (ProtocolVersions.IsKnown(version) && ProtocolVersions.EraOf(version) == ProtocolEra.Modern)
      {
        return true;
      }
    }

    return false;
  }

  private static bool Contains(IReadOnlyList<string> versions, string version)
  {
    foreach (var candidate in versions)
    {
      if (candidate == version)
      {
        return true;
      }
    }

    return false;
  }
}
