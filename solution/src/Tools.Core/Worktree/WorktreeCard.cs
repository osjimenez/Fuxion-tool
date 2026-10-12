using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Yaml;

namespace Fuxion.Tools.Core.Worktree;

/// <summary>A repository mounted in a worktree: where (relative to its root, '.' for the root), its name, and whether fx created its branch.</summary>
public sealed record WorktreeMount(string Path, string Repository, bool CreatedBranch);

/// <summary>
/// <c>~$worktree.yaml</c>, at the root of a worktree (plan P, decision 29): its branch, where it was created from, the
/// paths left out of it and what is mounted in it. Local, written by fx.
/// </summary>
public sealed record WorktreeCard(string Branch, string From, IReadOnlyList<string> LeftOut, IReadOnlyList<WorktreeMount> Worktrees)
{
	public const string Invalid = "worktree.invalid-card";
	public const string FileName = FxLayout.WorktreeFile;

	/// <summary>The root of the worktree that contains <paramref name="directory"/> (itself included): the folder with the card.</summary>
	public static string? FindRoot(string directory)
	{
		for (var dir = new DirectoryInfo(Path.GetFullPath(directory)); dir is not null; dir = dir.Parent)
			if (File.Exists(Path.Combine(dir.FullName, FileName)))
				return FxLayout.Normalize(dir.FullName);
		return null;
	}

	/// <summary>The card of the worktree at <paramref name="root"/>, if it has a readable one.</summary>
	public static WorktreeCard? Load(string root, ICollection<FxDiagnostic>? diagnostics = null)
	{
		var file = Path.Combine(root, FileName);
		if (!File.Exists(file))
			return null;
		var reader = new FxYamlReader(Invalid, FileName);
		WorktreeCard? card = null;
		if (reader.Load(File.ReadAllText(file)) is { } map && FxYamlReader.Scalar(map["branch"]) is { Length: > 0 } branch)
			card = new(branch, FxYamlReader.Scalar(map["from"]) ?? "", FxYamlReader.List(map["left-out"]),
				(map["worktrees"] as YamlSequence)?.Items.OfType<YamlMapping>()
				.Select(w => new WorktreeMount(FxYamlReader.Scalar(w["path"]) ?? "", FxYamlReader.Scalar(w["repository"]) ?? "", w["created-branch"] is YamlScalar { Value: "true" }))
				.Where(w => w.Path.Length > 0)
				.ToList() ?? []);
		if (card is null && !reader.HasErrors)
			reader.Error(null, "shape");
		foreach (var diagnostic in reader.Diagnostics)
			diagnostics?.Add(diagnostic);
		return card;
	}

	public void Save(string root)
		=> File.WriteAllText(Path.Combine(root, FileName), ToYaml().Replace("\n", Environment.NewLine));

	/// <summary>The file, with its header.</summary>
	public string ToYaml()
	{
		var entries = new List<(string, YamlValue)> { ("schema", FxYamlReader.Schema), ("branch", Branch), ("from", From) };
		if (LeftOut.Count > 0)
			entries.Add(("left-out", new YamlValue.Flow(LeftOut.Select(p => (YamlValue)p).ToArray())));
		entries.Add(("worktrees", new YamlValue.Block(Worktrees.Select(w => (YamlValue)new YamlValue.Map(
			("path", w.Path), ("repository", w.Repository), ("created-branch", new YamlValue.Raw(w.CreatedBranch ? "true" : "false")))).ToArray())));
		return FxHeader.Hash("fx worktree add, fx worktree mount, fx worktree unmount", "fx worktree mount, fx worktree unmount, fx worktree remove")
		       + YamlWriter.Document(new(entries.ToArray()));
	}

	/// <summary>The folder of a worktree of <paramref name="branch"/>: its name without '/' (<c>feature/x</c>: <c>feature-x</c>).</summary>
	public static string Folder(string branch) => branch.Replace('/', '-').Replace('\\', '-');
}
