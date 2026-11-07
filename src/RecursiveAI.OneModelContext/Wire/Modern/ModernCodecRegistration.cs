using System.Collections.Generic;

namespace RecursiveAI.OneModelContext.Wire.Modern;

// The modern (2026-07-28) codec registers itself here. No codec is registered yet, so a server or client built from this
// tree answers or negotiates nothing until one is.
internal static class ModernCodecRegistration
{
  internal static void AddServer(List<IServerCodec> codecs)
  {
  }

  internal static void AddClient(List<IClientCodec> codecs)
  {
  }
}
