using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Text;

namespace Fuxion.Tool.Core.Docs;

/// <summary>A file a change writes, and what it does to it.</summary>
/// <param name="File">Its path, as the change shows it.</param>
public sealed record DocFileChange(string File, IReadOnlyList<DiffHunk> Diff);

/// <summary>A file a change moves: from where, to where (as the change shows them).</summary>
public sealed record DocFileMove(string From, string To);

/// <summary>
/// A change of a catalog (the SPEC of the module, 14): every file it writes, moves or removes, worked out before
/// touching any; checked as the catalog would be with it, and done all at once at the end, or nothing. If a file changed
/// on disk since it was read, nothing is done either (<see cref="ChangedOnDisk"/>).
/// </summary>
public sealed class DocChange(DocCatalog catalog, Func<string, string> display)
{
	public const string ChangedOnDisk = "docs.changed-on-disk";

	static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	/// <summary>What it touches, by full path: what the file had (null: it did not exist) and what it will have (null: removed).</summary>
	readonly Dictionary<string, (byte[]? Before, byte[]? After)> _files = new(Paths);

	/// <summary>The files that move: where each goes, from where.</summary>
	readonly Dictionary<string, string> _moves = new(Paths);

	public DocCatalog Catalog => catalog;

	public Func<string, string> Display => display;

	public List<FxDiagnostic> Diagnostics { get; } = [];

	/// <summary>The documents it creates or changes, by their path in the catalog.</summary>
	public List<string> Documents { get; } = [];

	public bool Ok => !Diagnostics.Any(d => d.Severity == FxSeverity.Error);

	static bool Same(byte[]? before, byte[]? after) => before is null ? after is null : after is not null && before.AsSpan().SequenceEqual(after);

	/// <summary>Text in UTF-8, for its diff; null if it is not.</summary>
	static string? Decoded(byte[]? bytes)
	{
		try
		{
			return bytes is null ? "" : DocIntegrity.Utf8.GetString(bytes);
		}
		catch (DecoderFallbackException)
		{
			return null;
		}
	}

	/// <summary>Whether it changes any file.</summary>
	public bool Changed => _files.Values.Any(f => !Same(f.Before, f.After));

	public void Error(string code, FxText message, string? file = null, int? line = null, FxFix? fix = null)
		=> Diagnostics.Add(FxDiagnostic.Error(code, message, file, line, fix: fix));

	byte[]? Before(string full) => _files.TryGetValue(full, out var known) ? known.Before : FxFiles.Exists(full) ? FxFiles.ReadAllBytes(full) : null;

	/// <summary>
	/// The text <paramref name="full"/> will have: <paramref name="text"/>, in UTF-8 without a byte order mark. Unless it
	/// is not a <paramref name="document"/> (an annex), a file of the catalog counts as one of its documents.
	/// </summary>
	public void Set(string full, string text, bool document = true) => SetBytes(full, new UTF8Encoding(false).GetBytes(text), document);

	/// <summary>The bytes <paramref name="full"/> will have.</summary>
	public void SetBytes(string full, byte[] content, bool document = true)
	{
		_files[full] = (Before(full), content);
		if (document && DocumentOf(full) is { } path && !Documents.Contains(path))
			Documents.Add(path);
	}

	/// <summary>Removes <paramref name="full"/>.</summary>
	public void Delete(string full) => _files[full] = (Before(full), null);

	/// <summary><paramref name="from"/> goes to <paramref name="to"/>, with <paramref name="text"/>.</summary>
	public void Move(string from, string to, string text, bool document = true) => MoveBytes(from, to, new UTF8Encoding(false).GetBytes(text), document);

	/// <summary><paramref name="from"/> goes to <paramref name="to"/>, with <paramref name="content"/>.</summary>
	public void MoveBytes(string from, string to, byte[] content, bool document = true)
	{
		Delete(from);
		SetBytes(to, content, document);
		_moves[to] = from;
	}

	/// <summary>The text <paramref name="full"/> has in this change: what it writes, or what is on disk; null if it does not exist.</summary>
	public string? Text(string full)
	{
		if (_files.TryGetValue(full, out var known))
			return known.After is null ? null : Encoding.UTF8.GetString(known.After);
		return FxFiles.Exists(full) ? Encoding.UTF8.GetString(FxFiles.ReadAllBytes(full)) : null;
	}

	/// <summary>Whether this change writes or removes <paramref name="full"/>.</summary>
	public bool Touches(string full) => _files.ContainsKey(full);

	/// <summary>The document a file is part of, by the path of its Markdown in the catalog; null if it is not in it.</summary>
	string? DocumentOf(string full)
	{
		if (!catalog.Contains(full) || !(full.EndsWith(".md", StringComparison.Ordinal) || full.EndsWith(DocSidecar.Extension, StringComparison.Ordinal)))
			return null;
		var relative = FxLayout.Relative(catalog.Folder, full);
		return relative.EndsWith(DocSidecar.Extension, StringComparison.Ordinal) ? relative[..^DocSidecar.Extension.Length] + ".md" : relative;
	}

	/// <summary>The files it writes that change, each with its diff (of a file that moves, against where it was).</summary>
	public IReadOnlyList<DocFileChange> Files => _files
		.Where(f => f.Value.After is not null)
		.Select(f => (Path: f.Key, Before: _moves.TryGetValue(f.Key, out var from) ? _files[from].Before : f.Value.Before, f.Value.After))
		.Where(f => !Same(f.Before, f.After))
		.OrderBy(f => f.Path, StringComparer.Ordinal)
		.Select(f => new DocFileChange(display(f.Path), Decoded(f.Before) is { } before && Decoded(f.After) is { } after ? LineDiff.Compute(before, after) : []))
		.ToList();

	/// <summary>The files it moves.</summary>
	public IReadOnlyList<DocFileMove> Moved => _moves.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => new DocFileMove(display(m.Value), display(m.Key))).ToList();

	/// <summary>The files it removes (not those it moves).</summary>
	public IReadOnlyList<string> Removed => _files
		.Where(f => f.Value.After is null && f.Value.Before is not null && !_moves.Values.Contains(f.Key, Paths))
		.Select(f => display(f.Key))
		.Order(StringComparer.Ordinal)
		.ToList();

	/// <summary>Puts this change in place of the disk, for the flow of work, until it is disposed.</summary>
	public IDisposable Staged()
	{
		var overlay = FxFiles.Overlay([]);
		foreach (var (path, (_, after)) in _files)
			FxFiles.Stage(path, after);
		return overlay;
	}

	/// <summary>
	/// Checks the documents it touches as the catalog would be with it (the validator of the module, on the files it
	/// would write): their errors are its errors.
	/// </summary>
	public void Verify()
	{
		if (!Ok)
			return;
		using (Staged())
		{
			var after = DocCatalog.Scan(catalog.Root, catalog.Config);
			var touched = after.Files.Where(f => Documents.Contains(f.Path)).ToList();
			Diagnostics.AddRange(DocChecker.Check(after, display, touched).Diagnostics.Where(d => d.Severity == FxSeverity.Error));
		}
	}

	/// <summary>
	/// Does it, if it has no errors and no file it touches changed on disk since it was read; whether it did. A file it
	/// writes is UTF-8 without a byte order mark, its folder made if missing; a folder a removal leaves empty, in the
	/// catalog, goes too.
	/// </summary>
	public bool Write()
	{
		if (!Ok || !Changed)
			return false;
		foreach (var (path, (before, _)) in _files)
		{
			var now = File.Exists(path) ? File.ReadAllBytes(path) : null;
			if (now is null != before is null || now is not null && !now.AsSpan().SequenceEqual(before))
			{
				Error(ChangedOnDisk, new(ChangedOnDisk, display(path)));
				return false;
			}
		}
		foreach (var (path, (_, after)) in _files.Where(f => f.Value.After is null))
		{
			File.Delete(path);
			for (var folder = Path.GetDirectoryName(path); folder is not null && catalog.Contains(folder) && !Paths.Equals(Path.TrimEndingDirectorySeparator(folder),
				     Path.TrimEndingDirectorySeparator(catalog.Folder)) && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any();
			     folder = Path.GetDirectoryName(folder))
				Directory.Delete(folder);
		}
		foreach (var (path, (_, after)) in _files.Where(f => f.Value.After is not null))
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllBytes(path, after!);
		}
		return true;
	}
}
