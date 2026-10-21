using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Fuxion.Tool.Core.Docs;

/// <summary>A heading or a link of a Markdown, with its line (from 1).</summary>
public sealed record DocMarkdownItem(string Text, int Line);

/// <summary>
/// What the module reads of a Markdown: its first H1, the title of the document (the standard, its metadata model), and
/// its relative links, outside fenced code and code spans. It reads, never runs: the text is data.
/// </summary>
public static partial class DocMarkdown
{
	[GeneratedRegex(@"^ {0,3}(?<fence>`{3,}|~{3,})")]
	private static partial Regex Fence();

	[GeneratedRegex(@"^ {0,3}#(?:[ \t]+(?<text>.*?))?(?:[ \t]+#+)?[ \t]*$")]
	private static partial Regex Heading();

	[GeneratedRegex(@"(`+).*?\1")]
	private static partial Regex CodeSpan();

	// [text](target "title") and ![alt](target): a level of brackets inside the text, of parentheses inside the target
	[GeneratedRegex(@"(?<!\\)!?\[(?:[^\[\]\\]|\\.|\[[^\[\]]*\])*\]\(\s*(?<target><[^>]*>|(?:[^\s()\\]|\\.|\([^\s()]*\))*)(?:\s+(?:""[^""]*""|'[^']*'|\([^)]*\)))?\s*\)")]
	private static partial Regex InlineLink();

	// [label]: target
	[GeneratedRegex(@"^ {0,3}\[(?:[^\]\\]|\\.)+\]:\s*(?<target><[^>]*>|\S+)")]
	private static partial Regex Definition();

	[GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*:")]
	private static partial Regex Scheme();

	/// <summary>The lines of <paramref name="text"/> outside fenced code, with their number.</summary>
	static IEnumerable<(string Line, int Number)> Prose(string text)
	{
		var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
		string? fence = null;
		for (var i = 0; i < lines.Length; i++)
		{
			var line = lines[i];
			if (Fence().Match(line) is { Success: true } match)
			{
				var marker = match.Groups["fence"].Value;
				if (fence is null)
					fence = marker;
				else if (marker[0] == fence[0] && marker.Length >= fence.Length && line.Trim().Trim(marker[0]).Length == 0)
					fence = null;
				continue;
			}
			if (fence is null)
				yield return (line, i + 1);
		}
	}

	/// <summary>The first H1 (<c># Title</c>), without its closing hashes; null if it has none.</summary>
	public static DocMarkdownItem? Title(string text)
	{
		foreach (var (line, number) in Prose(text))
			if (Heading().Match(line) is { Success: true } match)
				return new(match.Groups["text"].Value.Trim(), number);
		return null;
	}

	/// <summary>
	/// The relative links of <paramref name="text"/>, as written (without <c>&lt;&gt;</c>): not a URL (a scheme), an anchor
	/// alone or an absolute path.
	/// </summary>
	public static IReadOnlyList<DocMarkdownItem> Links(string text)
	{
		var links = new List<DocMarkdownItem>();
		foreach (var (line, number) in Prose(text))
		{
			var prose = CodeSpan().Replace(line, m => new string(' ', m.Length));
			foreach (Match match in InlineLink().Matches(prose))
				Add(match.Groups["target"].Value, number);
			if (Definition().Match(prose) is { Success: true } definition)
				Add(definition.Groups["target"].Value, number);
		}
		return links;

		void Add(string target, int number)
		{
			target = target.Trim();
			if (target.StartsWith('<') && target.EndsWith('>'))
				target = target[1..^1].Trim();
			if (target.Length == 0 || target.StartsWith('#') || target.StartsWith('/') || target.StartsWith('\\') || Scheme().IsMatch(target))
				return;
			links.Add(new(target, number));
		}
	}

	/// <summary>The file a relative link points to: without its anchor or query, unescaped; null if nothing is left.</summary>
	public static string? File(string target)
	{
		var end = target.IndexOfAny(['#', '?']);
		var path = end < 0 ? target : target[..end];
		if (path.Length == 0)
			return null;
		try
		{
			return Uri.UnescapeDataString(path);
		}
		catch (UriFormatException)
		{
			return path;
		}
	}
}
