using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Fuxion.Tools.Core.Text;

/// <summary>A line of a diff: kept (<c>' '</c>), removed (<c>'-'</c>) or added (<c>'+'</c>).</summary>
public sealed record DiffLine(char Kind, string Text);

/// <summary>A hunk: the changed lines with some context around them, and where they are (lines from 1).</summary>
public sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<DiffLine> Lines);

/// <summary>
/// The difference between two texts, line by line, as <c>diff -u</c> shows it: what fx would change in a file it edits
/// (plan O, O3.4). The longest common subsequence, enough for the small files fx edits.
/// </summary>
public static class LineDiff
{
	/// <summary>The hunks from <paramref name="before"/> to <paramref name="after"/>, with <paramref name="context"/> lines around each change.</summary>
	public static IReadOnlyList<DiffHunk> Compute(string before, string after, int context = 2)
	{
		var a = Lines(before);
		var b = Lines(after);
		var common = new int[a.Length + 1, b.Length + 1];
		for (var i = a.Length - 1; i >= 0; i--)
			for (var j = b.Length - 1; j >= 0; j--)
				common[i, j] = a[i] == b[j] ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);

		// the whole script, with the line numbers of each side
		var script = new List<(DiffLine Line, int Old, int New)>();
		int x = 0, y = 0;
		while (x < a.Length || y < b.Length)
			if (x < a.Length && y < b.Length && a[x] == b[y])
				script.Add((new(' ', a[x++]), x, ++y));
			else if (y >= b.Length || (x < a.Length && common[x + 1, y] >= common[x, y + 1]))
				script.Add((new('-', a[x++]), x, y));
			else
				script.Add((new('+', b[y++]), x, y));

		var hunks = new List<DiffHunk>();
		var changes = script.Select((s, index) => (s, index)).Where(c => c.s.Line.Kind != ' ').Select(c => c.index).ToList();
		var k = 0;
		while (k < changes.Count)
		{
			var first = changes[k];
			var last = first;
			while (k + 1 < changes.Count && changes[k + 1] - last <= 2 * context + 1)
				last = changes[++k];
			k++;
			var from = Math.Max(0, first - context);
			var to = Math.Min(script.Count - 1, last + context);
			var lines = script.Skip(from).Take(to - from + 1).ToList();
			var oldCount = lines.Count(l => l.Line.Kind != '+');
			var newCount = lines.Count(l => l.Line.Kind != '-');
			// as diff -u: the first line of each side in the hunk, or the line before it if the side has none
			var oldStart = lines.FirstOrDefault(l => l.Line.Kind != '+') is { Line: not null } o ? o.Old : script[from].Old;
			var newStart = lines.FirstOrDefault(l => l.Line.Kind != '-') is { Line: not null } n ? n.New : script[from].New;
			hunks.Add(new(oldStart, oldCount, newStart, newCount, lines.Select(l => l.Line).ToList()));
		}
		return hunks;
	}

	/// <summary>The hunks as <c>diff -u</c> writes them, without the header of the files.</summary>
	public static string Unified(IEnumerable<DiffHunk> hunks)
	{
		var text = new StringBuilder();
		foreach (var hunk in hunks)
		{
			text.Append($"@@ -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} @@\n");
			foreach (var line in hunk.Lines)
				text.Append(line.Kind).Append(line.Text).Append('\n');
		}
		return text.ToString();
	}

	static string[] Lines(string text)
	{
		var lines = text.Replace("\r\n", "\n").Split('\n');
		return text.EndsWith('\n') ? lines[..^1] : lines;
	}
}
