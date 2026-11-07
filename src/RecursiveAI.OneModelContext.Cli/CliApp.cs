using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Protocol;

namespace RecursiveAI.OneModelContext.Cli;

/// <summary>Exit codes of the tool.</summary>
internal static class ExitCode
{
  public const int Ok = 0;

  /// <summary>The server answered with a protocol error, or a tool reported <c>isError</c>.</summary>
  public const int Failed = 1;

  public const int Usage = 2;

  /// <summary>The server could not be reached or started, or the connection broke.</summary>
  public const int Transport = 3;

  /// <summary>The server refused the credentials (HTTP 401 or 403).</summary>
  public const int Auth = 4;

  public const int Cancelled = 130;
}

/// <summary>The command line tool: parses arguments, connects, runs one command, prints. Takes its streams so tests can run it in process.</summary>
internal sealed partial class CliApp(TextWriter output, TextWriter error, TextReader input, string version, bool colour)
{
  internal const string Usage = """
    onemodelcontext: a command line for servers that speak the Model Context Protocol®.

    usage:
      onemodelcontext inspect <server>                    what the server is, which era answered, what it offers
      onemodelcontext call <server> <tool> [--arg k=v]... [--json @file | --args '{...}']
      onemodelcontext read <server> <uri>
      onemodelcontext prompt <server> <name> [--arg k=v]...
      onemodelcontext proxy <server> [--listen [host:]port] [--token T] [--allow-non-loopback] [--allow-origin O]... [--allow-host H]... [--path /mcp]

    proxy serves one upstream across one hop: a stdio command is served over HTTP (loopback unless --allow-non-loopback, which
    also requires --token), and an http(s) URL is served on this process's stdin and stdout. --token T or --token @file makes
    HTTP callers send "Authorization: Bearer T".

    <server> is a command line ("npx -y some-server"), an http(s) URL, or a name in the config file.
    The config file is --config, ./onemodelcontext.json or the per-user onemodelcontext/config.json:
      {"servers": {"files": {"command": "npx", "args": ["-y", "pkg"], "env": {"K": "v"}}, "web": {"url": "http://127.0.0.1:3001/mcp"}}}

    options:
      --era auto|modern|legacy   protocol era (default auto: modern first, legacy on fallback)
      --json                     print JSON (also --output json); with a value (@file or '{...}') it gives call arguments
      --arg k=v, -a k=v          a tool or prompt argument; typed from the tool's input schema
      --yes, -y                  answer yes when a server asks to confirm
      --bearer T|@file           bearer token for an http(s) server (sent as a header, never in the URL)
      --verbose, -v              show the server process's standard error
      --config FILE              server names file
      -h, --help   --version

    exit codes: 0 ok, 1 server error or tool isError, 2 usage, 3 transport, 4 authorization, 130 cancelled
    """;

  public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
  {
    var o = CliOptions.Parse(args);
    if (o.Version || o.Command is "version")
    {
      await output.WriteLineAsync("onemodelcontext " + version).ConfigureAwait(false);
      return ExitCode.Ok;
    }

    if (o.Help || o.Command is null or "help")
    {
      await (o.Help || o.Command is "help" ? output : error).WriteLineAsync(Usage).ConfigureAwait(false);
      return o.Help || o.Command is "help" ? ExitCode.Ok : ExitCode.Usage;
    }

    if (o.Error is not null)
    {
      return await FailUsageAsync(o.Error).ConfigureAwait(false);
    }

    int needed = o.Command switch
    {
      "inspect" or "proxy" => 1,
      "read" or "prompt" => 2,
      "call" => 2,
      _ => -1,
    };
    if (needed < 0)
    {
      return o.Command is "login" or "logout"
        ? await FailUsageAsync("'" + o.Command + "' is not available in this build.").ConfigureAwait(false)
        : await FailUsageAsync("unknown command '" + o.Command + "'.").ConfigureAwait(false);
    }

    if (o.Positional.Count != needed)
    {
      return await FailUsageAsync("'" + o.Command + "' takes " + (needed == 1 ? "a server" : needed == 2 && o.Command == "call" ? "a server and a tool" : "a server and a " + (o.Command == "read" ? "URI" : "name")) + ".").ConfigureAwait(false);
    }

    try
    {
      return o.Command switch
      {
        "inspect" => await InspectAsync(o, cancellationToken).ConfigureAwait(false),
        "call" => await CallAsync(o, cancellationToken).ConfigureAwait(false),
        "read" => await ReadAsync(o, cancellationToken).ConfigureAwait(false),
        "prompt" => await PromptAsync(o, cancellationToken).ConfigureAwait(false),
        _ => await ProxyAsync(o, cancellationToken).ConfigureAwait(false),
      };
    }
    catch (OperationCanceledException)
    {
      return ExitCode.Cancelled;
    }
    catch (ArgumentException ex)
    {
      return await FailUsageAsync(ex.Message).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is ContextException or NotSupportedException or IOException)
    {
      return await FailAsync(ex).ConfigureAwait(false);
    }
  }

  private async Task<int> FailUsageAsync(string message)
  {
    await error.WriteLineAsync("onemodelcontext: " + message + " Try --help.").ConfigureAwait(false);
    return ExitCode.Usage;
  }

  // The exception text may name a command line or a URL but never carries a token: the CLI sends only what --bearer gives, as a header,
  // and library errors scrub theirs. A connection that fails negotiation wraps the cause (for HTTP, the status line), so show that too.
  private async Task<int> FailAsync(Exception ex)
  {
    var detail = ex.InnerException is { } inner && !ex.Message.Contains(inner.Message, StringComparison.Ordinal) ? " " + inner.Message : "";
    var text = ex.Message + detail;

    // The transport words a refusal as "(HTTP 401)" or "(HTTP 403)"; there is no typed exception for it yet.
    var code = ex switch
    {
      ContextException when text.Contains("(HTTP 401)", StringComparison.Ordinal) || text.Contains("(HTTP 403)", StringComparison.Ordinal) => ExitCode.Auth,
      TransportException or NotSupportedException or IOException => ExitCode.Transport,
      _ => ExitCode.Failed,
    };
    var message = ex is ProtocolVersionException
      ? text.Replace("connect with EraPreference.Auto or EraPreference.Legacy", "run again with --era auto or --era legacy", StringComparison.Ordinal)
      : text;
    await error.WriteLineAsync("onemodelcontext: " + message).ConfigureAwait(false);
    return code;
  }

  /// <summary>Connects to the server named by the options, with confirmation questions put to the user.</summary>
  internal async Task<ContextClient> ConnectAsync(CliOptions o, string server, CancellationToken cancellationToken)
  {
    var spec = ServerResolver.Resolve(server, o.Config);
    Func<string, CancellationToken, ValueTask<bool>> confirm = ConfirmAsync(o);
    if (spec.Url is { } url)
    {
      // The credential goes in a header added to each request, never in the URL; the transport sends it to this origin only.
      var bearer = o.Bearer is null ? null : o.Bearer.StartsWith('@') ? (await File.ReadAllTextAsync(o.Bearer[1..], cancellationToken).ConfigureAwait(false)).Trim() : o.Bearer;
      var httpOptions = new HttpTransportOptions
      {
        AuthorizeRequest = bearer is null
          ? null
          : (request, _) =>
          {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
            return ValueTask.CompletedTask;
          },
      };
      if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint))
      {
        throw new ArgumentException("'" + url + "' is not a valid URL.");
      }

      return await ModelContext.ConnectAsync(ContextTransport.Http(endpoint, options: httpOptions), o.Era, confirm, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    var options = new ProcessTransportOptions
    {
      Environment = spec.Environment,
      StandardErrorHandler = o.Verbose ? line => error.WriteLine(line) : _ => { },
    };
    var transport = spec.Command is { } command
      ? ContextTransport.Process(command, spec.Arguments, options)
      : ContextTransport.ProcessFromCommandLine(spec.CommandLine!, options);
    return await ModelContext.ConnectAsync(transport, o.Era, confirm, cancellationToken: cancellationToken).ConfigureAwait(false);
  }

  private Func<string, CancellationToken, ValueTask<bool>> ConfirmAsync(CliOptions o) => async (question, token) =>
  {
    if (o.Yes)
    {
      return true;
    }

    // The prompt goes to standard error so standard output stays the command's result.
    await error.WriteAsync(question + " [y/N] ").ConfigureAwait(false);
    var line = await input.ReadLineAsync(token).ConfigureAwait(false);
    return line is not null && line.Trim().StartsWith('y');
  };

  private static string EraReason(CliOptions o, ContextClient client) =>
    o.Era != EraPreference.Auto
      ? "pinned by --era " + (o.Era == EraPreference.Modern ? "modern" : "legacy")
      : client.Era == ProtocolEra.Modern
        ? "the server answered server/discover"
        : "the server does not speak the modern era (no server/discover answer), so the client used initialize";

  private async Task<int> InspectAsync(CliOptions o, CancellationToken ct)
  {
    await using var client = await ConnectAsync(o, o.Positional[0], ct).ConfigureAwait(false);
    var caps = client.ServerCapabilities;
    ListedItems<Tool>? tools = caps.Tools is not null ? await client.ListToolsAsync(ct).ConfigureAwait(false) : null;
    ListedItems<Resource>? resources = caps.Resources is not null ? await client.ListResourcesAsync(ct).ConfigureAwait(false) : null;
    ListedItems<ResourceTemplate>? templates = caps.Resources is not null ? await client.ListResourceTemplatesAsync(ct).ConfigureAwait(false) : null;
    ListedItems<Prompt>? prompts = caps.Prompts is not null ? await client.ListPromptsAsync(ct).ConfigureAwait(false) : null;

    if (o.Json)
    {
      await output.WriteLineAsync(Render.InspectJson(client, EraReason(o, client), tools, resources, templates, prompts)).ConfigureAwait(false);
      return ExitCode.Ok;
    }

    var text = new StringBuilder();
    var render = new Render(colour);
    text.Append(render.Heading("server")).Append(": ").Append(client.ServerInfo.Name).Append(' ').AppendLine(client.ServerInfo.Version);
    text.Append(render.Heading("protocol")).Append(": ").Append(client.ProtocolVersion).Append(" (").Append(client.Era == ProtocolEra.Modern ? "modern" : "legacy").AppendLine(" era)");
    text.Append(render.Heading("era chosen")).Append(": ").AppendLine(EraReason(o, client));
    if (client.Instructions is { Length: > 0 } instructions)
    {
      text.Append(render.Heading("instructions")).Append(": ").AppendLine(instructions);
    }

    text.Append(render.Heading("capabilities")).Append(": ").AppendLine(Render.CapabilityNames(caps));
    if (caps.Extensions is { Count: > 0 } extensions)
    {
      text.Append(render.Heading("extensions")).Append(": ").AppendLine(string.Join(", ", extensions.Keys));
    }

    if (tools is not null)
    {
      text.Append(render.Heading("tools")).Append(" (").Append(tools.Count.ToString(CultureInfo.InvariantCulture)).AppendLine("):");
      foreach (var t in tools)
      {
        text.Append("  ").Append(t.Name).Append(Render.Summary(t.Description)).AppendLine();
      }
    }

    if (resources is not null)
    {
      text.Append(render.Heading("resources")).Append(" (").Append(resources.Count.ToString(CultureInfo.InvariantCulture)).AppendLine("):");
      foreach (var r in resources)
      {
        text.Append("  ").Append(r.Uri).Append(Render.Summary(r.Description ?? r.Name)).AppendLine();
      }

      foreach (var r in templates!)
      {
        text.Append("  ").Append(r.UriTemplate).Append(Render.Summary(r.Description ?? r.Name)).AppendLine(" (template)");
      }
    }

    if (prompts is not null)
    {
      text.Append(render.Heading("prompts")).Append(" (").Append(prompts.Count.ToString(CultureInfo.InvariantCulture)).AppendLine("):");
      foreach (var p in prompts)
      {
        text.Append("  ").Append(p.Name).Append(Render.Summary(p.Description)).AppendLine();
      }
    }

    await output.WriteAsync(text.ToString()).ConfigureAwait(false);
    return ExitCode.Ok;
  }

  private async Task<int> CallAsync(CliOptions o, CancellationToken ct)
  {
    if (o.ArgsJson is not null && o.Args.Count > 0)
    {
      return await FailUsageAsync("give --arg pairs or JSON arguments, not both.").ConfigureAwait(false);
    }

    await using var client = await ConnectAsync(o, o.Positional[0], ct).ConfigureAwait(false);
    var tool = o.Positional[1];
    JsonElement arguments;
    if (o.ArgsJson is not null)
    {
      arguments = ToolArguments.FromJson(o.ArgsJson);
    }
    else if (o.Args.Count > 0)
    {
      // Typing an argument needs the tool's schema, so list the tools once. A server without that tool answers the call itself.
      JsonElement schema = default;
      if (client.ServerCapabilities.Tools is not null)
      {
        foreach (var t in await client.ListToolsAsync(ct).ConfigureAwait(false))
        {
          if (t.Name == tool)
          {
            schema = t.InputSchema;
          }
        }
      }

      arguments = ToolArguments.FromPairs(o.Args, schema);
    }
    else
    {
      arguments = ToolArguments.Empty;
    }

    var result = await client.CallToolAsync(tool, arguments, ct).ConfigureAwait(false);
    if (o.Json)
    {
      await output.WriteLineAsync(JsonSerializer.Serialize(result, WireJsonContext.Default.CallToolResult)).ConfigureAwait(false);
    }
    else
    {
      await output.WriteAsync(Render.ToolResultText(result)).ConfigureAwait(false);
    }

    if (result.IsError == true)
    {
      if (!o.Json)
      {
        await error.WriteLineAsync("onemodelcontext: the tool reported an error.").ConfigureAwait(false);
      }

      return ExitCode.Failed;
    }

    return ExitCode.Ok;
  }

  private async Task<int> ReadAsync(CliOptions o, CancellationToken ct)
  {
    await using var client = await ConnectAsync(o, o.Positional[0], ct).ConfigureAwait(false);
    var result = await client.ReadResourceAsync(o.Positional[1], ct).ConfigureAwait(false);
    await output.WriteAsync(o.Json ? JsonSerializer.Serialize(result, WireJsonContext.Default.ReadResourceResult) + "\n" : Render.ResourceText(result)).ConfigureAwait(false);
    return ExitCode.Ok;
  }

  private async Task<int> PromptAsync(CliOptions o, CancellationToken ct)
  {
    await using var client = await ConnectAsync(o, o.Positional[0], ct).ConfigureAwait(false);
    var arguments = new Dictionary<string, string>();
    foreach (var pair in o.Args)
    {
      arguments[pair.Key] = pair.Value;
    }

    var result = await client.GetPromptAsync(o.Positional[1], arguments.Count > 0 ? arguments : null, ct).ConfigureAwait(false);
    await output.WriteAsync(o.Json ? JsonSerializer.Serialize(result, WireJsonContext.Default.GetPromptResult) + "\n" : Render.PromptText(result)).ConfigureAwait(false);
    return ExitCode.Ok;
  }
}
