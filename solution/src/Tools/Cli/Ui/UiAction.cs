using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Fuxion.Tools.Cli.Describe;
using Fuxion.Tools.Core.Explore;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Sync;
using Fuxion.Tools.Core.Workspace;
using Fuxion.Tools.Core.Worktree;

namespace Fuxion.Tools.Cli.Ui;

/// <summary>What an action did, or would do: a line per repository and per file that changes, and its diagnostics.</summary>
public sealed record UiOutcome(IReadOnlyList<string> Lines, IReadOnlyList<FxDiagnostic> Diagnostics)
{
	public bool Ok => Diagnostics.All(d => d.Severity != FxSeverity.Error);

	static string Esc(string? text) => FxConsole.Escape(text);

	/// <summary>Of <c>fx sync</c> or <c>fx repo …</c>: the actions on the repositories, the files that change (or would), and the diagnostics.</summary>
	public static UiOutcome From(IEnumerable<FxModuleResult> results)
	{
		var lines = new List<string>();
		var diagnostics = new List<FxDiagnostic>();
		foreach (var result in results)
		{
			lines.AddRange(result.Actions.Select(Line));
			lines.AddRange(result.Files.Where(f => f.Status != SyncFileStatus.Unchanged).Select(f =>
				$"[{(f.Status == SyncFileStatus.Refused ? "error" : "warning")}]{Esc(Texts.Get($"file.{JsonModule.FileStatus(f.Status)}"))}[/] {Esc(f.Path)}"));
			diagnostics.AddRange(result.Diagnostics.Where(d => d.Severity != FxSeverity.Info));
		}
		return new(lines, diagnostics);
	}

	/// <summary>Of <c>fx worktree mount|unmount</c>: its actions, and what its sync did.</summary>
	public static UiOutcome From(WorktreeResult result)
	{
		var sync = From(result.Sync);
		return new(result.Actions.Select(Line).Concat(sync.Lines).ToList(), result.Diagnostics.Where(d => d.Severity != FxSeverity.Info).Concat(sync.Diagnostics).ToList());
	}

	static string Line(RepositoryAction a)
		=> $"[{(a.Action.EndsWith("failed", StringComparison.Ordinal) || a.Action == "missing" ? "error" : a.Action is "superseded" or "unmounted" or "skipped" ? "muted" : "success")}]"
		   + $"{Esc(Texts.Get($"action.{a.Action}"))}[/] {Esc(a.Repository)}{(a.Detail is null ? "" : $" [muted]({Esc(a.Detail.ToString())})[/]")}";
}

/// <summary>
/// Something <c>fx ui</c> does on a node (plan O, O4.2): what it is, the command of the CLI that does the same (decision
/// 14.10), what it does in a sentence, and the work itself, on the core. <see cref="Preview"/>, what it would do, if
/// the command can tell (<c>--dry-run</c>).
/// </summary>
public sealed record UiAction(string Label, string Command, string Description, Func<IFxProgress, CancellationToken, UiOutcome> Run,
	Func<CancellationToken, UiOutcome>? Preview = null)
{
	/// <summary>An action that edits (O4.3b): it opens its form, or goes to the pending changes, instead of running.</summary>
	public Action? Open { get; init; }

	public override string ToString() => $"{Label} · {Command}";

	/// <summary>The option <c>--root</c> of a command run from <paramref name="start"/> on the root at <paramref name="path"/>; none if it is the same.</summary>
	public static string RootOption(string start, string path) => FxLayout.Relative(start, path) is var relative && relative == "." ? "" : $" --root {relative}";

	/// <summary>
	/// The actions of <paramref name="node"/>, the main one first: on what is not on disk, mounting it; on a root,
	/// syncing it, pulling and unmounting; anywhere else, syncing the root it is in (14.11). In a worktree (its
	/// <paramref name="card"/>), mounting and unmounting are the worktree's. The commands, from <paramref name="start"/>.
	/// </summary>
	public static IReadOnlyList<UiAction> For(FxTreeNode node, string start, WorktreeCard? card)
	{
		var actions = new List<UiAction>();
		var workspace = WorkspaceOf(node);
		string Root(string path) => RootOption(start, path);
		if (node.Kind is FxTreeKind.Declared or FxTreeKind.Parked)
		{
			if (node.SupersededBy is not null || workspace is null)
				return actions;
			if (card is not null)
				actions.Add(WorktreeMount(node, card));
			else if (node.Kind == FxTreeKind.Parked || !node.Mounted)
				actions.Add(Mount(node, workspace, mounted: true, Root(workspace.Path)));
			// mounted and not cloned: sync clones it
			actions.Add(Sync(workspace, Root(workspace.Path)));
			return actions;
		}
		var root = node;
		while (root.Kind is not (FxTreeKind.Workspace or FxTreeKind.Repository) && root.Parent is not null)
			root = root.Parent;
		if (root.Kind is not (FxTreeKind.Workspace or FxTreeKind.Repository))
			return actions;
		actions.Add(Sync(root, Root(root.Path)));
		if (root.Kind == FxTreeKind.Workspace)
			actions.Add(Pull(root, null, Root(root.Path)));
		else if (workspace is not null && root.Declaration is not null)
			actions.Add(Pull(workspace, root, Root(workspace.Path)));
		// unmounting: what its workspace declares, if it is not mandatory (in a worktree, nothing is)
		if (root == node && workspace is not null && root.Declaration is { } declaration && root.SupersededBy is null)
			if (card is not null)
				actions.Add(WorktreeUnmount(root, card));
			else if (declaration.Mount == MountPolicy.Manual)
				actions.Add(Mount(root, workspace, mounted: false, Root(workspace.Path)));
		return actions;
	}

	static FxTreeNode? WorkspaceOf(FxTreeNode node)
	{
		for (var at = node.Parent; at is not null; at = at.Parent)
			if (at.Kind == FxTreeKind.Workspace)
				return at;
		return null;
	}

	static UiAction Sync(FxTreeNode root, string option)
		=> new(Texts.Get("ui.action.sync"), $"fx sync{option}", Texts.Get("ui.action.sync.description", root.Identity?.FullName ?? root.Name),
			(progress, token) => UiOutcome.From(SyncEngine.Sync(root.Path, new(Skill: FxSkill.Text), progress, token)),
			token => UiOutcome.From(SyncEngine.Sync(root.Path, new(DryRun: true, Skill: FxSkill.Text), cancellationToken: token)));

	static UiAction Pull(FxTreeNode workspace, FxTreeNode? repository, string option)
	{
		var name = repository?.Declaration?.Name;
		return new(Texts.Get("ui.action.pull"), $"fx repo pull{(name is null ? "" : $" --repo {name}")}{option}", Texts.Get("ui.action.pull.description"),
			(progress, token) => UiOutcome.From([WorkspaceModule.Pull(WorkspaceManifest.Load(workspace.Path), name is null ? [] : [name], progress, token)]));
	}

	static UiAction Mount(FxTreeNode node, FxTreeNode workspace, bool mounted, string option)
	{
		var name = node.Declaration!.Name;
		var verb = mounted ? "mount" : "unmount";
		var description = mounted
			? Texts.Get(node.Kind == FxTreeKind.Parked ? "ui.action.unpark.description" : "ui.action.mount.description")
			: Texts.Get("ui.action.unmount.description", $"{FxLayout.Parking}/{FxLayout.Relative(workspace.Path, node.Path)}");
		return new(Texts.Get($"ui.action.{verb}"), $"fx repo {verb} {name}{option}", description,
			(_, _) => UiOutcome.From([WorkspaceModule.SetMount(WorkspaceManifest.Load(workspace.Path), [name], mounted)]));
	}

	static UiAction WorktreeMount(FxTreeNode node, WorktreeCard card)
	{
		var path = WorktreePath(node);
		return new(Texts.Get("ui.action.mount"), $"fx worktree mount {card.Branch} {path}", Texts.Get("ui.action.worktree-mount.description", card.Branch),
			(progress, token) => UiOutcome.From(WorktreeModule.Mount(Top(node).Path, card.Branch, [path], false, progress, token)));
	}

	static UiAction WorktreeUnmount(FxTreeNode node, WorktreeCard card)
	{
		var path = WorktreePath(node);
		return new(Texts.Get("ui.action.unmount"), $"fx worktree unmount {card.Branch} {path}", Texts.Get("ui.action.worktree-unmount.description"),
			(progress, token) => UiOutcome.From(WorktreeModule.Unmount(Top(node).Path, card.Branch, [path], progress, token)));
	}

	/// <summary>The root of the tree of <paramref name="node"/>: in a worktree, its root (it is on disk, the node may not be).</summary>
	static FxTreeNode Top(FxTreeNode node)
	{
		while (node.Parent is not null)
			node = node.Parent;
		return node;
	}

	/// <summary>Where a node is in its worktree: how <c>fx worktree mount|unmount</c> names it.</summary>
	static string WorktreePath(FxTreeNode node) => FxLayout.Relative(Top(node).Path, node.Path);
}
