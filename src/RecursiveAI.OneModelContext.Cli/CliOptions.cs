using System;
using System.Collections.Generic;
using System.Globalization;

namespace RecursiveAI.OneModelContext.Cli;

/// <summary>What the command line asked for. Parsing never throws: a problem becomes <see cref="Error"/>.</summary>
internal sealed class CliOptions
{
  public string? Command { get; private set; }

  public List<string> Positional { get; } = [];

  public EraPreference Era { get; private set; } = EraPreference.Auto;

  /// <summary>Print JSON instead of text.</summary>
  public bool Json { get; private set; }

  /// <summary>Answer yes to the server's confirmation questions.</summary>
  public bool Yes { get; private set; }

  public bool Verbose { get; private set; }

  public bool Help { get; private set; }

  public bool Version { get; private set; }

  /// <summary>Tool arguments as JSON text or <c>@file</c>.</summary>
  public string? ArgsJson { get; private set; }

  /// <summary><c>--arg key=value</c> pairs, in order.</summary>
  public List<KeyValuePair<string, string>> Args { get; } = [];

  public string? Config { get; private set; }

  /// <summary>Where <c>proxy</c> listens: <c>host:port</c> or a port.</summary>
  public string? Listen { get; private set; }

  public string ListenPath { get; private set; } = "/mcp";

  /// <summary>The bearer token <c>proxy</c> requires from its callers.</summary>
  public string? Token { get; private set; }

  /// <summary>Allows <c>proxy</c> to listen on a non-loopback address.</summary>
  public bool AllowNonLoopback { get; private set; }

  public List<string> AllowOrigins { get; } = [];

  /// <summary>The first problem found, or null.</summary>
  public string? Error { get; private set; }

  public static CliOptions Parse(IReadOnlyList<string> args)
  {
    var o = new CliOptions();
    for (var i = 0; i < args.Count; i++)
    {
      var a = args[i];
      if (a == "--")
      {
        // Everything after is positional, so a server command line can carry its own dashes.
        for (i++; i < args.Count; i++)
        {
          o.Positional.Add(args[i]);
        }

        break;
      }

      if (!a.StartsWith('-') || a == "-")
      {
        if (o.Command is null)
        {
          o.Command = a;
        }
        else
        {
          o.Positional.Add(a);
        }

        continue;
      }

      var eq = a.IndexOf('=', StringComparison.Ordinal);
      var name = eq > 0 && a.StartsWith("--", StringComparison.Ordinal) ? a[..eq] : a;
      string? inline = eq > 0 && a.StartsWith("--", StringComparison.Ordinal) ? a[(eq + 1)..] : null;

      string? Value()
      {
        if (inline is not null)
        {
          return inline;
        }

        if (i + 1 < args.Count)
        {
          return args[++i];
        }

        o.Error ??= "option " + name + " needs a value.";
        return null;
      }

      switch (name)
      {
        case "-h":
        case "--help":
          o.Help = true;
          break;
        case "--version":
          o.Version = true;
          break;
        case "--yes":
        case "-y":
          o.Yes = true;
          break;
        case "--verbose":
        case "-v":
          o.Verbose = true;
          break;
        case "--allow-non-loopback":
          o.AllowNonLoopback = true;
          break;
        case "--json":
          // `--json` alone selects JSON output. `--json @file` or `--json '{...}'` gives a call's arguments, as the usage line says.
          if (inline is null && i + 1 < args.Count && (args[i + 1].StartsWith('@') || args[i + 1].StartsWith('{')))
          {
            o.ArgsJson = args[++i];
          }
          else if (inline is not null && (inline.StartsWith('@') || inline.StartsWith('{')))
          {
            o.ArgsJson = inline;
          }
          else
          {
            o.Json = true;
          }

          break;
        case "--output":
          switch (Value())
          {
            case "json":
              o.Json = true;
              break;
            case "text":
              o.Json = false;
              break;
            case null:
              break;
            default:
              o.Error ??= "--output is text or json.";
              break;
          }

          break;
        case "--args":
          o.ArgsJson = Value();
          break;
        case "--arg":
        case "-a":
          if (Value() is { } pair)
          {
            var at = pair.IndexOf('=', StringComparison.Ordinal);
            if (at <= 0)
            {
              o.Error ??= "--arg takes key=value, not '" + pair + "'.";
            }
            else
            {
              o.Args.Add(new(pair[..at], pair[(at + 1)..]));
            }
          }

          break;
        case "--era":
          switch (Value())
          {
            case "auto":
              o.Era = EraPreference.Auto;
              break;
            case "modern":
              o.Era = EraPreference.Modern;
              break;
            case "legacy":
              o.Era = EraPreference.Legacy;
              break;
            case null:
              break;
            case var other:
              o.Error ??= "--era is auto, modern or legacy, not '" + other + "'.";
              break;
          }

          break;
        case "--config":
          o.Config = Value();
          break;
        case "--listen":
          o.Listen = Value();
          break;
        case "--path":
          o.ListenPath = Value() ?? o.ListenPath;
          break;
        case "--token":
          o.Token = Value();
          break;
        case "--allow-origin":
          if (Value() is { } origin)
          {
            o.AllowOrigins.Add(origin);
          }

          break;
        default:
          o.Error ??= "unknown option " + a + ".";
          break;
      }
    }

    return o;
  }

  /// <summary>Reads <c>host:port</c>, <c>[v6]:port</c> or a bare port. Host defaults to 127.0.0.1.</summary>
  public static bool TryParseListen(string? text, out string host, out int port)
  {
    host = "127.0.0.1";
    port = 3001;
    if (string.IsNullOrEmpty(text))
    {
      return true;
    }

    var portText = text;
    var colon = text.LastIndexOf(':');
    if (colon >= 0)
    {
      host = text[..colon].Trim('[', ']');
      portText = text[(colon + 1)..];
      if (host.Length == 0)
      {
        host = "127.0.0.1";
      }
    }

    return int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 0 and <= 65535;
  }
}
