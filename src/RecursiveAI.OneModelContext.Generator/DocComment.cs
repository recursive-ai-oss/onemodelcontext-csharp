using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RecursiveAI.OneModelContext.Generator;

/// <summary>Doc comments read from the source tree, so they work whether or not the project writes a documentation file.</summary>
internal sealed class DocComment
{
  private static readonly char[] Whitespace = { ' ', '\t', '\r', '\n' };

  public static readonly DocComment Empty = new(null, new Dictionary<string, string>());

  public DocComment(string? summary, Dictionary<string, string> parameters)
  {
    Summary = summary;
    Parameters = parameters;
  }

  public string? Summary { get; }

  public Dictionary<string, string> Parameters { get; }

  /// <summary>Reads the <c>///</c> block that leads <paramref name="node"/>.</summary>
  public static DocComment Read(SyntaxNode? node)
  {
    if (node is null)
    {
      return Empty;
    }

    // With documentation parsing off the compiler treats /// lines as ordinary comments, so collect the text of both forms.
    var lines = new StringBuilder();
    foreach (var trivia in node.GetLeadingTrivia())
    {
      if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
      {
        foreach (var line in trivia.ToFullString().Split('\n'))
        {
          lines.AppendLine(StripSlashes(line));
        }
      }
      else if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) && trivia.ToString().StartsWith("///", StringComparison.Ordinal))
      {
        lines.AppendLine(StripSlashes(trivia.ToString()));
      }
    }

    if (lines.Length == 0)
    {
      return Empty;
    }

    XElement root;
    try
    {
      root = XElement.Parse("<doc>" + lines + "</doc>");
    }
    catch (System.Xml.XmlException)
    {
      return Empty;
    }

    var summary = root.Element("summary");
    var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var param in root.Elements("param"))
    {
      var name = (string?)param.Attribute("name");
      if (name is not null)
      {
        parameters[name] = Flatten(param);
      }
    }

    return new DocComment(summary is null ? null : NullIfEmpty(Flatten(summary)), parameters);
  }

  private static string StripSlashes(string line)
  {
    var trimmed = line.Trim();
    return trimmed.StartsWith("///", StringComparison.Ordinal) ? trimmed.Substring(3) : trimmed;
  }

  private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

  // <see cref="X"/> becomes X, <paramref name="p"/> becomes p, <c> and <para> keep their text; whitespace collapses.
  private static string Flatten(XElement element)
  {
    var sb = new StringBuilder();
    Append(sb, element);
    var parts = sb.ToString().Split(Whitespace, StringSplitOptions.RemoveEmptyEntries);
    return string.Join(" ", parts);
  }

  private static void Append(StringBuilder sb, XElement element)
  {
    foreach (var node in element.Nodes())
    {
      switch (node)
      {
        case XText text:
          sb.Append(text.Value);
          break;
        case XElement child when child.Name.LocalName == "see" || child.Name.LocalName == "seealso":
          sb.Append(((string?)child.Attribute("cref") ?? (string?)child.Attribute("langword") ?? child.Value).Replace("T:", string.Empty).Replace("M:", string.Empty).Replace("P:", string.Empty));
          break;
        case XElement child when child.Name.LocalName == "paramref" || child.Name.LocalName == "typeparamref":
          sb.Append((string?)child.Attribute("name"));
          break;
        case XElement child when child.Name.LocalName == "para" || child.Name.LocalName == "br":
          sb.Append(' ');
          Append(sb, child);
          sb.Append(' ');
          break;
        case XElement child:
          Append(sb, child);
          break;
      }
    }
  }
}
