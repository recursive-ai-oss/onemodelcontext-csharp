using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Auth.Server;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.Jose;
using SharpFuzz;

// Fuzz target for the authorization server: arbitrary query strings, form bodies, registration JSON, Authorization headers,
// client assertions, ID-JAGs, resume links and client metadata documents. Only a typed HTTP answer may come out; an exception
// escaping HandleAsync or Parse, a hang or a memory blow-up is a finding.
//
// Select it with FUZZ_TARGET=authorization-server (fuzz/run.sh passes the environment through).
//
// Input layout: byte 0 selects the endpoint (0 authorize GET, 1 authorize POST, 2 token, 3 revoke, 4 register, 5 resume link,
// 6 client metadata document, 7 metadata and key set). For token and revoke the first line is the Authorization header value; the
// rest is the form body (or, for authorize GET, the query).
internal static class AuthorizationServerTarget
{
  private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
  private static readonly ECDsa IdpKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
  private static readonly AuthorizationServer Server = Create();

  internal static bool Selected => Environment.GetEnvironmentVariable("FUZZ_TARGET") == "authorization-server";

  internal static int Start(string[] args)
  {
    if (args.Length > 0 && args[0] == "replay")
    {
      foreach (var file in args.Skip(1))
      {
        Console.WriteLine(file + ": " + Run(File.ReadAllBytes(file)));
      }

      return 0;
    }

    Fuzzer.LibFuzzer.Run(span => Run(span));
    return 0;
  }

  private static AuthorizationServer Create()
  {
    var idp = JsonWebSigningKey.FromECDsa(IdpKey, "idp");
    var jwks = new MemoryStream();
    using (var w = new System.Text.Json.Utf8JsonWriter(jwks))
    {
      w.WriteStartObject();
      w.WriteStartArray("keys");
      idp.WritePublicJwk(w);
      w.WriteEndArray();
      w.WriteEndObject();
    }

    var store = new MemoryAuthorizationStore();
    var secretHash = ClientSecret.Hash("fuzz-secret-0123456789");
    store.SaveClientAsync(new RegisteredClient { ClientId = "app", RedirectUris = ["https://app.test/cb", "http://127.0.0.1/cb"], CreatedAt = DateTimeOffset.UnixEpoch }, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    store.SaveClientAsync(new RegisteredClient
    {
      ClientId = "svc",
      RedirectUris = ["https://app.test/cb"],
      TokenEndpointAuthMethod = "client_secret_basic",
      SecretHash = secretHash,
      GrantTypes = ["authorization_code", "refresh_token", "client_credentials", "urn:ietf:params:oauth:grant-type:jwt-bearer"],
      CreatedAt = DateTimeOffset.UnixEpoch,
    }, CancellationToken.None).AsTask().GetAwaiter().GetResult();

#pragma warning disable CS0618, OMC0004 // The fuzz target covers the deprecated registration endpoint too.
    return new AuthorizationServer(new AuthorizationServerOptions
    {
      Issuer = new Uri("https://as.fuzz"),
      Resources = ["https://mcp.fuzz/mcp"],
      SigningKeys = [JsonWebSigningKey.FromECDsa(Key, "fuzz-1")],
      Store = store,
      DynamicClientRegistration = true,
      ResumeKey = new byte[32],
      EnterpriseAuthorization = new EnterpriseAuthorizationOptions
      {
        IdentityProviders = [new TrustedIdentityProvider("https://idp.fuzz", new StaticJwksProvider(JsonWebKeySet.Parse(jwks.ToArray())!))],
      },
      AuthorizeUser = (_, _) => ValueTask.FromResult(AuthorizationDecision.Approve("fuzz-user")),
    });
#pragma warning restore CS0618, OMC0004
  }

  internal static int Run(ReadOnlySpan<byte> data)
  {
    if (data.Length == 0)
    {
      return -1;
    }

    var kind = data[0] % 8;
    var text = Encoding.UTF8.GetString(data[1..]);
    if (kind == 6)
    {
      _ = ClientMetadataResolver.Parse("https://client.fuzz/c.json", Encoding.UTF8.GetBytes(text), DateTimeOffset.UnixEpoch);
      return 0;
    }

    var newline = text.IndexOf('\n', StringComparison.Ordinal);
    var first = newline < 0 ? text : text[..newline];
    var rest = newline < 0 ? string.Empty : text[(newline + 1)..];
    var (method, path, query, body, type) = kind switch
    {
      0 => ("GET", "/authorize", text, string.Empty, string.Empty),
      1 => ("POST", "/authorize", string.Empty, text, "application/x-www-form-urlencoded"),
      2 => ("POST", "/token", string.Empty, rest, "application/x-www-form-urlencoded"),
      3 => ("POST", "/revoke", string.Empty, rest, "application/x-www-form-urlencoded"),
      4 => ("POST", "/register", string.Empty, text, "application/json"),
      5 => ("GET", "/authorize", "resume=" + Uri.EscapeDataString(text), string.Empty, string.Empty),
      _ => (newline % 2 == 0 ? "GET" : "HEAD", newline % 3 == 0 ? "/jwks" : "/.well-known/oauth-authorization-server", string.Empty, string.Empty, string.Empty),
    };

    using var exchange = new TargetExchange(method, path, query, Encoding.UTF8.GetBytes(body), type, kind is 2 or 3 && first.Length > 0 ? first : null);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    using var registration = timeout.Token.Register(exchange.Abort);
    try
    {
      Server.HandleAsync(exchange).AsTask().GetAwaiter().GetResult();
    }
    catch (OperationCanceledException)
    {
      // The harness gave up on a request that did not finish.
    }

    if (exchange.Split)
    {
      throw new InvalidOperationException("A response header contained a line break.");
    }

    return exchange.StatusCode;
  }

  private sealed class TargetExchange : IHttpExchange, IHttpRequest, IHttpResponse, IDisposable
  {
    private readonly CancellationTokenSource _abort = new();
    private readonly MemoryStream _body;

    internal TargetExchange(string method, string path, string query, byte[] body, string contentType, string? authorization)
    {
      Method = method;
      Path = path;
      Query = query;
      _body = new MemoryStream(body);
      ContentLength = body.Length;
      if (contentType.Length > 0)
      {
        Headers.Add("Content-Type", contentType);
      }

      if (authorization is not null && authorization.IndexOfAny(['\r', '\n']) < 0)
      {
        Headers.Add("Authorization", authorization);
      }
    }

    public IHttpRequest Request => this;

    public IHttpResponse Response => this;

    public ClaimsPrincipal? User => null;

    public CancellationToken Aborted => _abort.Token;

    public string Method { get; }

    public string Path { get; }

    public string Query { get; }

    public HttpHeaders Headers { get; } = new();

    public long? ContentLength { get; }

    Stream IHttpRequest.Body => _body;

    public IPAddress? RemoteAddress => IPAddress.Loopback;

    public int StatusCode { get; set; } = 200;

    Stream IHttpResponse.Body { get; } = Stream.Null;

    public void SetHeader(string name, string value)
    {
      // Headers set by the server are not inspected, but a value with a line break would be a response-splitting bug.
      if (value.IndexOfAny(['\r', '\n']) >= 0)
      {
        Split = true;
      }
    }

    internal bool Split { get; private set; }

    internal void Abort() => _abort.Cancel();

    public void Dispose()
    {
      _abort.Dispose();
      _body.Dispose();
    }
  }
}
