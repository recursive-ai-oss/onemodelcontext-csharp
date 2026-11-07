using System;
using System.IO;
using System.Net;
using System.Security.Claims;
using System.Threading;

namespace RecursiveAI.OneModelContext.Http;

internal sealed class ListenerRequest : IHttpRequest
{
  private readonly RequestHead _head;

  internal ListenerRequest(RequestHead head, Stream? body, IPAddress? remote, string scheme)
  {
    _head = head;
    Body = body ?? Stream.Null;
    RemoteAddress = remote;
    Scheme = scheme;
  }

  public string Method => _head.Method;

  public string Path => _head.Path;

  public string Query => _head.Query;

  public HttpHeaders Headers => _head.Headers;

  public long? ContentLength => _head.ContentLength;

  public Stream Body { get; }

  public IPAddress? RemoteAddress { get; }

  public string Scheme { get; }
}

internal sealed class ListenerExchange(ListenerRequest request, ListenerResponse response, CancellationToken aborted) : IHttpExchange
{
  public IHttpRequest Request => request;

  public IHttpResponse Response => response;

  public ClaimsPrincipal? User => null;

  public CancellationToken Aborted => aborted;
}
