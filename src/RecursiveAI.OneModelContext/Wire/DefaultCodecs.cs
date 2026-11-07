using System.Collections.Generic;
using RecursiveAI.OneModelContext.Wire.Legacy;
using RecursiveAI.OneModelContext.Wire.Modern;

namespace RecursiveAI.OneModelContext.Wire;

/// <summary>The codecs a server or client uses unless its options supply others: modern first, then legacy.</summary>
internal static class DefaultCodecs
{
  internal static List<IServerCodec> Server()
  {
    var codecs = new List<IServerCodec>();
    ModernCodecRegistration.AddServer(codecs);
    LegacyCodecRegistration.AddServer(codecs);
    return codecs;
  }

  internal static List<IClientCodec> Client()
  {
    var codecs = new List<IClientCodec>();
    ModernCodecRegistration.AddClient(codecs);
    LegacyCodecRegistration.AddClient(codecs);
    return codecs;
  }
}
