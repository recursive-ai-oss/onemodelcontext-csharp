using ChatTools;
using RecursiveAI.OneModelContext;

// A server's tools used by a chat client, on each protocol era; the code that uses the tools does not change.
foreach (var era in new[] { EraPreference.Modern, EraPreference.Legacy })
{
  Console.WriteLine($"{era}: {await Demo.RunAsync(era)}");
}
