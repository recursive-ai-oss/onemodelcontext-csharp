using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RecursiveAI.OneModelContext.Cli;

/// <summary>A server to connect to: a URL, or a way to start a process.</summary>
internal sealed class ServerSpec
{
  /// <summary>The URL of a Streamable HTTP server, or null for a process.</summary>
  public string? Url { get; init; }

  public string? Command { get; init; }

  public IReadOnlyList<string> Arguments { get; init; } = [];

  public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

  /// <summary>A command line to split into words, when the server was named on the command line.</summary>
  public string? CommandLine { get; init; }

  public string Describe() => Url ?? CommandLine ?? Command ?? "";
}

/// <summary>
/// Turns what the user typed into a <see cref="ServerSpec"/>. A name found in the config file wins; otherwise an
/// <c>http://</c> or <c>https://</c> value is a URL and anything else is a command line.
/// </summary>
/// <remarks>
/// The config is a small JSON file: <c>{"servers": {"name": {"command": "npx", "args": ["-y", "pkg"], "env": {"KEY": "value"}}, "other": {"url": "http://127.0.0.1:3001/mcp"}}}</c>.
/// </remarks>
internal static class ServerResolver
{
  public const string DefaultFileName = "onemodelcontext.json";

  /// <summary>The files looked at, in order, when <c>--config</c> is not given.</summary>
  public static IEnumerable<string> DefaultPaths()
  {
    yield return Path.Combine(Directory.GetCurrentDirectory(), DefaultFileName);
    var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
    if (appData.Length > 0)
    {
      yield return Path.Combine(appData, "onemodelcontext", "config.json");
    }
  }

  public static ServerSpec Resolve(string server, string? configPath)
  {
    if (string.IsNullOrWhiteSpace(server))
    {
      throw new ArgumentException("name a server: a command line, a URL or an entry in the config file.");
    }

    var text = server.Trim();
    var looksLikeName = !text.Contains(' ', StringComparison.Ordinal) && !text.Contains('/', StringComparison.Ordinal) && !text.Contains(':', StringComparison.Ordinal);
    if (looksLikeName || configPath is not null)
    {
      foreach (var path in configPath is not null ? new[] { configPath } : DefaultPaths())
      {
        if (!File.Exists(path))
        {
          if (configPath is not null)
          {
            throw new ArgumentException("config file '" + path + "' does not exist.");
          }

          continue;
        }

        if (TryFromConfig(path, text) is { } spec)
        {
          return spec;
        }
      }
    }

    if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
      return new ServerSpec { Url = text };
    }

    return new ServerSpec { CommandLine = text };
  }

  private static ServerSpec? TryFromConfig(string path, string name)
  {
    JsonDocument doc;
    try
    {
      doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
    }
    catch (JsonException ex)
    {
      throw new ArgumentException("config file '" + path + "' is not valid JSON: " + ex.Message, ex);
    }

    using (doc)
    {
      if (doc.RootElement.ValueKind != JsonValueKind.Object
          || !doc.RootElement.TryGetProperty("servers", out var servers)
          || servers.ValueKind != JsonValueKind.Object
          || !servers.TryGetProperty(name, out var entry)
          || entry.ValueKind != JsonValueKind.Object)
      {
        return null;
      }

      if (entry.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
      {
        return new ServerSpec { Url = url.GetString() };
      }

      if (!entry.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String)
      {
        throw new ArgumentException("server '" + name + "' in '" + path + "' needs a \"command\" or a \"url\".");
      }

      var args = new List<string>();
      if (entry.TryGetProperty("args", out var argArray) && argArray.ValueKind == JsonValueKind.Array)
      {
        foreach (var a in argArray.EnumerateArray())
        {
          args.Add(a.ValueKind == JsonValueKind.String ? a.GetString()! : a.GetRawText());
        }
      }

      var env = new Dictionary<string, string?>();
      if (entry.TryGetProperty("env", out var envObject) && envObject.ValueKind == JsonValueKind.Object)
      {
        foreach (var p in envObject.EnumerateObject())
        {
          env[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
        }
      }

      return new ServerSpec { Command = command.GetString(), Arguments = args, Environment = env };
    }
  }
}
