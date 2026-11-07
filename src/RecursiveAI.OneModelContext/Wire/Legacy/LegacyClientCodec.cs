using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Wire.Legacy;

/// <summary>The client side of revisions 2024-11-05 to 2025-11-25: <c>initialize</c>, then plain requests.</summary>
internal sealed class LegacyClientCodec : IClientCodec
{
  public ProtocolEra Era => ProtocolEra.Legacy;

  public async ValueTask<ClientSession?> NegotiateAsync(JsonRpcConnection connection, ClientHello hello, CancellationToken cancellationToken)
  {
    string? preferred = null;
    foreach (var version in hello.SupportedVersions)
    {
      if (LegacyShape.IsLegacy(version) && (preferred is null || LegacyShape.Rank(version) > LegacyShape.Rank(preferred)))
      {
        preferred = version;
      }
    }

    if (preferred is null)
    {
      return null;
    }

    var parameters = new InitializeRequestParams { ProtocolVersion = preferred, Capabilities = hello.Capabilities, ClientInfo = hello.ClientInfo };
    JsonElement raw;
    try
    {
      raw = await connection.SendRequestAsync(Methods.Initialize, JsonSerializer.SerializeToElement(parameters, WireJsonContext.Default.InitializeRequestParams), cancellationToken).ConfigureAwait(false);
    }
    catch (JsonRpcRemoteException remote)
    {
      // lifecycle: an unsupported version is -32602 with data.supported; a 2026-07-28 server may use -32022.
      if (remote.Code == ProtocolErrorCodes.UnsupportedProtocolVersion
        || (remote.Code == JsonRpcErrorCodes.InvalidParams && remote.ErrorData.ValueKind == JsonValueKind.Object && remote.ErrorData.TryGetProperty("supported", out _)))
      {
        var versions = ProtocolVersionException.FromData(remote.Error);
        throw new ProtocolVersionException(hello.SupportedVersions, versions.Remote, preferred);
      }

      // Anything else: the server does not speak this era, so the caller may try another.
      return null;
    }

    InitializeResult result;
    try
    {
      result = raw.Deserialize(WireJsonContext.Default.InitializeResult) ?? throw new JsonException("null result");
    }
    catch (JsonException)
    {
      return null;
    }

    // lifecycle: if the client does not support the version the server answered with, it SHOULD disconnect.
    if (!LegacyShape.IsLegacy(result.ProtocolVersion) || !Contains(hello.SupportedVersions, result.ProtocolVersion))
    {
      throw new ProtocolVersionException(hello.SupportedVersions, [result.ProtocolVersion], preferred);
    }

    await connection.SendNotificationAsync(Methods.Initialized, default, cancellationToken).ConfigureAwait(false);
    return new ClientSession(this, result.ProtocolVersion, result.ServerInfo, result.Capabilities) { Instructions = result.Instructions };
  }

  public JsonElement EncodeParams(ClientSession session, ClientHello hello, string method, RequestParams? parameters)
  {
    if (parameters is null)
    {
      return default;
    }

    var info = WireJsonContext.Default.GetTypeInfo(parameters.GetType())
      ?? throw new InvalidOperationException("The parameters type is not a protocol record.");
    var element = JsonSerializer.SerializeToElement(parameters, info);

    // inputResponses and requestState are 2026-07-28 members; a legacy server must never see them.
    if (element.ValueKind == JsonValueKind.Object && (element.TryGetProperty("inputResponses", out _) || element.TryGetProperty("requestState", out _)))
    {
      var node = (JsonObject)JsonNode.Parse(element.GetRawText())!;
      node.Remove("inputResponses");
      node.Remove("requestState");
      element = JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }

    return element;
  }

  public Result DecodeResult(ClientSession session, string method, JsonElement result) => ResultTypes.Decode(method, result);

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
