using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RecursiveAI.OneModelContext.Extensions.Skills;

/// <summary>
/// Reads the YAML frontmatter of a <c>SKILL.md</c> into JSON. Agent Skills frontmatter is a small, flat-ish document (a mapping of strings, a
/// nested mapping or two, sometimes a list), so this reads that subset of YAML 1.2 and refuses the rest with a <see cref="FormatException"/>
/// rather than guessing: mappings and sequences by indentation, plain, single-quoted and double-quoted scalars, folded and literal block
/// scalars, short flow collections, comments, and core-schema scalar types (null, booleans, numbers). Anchors, aliases, tags, complex keys,
/// merge keys and several documents are not supported. The input is bounded in size and depth.
/// </summary>
internal static class SkillYaml
{
  internal const int MaxBytes = 64 * 1024;
  private const int MaxDepth = 16;

  /// <summary>
  /// Splits a <c>SKILL.md</c> into its frontmatter and body. The file must begin with a line <c>---</c> and the frontmatter ends at the next
  /// line that is <c>---</c> (or <c>...</c>).
  /// </summary>
  /// <exception cref="FormatException">There is no frontmatter, or it is not in the supported YAML subset.</exception>
  internal static JsonElement ParseFrontmatter(ReadOnlySpan<byte> skillMd)
  {
    if (skillMd.Length >= 3 && skillMd[0] == 0xEF && skillMd[1] == 0xBB && skillMd[2] == 0xBF)
    {
      skillMd = skillMd[3..];
    }

    string text;
    try
    {
      text = new UTF8Encoding(false, true).GetString(skillMd);
    }
    catch (ArgumentException ex)
    {
      throw new FormatException("SKILL.md is not valid UTF-8.", ex);
    }

    var lines = text.Split('\n');
    if (lines.Length == 0 || lines[0].TrimEnd('\r') != "---")
    {
      throw new FormatException("SKILL.md must begin with a YAML frontmatter block that starts with a line '---'.");
    }

    var end = -1;
    for (var i = 1; i < lines.Length; i++)
    {
      var line = lines[i].TrimEnd('\r');
      if (line is "---" or "...")
      {
        end = i;
        break;
      }
    }

    if (end < 0)
    {
      throw new FormatException("The frontmatter of SKILL.md is not closed by a line '---'.");
    }

    var yaml = string.Join('\n', lines, 1, end - 1).Replace("\r", string.Empty, StringComparison.Ordinal);
    if (Encoding.UTF8.GetByteCount(yaml) > MaxBytes)
    {
      throw new FormatException("The frontmatter is larger than " + MaxBytes + " bytes.");
    }

    return Parse(yaml);
  }

  /// <summary>Reads a YAML document (a mapping) into a JSON object.</summary>
  internal static JsonElement Parse(string yaml)
  {
    var parser = new Parser(yaml);
    var root = parser.ParseDocument();
    var buffer = new ArrayBufferWriter<byte>();
    using (var writer = new Utf8JsonWriter(buffer))
    {
      Write(writer, root);
    }

    using var document = JsonDocument.Parse(buffer.WrittenMemory);
    return document.RootElement.Clone();
  }

  private static void Write(Utf8JsonWriter writer, object? node)
  {
    switch (node)
    {
      case null:
        writer.WriteNullValue();
        break;
      case bool b:
        writer.WriteBooleanValue(b);
        break;
      case long l:
        writer.WriteNumberValue(l);
        break;
      case double d:
        writer.WriteNumberValue(d);
        break;
      case string s:
        writer.WriteStringValue(s);
        break;
      case List<KeyValuePair<string, object?>> map:
        writer.WriteStartObject();
        foreach (var (key, value) in map)
        {
          writer.WritePropertyName(key);
          Write(writer, value);
        }

        writer.WriteEndObject();
        break;
      case List<object?> list:
        writer.WriteStartArray();
        foreach (var item in list)
        {
          Write(writer, item);
        }

        writer.WriteEndArray();
        break;
      default:
        throw new InvalidOperationException("Unexpected YAML node.");
    }
  }

  private readonly record struct Line(int Number, int Indent, string Text);

  private sealed class Parser
  {
    private readonly List<Line> _lines = [];
    private int _position;

    internal Parser(string yaml)
    {
      var number = 0;
      foreach (var raw in yaml.Split('\n'))
      {
        number++;
        var indent = 0;
        while (indent < raw.Length && raw[indent] == ' ')
        {
          indent++;
        }

        if (indent < raw.Length && raw[indent] == '\t')
        {
          throw Error(number, "tabs are not allowed in indentation");
        }

        _lines.Add(new Line(number, indent, raw[indent..]));
      }
    }

    internal object? ParseDocument()
    {
      SkipBlank();
      if (_position >= _lines.Count)
      {
        throw new FormatException("The frontmatter is empty.");
      }

      var first = _lines[_position];
      if (first.Text.StartsWith("--- ", StringComparison.Ordinal) || first.Text.StartsWith('%'))
      {
        throw Error(first.Number, "directives and document markers are not supported");
      }

      var root = ParseBlock(first.Indent, 0);
      SkipBlank();
      if (_position < _lines.Count)
      {
        throw Error(_lines[_position].Number, "unexpected content (check the indentation)");
      }

      if (root is not List<KeyValuePair<string, object?>>)
      {
        throw new FormatException("The frontmatter must be a YAML mapping.");
      }

      return root;
    }

    private static FormatException Error(int line, string what) => new("Frontmatter line " + line.ToString(CultureInfo.InvariantCulture) + ": " + what + ".");

    private static bool IsBlank(Line line) => line.Text.Length == 0 || line.Text[0] == '#';

    private void SkipBlank()
    {
      while (_position < _lines.Count && IsBlank(_lines[_position]))
      {
        _position++;
      }
    }

    // Parses the block that starts at the current line, whose lines are indented by exactly `indent`.
    private object? ParseBlock(int indent, int depth)
    {
      if (depth > MaxDepth)
      {
        throw Error(_lines[_position].Number, "nesting is deeper than " + MaxDepth);
      }

      var line = _lines[_position];
      return line.Text == "-" || line.Text.StartsWith("- ", StringComparison.Ordinal)
        ? ParseSequence(indent, depth)
        : ParseMapping(indent, depth);
    }

    private List<KeyValuePair<string, object?>> ParseMapping(int indent, int depth)
    {
      var map = new List<KeyValuePair<string, object?>>();
      var seen = new HashSet<string>(StringComparer.Ordinal);
      while (true)
      {
        SkipBlank();
        if (_position >= _lines.Count || _lines[_position].Indent < indent)
        {
          return map;
        }

        var line = _lines[_position];
        if (line.Indent > indent)
        {
          throw Error(line.Number, "unexpected indentation");
        }

        if (line.Text == "-" || line.Text.StartsWith("- ", StringComparison.Ordinal))
        {
          return map;
        }

        var (key, rest) = SplitKey(line);
        if (!seen.Add(key))
        {
          throw Error(line.Number, "the key '" + key + "' appears twice");
        }

        _position++;
        map.Add(new KeyValuePair<string, object?>(key, ParseValue(rest, indent, line, depth)));
      }
    }

    private List<object?> ParseSequence(int indent, int depth)
    {
      var list = new List<object?>();
      while (true)
      {
        SkipBlank();
        if (_position >= _lines.Count || _lines[_position].Indent != indent)
        {
          if (_position < _lines.Count && _lines[_position].Indent > indent)
          {
            throw Error(_lines[_position].Number, "unexpected indentation");
          }

          return list;
        }

        var line = _lines[_position];
        if (line.Text != "-" && !line.Text.StartsWith("- ", StringComparison.Ordinal))
        {
          return list;
        }

        var rest = line.Text.Length > 1 ? line.Text[2..] : string.Empty;
        var offset = 2;
        while (rest.StartsWith(' '))
        {
          rest = rest[1..];
          offset++;
        }

        if (rest.Length == 0 || rest[0] == '#')
        {
          _position++;
          SkipBlank();
          if (_position < _lines.Count && _lines[_position].Indent > indent)
          {
            list.Add(ParseBlock(_lines[_position].Indent, depth + 1));
          }
          else
          {
            list.Add(null);
          }

          continue;
        }

        if (LooksLikeMappingEntry(rest))
        {
          // "- key: value": the item is a mapping whose first entry sits on the dash line; its other entries align with that entry.
          _lines[_position] = new Line(line.Number, indent + offset, rest);
          list.Add(ParseBlock(indent + offset, depth + 1));
          continue;
        }

        _position++;
        list.Add(ParseValue(rest, indent, line, depth + 1));
      }
    }

    private static bool LooksLikeMappingEntry(string text)
    {
      if (text[0] is '[' or '{' or '|' or '>' or '#')
      {
        return false;
      }

      return FindKeyEnd(text) >= 0;
    }

    // The index of the ':' that ends a key (followed by a space or the end of the text), outside quotes; or -1.
    private static int FindKeyEnd(string text)
    {
      var i = 0;
      if (text.Length > 0 && text[0] is '"' or '\'')
      {
        var quote = text[0];
        i = 1;
        while (i < text.Length)
        {
          if (text[i] == '\\' && quote == '"')
          {
            i += 2;
            continue;
          }

          if (text[i] == quote)
          {
            if (quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
            {
              i += 2;
              continue;
            }

            i++;
            break;
          }

          i++;
        }

        return i < text.Length && text[i] == ':' && (i + 1 == text.Length || text[i + 1] == ' ') ? i : -1;
      }

      for (; i < text.Length; i++)
      {
        if (text[i] == '#' && i > 0 && text[i - 1] == ' ')
        {
          return -1;
        }

        if (text[i] == ':' && (i + 1 == text.Length || text[i + 1] == ' '))
        {
          return i;
        }
      }

      return -1;
    }

    private static (string Key, string Remainder) SplitKey(Line line)
    {
      var end = FindKeyEnd(line.Text);
      if (end < 0)
      {
        throw Error(line.Number, "expected 'key: value'");
      }

      var keyText = line.Text[..end].TrimEnd();
      var key = keyText.Length > 0 && keyText[0] is '"' or '\'' ? Unquote(keyText, line.Number) : keyText;
      if (key.Length == 0 || key[0] is '?' or '&' or '*' or '!' or '<')
      {
        throw Error(line.Number, "this kind of key is not supported");
      }

      return (key, line.Text[(end + 1)..].TrimStart());
    }

    // Reads the value of a mapping entry or sequence item. `rest` is the text after the key (or dash) on its line; the line is consumed.
    private object? ParseValue(string rest, int indent, Line line, int depth)
    {
      if (depth > MaxDepth)
      {
        throw Error(line.Number, "nesting is deeper than " + MaxDepth);
      }

      if (rest.Length == 0 || rest[0] == '#')
      {
        SkipBlank();
        if (_position < _lines.Count)
        {
          var next = _lines[_position];
          if (next.Indent > indent)
          {
            return ParseBlock(next.Indent, depth + 1);
          }

          // A sequence may sit at the same indentation as its key.
          if (next.Indent == indent && (next.Text == "-" || next.Text.StartsWith("- ", StringComparison.Ordinal)))
          {
            return ParseSequence(indent, depth + 1);
          }
        }

        return null;
      }

      switch (rest[0])
      {
        case '|' or '>':
          return ParseBlockScalar(rest, indent, line);
        case '[' or '{':
          return ParseFlow(rest, line.Number, depth);
        case '"' or '\'':
          return ParseQuoted(rest, indent, line);
        case '&' or '*' or '!' or '@' or '`' or '%':
          throw Error(line.Number, "anchors, aliases and tags are not supported");
        default:
          return ParsePlain(rest, indent, line);
      }
    }

    private object? ParsePlain(string first, int indent, Line line)
    {
      var text = StripComment(first);
      // Continuation lines of a plain scalar are indented deeper than its key and fold into spaces.
      while (_position < _lines.Count)
      {
        var next = _lines[_position];
        if (IsBlank(next) && next.Text.Length == 0)
        {
          // A blank line inside a plain scalar is a line break; stop at it unless text follows at the continuation indentation.
          var after = _position + 1;
          while (after < _lines.Count && _lines[after].Text.Length == 0)
          {
            after++;
          }

          if (after < _lines.Count && _lines[after].Indent > indent && _lines[after].Text[0] != '#' && !LooksLikeMappingEntry(_lines[after].Text))
          {
            text += "\n";
            _position = after;
            continue;
          }

          break;
        }

        if (next.Indent <= indent || next.Text[0] == '#' || LooksLikeMappingEntry(next.Text))
        {
          break;
        }

        text += (text.EndsWith('\n') ? string.Empty : " ") + StripComment(next.Text);
        _position++;
      }

      return ResolvePlain(text.Trim(), line.Number);
    }

    private static string StripComment(string text)
    {
      for (var i = 1; i < text.Length; i++)
      {
        if (text[i] == '#' && text[i - 1] == ' ')
        {
          return text[..i].TrimEnd();
        }
      }

      return text.TrimEnd();
    }

    private static object? ResolvePlain(string text, int lineNumber)
    {
      switch (text)
      {
        case "" or "~" or "null" or "Null" or "NULL":
          return null;
        case "true" or "True" or "TRUE":
          return true;
        case "false" or "False" or "FALSE":
          return false;
      }

      if (text.Length > 0 && (char.IsAsciiDigit(text[0]) || text[0] is '-' or '+' or '.'))
      {
        if (IsInteger(text) && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
        {
          return whole;
        }

        if (IsFloat(text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
        {
          return number;
        }
      }

      if (text[0] is '|' or '>' or '@' or '`')
      {
        throw Error(lineNumber, "this scalar form is not supported");
      }

      return text;
    }

    private static bool IsInteger(string text)
    {
      var start = text[0] is '-' or '+' ? 1 : 0;
      if (start == text.Length)
      {
        return false;
      }

      for (var i = start; i < text.Length; i++)
      {
        if (!char.IsAsciiDigit(text[i]))
        {
          return false;
        }
      }

      return true;
    }

    private static bool IsFloat(string text)
    {
      var i = text[0] is '-' or '+' ? 1 : 0;
      var digits = 0;
      var seenDot = false;
      var seenExp = false;
      for (; i < text.Length; i++)
      {
        var c = text[i];
        if (char.IsAsciiDigit(c))
        {
          digits++;
        }
        else if (c == '.' && !seenDot && !seenExp)
        {
          seenDot = true;
        }
        else if (c is 'e' or 'E' && !seenExp && digits > 0 && i + 1 < text.Length)
        {
          seenExp = true;
          if (text[i + 1] is '-' or '+')
          {
            i++;
          }
        }
        else
        {
          return false;
        }
      }

      return digits > 0;
    }

    private string ParseQuoted(string first, int indent, Line line)
    {
      // A quoted scalar may continue on following lines (they fold into spaces) until its closing quote.
      var text = first;
      while (!IsClosed(text))
      {
        if (_position >= _lines.Count)
        {
          throw Error(line.Number, "a quoted string is not closed");
        }

        var next = _lines[_position++];
        text += next.Text.Length == 0 ? "\n" : (text.EndsWith('\n') ? string.Empty : " ") + next.Text;
      }

      var end = ClosingQuote(text);
      var tail = StripComment(text[(end + 1)..]);
      if (tail.Length > 0)
      {
        throw Error(line.Number, "unexpected text after a quoted string");
      }

      _ = indent;
      return Unquote(text[..(end + 1)], line.Number);
    }

    private static bool IsClosed(string text) => ClosingQuote(text) >= 0;

    private static int ClosingQuote(string text)
    {
      var quote = text[0];
      for (var i = 1; i < text.Length; i++)
      {
        if (quote == '"' && text[i] == '\\')
        {
          i++;
          continue;
        }

        if (text[i] == quote)
        {
          if (quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
          {
            i++;
            continue;
          }

          return i;
        }
      }

      return -1;
    }

    private static string Unquote(string quoted, int lineNumber)
    {
      var quote = quoted[0];
      var body = quoted[1..^1];
      if (quote == '\'')
      {
        return body.Replace("''", "'", StringComparison.Ordinal);
      }

      var builder = new StringBuilder(body.Length);
      for (var i = 0; i < body.Length; i++)
      {
        var c = body[i];
        if (c != '\\')
        {
          builder.Append(c);
          continue;
        }

        if (++i >= body.Length)
        {
          throw Error(lineNumber, "an escape sequence is cut off");
        }

        switch (body[i])
        {
          case 'n': builder.Append('\n'); break;
          case 't': builder.Append('\t'); break;
          case 'r': builder.Append('\r'); break;
          case '0': builder.Append('\0'); break;
          case '\\': builder.Append('\\'); break;
          case '"': builder.Append('"'); break;
          case '/': builder.Append('/'); break;
          case ' ': builder.Append(' '); break;
          case 'u' when i + 4 < body.Length + 0 && int.TryParse(body.AsSpan(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code):
            builder.Append((char)code);
            i += 4;
            break;
          default:
            throw Error(lineNumber, "the escape sequence '\\" + body[i] + "' is not supported");
        }
      }

      return builder.ToString();
    }

    private string ParseBlockScalar(string header, int indent, Line line)
    {
      var folded = header[0] == '>';
      var chomp = 'c';
      foreach (var c in header[1..])
      {
        if (c is '-' or '+')
        {
          chomp = c;
        }
        else if (!char.IsAsciiDigit(c) && c != ' ')
        {
          if (c == '#')
          {
            break;
          }

          throw Error(line.Number, "this block scalar header is not supported");
        }
      }

      var content = new List<string>();
      var blockIndent = -1;
      while (_position < _lines.Count)
      {
        var next = _lines[_position];
        if (next.Text.Length == 0)
        {
          content.Add(string.Empty);
          _position++;
          continue;
        }

        if (blockIndent < 0)
        {
          if (next.Indent <= indent)
          {
            break;
          }

          blockIndent = next.Indent;
        }

        if (next.Indent < blockIndent)
        {
          break;
        }

        content.Add(new string(' ', next.Indent - blockIndent) + next.Text);
        _position++;
      }

      var trailing = 0;
      while (content.Count > 0 && content[^1].Length == 0)
      {
        content.RemoveAt(content.Count - 1);
        trailing++;
      }

      var text = new StringBuilder();
      for (var i = 0; i < content.Count; i++)
      {
        if (i > 0)
        {
          // Folded scalars join lines with a space unless a blank line (or an indented line) says otherwise.
          if (!folded || content[i].Length == 0 || content[i - 1].Length == 0 || content[i][0] == ' ' || content[i - 1][0] == ' ')
          {
            text.Append('\n');
          }
          else
          {
            text.Append(' ');
          }
        }

        text.Append(content[i]);
      }

      if (content.Count > 0)
      {
        if (chomp == 'c')
        {
          text.Append('\n');
        }
        else if (chomp == '+')
        {
          text.Append('\n', 1 + trailing);
        }
      }

      return text.ToString();
    }

    private static object? ParseFlow(string text, int lineNumber, int depth)
    {
      var position = 0;
      var value = FlowValue(text, ref position, lineNumber, depth);
      while (position < text.Length && text[position] == ' ')
      {
        position++;
      }

      if (position < text.Length && text[position] != '#')
      {
        throw Error(lineNumber, "unexpected text after a flow collection");
      }

      return value;
    }

    private static object? FlowValue(string text, ref int position, int lineNumber, int depth)
    {
      if (depth > MaxDepth)
      {
        throw Error(lineNumber, "nesting is deeper than " + MaxDepth);
      }

      SkipSpaces(text, ref position);
      if (position >= text.Length)
      {
        throw Error(lineNumber, "a flow collection is not closed on its line");
      }

      switch (text[position])
      {
        case '[':
          position++;
          var list = new List<object?>();
          while (true)
          {
            SkipSpaces(text, ref position);
            if (position < text.Length && text[position] == ']')
            {
              position++;
              return list;
            }

            list.Add(FlowValue(text, ref position, lineNumber, depth + 1));
            SkipSpaces(text, ref position);
            if (position < text.Length && text[position] == ',')
            {
              position++;
            }
            else if (position >= text.Length || text[position] != ']')
            {
              throw Error(lineNumber, "a flow sequence is not closed on its line");
            }
          }

        case '{':
          position++;
          var map = new List<KeyValuePair<string, object?>>();
          var seen = new HashSet<string>(StringComparer.Ordinal);
          while (true)
          {
            SkipSpaces(text, ref position);
            if (position < text.Length && text[position] == '}')
            {
              position++;
              return map;
            }

            var key = FlowScalar(text, ref position, lineNumber, ':') as string
              ?? throw Error(lineNumber, "flow mapping keys must be strings");
            SkipSpaces(text, ref position);
            if (position >= text.Length || text[position] != ':')
            {
              throw Error(lineNumber, "expected ':' in a flow mapping");
            }

            position++;
            if (!seen.Add(key))
            {
              throw Error(lineNumber, "the key '" + key + "' appears twice");
            }

            map.Add(new KeyValuePair<string, object?>(key, FlowValue(text, ref position, lineNumber, depth + 1)));
            SkipSpaces(text, ref position);
            if (position < text.Length && text[position] == ',')
            {
              position++;
            }
            else if (position >= text.Length || text[position] != '}')
            {
              throw Error(lineNumber, "a flow mapping is not closed on its line");
            }
          }

        default:
          return FlowScalar(text, ref position, lineNumber, ',');
      }
    }

    private static object? FlowScalar(string text, ref int position, int lineNumber, char stop)
    {
      SkipSpaces(text, ref position);
      if (position < text.Length && text[position] is '"' or '\'')
      {
        var end = ClosingQuote(text[position..]);
        if (end < 0)
        {
          throw Error(lineNumber, "a quoted string is not closed on its line");
        }

        var quoted = Unquote(text.Substring(position, end + 1), lineNumber);
        position += end + 1;
        return quoted;
      }

      var start = position;
      while (position < text.Length && text[position] is not (',' or ']' or '}') && !(text[position] == stop && stop == ':'))
      {
        position++;
      }

      return ResolvePlain(text[start..position].Trim(), lineNumber);
    }

    private static void SkipSpaces(string text, ref int position)
    {
      while (position < text.Length && text[position] == ' ')
      {
        position++;
      }
    }
  }
}
