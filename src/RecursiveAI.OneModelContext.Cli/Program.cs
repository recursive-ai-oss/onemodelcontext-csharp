using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace RecursiveAI.OneModelContext.Cli;

internal static class Program
{
  private static async Task<int> Main(string[] args)
  {
    var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    var plus = version.IndexOf('+', StringComparison.Ordinal);
    if (plus >= 0)
    {
      version = version[..plus];
    }

    // Colour only on a terminal, and never when NO_COLOR is set (https://no-color.org).
    var colour = !Console.IsOutputRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
      e.Cancel = true;
      cancel.Cancel();
    };

    var app = new CliApp(Console.Out, Console.Error, Console.In, version, colour);
    return await app.RunAsync(args, cancel.Token).ConfigureAwait(false);
  }
}
