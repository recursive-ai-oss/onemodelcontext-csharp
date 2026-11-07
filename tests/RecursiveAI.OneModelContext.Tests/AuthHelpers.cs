using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Jose;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>Keys and tokens generated for one test run; nothing here is a real credential.</summary>
internal sealed class TestIssuer : IDisposable
{
  internal const string Issuer = "https://issuer.test";
  internal const string Resource = "https://mcp.test/mcp";

  private readonly RSA _rsa = RSA.Create(2048);
  private readonly ECDsa _p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
  private readonly ECDsa _p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
  private readonly ECDsa _p521 = ECDsa.Create(ECCurve.NamedCurves.nistP521);

  internal RSA Rsa => _rsa;

  internal static string B64(ReadOnlySpan<byte> bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);

  internal string RsaJwk(string kid)
  {
    var p = _rsa.ExportParameters(false);
    return $$"""{"kty":"RSA","kid":"{{kid}}","use":"sig","n":"{{B64(p.Modulus!)}}","e":"{{B64(p.Exponent!)}}"}""";
  }

  internal static string EcJwk(ECDsa key, string kid, string crv)
  {
    var p = key.ExportParameters(false);
    return $$"""{"kty":"EC","kid":"{{kid}}","crv":"{{crv}}","x":"{{B64(p.Q.X!)}}","y":"{{B64(p.Q.Y!)}}"}""";
  }

  internal string Jwks(params string[] extra) =>
    "{\"keys\":[" + string.Join(',', [RsaJwk("rsa-1"), EcJwk(_p256, "p256", "P-256"), EcJwk(_p384, "p384", "P-384"), EcJwk(_p521, "p521", "P-521"), .. extra]) + "]}";

  internal static string EdwardsJwk(string kid) => $$"""{"kty":"OKP","crv":"Ed25519","kid":"{{kid}}","x":"{{B64(new byte[32])}}"}""";

  /// <summary>Signs a token. <paramref name="claims"/> override the defaults; a null value removes the claim.</summary>
  internal string Mint(string alg = "RS256", string? kid = null, TimeProvider? time = null, Dictionary<string, object?>? claims = null, byte[]? signWith = null)
  {
    var now = (time ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
    var payload = new Dictionary<string, object?>
    {
      ["iss"] = Issuer,
      ["aud"] = Resource,
      ["sub"] = "user-1",
      ["scope"] = "files:read",
      ["iat"] = now,
      ["exp"] = now + 600,
    };
    foreach (var (name, value) in claims ?? [])
    {
      if (value is null)
      {
        payload.Remove(name);
      }
      else
      {
        payload[name] = value;
      }
    }

    kid ??= alg switch { "ES256" => "p256", "ES384" => "p384", "ES512" => "p521", _ => "rsa-1" };
    var header = alg == "none"
      ? JsonSerializer.Serialize(new Dictionary<string, object> { ["alg"] = "none", ["typ"] = "JWT" })
      : JsonSerializer.Serialize(new Dictionary<string, object> { ["alg"] = alg, ["typ"] = "at+jwt", ["kid"] = kid });
    var input = B64(Encoding.UTF8.GetBytes(header)) + "." + B64(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
    var data = Encoding.ASCII.GetBytes(input);
    byte[] signature = alg switch
    {
      "none" => [],
      "RS256" => _rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
      "PS256" => _rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss),
      "ES256" => _p256.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
      "ES384" => _p384.SignData(data, HashAlgorithmName.SHA384, DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
      "ES512" => _p521.SignData(data, HashAlgorithmName.SHA512, DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
      "HS256" => HMACSHA256.HashData(signWith ?? throw new InvalidOperationException("HS256 needs a secret."), data),
      _ => [1, 2, 3],
    };
    return input + "." + B64(signature);
  }

  public void Dispose()
  {
    _rsa.Dispose();
    _p256.Dispose();
    _p384.Dispose();
    _p521.Dispose();
  }
}

/// <summary>An HTTP handler that answers from a function and counts calls, so network behaviour is tested without sockets.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
  private int _calls;

  internal int Calls => Volatile.Read(ref _calls);

  internal List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
  {
    Interlocked.Increment(ref _calls);
    var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
    lock (Seen)
    {
      Seen.Add((request, body));
    }

    return respond(request);
  }

  internal static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
    new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
