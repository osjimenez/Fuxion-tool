using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Results;

namespace Fuxion.Tools.Core.Workspace;

/// <summary>A mounted declaration of a repository in a tree: its full name, where it goes, how high and who declares it.</summary>
public sealed record RepositoryCopy(string FullName, string FullPath, int Height, string Workspace, int Line);

/// <summary>
/// The copies of the same repository in a tree (plan P, decision 19): the declarations mounted in the workspaces on
/// disk, from the highest workspace down, and the same repository is the same <c>url</c>. The highest copy is the one
/// used, guest or not; the others are superseded, and the workspace that declares them parks them. Two copies at the
/// top, at the same height, are an error: fx cannot tell which one to use, and parks none of them. Read-only: it reads
/// manifests and the local state, and looks at what is on disk.
/// </summary>
public sealed class WorkspaceCopies
{
	public const string SameHeight = "workspace.same-height";

	/// <summary>More workspaces than this, one inside another, are not walked (a loop of junctions, say).</summary>
	const int MaxHeight = 32;

	// every declaration of the workspaces reached (mounted or not), by its full path: the repository (its url) and how high
	readonly Dictionary<string, (string Key, int Height)> _declared = new(StringComparer.OrdinalIgnoreCase);
	// the copy used of each repository: the highest mounted one
	readonly Dictionary<string, RepositoryCopy> _used = new(StringComparer.OrdinalIgnoreCase);
	readonly HashSet<string> _reached = new(StringComparer.OrdinalIgnoreCase);
	readonly List<IReadOnlyList<RepositoryCopy>> _ties = [];

	public static WorkspaceCopies None { get; } = new();

	/// <summary>
	/// The copy used instead of the declaration at <paramref name="fullPath"/>: a mounted one higher in the tree. For a
	/// declaration that is not mounted, the one that would supersede it if it were.
	/// </summary>
	public RepositoryCopy? SupersededBy(string fullPath)
		=> _declared.TryGetValue(FxLayout.Normalize(fullPath), out var declared) && _used.TryGetValue(declared.Key, out var used) && used.Height < declared.Height
			? used
			: null;

	/// <summary>
	/// The highest workspace on disk that contains <paramref name="directory"/> (itself included), as Fuxion.Tools.Sdk
	/// finds it: the root of a worktree stops the search.
	/// </summary>
	public static string? Top(string directory)
	{
		string? top = null;
		for (var dir = new DirectoryInfo(Path.GetFullPath(directory)); dir is not null; dir = dir.Parent)
		{
			if (WorkspaceManifest.IsWorkspace(dir.FullName))
				top = FxLayout.Normalize(dir.FullName);
			if (File.Exists(Path.Combine(dir.FullName, FxLayout.WorktreeFile)))
				break;
		}
		return top;
	}

	/// <summary>
	/// The copies of the tree <paramref name="directory"/> is in, from its highest workspace on disk; if that one does not
	/// reach it (nobody declares it on the way down), from the nearest workspace around it. It sees what is on disk now:
	/// a workspace surveys when it mounts, once what is above it is mounted.
	/// </summary>
	public static WorkspaceCopies Survey(string directory)
	{
		var start = FxLayout.Normalize(directory);
		if (Top(start) is not { } top)
			return None;
		var copies = Walk(top);
		if (!copies._reached.Contains(start) && WorkspaceManifest.FindRoot(start) is { } nearest && !FxLayout.SameFolder(nearest, top))
			copies = Walk(nearest);
		return copies;
	}

	/// <summary>
	/// The ties where <paramref name="workspace"/> declares the first copy, so that each one is reported once: two copies
	/// or more of a repository at the same height, the highest it is mounted at. <paramref name="manifestFile"/>: its
	/// manifest, as shown.
	/// </summary>
	public IEnumerable<FxDiagnostic> Ties(string workspace, string manifestFile)
		=> _ties.Where(t => FxLayout.SameFolder(t[0].Workspace, workspace)).Select(tie =>
		{
			var name = tie[0].FullName[(tie[0].FullName.LastIndexOf('/') + 1)..];
			return FxDiagnostic.Error(SameHeight, new(SameHeight, name, tie[0].FullName, tie[1].FullName), manifestFile, tie[0].Line, name, FxFix.Do($"{SameHeight}.fix"));
		});

	/// <summary>Level by level from <paramref name="top"/>: what a higher level mounts supersedes what a lower one does.</summary>
	static WorkspaceCopies Walk(string top)
	{
		var copies = new WorkspaceCopies();
		copies._reached.Add(top);
		var level = new List<(string Root, string FullName)> { (top, new FxNode(top).SelfIdentity().FullName) };
		for (var height = 1; level.Count > 0 && height <= MaxHeight; height++)
		{
			var found = new List<(RepositoryCopy Copy, WorkspaceRepository Repository, bool Present)>();
			foreach (var (root, fullName) in level)
			{
				WorkspaceManifest manifest;
				try
				{
					manifest = WorkspaceManifest.Load(root);
				}
				catch (WorkspaceManifestException)
				{
					continue; // its sync reports it
				}
				var state = WorkspaceState.Load(root);
				foreach (var repository in manifest.Repositories)
				{
					var path = manifest.FullPath(repository);
					var present = FxLayout.IsGitRoot(path);
					// the same repository is the same url; one without url is only itself
					copies._declared[path] = (repository.Url is { } url ? WorkspaceModule.NormalizeUrl(url) : path, height);
					if (state.IsMounted(repository, present))
						found.Add((new($"{fullName}/{repository.Name}", path, height, root, repository.Line), repository, present));
				}
			}
			var next = new List<(string Root, string FullName)>();
			foreach (var group in found.GroupBy(f => copies._declared[f.Copy.FullPath].Key, StringComparer.OrdinalIgnoreCase))
			{
				if (copies._used.ContainsKey(group.Key))
					continue; // superseded: nothing of it is walked
				copies._used[group.Key] = group.First().Copy;
				if (group.Count() > 1)
					copies._ties.Add(group.Select(g => g.Copy).ToList());
				foreach (var (copy, repository, present) in group)
				{
					copies._reached.Add(copy.FullPath);
					// a guest is used, not walked: what it mounts is its own tree's
					if (present && repository.Inherits && WorkspaceManifest.IsWorkspace(copy.FullPath))
						next.Add((copy.FullPath, copy.FullName));
				}
			}
			level = next;
		}
		return copies;
	}
}
