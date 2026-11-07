using System.Collections.Generic;

namespace RecursiveAI.OneModelContext.Wire.Modern;

// The modern (2026-07-28) codec registers itself here.
internal static class ModernCodecRegistration
{
  internal static void AddServer(List<IServerCodec> codecs) => codecs.Add(new ModernServerCodec());

  internal static void AddClient(List<IClientCodec> codecs) => codecs.Add(new ModernClientCodec());
}
