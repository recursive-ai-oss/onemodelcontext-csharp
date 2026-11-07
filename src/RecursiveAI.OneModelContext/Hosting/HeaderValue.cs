using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneModelContext.Http;

namespace RecursiveAI.OneModelContext.Hosting;

/// <summary>
/// The values 2026-07-28 Streamable HTTP mirrors from the request body into headers (<c>Mcp-Name</c> and <c>Mcp-Param-{Name}</c>), and the
/// check that the two agree ("Value Encoding", "Server Validation").
/// </summary>
internal static class HeaderValue
{
  private const string Prefix = "=?base64?";
  private const string Suffix = "?=";
  private const int MaxSchemaDepth = 8;

  /// <summary>
  /// Reads a header value that may use the Base64 sentinel form <c>=?base64?...?=</c>. A plain value must be visible ASCII, space or tab;
  /// anything else, or a sentinel that is not valid Base64 of valid UTF-8, is refused.
  /// </summary>
  internal static bool TryDecode(string raw, out string value)
  {
    if (raw.Length >= Prefix.Length + Suffix.Length && raw.StartsWith(Prefix, StringComparison.Ordinal) && raw.EndsWith(Suffix, StringComparison.Ordinal))
    {
      var inner = raw.AsSpan(Prefix.Length, raw.Length - Prefix.Length - Suffix.Length);
      var bytes = new byte[inner.Length];
      if (Convert.TryFromBase64Chars(inner, bytes, out var written))
      {
        try
        {
          value = new UTF8Encoding(false, true).GetString(bytes, 0, written);
          return true;
        }
        catch (DecoderFallbackException)
        {
          // Not UTF-8.
        }
      }

      value = string.Empty;
      return false;
    }

    foreach (var c in raw)
    {
      if (!(c is '\t' || (c >= 0x20 && c <= 0x7E)))
      {
        value = string.Empty;
        return false;
      }
    }

    value = raw;
    return true;
  }

  /// <summary>
  /// Checks the <c>Mcp-Param-*</c> headers of a <c>tools/call</c> against the arguments: every parameter the tool's schema marks with
  /// <c>x-mcp-header</c> and that has a value in the body must have a matching header; a header that is present must be well formed.
  /// </summary>
  /// <returns>The -32020 error to answer with, or null when everything agrees.</returns>
  internal static ProtocolException? CheckParameterHeaders(HttpHeaders headers, JsonElement inputSchema, JsonElement arguments)
  {
    var annotated = new List<(string Header, string[] Path)>();
    Collect(inputSchema, [], annotated, 0);
    foreach (var (name, path) in annotated)
    {
      var headerName = "Mcp-Param-" + name;
      var values = headers.GetAll(headerName);
      var bodyValue = Lookup(arguments, path);
      var hasBodyValue = bodyValue.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
      if (values.Count > 1)
      {
        return new HeaderMismatchException("Header mismatch: " + headerName + " occurs more than once.");
      }

      if (values.Count == 1)
      {
        if (!TryDecode(values[0], out var decoded))
        {
          return new HeaderMismatchException("Header mismatch: " + headerName + " contains invalid characters.");
        }

        if (hasBodyValue && !Matches(decoded, bodyValue))
        {
          return new HeaderMismatchException("Header mismatch: " + headerName + " does not match the request body.");
        }
      }
      else if (values.Count == 0 && hasBodyValue && bodyValue.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
      {
        return new HeaderMismatchException("Header mismatch: " + headerName + " is required because the request carries that parameter.");
      }
    }

    return null;
  }

  private static bool Matches(string header, JsonElement body) => body.ValueKind switch
  {
    JsonValueKind.String => string.Equals(header, body.GetString(), StringComparison.Ordinal),
    JsonValueKind.True => header == "true",
    JsonValueKind.False => header == "false",
    // Integers compare numerically, so 42.0 equals 42 (Server Validation note).
    JsonValueKind.Number => body.TryGetDecimal(out var number)
      && decimal.TryParse(header, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
      && parsed == number,
    _ => true,
  };

  // Only properties reachable through "properties" keys count; an annotation elsewhere makes the tool definition invalid, which the
  // server cannot repair, so such annotations are not enforced here.
  private static void Collect(JsonElement schema, List<string> path, List<(string, string[])> found, int depth)
  {
    if (depth > MaxSchemaDepth || schema.ValueKind != JsonValueKind.Object
      || !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
    {
      return;
    }

    foreach (var property in properties.EnumerateObject())
    {
      path.Add(property.Name);
      if (property.Value.ValueKind == JsonValueKind.Object
        && property.Value.TryGetProperty("x-mcp-header", out var header)
        && header.ValueKind == JsonValueKind.String
        && header.GetString() is { Length: > 0 } headerName)
      {
        found.Add((headerName, [.. path]));
      }

      Collect(property.Value, path, found, depth + 1);
      path.RemoveAt(path.Count - 1);
    }
  }

  private static JsonElement Lookup(JsonElement arguments, string[] path)
  {
    var current = arguments;
    foreach (var segment in path)
    {
      if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
      {
        return default;
      }
    }

    return current;
  }
}
