using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Workspace;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Styling;

namespace Fuxion.Tools.Cli.Repo;

/// <summary><c>fx repo list|status|mount|unmount|pull</c> (design §10.2): the repositories of the workspace.</summary>
public static class RepoCommand
{
	public static Command Create(GlobalOptions global, string currentDirectory)
	{
		var command = new Command("repo", Texts.Get("repo"));

		var list = new Command("list", Texts.Get("repo.list"));
		list.SetAction((result, cancellationToken) => Run(result, global, currentDirectory, (console, token) => List(console, withStatus: false, all: true, token), cancellationToken));
		command.Add(list);

		var all = new Option<bool>("--all") { Description = Texts.Get("repo.status.all") };
		var status = new Command("status", Texts.Get("repo.status")) { all };
		status.SetAction((result, cancellationToken) => Run(result, global, currentDirectory, (console, token) => List(console, withStatus: true, result.GetValue(all), token), cancellationToken));
		command.Add(status);

		foreach (var mounted in new[] { true, false })
		{
			var names = new Argument<string[]>("names") { Description = Texts.Get("repo.names"), Arity = ArgumentArity.OneOrMore };
			var mount = new Command(mounted ? "mount" : "unmount", Texts.Get(mounted ? "repo.mount" : "repo.unmount"))
			{
				names
			};
			mount.SetAction(result => Mount(Console(result, global, currentDirectory), result.GetValue(names) ?? [], mounted));
			command.Add(mount);
		}

		var only = new Option<string[]>("--repo") { Description = Texts.Get("repo.only"), AllowMultipleArgumentsPerToken = true };
		var pull = new Command("pull", Texts.Get("repo.pull")) { only };
		pull.SetAction((result, cancellationToken) => Run(result, global, currentDirectory, (console, token) => Pull(console, result.GetValue(only) ?? [], token), cancellationToken));
		command.Add(pull);
		return command;
	}

	static FxConsole Console(ParseResult result, GlobalOptions global, string currentDirectory)
		=> new(result.InvocationConfiguration.Output, result.InvocationConfiguration.Error, global.Bind(result, currentDirectory));

	/// <summary>A command that can take a while (git on every repository): Ctrl+C cancels it.</summary>
	static Task<int> Run(ParseResult result, GlobalOptions global, string currentDirectory, Func<FxConsole, CancellationToken, int> command,
		CancellationToken cancellationToken)
	{
		var console = Console(result, global, currentDirectory);
		return Task.FromResult(console.Run(token => command(console, token), cancellationToken));
	}

	/// <summary>The manifest, or the error written as the command's document.</summary>
	static WorkspaceManifest? Manifest(FxConsole console, Action<IReadOnlyList<FxDiagnostic>> writeJson)
	{
		var (manifest, error) = WorkspaceModule.Find(console.Settings.Directory);
		if (manifest is not null)
			return manifest;
		FxDiagnostic[] errors = [error!];
		if (console.Settings.IsMachine)
			writeJson(errors);
		console.Report(errors);
		return null;
	}

	/// <summary>A repository that asks for attention: not cloned, with changes, ahead or behind, without a branch with an upstream.</summary>
	static bool NeedsAttention(RepositoryState r)
		=> !r.Present || r.Status is null or { Changes: > 0 } or { Ahead: > 0 } or { Behind: > 0 } or { Detached: true } or { Upstream: null };

	static Table Table(FxConsole console, params string[] headers)
	{
		var table = new Table().Headers(headers.Select(h => (Visual)new TextBlock(h)).ToArray());
		// without decoration where there are no colours (a pipe, an agent, --plain): columns, as gh or kubectl
		if (!console.Capabilities.CanColor)
			table.Style(TableStyle.Default with { ShowOuterBorder = false, ShowVerticalLines = false, ShowHeaderSeparator = false, CellPadding = new(0, 0, 2, 0) });
		return table;
	}

	static Visual Cell(FxConsole console, string text, string? color = null)
	{
		if (color is null || !console.Capabilities.CanColor)
			return new TextBlock(text);
		// a string: the interpolated overload of Markup would escape the holes again
		string markup = $"[{color}]{FxConsole.Escape(text)}[/]";
		return new Markup(markup) { Wrap = false };
	}

	static int List(FxConsole console, bool withStatus, bool all, CancellationToken cancellationToken)
	{
		var schema = withStatus ? "fx-repo-status/1" : "fx-repo-list/1";
		void Json(IReadOnlyList<JsonRepository> repositories, IReadOnlyList<FxDiagnostic> diagnostics)
			=> console.WriteJson(new RepositoriesDocument(schema, FxConsole.Ok(diagnostics), repositories, JsonDiagnostic.From(diagnostics)), c => c.RepositoriesDocument);
		if (Manifest(console, errors => Json([], errors)) is not { } manifest)
			return 1;
		var repositories = console.WithProgress((progress, cancel) => WorkspaceModule.Repositories(manifest, withStatus, progress, cancel), cancellationToken)
			.Where(r => !withStatus || r.Mounted)
			.ToList();
		if (console.Settings.IsMachine)
		{
			Json(repositories.Select(r => new JsonRepository(
				r.Repository.Name,
				r.Repository.Path,
				r.Repository.Mount == MountPolicy.Mandatory ? "mandatory" : "manual",
				r.Mounted,
				r.Present,
				r.Status?.Branch,
				r.Status?.Detached,
				r.Status?.Upstream,
				r.Status?.Ahead,
				r.Status?.Behind,
				r.Status?.Changes)).ToList(), []);
			return 0;
		}

		if (withStatus)
		{
			// only what asks for attention, unless --all (plan O, decision 8)
			var shown = all ? repositories : repositories.Where(NeedsAttention).ToList();
			if (shown.Count > 0)
			{
				var table = Table(console, Texts.Get("repo.column.repository"), Texts.Get("repo.column.branch"), Texts.Get("repo.column.ahead"),
					Texts.Get("repo.column.behind"), Texts.Get("repo.column.changes"));
				foreach (var r in shown)
					table.AddRow(
						Cell(console, r.Repository.Name),
						Cell(console, r.Status?.Branch ?? Texts.Get(r.Present ? "repo.detached" : "repo.not-cloned"), r.Present ? null : "red"),
						Cell(console, r.Status?.Ahead.ToString() ?? "", r.Status?.Ahead > 0 ? "yellow" : null),
						Cell(console, r.Status?.Behind.ToString() ?? "", r.Status?.Behind > 0 ? "yellow" : null),
						Cell(console, r.Status?.Changes.ToString() ?? "", r.Status?.Changes > 0 ? "yellow" : null));
				console.Write(table);
			}
			var clean = repositories.Count - repositories.Count(NeedsAttention);
			if (!all)
				console.Line(clean == repositories.Count
					? $"[green]✓[/] {FxConsole.Escape(Texts.Get("repo.all-clean", repositories.Count))}"
					: $"[gray]{FxConsole.Escape(Texts.Get("repo.some-clean", clean, repositories.Count))}[/]");
			return 0;
		}

		var repoTable = Table(console, Texts.Get("repo.column.repository"), Texts.Get("repo.column.path"), Texts.Get("repo.column.mount"),
			Texts.Get("repo.column.mounted"), Texts.Get("repo.column.cloned"));
		foreach (var r in repositories)
			repoTable.AddRow(
				Cell(console, r.Repository.Name),
				Cell(console, r.Repository.Path),
				Cell(console, r.Repository.Mount == MountPolicy.Mandatory ? "mandatory" : "manual"),
				Cell(console, Texts.Get(r.Mounted ? "yes" : "no"), r.Mounted ? "green" : "gray"),
				Cell(console, Texts.Get(r.Present ? "yes" : "no"), r.Present ? "green" : r.Mounted ? "red" : "gray"));
		console.Write(repoTable);
		return 0;
	}

	static int Mount(FxConsole console, IReadOnlyList<string> names, bool mounted)
	{
		const string schema = "fx-repo-mount/1";
		void Json(IReadOnlyList<FxDiagnostic> diagnostics)
			=> console.WriteJson(new MountDocument(schema, FxConsole.Ok(diagnostics), JsonDiagnostic.From(diagnostics)), c => c.MountDocument);
		if (Manifest(console, Json) is not { } manifest)
			return 1;
		var diagnostics = WorkspaceModule.SetMount(manifest, names, mounted);
		if (console.Settings.IsMachine)
			Json(diagnostics);
		else if (diagnostics.Count == 0)
			console.Line($"[green]✓[/] {FxConsole.Escape(Texts.Get(mounted ? "repo.mounted" : "repo.unmounted", string.Join(", ", names)))}");
		return console.Report(diagnostics);
	}

	static int Pull(FxConsole console, IReadOnlyCollection<string> only, CancellationToken cancellationToken)
	{
		const string schema = "fx-repo-pull/1";
		void Json(IReadOnlyList<RepositoryAction> actions, IReadOnlyList<FxDiagnostic> diagnostics)
			=> console.WriteJson(new PullDocument(schema, FxConsole.Ok(diagnostics),
				actions.Select(a => new JsonRepositoryAction(a.Repository, a.Action, a.Detail?.English)).ToList(), JsonDiagnostic.From(diagnostics)), c => c.PullDocument);
		if (Manifest(console, errors => Json([], errors)) is not { } manifest)
			return 1;
		var result = console.WithProgress((progress, cancel) => WorkspaceModule.Pull(manifest, only, progress, cancel), cancellationToken);
		if (console.Settings.IsMachine)
			Json(result.Actions, result.Diagnostics);
		else
			foreach (var a in result.Actions)
			{
				var color = a.Action switch { "pulled" => "green", "failed" => "red", _ => "gray" };
				console.Line($"[{color}]{FxConsole.Escape(Texts.Get($"action.{a.Action}")),-8}[/] {FxConsole.Escape(a.Repository)}{(a.Detail is null ? "" : $" [gray]({FxConsole.Escape(a.Detail.ToString())})[/]")}");
			}
		return console.Report(result.Diagnostics);
	}
}
