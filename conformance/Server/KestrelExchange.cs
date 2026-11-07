using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using RecursiveAI.OneModelContext.Http;

namespace RecursiveAI.OneModelContext.ConformanceServer;

/// <summary>Presents an ASP.NET Core request to <see cref="Hosting.StreamableHttpEndpoint"/>. Kestrel is only the test host here.</summary>
internal sealed class KestrelExchange : IHttpExchange
{
  private readonly HttpContext _context;
  private readonly Req _request;
  private readonly Res _response;

  internal KestrelExchange(HttpContext context)
  {
    _context = context;
    _request = new Req(context.Request);
    _response = new Res(context.Response);
    context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
  }

  public IHttpRequest Request => _request;

  public IHttpResponse Response => _response;

  public ClaimsPrincipal? User => _context.User.Identity?.IsAuthenticated == true ? _context.User : null;

  public CancellationToken Aborted => _context.RequestAborted;

  private sealed class Req : IHttpRequest
  {
    private readonly HttpRequest _inner;

    internal Req(HttpRequest inner)
    {
      _inner = inner;
      foreach (var header in inner.Headers)
      {
        foreach (var value in header.Value)
        {
          if (value is not null)
          {
            Headers.Add(header.Key, value);
          }
        }
      }
    }

    public string Method => _inner.Method.ToUpperInvariant();

    public string Path => _inner.Path.Value ?? "/";

    public HttpHeaders Headers { get; } = new();

    public long? ContentLength => _inner.ContentLength;

    public Stream Body => _inner.Body;

    public IPAddress? RemoteAddress => _inner.HttpContext.Connection.RemoteIpAddress;
  }

  private sealed class Res(HttpResponse inner) : IHttpResponse
  {
    public int StatusCode
    {
      get => inner.StatusCode;
      set => inner.StatusCode = value;
    }

    public void SetHeader(string name, string value) => inner.Headers[name] = value;

    public Stream Body => inner.Body;
  }
}
