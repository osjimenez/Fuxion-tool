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

/// <summary>
/// A change of a catalog (the SPEC of the module, 14): every file it writes, worked out before writing any; checked as
/// the catalog would be with it, and written all at once at the end, or nothing. If a file changed on disk since it was
/// read, nothing is written either (<see cref="ChangedOnDisk"/>).
/// </summary>
public sealed class DocChange(DocCatalog catalog, Func<string, string> display)
{
	public const string ChangedOnDisk = "docs.changed-on-disk";

	/// <summary>What it writes, by full path: what the file had (null: it did not exist) and its new text.</summary>
	readonly Dictionary<string, (byte[]? Before, string After)> _files = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

	public DocCatalog Catalog => catalog;

	public Func<string, string> Display => display;

	public List<FxDiagnostic> Diagnostics { get; } = [];

	/// <summary>The documents it creates or changes, by their path in the catalog.</summary>
	public List<string> Documents { get; } = [];

	public bool Ok => !Diagnostics.Any(d => d.Severity == FxSeverity.Error);

	/// <summary>Whether it changes any file.</summary>
	public bool Changed => _files.Values.Any(f => f.Before is null || !f.Before.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(f.After)));

	public void Error(string code, FxText message, string? file = null, int? line = null, FxFix? fix = null)
		=> Diagnostics.Add(FxDiagnostic.Error(code, message, file, line, fix: fix));

	/// <summary>The text <paramref name="full"/> will have: <paramref name="text"/>, in UTF-8 without a byte order mark.</summary>
	public void Set(string full, string text)
	{
		var before = _files.TryGetValue(full, out var known) ? known.Before : FxFiles.Exists(full) ? FxFiles.ReadAllBytes(full) : null;
		_files[full] = (before, text);
		if (DocumentOf(full) is { } path && !Documents.Contains(path))
			Documents.Add(path);
	}

	/// <summary>The document a file is part of, by the path of its Markdown in the catalog; null if it is not in it.</summary>
	string? DocumentOf(string full)
	{
		if (!catalog.Contains(full))
			return null;
		var relative = FxLayout.Relative(catalog.Folder, full);
		return relative.EndsWith(DocSidecar.Extension, StringComparison.Ordinal) ? relative[..^DocSidecar.Extension.Length] + ".md" : relative;
	}

	/// <summary>The files that change, each with its diff.</summary>
	public IReadOnlyList<DocFileChange> Files => _files
		.Where(f => f.Value.Before is null || !f.Value.Before.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(f.Value.After)))
		.OrderBy(f => f.Key, StringComparer.Ordinal)
		.Select(f => new DocFileChange(display(f.Key), LineDiff.Compute(f.Value.Before is null ? "" : Encoding.UTF8.GetString(f.Value.Before), f.Value.After)))
		.ToList();

	/// <summary>
	/// Checks the documents it touches as the catalog would be with it (the validator of the module, on the files it
	/// would write): their errors are its errors.
	/// </summary>
	public void Verify()
	{
		if (!Ok)
			return;
		using (FxFiles.Overlay(_files.Select(f => KeyValuePair.Create(f.Key, f.Value.After))))
		{
			var after = DocCatalog.Scan(catalog.Root, catalog.Config);
			var touched = after.Files.Where(f => Documents.Contains(f.Path)).ToList();
			Diagnostics.AddRange(DocChecker.Check(after, display, touched).Diagnostics.Where(d => d.Severity == FxSeverity.Error));
		}
	}

	/// <summary>
	/// Writes it, if it has no errors and no file it writes changed on disk since it was read; whether it wrote. A file
	/// it writes is UTF-8 without a byte order mark, its folder made if missing.
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
		foreach (var (path, (_, after)) in _files)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, after, new UTF8Encoding(false));
		}
		return true;
	}
}
