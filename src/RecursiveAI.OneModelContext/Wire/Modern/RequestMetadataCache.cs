using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Wire.Modern;

// Modern peers repeat their identity on every request. Keep one exact JSON match per codec, never an unbounded client map.
// A changed capability, unknown member, trace or progress token is part of the key, not an assumption about the peer.
internal sealed class RequestMetadataCache
{
  private sealed record Entry(string Json, MetaObject Meta);

  private Entry? _last;

  internal MetaObject Read(JsonElement element)
  {
    var json = element.GetRawText();
    var last = Volatile.Read(ref _last);
    if (last is not null && string.Equals(json, last.Json, System.StringComparison.Ordinal))
    {
      return Copy(last.Meta);
    }

    var meta = element.Deserialize(WireJsonContext.Default.MetaObject)!;
    // This is an optimization bound, not an input limit: larger metadata still decodes normally and is never retained here.
    if (json.Length <= 4096)
    {
      Volatile.Write(ref _last, new Entry(json, meta));
      return Copy(meta);
    }

    return meta;
  }

  // Protocol records expose mutable dictionaries and lists. Never hand the cached instances to a handler, including on a miss.
  // JsonElements and strings are immutable and can be shared; all mutable containers and records must be copied.
  private static MetaObject Copy(MetaObject value) => new()
  {
    ProtocolVersion = value.ProtocolVersion,
    ClientInfo = Copy(value.ClientInfo),
    ClientCapabilities = Copy(value.ClientCapabilities),
    ServerInfo = Copy(value.ServerInfo),
    ProgressToken = value.ProgressToken,
    SubscriptionId = value.SubscriptionId,
    LogLevel = value.LogLevel,
    TraceParent = value.TraceParent,
    TraceState = value.TraceState,
    Baggage = value.Baggage,
    AdditionalProperties = Copy(value.AdditionalProperties),
  };

  private static Dictionary<string, JsonElement>? Copy(Dictionary<string, JsonElement>? value) =>
    value is null ? null : new(value, value.Comparer);

  private static Implementation? Copy(Implementation? value)
  {
    if (value is null)
    {
      return null;
    }

    List<Icon>? icons = null;
    if (value.Icons is { } source)
    {
      icons = new(source.Count);
      foreach (var icon in source)
      {
        icons.Add(icon is null ? null! : new Icon
        {
          Source = icon.Source,
          MimeType = icon.MimeType,
          Theme = icon.Theme,
          Sizes = icon.Sizes is null ? null : new(icon.Sizes),
          AdditionalProperties = Copy(icon.AdditionalProperties),
        });
      }
    }

    return new Implementation
    {
      Name = value.Name,
      Version = value.Version,
      Title = value.Title,
      Description = value.Description,
      WebsiteUrl = value.WebsiteUrl,
      Icons = icons,
      AdditionalProperties = Copy(value.AdditionalProperties),
    };
  }

  private static ClientCapabilities? Copy(ClientCapabilities? value) => value is null ? null : new()
  {
    Experimental = Copy(value.Experimental),
    Extensions = Copy(value.Extensions),
    Tasks = value.Tasks,
    Roots = value.Roots is not { } roots ? null : new RootsCapability
    {
      ListChanged = roots.ListChanged,
      AdditionalProperties = Copy(roots.AdditionalProperties),
    },
    Sampling = value.Sampling is not { } sampling ? null : new SamplingCapability
    {
      Context = sampling.Context,
      Tools = sampling.Tools,
      AdditionalProperties = Copy(sampling.AdditionalProperties),
    },
    Elicitation = value.Elicitation is not { } elicitation ? null : new ElicitationCapability
    {
      Form = elicitation.Form,
      Url = elicitation.Url,
      AdditionalProperties = Copy(elicitation.AdditionalProperties),
    },
    AdditionalProperties = Copy(value.AdditionalProperties),
  };
}
