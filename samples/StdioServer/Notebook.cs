using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using RecursiveAI.OneModelContext;

namespace StdioServer;

/// <summary>A note.</summary>
/// <param name="Title">The note's title.</param>
/// <param name="Body">The text.</param>
public sealed record Note(string Title, string Body);

/// <summary>The kinds of search a notebook offers.</summary>
public enum Match
{
  Title,
  Body,
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(Note))]
public sealed partial class NotebookJson : JsonSerializerContext
{
}

/// <summary>A notebook the model can read and write.</summary>
public partial class Notebook
{
  private readonly ConcurrentDictionary<string, Note> _notes = new(StringComparer.Ordinal);

  /// <summary>Saves a note, replacing one with the same title.</summary>
  /// <param name="title">The note's title.</param>
  /// <param name="body">The text to keep.</param>
  [Tool(Idempotent = true)]
  public string SaveNote(string title, string body)
  {
    _notes[title] = new Note(title, body);
    return $"Saved '{title}'.";
  }

  /// <summary>Finds a note by its title.</summary>
  /// <param name="title">The note's title.</param>
  [Tool(ReadOnly = true)]
  public Note? GetNote(string title) => _notes.TryGetValue(title, out var note) ? note : null;

  /// <summary>Lists the titles that contain some text.</summary>
  /// <param name="text">What to look for.</param>
  /// <param name="match">Where to look.</param>
  /// <param name="limit">The most titles to return.</param>
  [Tool(ReadOnly = true)]
  public string[] Search(string text, Match match = Match.Title, [Range(1, 100)] int limit = 10)
  {
    var hits = _notes.Values.Where(n => (match == Match.Title ? n.Title : n.Body).Contains(text, StringComparison.OrdinalIgnoreCase));
    return [.. hits.Select(n => n.Title).Order(StringComparer.Ordinal).Take(limit)];
  }

  /// <summary>A note's text, by title.</summary>
  /// <param name="title">The note's title.</param>
  [Resource("notes://{title}", MimeType = "text/plain")]
  public string ReadNote(string title) => _notes.TryGetValue(title, out var note) ? note.Body : throw new ProtocolException(-32602, "No such note.");

  /// <summary>Asks for a summary of one note.</summary>
  /// <param name="title">The note to summarize.</param>
  [Prompt]
  public static string Summarize(string title) => $"Summarize the note titled '{title}' in two sentences.";
}
