using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RecursiveAI.OneModelContext.Http;

/// <summary>A request head that could not be accepted: the status to answer with, then the connection is closed.</summary>
internal readonly record struct HeadError(int Status, string Reason);

/// <summary>A parsed request line and header block.</summary>
internal sealed class RequestHead
{
  internal required string Method { get; init; }

  internal required string Path { get; init; }

  internal required HttpHeaders Headers { get; init; }

  internal required bool Http11 { get; init; }

  /// <summary>The declared body length, 0 when the request has no body, null when the body is chunked.</summary>
  internal long? ContentLength { get; init; }

  internal bool Chunked { get; init; }

  /// <summary>Whether the client asked to close the connection after this request, or HTTP/1.0 without keep-alive.</summary>
  internal bool Close { get; init; }

  internal bool ExpectContinue { get; init; }
}

/// <summary>
/// Parses the head of an HTTP/1.1 request strictly (RFC 9112 sections 3 to 6). Anything ambiguous is refused rather than guessed
/// at, because disagreement between a parser and the proxy in front of it is what request smuggling feeds on: bare CR or LF,
/// obsolete line folding (section 5.2), whitespace before a colon (section 5.1), control characters in values, a second
/// <c>Host</c> (section 3.2), a second or malformed <c>Content-Length</c>, <c>Content-Length</c> together with
/// <c>Transfer-Encoding</c> (section 6.3), and any transfer coding other than a lone <c>chunked</c>.
/// </summary>
internal static class RequestHeadParser
{
  private static readonly SearchValues<byte> TokenChars = SearchValues.Create("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"u8);

  internal static bool IsToken(ReadOnlySpan<byte> value) => !value.IsEmpty && value.IndexOfAnyExcept(TokenChars) < 0;

  /// <summary>Whether a forwarded host value looks like a host name or address with an optional port, and nothing else.</summary>
  internal static bool IsHostLike(string value)
  {
    if (value.Length is 0 or > 255)
    {
      return false;
    }

    foreach (var c in value)
    {
      if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '[' or ']' or '_'))
      {
        return false;
      }
    }

    return true;
  }

  internal static bool IsToken(string value)
  {
    if (value.Length == 0)
    {
      return false;
    }

    foreach (var c in value)
    {
      if (c > 0x7E || !TokenChars.Contains((byte)c))
      {
        return false;
      }
    }

    return true;
  }

  /// <summary>
  /// Parses <paramref name="block"/>, the bytes before the blank line that ends the head. Returns null and sets
  /// <paramref name="head"/> on success, or the error to answer with.
  /// </summary>
  internal static HeadError? TryParse(ReadOnlySpan<byte> block, HttpServerOptions options, out RequestHead? head)
  {
    head = null;

    // RFC 9112 section 2.2: a bare LF or CR is not a line ending. Refusing both closes the gap between servers that accept LF alone
    // and those that do not.
    for (var i = block.IndexOfAny((byte)'\r', (byte)'\n'); i >= 0;)
    {
      if (block[i] != (byte)'\r' || i + 1 >= block.Length || block[i + 1] != (byte)'\n')
      {
        return new HeadError(400, "Bare CR or LF in the request head.");
      }

      var next = block[(i + 2)..].IndexOfAny((byte)'\r', (byte)'\n');
      i = next < 0 ? -1 : i + 2 + next;
    }

    var position = 0;
    var line = NextLine(block, ref position);
    if (line.Length > options.MaxRequestLineBytes)
    {
      return new HeadError(414, "The request line is too long.");
    }

    // Request line: method SP request-target SP HTTP-version, with exactly one space between the parts (section 3).
    var firstSpace = line.IndexOf((byte)' ');
    if (firstSpace <= 0)
    {
      return new HeadError(400, "Malformed request line.");
    }

    var afterMethod = line[(firstSpace + 1)..];
    var secondSpace = afterMethod.IndexOf((byte)' ');
    if (secondSpace <= 0)
    {
      return new HeadError(400, "Malformed request line.");
    }

    var methodBytes = line[..firstSpace];
    var target = afterMethod[..secondSpace];
    var version = afterMethod[(secondSpace + 1)..];

    // Methods are case-sensitive tokens (RFC 9110 section 9.1); an unknown lower-case spelling is refused, not folded to upper case.
    if (!IsToken(methodBytes) || methodBytes.Length > 32 || methodBytes.IndexOfAnyInRange((byte)'a', (byte)'z') >= 0)
    {
      return new HeadError(400, "Malformed method.");
    }

    bool http11;
    if (version.SequenceEqual("HTTP/1.1"u8))
    {
      http11 = true;
    }
    else if (version.SequenceEqual("HTTP/1.0"u8))
    {
      http11 = false;
    }
    else if (version.Length == 8 && version.StartsWith("HTTP/"u8) && char.IsAsciiDigit((char)version[5]) && version[6] == (byte)'.' && char.IsAsciiDigit((char)version[7]))
    {
      return new HeadError(505, "Only HTTP/1.0 and HTTP/1.1 are supported.");
    }
    else
    {
      return new HeadError(400, "Malformed HTTP version.");
    }

    // Origin-form only (section 3.2.1). Absolute-form is for proxies; a bare "*" is for OPTIONS and nothing here serves it.
    if (target.IsEmpty || target[0] != (byte)'/' || target.IndexOfAnyExceptInRange((byte)0x21, (byte)0x7E) >= 0 || target.IndexOf((byte)'#') >= 0)
    {
      return new HeadError(400, "Only origin-form request targets are accepted.");
    }

    var question = target.IndexOf((byte)'?');
    var pathBytes = question < 0 ? target : target[..question];
    var path = DecodePath(pathBytes);
    if (path is null)
    {
      return new HeadError(400, "Malformed request path.");
    }

    var headers = new HttpHeaders();
    var count = 0;
    string? contentLength = null;
    string? transferEncoding = null;
    var contentLengthCount = 0;
    var transferEncodingCount = 0;
    var hostCount = 0;
    var close = !http11;
    var sawClose = false;
    var expectContinue = false;
    while (position <= block.Length)
    {
      line = NextLine(block, ref position);
      if (line.IsEmpty)
      {
        return new HeadError(400, "Empty header line.");
      }

      // RFC 9112 section 5.2: obs-fold. A server that does not want to unfold it MUST reject the message.
      if (line[0] is (byte)' ' or (byte)'\t')
      {
        return new HeadError(400, "Obsolete line folding is not accepted.");
      }

      if (++count > options.MaxHeaderCount)
      {
        return new HeadError(431, "Too many header fields.");
      }

      var colon = line.IndexOf((byte)':');
      if (colon <= 0)
      {
        return new HeadError(400, "Malformed header field.");
      }

      // Section 5.1: no whitespace between the name and the colon; the token check also refuses it.
      var nameBytes = line[..colon];
      if (!IsToken(nameBytes))
      {
        return new HeadError(400, "Malformed header name.");
      }

      var valueBytes = TrimOws(line[(colon + 1)..]);
      foreach (var b in valueBytes)
      {
        if ((b < 0x20 && b != (byte)'\t') || b == 0x7F)
        {
          return new HeadError(400, "Control character in a header value.");
        }
      }

      var name = Encoding.ASCII.GetString(nameBytes);
      var value = Encoding.Latin1.GetString(valueBytes);
      headers.Add(name, value);
      if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
      {
        hostCount++;
      }
      else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
      {
        contentLength = value;
        contentLengthCount++;
      }
      else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
      {
        transferEncoding = value;
        transferEncodingCount++;
      }
      else if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
      {
        foreach (var token in value.Split(','))
        {
          var t = token.Trim();
          if (t.Equals("close", StringComparison.OrdinalIgnoreCase))
          {
            close = true;
            sawClose = true;
          }
          else if (t.Equals("keep-alive", StringComparison.OrdinalIgnoreCase) && !http11 && !sawClose)
          {
            close = false;
          }
        }
      }
      else if (name.Equals("Expect", StringComparison.OrdinalIgnoreCase))
      {
        if (!value.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
        {
          return new HeadError(417, "Only 100-continue is supported in Expect.");
        }

        expectContinue = true;
      }
    }

    // Section 3.2: exactly one Host in HTTP/1.1.
    if (hostCount > 1 || (http11 && hostCount == 0))
    {
      return new HeadError(400, "A request needs exactly one Host header.");
    }

    long? length = 0;
    var chunked = false;
    if (transferEncodingCount > 0)
    {
      // Section 6.1: Transfer-Encoding in a request is HTTP/1.1; 6.3 rule 3: with Content-Length it is a smuggling attempt.
      if (contentLengthCount > 0 || !http11 || transferEncodingCount > 1)
      {
        return new HeadError(400, "Conflicting message framing.");
      }

      if (!transferEncoding!.Equals("chunked", StringComparison.OrdinalIgnoreCase))
      {
        return new HeadError(501, "Only the chunked transfer coding is supported.");
      }

      chunked = true;
      length = null;
    }
    else if (contentLengthCount > 0)
    {
      // Section 6.3 rule 5 allows folding identical repeated values; refusing every repeat is stricter and just as safe.
      if (contentLengthCount > 1 || !TryParseLength(contentLength!, out var parsed))
      {
        return new HeadError(400, "Invalid Content-Length.");
      }

      length = parsed;
    }

    head = new RequestHead
    {
      Method = Encoding.ASCII.GetString(methodBytes),
      Path = path,
      Headers = headers,
      Http11 = http11,
      ContentLength = length,
      Chunked = chunked,
      Close = close,
      ExpectContinue = expectContinue,
    };
    return null;
  }

  private static ReadOnlySpan<byte> TrimOws(ReadOnlySpan<byte> value)
  {
    var start = 0;
    var end = value.Length;
    while (start < end && value[start] is (byte)' ' or (byte)'\t')
    {
      start++;
    }

    while (end > start && value[end - 1] is (byte)' ' or (byte)'\t')
    {
      end--;
    }

    return value[start..end];
  }

  private static ReadOnlySpan<byte> NextLine(ReadOnlySpan<byte> block, ref int position)
  {
    var rest = block[Math.Min(position, block.Length)..];
    var end = rest.IndexOf("\r\n"u8);
    if (end < 0)
    {
      position = block.Length + 1;
      return rest;
    }

    position += end + 2;
    return rest[..end];
  }

  /// <summary>Content-Length is digits only (RFC 9110 section 8.6): no sign, no spaces, no list, at most 18 digits so it fits a long.</summary>
  internal static bool TryParseLength(string value, out long length)
  {
    length = 0;
    if (value.Length is 0 or > 18)
    {
      return false;
    }

    foreach (var c in value)
    {
      if (!char.IsAsciiDigit(c))
      {
        return false;
      }
    }

    length = long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    return true;
  }

  // The path as the handler sees it: percent-decoded as UTF-8, no controls. Dot segments are not removed; routing is by exact match.
  private static string? DecodePath(ReadOnlySpan<byte> raw)
  {
    var text = Encoding.ASCII.GetString(raw);
    if (text.Contains('%', StringComparison.Ordinal))
    {
      for (var i = text.IndexOf('%', StringComparison.Ordinal); i >= 0; i = text.IndexOf('%', i + 1))
      {
        if (i + 2 >= text.Length || !char.IsAsciiHexDigit(text[i + 1]) || !char.IsAsciiHexDigit(text[i + 2]))
        {
          return null;
        }
      }

      text = Uri.UnescapeDataString(text);
    }

    foreach (var c in text)
    {
      if (c < 0x20 || c == 0x7F || c == '\uFFFD')
      {
        return null;
      }
    }

    return text;
  }
}
