using System;
using System.Reflection;

namespace RecursiveAI.OneModelContext.Cli;

internal static class Program
{
  private static int Main(string[] args)
  {
    var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    var plus = version.IndexOf('+', StringComparison.Ordinal);
    if (plus >= 0)
    {
      version = version[..plus];
    }

    if (args.Length == 0 || args[0] is "--version" or "version")
    {
      Console.Out.WriteLine("onemodelcontext " + version);
      return 0;
    }

    Console.Error.WriteLine("onemodelcontext: unknown command '" + args[0] + "'. Only --version is available in this build.");
    return 2;
  }
}
