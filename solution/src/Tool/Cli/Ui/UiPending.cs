using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Mounts;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Text;

namespace Fuxion.Tool.Cli.Ui;

/// <summary>An edit waiting to be applied: the command of the CLI that does the same (decision 10) and how to make it again.</summary>
public sealed record UiEdit(string Command, Func<IReadOnlyList<ManifestChange>> Make);

/// <summary>
/// The changes of <c>fx ui</c> that are not written yet (plan O, O4.3b; decision 14.9). Each edit is made over the
/// ones before it, through the read layer of the core (<see cref="FxFiles"/>), so it reads what they left; it is kept
/// if it is valid and changes something. <see cref="Overlay"/> puts them in place of the disk, to tell what
/// <c>fx sync</c> would do with them; <see cref="Apply"/> writes them, unless a file changed on disk meanwhile.
/// </summary>
public sealed class UiPending
{
	public const string ChangedOnDisk = "ui.changed-on-disk";

	readonly List<UiEdit> _edits = [];
	// full path → its text on disk when it was first edited (null: not there), and its last change (what is written)
	readonly Dictionary<string, (string? Original, ManifestChange Last)> _files = new(StringComparer.OrdinalIgnoreCase);

	public IReadOnlyList<UiEdit> Edits => _edits;

	public bool Any => _edits.Count > 0;

	/// <summary>The pending texts in place of their files, for this flow of work, until it is disposed.</summary>
	public IDisposable Overlay() => FxFiles.Overlay(_files.Select(f => KeyValuePair.Create(f.Key, f.Value.Last.After.Text)));

	/// <summary>Makes <paramref name="edit"/> over the pending ones and keeps it if it is valid and changes something; its changes, with what they found.</summary>
	public IReadOnlyList<ManifestChange> Add(UiEdit edit)
	{
		IReadOnlyList<ManifestChange> changes;
		using (Overlay())
			changes = edit.Make();
		if (!changes.All(c => c.Ok) || !changes.Any(c => c.Changed))
			return changes;
		foreach (var change in changes.Where(c => c.Changed))
		{
			var full = Full(change);
			var original = _files.TryGetValue(full, out var known) ? known.Original : File.Exists(full) ? File.ReadAllText(full) : null;
			_files[full] = (original, change);
		}
		_edits.Add(edit);
		return changes;
	}

	/// <summary>Where a change goes: its file, from its root (the editor knows it; the change keeps it relative).</summary>
	static string Full(ManifestChange change) => FxLayout.Full(change.Root, change.File);

	/// <summary>Takes back the last edit: the others are made again, from the disk.</summary>
	public void Undo()
	{
		if (_edits.Count == 0)
			return;
		var keep = _edits.Take(_edits.Count - 1).ToList();
		Discard();
		foreach (var edit in keep)
			Add(edit);
	}

	public void Discard()
	{
		_edits.Clear();
		_files.Clear();
	}

	/// <summary>Each file that changes, from <paramref name="from"/>, and its diff from what is on disk.</summary>
	public IReadOnlyList<(string File, IReadOnlyList<DiffHunk> Diff)> Diffs(string from)
		=> _files.Select(f => (FxLayout.Relative(from, f.Key), LineDiff.Compute(f.Value.Original ?? "", f.Value.Last.After.Text))).ToList();

	/// <summary>
	/// Writes the pending changes, all or none: none if a file changed on disk since it was first edited here (it would
	/// lose that change). Then nothing is pending.
	/// </summary>
	public IReadOnlyList<FxDiagnostic> Apply()
	{
		var changed = _files.Where(f => (File.Exists(f.Key) ? File.ReadAllText(f.Key) : null) != f.Value.Original).Select(f => f.Key).ToList();
		if (changed.Count > 0)
			return changed.Select(f => FxDiagnostic.Error(ChangedOnDisk, FxText.Plain(Texts.Get(ChangedOnDisk, f)))).ToList();
		foreach (var (_, (_, last)) in _files)
			ManifestEditor.Write(last.Root, last);
		Discard();
		return [];
	}
}
