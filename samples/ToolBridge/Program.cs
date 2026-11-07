using RecursiveAI.OneModelContext;
using ToolBridge;

// The same bridged tool, asked for confirmation on each era. The tool code does not know which era it is on.
foreach (var era in new[] { EraPreference.Modern, EraPreference.Legacy })
{
  foreach (var approve in new[] { true, false })
  {
    Console.WriteLine($"-- {era}, user {(approve ? "approves" : "declines")}");
    await Demo.RunAsync(era, approve, Console.Out);
  }
}
