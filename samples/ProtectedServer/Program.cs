using System.Security.Cryptography;
using ProtectedServer;

// Set PROTECTED_SAMPLE_TOKEN to choose the token; otherwise a random one is made and written to stderr for this run only.
var token = Environment.GetEnvironmentVariable("PROTECTED_SAMPLE_TOKEN");
if (string.IsNullOrEmpty(token))
{
  token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
  Console.Error.WriteLine($"bearer token for this run: {token}");
}

await Site.Build(args, token).RunAsync();
