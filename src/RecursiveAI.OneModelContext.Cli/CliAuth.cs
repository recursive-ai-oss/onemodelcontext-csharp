using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.OAuth;

namespace RecursiveAI.OneModelContext.Cli;

// login, logout, and the stored credentials the other commands use for http(s) servers.
internal sealed partial class CliApp
{
  private readonly List<IDisposable> _disposables = [];
  private bool _noBrowser;

  /// <summary>How <c>login</c> shows the authorization URL to the user. Null opens the system browser; tests replace it.</summary>
  internal Func<Uri, CancellationToken, ValueTask>? OpenBrowser { get; set; }

  private void DisposeAuth()
  {
    foreach (var d in _disposables)
    {
      d.Dispose();
    }

    _disposables.Clear();
  }

  private static string TokenFile(CliOptions o)
  {
    if (o.TokenStore is { Length: > 0 } explicitPath)
    {
      return explicitPath;
    }

    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    if (appData.Length == 0)
    {
      throw new ArgumentException("this account has no configuration directory; give --token-store FILE.");
    }

    return Path.Combine(appData, "onemodelcontext", "tokens.json");
  }

  private static Uri ResourceUri(CliOptions o)
  {
    var spec = ServerResolver.Resolve(o.Positional[0], o.Config);
    if (spec.Url is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
    {
      throw new ArgumentException("'" + o.Positional[0] + "' is not an http(s) server; only those sign in.");
    }

    return uri;
  }

  // The stored tokens of an http(s) server, used by every command. No user agent: when the stored tokens cannot be refreshed or widened,
  // the command fails with exit 4 and says to run login, so a script never waits for a browser. A store that does not exist yet is
  // not created by reading it.
  private OAuthClient? StoredCredentials(CliOptions o, string url)
  {
    if (!Uri.TryCreate(url, UriKind.Absolute, out var resource))
    {
      return null;
    }

    var file = TokenFile(o);
    var store = new FileTokenStore(file);
    _disposables.Add(store);
    var client = new OAuthClient(new OAuthClientOptions(resource) { Store = store, PreRegistered = PreRegistered(o) });
    _disposables.Add(client);
    return client;
  }

  private PreRegisteredClient? PreRegistered(CliOptions o)
  {
    if (o.ClientId is not { Length: > 0 } id || IsMetadataUrl(id))
    {
      return null;
    }

    var secret = ReadSecret(o.ClientSecret);
    if (secret is not null && o.Issuer is not { Length: > 0 })
    {
      // A secret goes only where the user said it belongs (the server's own metadata could name any authorization server).
      throw new ArgumentException("--client-secret needs --issuer URL: the issuer of the authorization server that issued the client.");
    }

    return new PreRegisteredClient(id, secret, o.Issuer is { Length: > 0 } issuer ? issuer : null);
  }

  // env:NAME, - (standard input), @file, the ONEMODELCONTEXT_CLIENT_SECRET variable when the option is absent, else the literal.
  private string? ReadSecret(string? value)
  {
    string? secret;
    if (value is null)
    {
      secret = Environment.GetEnvironmentVariable("ONEMODELCONTEXT_CLIENT_SECRET");
    }
    else if (value == "-")
    {
      secret = input.ReadLine();
    }
    else if (value.StartsWith("env:", StringComparison.Ordinal))
    {
      secret = Environment.GetEnvironmentVariable(value[4..]) ?? throw new ArgumentException("the environment variable " + value[4..] + " is not set.");
    }
    else if (value.StartsWith('@'))
    {
      secret = File.ReadAllText(value[1..]);
    }
    else
    {
      secret = value;
    }

    secret = secret?.Trim();
    return string.IsNullOrEmpty(secret) ? null : secret;
  }

  private static bool IsMetadataUrl(string id) => id.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

  private async Task<int> LoginAsync(CliOptions o, CancellationToken ct)
  {
    var resource = ResourceUri(o);
    var real = new FileTokenStore(TokenFile(o));
    _disposables.Add(real);
    var store = new StagingTokenStore(real);

    // Registration order follows the authorization specification: a metadata document URL, then pre-registered credentials, then
    // whatever dynamic registration the server still offers.
    var receiver = new LoopbackRedirectReceiver((url, token) => OpenBrowser is { } open ? open(url, token) : LaunchBrowserAsync(url), o.RedirectPort);
    _noBrowser = o.NoBrowser;
    _disposables.Add(receiver);
    var agent = new PrintingAgent(receiver, error);
    var client = new OAuthClient(new OAuthClientOptions(resource)
    {
      Store = store,
      UserAgent = agent,
      ClientIdMetadataUrl = o.ClientId is { } id && IsMetadataUrl(id) ? id : null,
      PreRegistered = PreRegistered(o),
      ClientName = "onemodelcontext",
    });
    _disposables.Add(client);

    // Signing in starts when the server first refuses the connection; a server that needs no authorization never does.
    // A new sign-in replaces what is stored, so it also widens access: the scope comes from the server's current challenge. The old
    // tokens stay in the file until the new ones exist, so a sign-in that fails or is abandoned leaves the user signed in as before.
    var http = client.CreateHttpClient();
    _disposables.Add(http);
    await using (await ModelContext.ConnectAsync(ContextTransport.Http(resource, http), o.Era, cancellationToken: ct).ConfigureAwait(false))
    {
    }

    if (await client.GetAccessTokenAsync(ct).ConfigureAwait(false) is null)
    {
      await output.WriteLineAsync("The server at " + resource.GetLeftPart(UriPartial.Path) + " did not ask for authorization.").ConfigureAwait(false);
      return ExitCode.Ok;
    }

    await store.CommitAsync(ct).ConfigureAwait(false);
    await output.WriteLineAsync("Signed in to " + resource.GetLeftPart(UriPartial.Path) + ".").ConfigureAwait(false);
    return ExitCode.Ok;
  }

  // Reads see no token records and writes of them are held back, so login starts from nothing without erasing anything. Client
  // registrations and the like pass straight through.
  private sealed class StagingTokenStore(FileTokenStore real) : ITokenStore
  {
    private readonly Dictionary<string, TokenRecord> _staged = new(StringComparer.Ordinal);

    private static bool IsToken(string key) => key.StartsWith("token:", StringComparison.Ordinal);

    public ValueTask<TokenRecord?> GetAsync(string key, CancellationToken cancellationToken) =>
      IsToken(key) ? ValueTask.FromResult(_staged.GetValueOrDefault(key)) : real.GetAsync(key, cancellationToken);

    public ValueTask SetAsync(string key, TokenRecord record, CancellationToken cancellationToken)
    {
      if (IsToken(key))
      {
        _staged[key] = record;
        return ValueTask.CompletedTask;
      }

      return real.SetAsync(key, record, cancellationToken);
    }

    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken)
    {
      if (IsToken(key))
      {
        _staged.Remove(key);
        return ValueTask.CompletedTask;
      }

      return real.RemoveAsync(key, cancellationToken);
    }

    public async ValueTask CommitAsync(CancellationToken cancellationToken)
    {
      foreach (var (key, record) in _staged)
      {
        await real.SetAsync(key, record, cancellationToken).ConfigureAwait(false);
      }
    }
  }

  private async Task<int> LogoutAsync(CliOptions o, CancellationToken ct)
  {
    var resource = ResourceUri(o);
    using var store = new FileTokenStore(TokenFile(o));
    using var client = new OAuthClient(new OAuthClientOptions(resource) { Store = store });
    await client.SignOutAsync(ct).ConfigureAwait(false);
    await output.WriteLineAsync("Signed out of " + resource.GetLeftPart(UriPartial.Path) + ".").ConfigureAwait(false);
    return ExitCode.Ok;
  }

  // The URL carries state and a PKCE challenge, which are not secrets; it is printed so a headless user can open it elsewhere.
  private sealed class PrintingAgent(LoopbackRedirectReceiver inner, TextWriter error) : IAuthorizationUserAgent
  {
    public Uri RedirectUri => inner.RedirectUri;

    public async ValueTask<Uri> AuthorizeAsync(Uri authorizationUrl, CancellationToken cancellationToken)
    {
      await error.WriteLineAsync("Open this address to sign in (waiting for the redirect to " + inner.RedirectUri + "):\n" + authorizationUrl).ConfigureAwait(false);
      return await inner.AuthorizeAsync(authorizationUrl, cancellationToken).ConfigureAwait(false);
    }
  }

  // --no-browser is handled by not launching: the printed address is all there is.
  private ValueTask LaunchBrowserAsync(Uri url)
  {
    if (_noBrowser || url.Scheme is not ("http" or "https"))
    {
      return ValueTask.CompletedTask;
    }

    try
    {
      var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
      if (OperatingSystem.IsWindows())
      {
        start.FileName = "explorer.exe";
      }
      else if (OperatingSystem.IsMacOS())
      {
        start.FileName = "open";
      }
      else
      {
        start.FileName = "xdg-open";
      }

      start.ArgumentList.Add(url.AbsoluteUri);
      Process.Start(start)?.Dispose();
    }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
      // No opener on this machine: the address was printed already.
    }

    return ValueTask.CompletedTask;
  }

  private static string? FindAuthFailure(Exception ex)
  {
    for (var e = ex; e is not null; e = e.InnerException)
    {
      if (e is AuthorizationException a)
      {
        return Summarise(a);
      }

      if (e is OAuthException)
      {
        return e.Message + " Run 'onemodelcontext login <url>'.";
      }
    }

    return null;
  }

  // The challenge is the server's text, so control characters are dropped before it reaches a terminal. It never holds our token.
  private static string Summarise(AuthorizationException a)
  {
    var sb = new StringBuilder();
    sb.Append(a.StatusCode == 403 ? "the server refused this account's access (HTTP 403)" : "the server requires authorization (HTTP 401)");
    if (a.Challenge is { } c)
    {
      if (c.Error is { } err)
      {
        sb.Append(": ").Append(err);
      }

      if (c.Scope is { } scope)
      {
        sb.Append("; scope needed: ").Append(scope);
      }

      if (c.ErrorDescription is { } description)
      {
        sb.Append("; ").Append(description);
      }
    }

    sb.Append(a.StatusCode == 401 ? ". Run 'onemodelcontext login <url>' or pass --bearer." : ". Run 'onemodelcontext login <url>' to sign in again with more access.");
    var text = new StringBuilder(sb.Length);
    foreach (var ch in sb.ToString())
    {
      text.Append(char.IsControl(ch) ? ' ' : ch);
    }

    return text.ToString();
  }
}
