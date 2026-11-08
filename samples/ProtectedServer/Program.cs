using ProtectedServer;

// The host supplies the token; never print credentials to diagnostics or standard output.
var token = Environment.GetEnvironmentVariable("PROTECTED_SAMPLE_TOKEN");
if (string.IsNullOrEmpty(token))
{
  Console.Error.WriteLine("Set PROTECTED_SAMPLE_TOKEN before starting the sample.");
  return 1;
}

await Site.Build(args, token).RunAsync();

return 0;
