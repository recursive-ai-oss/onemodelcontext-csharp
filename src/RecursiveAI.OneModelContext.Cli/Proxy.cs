using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Hosting;
using RecursiveAI.OneModelContext.Http;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Cli;

internal sealed partial class CliApp
{
  /// <summary>
  /// Serves one upstream server across one transport hop. A stdio upstream is served over Streamable HTTP on the built-in listener;
  /// an HTTP upstream is served on standard input and output. It copies the upstream's tools, resources and prompts when it starts,
  /// so a list that changes later is not followed. It is not a gateway: exactly one upstream.
  /// </summary>
  internal async Task<int> ProxyAsync(CliOptions o, CancellationToken cancellationToken)
  {
    var spec = ServerResolver.Resolve(o.Positional[0], o.Config);
    var toStdio = spec.Url is not null;
    if (toStdio && (o.Listen is not null || o.Token is not null || o.AllowNonLoopback || o.AllowOrigins.Count > 0 || o.AllowHosts.Count > 0))
    {
      return await FailUsageAsync("serving on stdio takes no --listen, --token, --allow-* options.").ConfigureAwait(false);
    }

    IPAddress address = IPAddress.Loopback;
    var port = 3001;
    string? token = null;
    if (!toStdio)
    {
      if (!CliOptions.TryParseListen(o.Listen, out var host, out port))
      {
        return await FailUsageAsync("--listen is [host:]port.").ConfigureAwait(false);
      }

      if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
      {
        host = "127.0.0.1";
      }

      if (!IPAddress.TryParse(host, out var parsed))
      {
        return await FailUsageAsync("--listen needs an IP address or localhost, not '" + host + "'.").ConfigureAwait(false);
      }

      address = parsed;
      if (!IPAddress.IsLoopback(address) && !o.AllowNonLoopback)
      {
        return await FailUsageAsync("refusing to listen on " + address + ": it is not a loopback address. Pass --allow-non-loopback (and --token) to do it on purpose.").ConfigureAwait(false);
      }

      if (o.Token is not null)
      {
        token = o.Token.StartsWith('@') ? (await File.ReadAllTextAsync(o.Token[1..], cancellationToken).ConfigureAwait(false)).Trim() : o.Token;
        if (token.Length == 0)
        {
          return await FailUsageAsync("--token is empty.").ConfigureAwait(false);
        }
      }

      if (!IPAddress.IsLoopback(address) && token is null)
      {
        return await FailUsageAsync("a proxy that listens beyond loopback needs --token.").ConfigureAwait(false);
      }
    }

    await using var upstream = await ConnectAsync(o, o.Positional[0], cancellationToken).ConfigureAwait(false);
    using var cancelOnUpstreamClose = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var stop = cancelOnUpstreamClose.Token;
    _ = upstream.Completion.ContinueWith(_ => cancelOnUpstreamClose.Cancel(), TaskScheduler.Default);
    var server = await MirrorAsync(upstream, stop).ConfigureAwait(false);

    if (toStdio)
    {
      await error.WriteLineAsync("onemodelcontext: serving " + spec.Describe() + " on standard input and output.").ConfigureAwait(false);
      try
      {
        await server.RunAsync(ContextTransport.Stdio(), stop).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        // Interrupted or the upstream went away.
      }

      return ExitCode.Ok;
    }

    var hosts = new System.Collections.Generic.List<string>(o.AllowHosts);
    if (!address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) && !IPAddress.IsLoopback(address))
    {
      hosts.Add(address.ToString());
    }

    using var endpoint = new StreamableHttpEndpoint(server, new StreamableHttpOptions { AllowedOrigins = o.AllowOrigins, AllowedHosts = hosts });
    var path = o.ListenPath;
    var expected = token is null ? null : SHA256.HashData(Encoding.UTF8.GetBytes(token));
    await using var http = new HttpServer(
      exchange => Handle(exchange),
      new HttpServerOptions { Address = address, Port = port },
      server.TimeProvider);
    http.Start();

    if (!IPAddress.IsLoopback(address))
    {
      await error.WriteLineAsync("warning: listening on " + address + ", which other machines can reach, over plain HTTP: the token and every message travel unencrypted. Put TLS or a trusted proxy in front.").ConfigureAwait(false);
    }

    await output.WriteLineAsync("listening on " + new Uri(http.BaseAddress!, path)).ConfigureAwait(false);
    await output.FlushAsync(stop).ConfigureAwait(false);
    try
    {
      await Task.Delay(Timeout.Infinite, stop).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      // Interrupted or the upstream went away.
    }

    return ExitCode.Ok;

    ValueTask Handle(IHttpExchange exchange)
    {
      if (exchange.Request.Path != path)
      {
        exchange.Response.StatusCode = 404;
        return exchange.Response.Body.FlushAsync(exchange.Aborted).AsValueTask();
      }

      if (expected is not null && !Authorized(exchange.Request, expected))
      {
        // RFC 6750 section 3: a bearer challenge. The reason is not spelled out and the token is never echoed.
        exchange.Response.StatusCode = 401;
        exchange.Response.SetHeader("WWW-Authenticate", "Bearer");
        return exchange.Response.Body.FlushAsync(exchange.Aborted).AsValueTask();
      }

      return endpoint.HandleAsync(exchange);
    }
  }

  // Compares hashes of the two tokens so the comparison time does not depend on where they differ or how long they are.
  private static bool Authorized(IHttpRequest request, byte[] expectedHash)
  {
    if (!request.Headers.TryGetFirst("Authorization", out var value) || value is null)
    {
      return false;
    }

    const string scheme = "Bearer ";
    if (!value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
    {
      return false;
    }

    var presented = SHA256.HashData(Encoding.UTF8.GetBytes(value[scheme.Length..].Trim()));
    return CryptographicOperations.FixedTimeEquals(presented, expectedHash);
  }

  private static async Task<ContextServer> MirrorAsync(ContextClient upstream, CancellationToken cancellationToken)
  {
    var server = new ContextServer(upstream.ServerInfo.Name, upstream.ServerInfo.Version) { Instructions = upstream.Instructions };
    if (upstream.ServerCapabilities.Tools is not null)
    {
      foreach (var tool in await upstream.ListToolsAsync(cancellationToken).ConfigureAwait(false))
      {
        var name = tool.Name;
        server.Tools.Add(
          new ToolDefinition(name, tool.Description, tool.InputSchema) { Title = tool.Title, OutputSchema = tool.OutputSchema, Annotations = tool.Annotations },
          (arguments, context) => upstream.CallToolAsync(name, arguments, context.CancellationToken));
      }
    }

    if (upstream.ServerCapabilities.Resources is not null)
    {
      foreach (var resource in await upstream.ListResourcesAsync(cancellationToken).ConfigureAwait(false))
      {
        server.Resources.Add(
          new ResourceDefinition(resource.Uri, resource.Name) { Title = resource.Title, Description = resource.Description, MimeType = resource.MimeType, Size = resource.Size, Annotations = resource.Annotations },
          (uri, context) => upstream.ReadResourceAsync(uri, context.CancellationToken));
      }

      foreach (var template in await upstream.ListResourceTemplatesAsync(cancellationToken).ConfigureAwait(false))
      {
        server.ResourceTemplates.Add(
          new ResourceTemplateDefinition(template.UriTemplate, template.Name) { Title = template.Title, Description = template.Description, MimeType = template.MimeType, Annotations = template.Annotations },
          (uri, context) => upstream.ReadResourceAsync(uri, context.CancellationToken));
      }
    }

    if (upstream.ServerCapabilities.Prompts is not null)
    {
      foreach (var prompt in await upstream.ListPromptsAsync(cancellationToken).ConfigureAwait(false))
      {
        var name = prompt.Name;
        server.Prompts.Add(
          new PromptDefinition(name, prompt.Description) { Title = prompt.Title, Arguments = prompt.Arguments },
          (arguments, context) => upstream.GetPromptAsync(name, arguments, context.CancellationToken));
      }
    }

    return server;
  }
}

internal static class ValueTaskExtensions
{
  public static ValueTask AsValueTask(this Task task) => new(task);
}
