using System;
using System.CommandLine;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fuxion.Tool.Cli.Repo;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Worktree;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Styling;

namespace Fuxion.Tool.Cli.Worktree;

/// <summary><c>fx worktree add|mount|unmount|list|remove</c> (plan P, decision 29): a branch to work on apart, in worktrees.</summary>
public static class WorktreeCommand
{
	public static Command Create(GlobalOptions global, string currentDirectory)
	{
		var command = new Command("worktree", Texts.Get("worktree"));

		Argument<string> Branch() => new("branch") { Description = Texts.Get("worktree.branch") };
		Option<bool> Mandatory() => new("--mandatory") { Description = Texts.Get("worktree.mandatory") };

		var addBranch = Branch();
		var @in = new Option<string?>("--in") { Description = Texts.Get("worktree.in") };
		var addMandatory = Mandatory();
		var add = new Command("add", Texts.Get("worktree.add")) { addBranch, @in, addMandatory }.Describe("fx-worktree-add/1", writes: true, network: true);
		add.SetAction((result, cancellationToken) => Run(result, global, currentDirectory, "fx-worktree-add/1", (console, progress, token) =>
			WorktreeModule.Add(console.Settings.Directory, result.GetValue(addBranch)!, result.GetValue(@in), result.GetValue(addMandatory), progress, token), cancellationToken));
		command.Add(add);

		var mountBranch = Branch();
		var targets = new Argument<string[]>("targets") { Description = Texts.Get("worktree.mount.targets"), Arity = ArgumentArity.OneOrMore };
		var mountMandatory = Mandatory();
		var mount = new Command("mount", Texts.Get("worktree.mount")) { mountBranch, targets, mountMandatory }.Describe("fx-worktree-mount/1", writes: true, network: true);
		mount.SetAction((result, cancellationToken) => Run(result, global, currentDirectory, "fx-worktree-mount/1", (console, progress, token) =>
			WorktreeModule.Mount(console.Settings.Directory, result.GetValue(mountBranch)!, result.GetValue(targets) ?? [], result.GetValue(mountMandatory), progress, token),
			cancellationToken));
		command.Add(mount);

		var unmountBranch = Branch();
		var repositories = new Argument<string[]>("repositories") { Description = Texts.Get("worktree.unmount.repositories"), Arity = ArgumentArity.OneOrMore };
		var unmount = new Command("unmount", Texts.Get("worktree.unmount")) { unmountBranch, repositories }.Describe("fx-worktree-unmount/1", writes: true);
		unmount.SetAction((result, cancellationToken) => Run(result, global, currentDirectory, "fx-worktree-unmount/1", (console, progress, token) =>
			WorktreeModule.Unmount(console.Settings.Directory, result.GetValue(unmountBranch)!, result.GetValue(repositories) ?? [], progress, token), cancellationToken));
		command.Add(unmount);

		var list = new Command("list", Texts.Get("worktree.list")).Describe("fx-worktree-list/1");
		list.SetAction((result, cancellationToken) =>
		{
			var console = RepoCommand.Console(result, global, currentDirectory);
			return Task.FromResult(console.Run(token => List(console, token), cancellationToken));
		});
		command.Add(list);

		var removeBranch = Branch();
		var remove = new Command("remove", Texts.Get("worktree.remove")) { removeBranch }.Describe("fx-worktree-remove/1", writes: true);
		remove.SetAction((result, cancellationToken) => Run(result, global, currentDirectory, "fx-worktree-remove/1", (console, progress, token) =>
			WorktreeModule.Remove(console.Settings.Directory, result.GetValue(removeBranch)!, progress, token), cancellationToken));
		command.Add(remove);
		return command;
	}

	/// <summary>A command that changes a worktree: git on every repository, with progress; Ctrl+C cancels it.</summary>
	static Task<int> Run(ParseResult result, GlobalOptions global, string currentDirectory, string schema, Func<FxConsole, IFxProgress?, CancellationToken, WorktreeResult> work,
		CancellationToken cancellationToken)
	{
		var console = RepoCommand.Console(result, global, currentDirectory);
		return Task.FromResult(console.Run(token =>
		{
			var outcome = console.WithProgress((progress, cancel) => work(console, progress, cancel), token);
			var diagnostics = outcome.AllDiagnostics.ToList();
			if (console.Settings.IsMachine)
			{
				console.WriteJson(new WorktreeDocument(schema, FxConsole.Ok(diagnostics), outcome.Root, outcome.Branch,
					outcome.Actions.Select(a => new JsonRepositoryAction(a.Repository, a.Action, a.Detail?.English)).ToList(),
					outcome.Sync.Select(JsonModule.From).ToList(), JsonDiagnostic.From(diagnostics)), c => c.WorktreeDocument);
				return console.Report(diagnostics);
			}
			foreach (var a in console.NotShown(outcome.Actions))
			{
				var color = a.Action is "superseded" ? "gray" : "green";
				console.Line($"[{color}]{FxConsole.Escape(Texts.Get($"action.{a.Action}")),-10}[/] {FxConsole.Escape(a.Repository)}{(a.Detail is null ? "" : $" [gray]({FxConsole.Escape(a.Detail.ToString())})[/]")}");
			}
			if (FxConsole.Ok(diagnostics) && outcome.Root is not null)
			{
				var where = FxLayout.Relative(console.Settings.Directory, outcome.Root).TrimEnd('/');
				var line = schema == "fx-worktree-remove/1" ? Texts.Get("worktree.removed", outcome.Branch) : Texts.Get("worktree.ready", outcome.Branch, where);
				console.Line($"[green]✓[/] {FxConsole.Escape(line)}");
			}
			return console.Report(diagnostics);
		}, cancellationToken));
	}

	static int List(FxConsole console, CancellationToken cancellationToken)
	{
		var worktrees = console.WithProgress((_, _) => WorktreeModule.List(console.Settings.Directory, withStatus: true), cancellationToken);
		if (console.Settings.IsMachine)
		{
			console.WriteJson(new WorktreeListDocument("fx-worktree-list/1", true, worktrees.Select(w => new JsonWorktree(w.Card.Branch, w.Root, w.Card.From, w.Card.LeftOut,
				w.Card.Worktrees.Select(m => new JsonWorktreeMount(m.Path, m.Repository, m.CreatedBranch, w.Changes?.GetValueOrDefault(m.Path))).ToList())).ToList(), []),
				c => c.WorktreeListDocument);
			return 0;
		}
		if (worktrees.Count == 0)
		{
			console.Line($"[gray]{FxConsole.Escape(Texts.Get("worktree.none"))}[/]");
			return 0;
		}
		var table = new Table().Headers(new TextBlock(Texts.Get("worktree.column.branch")), new TextBlock(Texts.Get("repo.column.path")),
			new TextBlock(Texts.Get("worktree.column.from")), new TextBlock(Texts.Get("worktree.column.repositories")), new TextBlock(Texts.Get("repo.column.changes")));
		if (!console.Capabilities.CanColor)
			table.Style(TableStyle.Default with { ShowOuterBorder = false, ShowVerticalLines = false, ShowHeaderSeparator = false, CellPadding = new(0, 0, 2, 0) });
		foreach (var w in worktrees)
		{
			var changes = w.Changes?.Values.Sum() ?? 0;
			table.AddRow(new TextBlock(w.Card.Branch), new TextBlock(FxLayout.Relative(console.Settings.Directory, w.Root).TrimEnd('/')), new TextBlock(w.Card.From),
				new TextBlock(string.Join(", ", w.Card.Worktrees.Select(m => m.Repository))), new TextBlock(changes.ToString()));
		}
		console.Write(table);
		return 0;
	}
}
