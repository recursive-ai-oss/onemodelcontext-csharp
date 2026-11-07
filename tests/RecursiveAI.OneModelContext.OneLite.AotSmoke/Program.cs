using RecursiveAI.OneModelContext.Auth.Server;
using RecursiveAI.OneModelContext.Extensions.Tasks;
using RecursiveAI.OneModelContext.OAuth;
using RecursiveAI.OneModelContext.OneLite;

// Publishes with NativeAOT (-p:SmokeMode=aot) and exits non-zero when a store round trip fails: the three OneLite stores on one file.
var directory = Path.Combine(Path.GetTempPath(), "omc-onelite-aot-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var path = Path.Combine(directory, "stores.db");
try
{
  var now = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
  await using (var auth = await OneLiteAuthorizationStore.OpenAsync(path))
  {
    await auth.SaveClientAsync(new RegisteredClient { ClientId = "app", RedirectUris = ["https://app.test/cb"], CreatedAt = now }, default);
    await auth.SaveCodeAsync(new AuthorizationCodeGrant
    {
      CodeHash = "c", ClientId = "app", RedirectUri = "https://app.test/cb", Subject = "alice", Resource = "r", CodeChallenge = "x",
      FamilyId = "f", Claims = new Dictionary<string, string> { ["k"] = "v" }, ExpiresAt = now.AddMinutes(1),
    }, default);
    var first = await auth.RedeemCodeAsync("c", now, default);
    var second = await auth.RedeemCodeAsync("c", now, default);
    if ((await auth.FindClientAsync("app", default))?.RedirectUris[0] != "https://app.test/cb" || first.Status != RedemptionStatus.Redeemed
      || first.Grant?.Claims["k"] != "v" || second.Status != RedemptionStatus.AlreadyRedeemed)
    {
      Console.Error.WriteLine("authorization store failed");
      return 1;
    }
  }

  await using (var tokens = await OneLiteTokenStore.OpenAsync(path))
  {
    await tokens.SetAsync("k", new TokenRecord { AccessToken = "a", ExpiresAt = now }, default);
    if ((await tokens.GetAsync("k", default))?.AccessToken != "a")
    {
      Console.Error.WriteLine("token store failed");
      return 1;
    }
  }

  await using (var tasks = await OneLiteTaskStore.OpenAsync(path))
  {
    await tasks.CreateAsync(new TaskRecord { TaskId = "t", Owner = "", ToolName = "x", Status = TaskStates.Working, CreatedAt = now, LastUpdatedAt = now }, default);
    var read = await tasks.GetAsync("t", default);
    if (read is null || await tasks.UpdateAsync(read with { Status = TaskStates.Completed }, default) is null || await tasks.UpdateAsync(read, default) is not null)
    {
      Console.Error.WriteLine("task store failed");
      return 1;
    }
  }

  Console.WriteLine("ok");
  return 0;
}
finally
{
  try
  {
    Directory.Delete(directory, recursive: true);
  }
  catch (IOException)
  {
    // Temp folder; nothing depends on it.
  }
}
