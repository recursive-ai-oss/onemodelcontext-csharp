using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Claims;
using System.Threading;

namespace RecursiveAI.OneModelContext.Http;

/// <summary>
/// The headers of one HTTP message. Names compare case-insensitively (RFC 9110 section 5.1); a name can occur more than once.
/// </summary>
public sealed class HttpHeaders
{
  private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Adds a value. An existing header of the same name keeps its values and gains this one.</summary>
  /// <param name="name">The header name.</param>
  /// <param name="value">The value.</param>
  public void Add(string name, string value)
  {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(value);
    if (!_values.TryGetValue(name, out var list))
    {
      _values[name] = list = [];
    }

    list.Add(value);
  }

  /// <summary>Replaces every value of a header with one.</summary>
  /// <param name="name">The header name.</param>
  /// <param name="value">The value.</param>
  public void Set(string name, string value)
  {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(value);
    _values[name] = [value];
  }

  /// <summary>Gets the first value of a header.</summary>
  /// <param name="name">The header name.</param>
  /// <param name="value">The value, or null when the header is absent.</param>
  public bool TryGetFirst(string name, out string? value)
  {
    if (_values.TryGetValue(name, out var list) && list.Count > 0)
    {
      value = list[0];
      return true;
    }

    value = null;
    return false;
  }

  /// <summary>Gets the first value of a header, or null.</summary>
  /// <param name="name">The header name.</param>
  public string? GetFirst(string name) => TryGetFirst(name, out var value) ? value : null;

  /// <summary>Gets every value of a header, in the order added.</summary>
  /// <param name="name">The header name.</param>
  public IReadOnlyList<string> GetAll(string name) => _values.TryGetValue(name, out var list) ? list : [];

  /// <summary>Removes a header and all its values.</summary>
  /// <param name="name">The header name.</param>
  /// <returns>True when the header was present.</returns>
  public bool Remove(string name) => _values.Remove(name);

  /// <summary>Whether the header is present.</summary>
  /// <param name="name">The header name.</param>
  public bool Contains(string name) => _values.ContainsKey(name);

  /// <summary>Every header name present, in no particular order.</summary>
  public IEnumerable<string> Names => _values.Keys;
}

/// <summary>An HTTP request as a host presents it to a handler.</summary>
public interface IHttpRequest
{
  /// <summary>The method in upper case, such as <c>POST</c>.</summary>
  string Method { get; }

  /// <summary>The request path, without the query.</summary>
  string Path { get; }

  /// <summary>The raw query string without the leading <c>?</c>, still percent-encoded; empty when the request has none. Default empty for hosts that do not say.</summary>
  string Query => "";

  /// <summary>The headers.</summary>
  HttpHeaders Headers { get; }

  /// <summary>The declared body length, or null when it is not known (chunked).</summary>
  long? ContentLength { get; }

  /// <summary>The request body. Reading it is bounded by the handler, not by the host.</summary>
  Stream Body { get; }

  /// <summary>The peer's address, or null when the host does not know it.</summary>
  IPAddress? RemoteAddress { get; }

  /// <summary>
  /// The scheme the caller used, <c>http</c> or <c>https</c>. A host behind a trusted proxy reports the scheme the proxy saw. Default
  /// <c>http</c> for hosts that do not say.
  /// </summary>
  string Scheme => "http";
}

/// <summary>An HTTP response a handler fills in. Set the status and headers before the first write to <see cref="Body"/>.</summary>
public interface IHttpResponse
{
  /// <summary>The status code. Settable until the first byte of the body is written.</summary>
  int StatusCode { get; set; }

  /// <summary>Sets a header, replacing earlier values. Valid until the first byte of the body is written.</summary>
  /// <param name="name">The header name.</param>
  /// <param name="value">The value; it must not contain a line break.</param>
  void SetHeader(string name, string value);

  /// <summary>The response body. A write sends the headers if they are not sent yet; a flush pushes the bytes to the peer.</summary>
  Stream Body { get; }
}

/// <summary>
/// One request and its response on a host (the built-in listener, ASP.NET Core, a test). Handlers are written against this so that
/// one implementation of the protocol serves every host.
/// </summary>
public interface IHttpExchange
{
  /// <summary>The request.</summary>
  IHttpRequest Request { get; }

  /// <summary>The response.</summary>
  IHttpResponse Response { get; }

  /// <summary>The caller the host authenticated, or null.</summary>
  ClaimsPrincipal? User { get; }

  /// <summary>Cancelled when the peer disconnects or the host shuts down.</summary>
  CancellationToken Aborted { get; }
}
