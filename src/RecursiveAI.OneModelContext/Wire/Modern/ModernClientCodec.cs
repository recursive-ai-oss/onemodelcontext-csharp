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

    var parameters = ModernEnvelope.WrapParams(default, wanted, hello.ClientInfo, hello.Capabilities);
    JsonElement raw;
    try
    {
      raw = await connection.SendRequestAsync(Methods.ServerDiscover, parameters, cancellationToken).ConfigureAwait(false);
    }
    catch (JsonRpcRemoteException remote)
    {
      return HandleProbeError(remote.Error, hello, wanted);
    }

    DiscoverResult discover;
    try
    {
      discover = raw.Deserialize(WireJsonContext.Default.DiscoverResult) ?? throw new JsonException("null result");
    }
    catch (JsonException)
    {
      // Something answered server/discover with a body that is not a DiscoverResult: not a modern server.
      return null;
    }

    hello.ServerVersionsSeen.AddRange(discover.SupportedVersions);
    var common = FirstCommonModern(hello.SupportedVersions, discover.SupportedVersions);
    if (common is null)
    {
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

  public JsonElement EncodeParams(ClientSession session, ClientHello hello, string method, RequestParams? parameters)
  {
    var serialized = default(JsonElement);
    if (parameters is not null)
    {
      var info = WireJsonContext.Default.GetTypeInfo(parameters.GetType())
        ?? throw new InvalidOperationException("The parameter type " + parameters.GetType().Name + " is not a protocol record.");
      serialized = JsonSerializer.SerializeToElement(parameters, info);
    }

    return ModernEnvelope.WrapParams(serialized, session.ProtocolVersion, hello.ClientInfo, hello.Capabilities);
  }

  // 2026-07-28 basic/versioning: an absent resultType is read as complete, which ResultTypes.Decode does by treating every result
  // that is not input_required as final.
  public Result DecodeResult(ClientSession session, string method, JsonElement result) => ResultTypes.Decode(method, result);

  private static ClientSession? HandleProbeError(JsonRpcError error, ClientHello hello, string wanted)
  {
    if (error.Code != ProtocolErrorCodes.UnsupportedProtocolVersion)
    {
      // Any other error answers for a legacy server (basic/versioning, stdio backward compatibility): fall back to initialize.
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
