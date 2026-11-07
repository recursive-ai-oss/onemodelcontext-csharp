using System.Collections.Generic;

namespace RecursiveAI.OneModelContext.Wire.Legacy;

// The legacy (2024-11-05 to 2025-11-25) codec.
internal static class LegacyCodecRegistration
{
  internal static void AddServer(List<IServerCodec> codecs) => codecs.Add(new LegacyServerCodec());

  internal static void AddClient(List<IClientCodec> codecs) => codecs.Add(new LegacyClientCodec());
}
