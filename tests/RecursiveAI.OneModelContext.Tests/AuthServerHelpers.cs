using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Http;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>An HTTP exchange in memory for endpoints that are not the Streamable HTTP endpoint.</summary>
internal sealed class AsExchange : IHttpExchange
{
  private readonly Req _request;
  private readonly Res _response = new();

  internal AsExchange(string method, string path, string query = "", string? body = null, string contentType = "application/x-www-form-urlencoded", params (string Name, string Value)[] headers)
  {
    var bytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
    _request = new Req(method, path, query, new MemoryStream(bytes), bytes.Length);
    if (body is not null)
    {
      _request.Headers.Add("Content-Type", contentType);
    }

    foreach (var (name, value) in headers)
    {
      _request.Headers.Add(name, value);
    }
  }

  public IHttpRequest Request => _request;

  public IHttpResponse Response => _response;

  public System.Security.Claims.ClaimsPrincipal? User => null;

  public CancellationToken Aborted => CancellationToken.None;

  internal int Status => _response.StatusCode;

  internal HttpHeaders ResponseHeaders => _response.Headers;

  internal string BodyText => Encoding.UTF8.GetString(_response.Written.ToArray());

  internal JsonElement Json => JsonDocument.Parse(_response.Written.ToArray()).RootElement.Clone();

  /// <summary>The parameters of the redirect the server answered with, from its query string.</summary>
  internal Dictionary<string, string> RedirectQuery
  {
    get
    {
      var location = ResponseHeaders.GetFirst("Location") ?? throw new InvalidOperationException("No redirect (status " + Status + ").");
      var q = location[(location.IndexOf('?', StringComparison.Ordinal) + 1)..];
      var result = new Dictionary<string, string>();
      foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
      {
        var eq = pair.IndexOf('=', StringComparison.Ordinal);
        result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
      }

      return result;
    }
  }

  private sealed class Req(string method, string path, string query, Stream body, long length) : IHttpRequest
  {
    public string Method { get; } = method;

    public string Path { get; } = path;

    public string Query { get; } = query;

    public HttpHeaders Headers { get; } = new();

    public long? ContentLength { get; } = length;

    public Stream Body { get; } = body;

    public IPAddress? RemoteAddress => IPAddress.Loopback;
  }

  private sealed class Res : IHttpResponse
  {
    public int StatusCode { get; set; } = 200;

    public HttpHeaders Headers { get; } = new();

    public MemoryStream Written { get; } = new();

    public void SetHeader(string name, string value) => Headers.Set(name, value);

    public Stream Body => Written;
  }
}

/// <summary>
/// A tiny HTTPS (or plain HTTP) server on a loopback port for fetch tests, with a throwaway certificate generated per run.
/// Routes can redirect, send big bodies, or stop halfway to play a slow server.
/// </summary>
internal sealed class LoopbackServer : IDisposable
{
  private readonly TcpListener _listener;
  private readonly X509Certificate2? _certificate;
  private readonly CancellationTokenSource _stop = new();
  private readonly ConcurrentDictionary<string, Route> _routes = new();
  private int _hits;

  internal LoopbackServer(bool tls = true)
  {
    if (tls)
    {
      using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
      var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
      using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
      _certificate = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    // The port range given to this unit; the first free one wins.
    for (var port = 48100; ; port++)
    {
      var listener = new TcpListener(IPAddress.Loopback, port);
      try
      {
        listener.Start();
        _listener = listener;
        Port = port;
        break;
      }
      catch (SocketException) when (port < 48199)
      {
        listener.Stop();
      }
    }

    _ = Task.Run(AcceptLoopAsync);
  }

  /// <summary>Trusts only this fixture's throwaway certificate (subject <c>CN=localhost</c>, made per run).</summary>
  internal static bool Trust(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) => certificate?.Subject == "CN=localhost";

  internal int Port { get; }

  internal int Hits => Volatile.Read(ref _hits);

  internal string Url(string path) => (_certificate is null ? "http" : "https") + "://127.0.0.1:" + Port + path;

  internal sealed record Route(int Status = 200, string ContentType = "application/json", byte[]? Body = null, string? Location = null, string? CacheControl = null, bool Stall = false, int? ContentLength = null);

  internal void Add(string path, Route route) => _routes[path] = route;

  internal void AddJson(string path, string json, string? cacheControl = null) => Add(path, new Route(Body: Encoding.UTF8.GetBytes(json), CacheControl: cacheControl));

  private async Task AcceptLoopAsync()
  {
    while (!_stop.IsCancellationRequested)
    {
      TcpClient client;
      try
      {
        client = await _listener.AcceptTcpClientAsync(_stop.Token);
      }
      catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
      {
        return;
      }

      _ = Task.Run(() => ServeAsync(client));
    }
  }

  private async Task ServeAsync(TcpClient client)
  {
    using (client)
    {
      try
      {
        Stream stream = client.GetStream();
        if (_certificate is not null)
        {
          var ssl = new SslStream(stream);
          await ssl.AuthenticateAsServerAsync(_certificate);
          stream = ssl;
        }

        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && head.Length < 8192)
        {
          if (await stream.ReadAsync(one, _stop.Token) == 0)
          {
            return;
          }

          head.Append((char)one[0]);
        }

        Interlocked.Increment(ref _hits);
        var path = head.ToString().Split(' ')[1];
        var route = _routes.GetValueOrDefault(path) ?? new Route(404, Body: "{}"u8.ToArray());
        var body = route.Body ?? [];
        var response = new StringBuilder();
        response.Append("HTTP/1.1 ").Append(route.Status).Append(" X\r\n");
        response.Append("Content-Type: ").Append(route.ContentType).Append("\r\n");
        response.Append("Content-Length: ").Append(route.ContentLength ?? body.Length).Append("\r\nConnection: close\r\n");
        if (route.Location is not null)
        {
          response.Append("Location: ").Append(route.Location).Append("\r\n");
        }

        if (route.CacheControl is not null)
        {
          response.Append("Cache-Control: ").Append(route.CacheControl).Append("\r\n");
        }

        response.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response.ToString()), _stop.Token);
        if (route.Stall)
        {
          // Send a little, then go quiet until the test ends: a slow-loris response.
          await stream.WriteAsync(body.AsMemory(0, Math.Min(10, body.Length)), _stop.Token);
          await stream.FlushAsync(_stop.Token);
          await Task.Delay(Timeout.Infinite, _stop.Token);
        }

        await stream.WriteAsync(body, _stop.Token);
        await stream.FlushAsync(_stop.Token);
      }
      catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
      {
        // The client hung up or the test ended.
      }
    }
  }

  public void Dispose()
  {
    _stop.Cancel();
    _listener.Stop();
    _certificate?.Dispose();
  }
}
