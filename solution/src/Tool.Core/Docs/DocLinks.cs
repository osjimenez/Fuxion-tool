using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Fuxion.Tool.Core.Layout;

namespace Fuxion.Tool.Core.Docs;

/// <summary>
/// The relative links of Markdown when files move (<c>move</c>, <c>adopt</c>; the SPEC of the module, 5.5 and 5.6):
/// a link to a file that moves points to where it goes, and the links of a Markdown that moves keep pointing to the
/// same files from its new folder. Only the path of a link changes: its anchor, its query, its <c>&lt;&gt;</c> and its
/// <c>%20</c> stay. A link that led nowhere is left as it was.
/// </summary>
public static class DocLinks
{
	static readonly bool IgnoreCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
	static readonly StringComparer Paths = IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	/// <summary>
	/// <paramref name="text"/>, the Markdown that was at <paramref name="from"/> and goes to <paramref name="to"/> (the
	/// same, if it stays), with its links to the files of <paramref name="moves"/> (full path → full path; a folder moves
	/// with all it has) pointing to where they go.
	/// </summary>
	public static string Rewrite(string text, string from, string to, IReadOnlyDictionary<string, string> moves)
	{
		var oldFolder = Path.GetDirectoryName(from)!;
		var newFolder = Path.GetDirectoryName(to)!;
		var result = new StringBuilder();
		var last = 0;
		foreach (var link in DocMarkdown.LinkSpans(text))
		{
			if (DocMarkdown.File(link.Target) is not { } file)
				continue;
			var target = Path.GetFullPath(Path.Combine(oldFolder, file));
			if (!FxFiles.Exists(target) && !FxFiles.DirectoryExists(target) && Where(target, moves) is null)
				continue;
			var moved = Where(target, moves) ?? target;
			if (Paths.Equals(oldFolder, newFolder) && Paths.Equals(moved, target))
				continue;
			var path = Path.GetRelativePath(newFolder, moved).Replace('\\', '/');
			if (file.EndsWith('/') && !path.EndsWith('/'))
				path += '/';
			var original = link.Target;
			var cut = original.IndexOfAny(['#', '?']);
			var rest = cut >= 0 ? original[cut..] : "";
			var written = cut >= 0 ? original[..cut] : original;
			if (written.StartsWith("./", StringComparison.Ordinal) && !path.StartsWith("../", StringComparison.Ordinal))
				path = $"./{path}";
			if (written.Contains('%'))
				path = Uri.EscapeDataString(path).Replace("%2F", "/").Replace("%2E", ".");
			var replacement = link.Written.StartsWith('<') ? $"<{path}{rest}>" : $"{path}{rest}";
			result.Append(text, last, link.Start - last).Append(replacement);
			last = link.Start + link.Length;
		}
		return last == 0 ? text : result.Append(text, last, text.Length - last).ToString();
	}

	/// <summary>Where <paramref name="path"/> goes: itself, or inside a folder that moves; null if it does not move.</summary>
	public static string? Where(string path, IReadOnlyDictionary<string, string> moves)
	{
		if (moves.TryGetValue(path, out var to))
			return to;
		foreach (var (from, folder) in moves)
			if (path.StartsWith(from + Path.DirectorySeparatorChar, IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
				return folder + path[from.Length..];
		return null;
	}

	/// <summary>
	/// The links of the rest of the catalog, once the files of <paramref name="moves"/> move: every Markdown of the catalog
	/// that stays, with its links to them pointing to where they go. A document that was clean (its content and its
	/// metadata as they were finalized) is sealed again with its <c>updatedAt</c> kept: rewriting links is not an edit.
	/// One with an edit not finalized keeps its integrity, and the edit stays to finalize.
	/// </summary>
	internal static void Relink(DocChange change, IReadOnlyDictionary<string, string> moves)
	{
		var documents = change.Catalog.Files.Where(f => f.Markdown is not null).ToDictionary(f => f.Markdown!, Paths);
		foreach (var markdown in change.Catalog.Markdowns())
		{
			// what moves is rewritten as it moves; what goes, goes
			if (Where(markdown, moves) is not null || change.Touches(markdown))
				continue;
			var before = change.Text(markdown)!;
			var after = Rewrite(before, markdown, markdown, moves);
			if (after == before)
				continue;
			var file = documents.GetValueOrDefault(markdown);
			change.Set(markdown, after, document: file?.Sidecar is not null);
			if (file?.Sidecar is null)
				continue;
			// a sidecar the change writes already (a register that gets entries): sealed again as the change leaves it
			if (change.Touches(file.Sidecar))
			{
				using var json = DocSidecar.Parse(change.Text(file.Sidecar)!);
				change.Set(file.Sidecar, DocSidecar.From(json.RootElement).Seal(DocIntegrity.Content(after)).ToJson());
			}
			else if (Resealed(file, after) is { } sidecar)
				change.Set(file.Sidecar, sidecar);
		}
	}

	/// <summary>The sidecar of <paramref name="file"/> sealed again for <paramref name="text"/>, its <c>updatedAt</c> kept; null if it was not clean.</summary>
	internal static string? Resealed(DocFile file, string text)
	{
		if (DocChecker.Json(file.Sidecar!) is not { } json || DocsSchemas.Validate(json).Count > 0)
			return null;
		var sidecar = DocSidecar.From(json);
		try
		{
			if (sidecar.Integrity!.Content != DocIntegrity.Content(FxFiles.ReadAllBytes(file.Markdown!)) || DocIntegrity.Metadata(json) != sidecar.Integrity.Metadata)
				return null;
		}
		catch (Exception ex) when (ex is DecoderFallbackException or JcsException)
		{
			return null;
		}
		return sidecar.Seal(DocIntegrity.Content(text)).ToJson();
	}

	/// <summary>Whether <paramref name="text"/>, the Markdown at <paramref name="at"/>, links to any of <paramref name="paths"/>; the links that do.</summary>
	public static IReadOnlyList<DocLink> To(string text, string at, IReadOnlyCollection<string> paths)
	{
		var folder = Path.GetDirectoryName(at)!;
		var set = new HashSet<string>(paths, Paths);
		return DocMarkdown.LinkSpans(text)
			.Where(l => DocMarkdown.File(l.Target) is { } file && set.Contains(Path.GetFullPath(Path.Combine(folder, file))))
			.ToList();
	}
}
