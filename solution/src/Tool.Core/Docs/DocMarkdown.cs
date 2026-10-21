using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Fuxion.Tool.Core.Docs;

/// <summary>A heading or a link of a Markdown, with its line (from 1).</summary>
public sealed record DocMarkdownItem(string Text, int Line);

/// <summary>A link of a Markdown where it is: its target as written (with its <c>&lt;&gt;</c>, if it has them), its line and its place in the text.</summary>
public sealed record DocLink(string Written, int Line, int Start, int Length)
{
	/// <summary>The target, without its <c>&lt;&gt;</c>.</summary>
	public string Target => Written.StartsWith('<') && Written.EndsWith('>') ? Written[1..^1].Trim() : Written.Trim();
}

/// <summary>What the block "status of this document" of a bootstrap document says; null what it does not say.</summary>
public sealed record DocHeader(string? Type, string? Profile, string? Status, string? Created);

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

	// type: rfc, in a block of the header
	[GeneratedRegex(@"^\s*(?<key>type|profile|status|created)\s*:\s*(?<value>[^\s#]+)")]
	private static partial Regex HeaderLine();

	// - Tipo: `adr`. Estado: `accepted`. Creación: `2026-10-18`.
	[GeneratedRegex(@"(?<key>Tipo|Type|Perfil|Profile|Estado|Status|Creación|Created)\s*:\s*`(?<value>[^`]+)`")]
	private static partial Regex HeaderBullet();

	/// <summary>The lines of <paramref name="text"/> (without their line end), each with its number and where it starts.</summary>
	static IEnumerable<(string Line, int Number, int Offset)> Lines(string text)
	{
		var number = 0;
		for (var start = 0; start <= text.Length;)
		{
			var end = text.IndexOf('\n', start);
			var line = end < 0 ? text[start..] : text[start..end];
			yield return (line.TrimEnd('\r'), ++number, start);
			if (end < 0)
				yield break;
			start = end + 1;
		}
	}

	/// <summary>The lines of <paramref name="text"/> outside fenced code.</summary>
	static IEnumerable<(string Line, int Number, int Offset)> Prose(string text)
	{
		string? fence = null;
		foreach (var (line, number, offset) in Lines(text))
		{
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
				yield return (line, number, offset);
		}
	}

	[GeneratedRegex(@"^ {0,3}(?<level>#{1,6})(?:[ \t]+(?<text>.*?))?(?:[ \t]+#+)?[ \t]*$")]
	private static partial Regex AnyHeading();

	/// <summary>
	/// The section of <paramref name="text"/> under the heading <paramref name="heading"/> (its text, of any level): the
	/// heading and all that follows it up to the next heading of its level or above, outside code. Null if it has none.
	/// </summary>
	public static string? Section(string text, string heading)
	{
		int? level = null;
		var start = 0;
		string? fence = null;
		foreach (var (line, _, offset) in Lines(text))
		{
			if (Fence().Match(line) is { Success: true } match)
			{
				var marker = match.Groups["fence"].Value;
				if (fence is null)
					fence = marker;
				else if (marker[0] == fence[0] && marker.Length >= fence.Length && line.Trim().Trim(marker[0]).Length == 0)
					fence = null;
				continue;
			}
			if (fence is not null || AnyHeading().Match(line) is not { Success: true } found)
				continue;
			var depth = found.Groups["level"].Length;
			if (level is { } open && depth <= open)
				return text[start..offset].TrimEnd() + "\n";
			if (level is null && found.Groups["text"].Value.Trim() == heading.Trim())
				(level, start) = (depth, offset);
		}
		return level is null ? null : text[start..].TrimEnd() + "\n";
	}

	/// <summary>The first H1 (<c># Title</c>), without its closing hashes; null if it has none.</summary>
	public static DocMarkdownItem? Title(string text)
	{
		foreach (var (line, number, _) in Prose(text))
			if (Heading().Match(line) is { Success: true } match)
				return new(match.Groups["text"].Value.Trim(), number);
		return null;
	}

	/// <summary><paramref name="text"/> with <paramref name="title"/> as its first H1: in place of the one it has, or as its first line.</summary>
	public static string WithTitle(string text, string title)
	{
		foreach (var (line, _, offset) in Prose(text))
			if (Heading().IsMatch(line))
				return $"{text[..offset]}# {title}{text[(offset + line.Length)..]}";
		var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
		return $"# {title}{newLine}{newLine}{text}";
	}

	/// <summary>
	/// The relative links of <paramref name="text"/>, where they are: not a URL (a scheme), an anchor alone or an
	/// absolute path. In links and images, inline or by reference; never in code.
	/// </summary>
	public static IReadOnlyList<DocLink> LinkSpans(string text)
	{
		var links = new List<DocLink>();
		foreach (var (line, number, offset) in Prose(text))
		{
			var prose = CodeSpan().Replace(line, m => new string(' ', m.Length));
			foreach (Match match in InlineLink().Matches(prose))
				Add(match.Groups["target"], number, offset);
			if (Definition().Match(prose) is { Success: true } definition)
				Add(definition.Groups["target"], number, offset);
		}
		return links;

		void Add(Group target, int number, int offset)
		{
			var link = new DocLink(target.Value, number, offset + target.Index, target.Length);
			var path = link.Target;
			if (path.Length > 0 && !path.StartsWith('#') && !path.StartsWith('/') && !path.StartsWith('\\') && !Scheme().IsMatch(path))
				links.Add(link);
		}
	}

	/// <summary>The relative links of <paramref name="text"/>, as written (without <c>&lt;&gt;</c>), with their line.</summary>
	public static IReadOnlyList<DocMarkdownItem> Links(string text) => LinkSpans(text).Select(l => new DocMarkdownItem(l.Target, l.Line)).ToList();

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

	/// <summary>
	/// What the block "status of this document" says: the first section of the Markdown (from its first <c>##</c> to the
	/// next), in either of its forms: lines <c>type: rfc</c> (as in a code block), or values between backticks after
	/// their name (<c>Tipo: `adr`</c>, <c>Estado</c>, <c>Perfil</c>, <c>Creación</c>, or in English). The first of each wins.
	/// </summary>
	public static DocHeader Header(string text)
	{
		string? type = null, profile = null, status = null, created = null;
		var section = 0;
		foreach (var (line, _, _) in Lines(text))
		{
			if (line.StartsWith("## ", StringComparison.Ordinal) && ++section > 1)
				break;
			if (section == 0)
				continue;
			var pairs = HeaderBullet().Matches(line).Select(m => (m.Groups["key"].Value, m.Groups["value"].Value.Trim())).ToList();
			if (HeaderLine().Match(line) is { Success: true } plain)
				pairs.Add((plain.Groups["key"].Value, plain.Groups["value"].Value));
			foreach (var (key, value) in pairs)
				switch (key)
				{
					case "type" or "Tipo" or "Type":
						type ??= value;
						break;
					case "profile" or "Perfil" or "Profile":
						profile ??= value;
						break;
					case "status" or "Estado" or "Status":
						status ??= value;
						break;
					default:
						created ??= value;
						break;
				}
		}
		return new(type, profile, status, created);
	}
}
