using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fuxion.Tool.Core.Mounts;
using Fuxion.Tool.Core.Results;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Styling;

namespace Fuxion.Tool.Cli.Mounts;

/// <summary><c>fx mount list|status|park|unpark|pull</c> (design §10.2; plan spaces, decision 18): the mounts of the space.</summary>
public static class MountCommand
{
	public const string ListSchema = "fx-mount-list/1";
	public const string StatusSchema = "fx-mount-status/1";
	public const string ParkSchema = "fx-mount-park/1";
	public const string PullSchema = "fx-mount-pull/1";

	public static Command Create(GlobalOptions global, string currentDirectory)
	{
		var command = new Command("mount", Texts.Get("mount"));

		var list = new Command("list", Texts.Get("mount.list")).Describe(ListSchema);
		list.SetAction((result, cancellationToken) => Run(result, global, currentDirectory, (console, token) => List(console, withStatus: false, all: true, token), cancellationToken));
		command.Add(list);

		var all = new Option<bool>("--all") { Description = Texts.Get("mount.status.all") };
		var status = new Command("status", Texts.Get("mount.status")) { all }.Describe(StatusSchema);
		status.SetAction((result, cancellationToken) => Run(result, global, currentDirectory, (console, token) => List(console, withStatus: true, result.GetValue(all), token), cancellationToken));
		command.Add(status);

		foreach (var parked in new[] { true, false })
		{
			// optional for the parser: asked if missing (plan O, O3.3)
			var names = new Argument<string[]>("names") { Description = Texts.Get("mount.names"), Arity = ArgumentArity.ZeroOrMore };
			var park = new Command(parked ? "park" : "unpark", Texts.Get(parked ? "mount.park" : "mount.unpark"))
			{
				names
			}.Describe(ParkSchema, writes: true);
			park.SetAction(result =>
			{
				var console = Console(result, global, currentDirectory);
				return console.Run(token => Park(console, result.GetValue(names) ?? [], parked, token));
			});
			command.Add(park);
		}

		var only = new Argument<string[]>("names") { Description = Texts.Get("mount.only"), Arity = ArgumentArity.ZeroOrMore };
		var pull = new Command("pull", Texts.Get("mount.pull")) { only }.Describe(PullSchema, writes: true, network: true);
		pull.SetAction((result, cancellationToken) => Run(result, global, currentDirectory, (console, token) => Pull(console, result.GetValue(only) ?? [], token), cancellationToken));
		command.Add(pull);
		// the ones that edit mounts.yaml (plan O, O3.4)
		ManifestCommands.AddTo(command, global, currentDirectory);
		return command;
	}

	internal static FxConsole Console(ParseResult result, GlobalOptions global, string currentDirectory)
		=> new(result.InvocationConfiguration.Output, result.InvocationConfiguration.Error, global.Bind(result, currentDirectory));

	/// <summary>A command that can take a while (git on every repository): Ctrl+C cancels it.</summary>
	static Task<int> Run(ParseResult result, GlobalOptions global, string currentDirectory, Func<FxConsole, CancellationToken, int> command,
		CancellationToken cancellationToken)
	{
		var console = Console(result, global, currentDirectory);
		return Task.FromResult(console.Run(token => command(console, token), cancellationToken));
	}

	/// <summary>The manifest, or the error written as the command's document.</summary>
	internal static MountsManifest? Manifest(FxConsole console, Action<IReadOnlyList<FxDiagnostic>> writeJson)
	{
		var (manifest, error) = MountsModule.Find(console.Settings.Directory);
		if (manifest is not null)
			return manifest;
		FxDiagnostic[] errors = [error!];
		if (console.Settings.IsMachine)
			writeJson(errors);
		console.Report(errors);
		return null;
	}

	/// <summary>
	/// A repository that asks for attention: not cloned (or parked, unless a higher copy is used), with changes, ahead or
	/// behind, without a branch with an upstream.
	/// </summary>
	static bool NeedsAttention(RepositoryState r)
		=> r.SupersededBy is null && !r.Present || r.Present && r.Status is null or { Changes: > 0 } or { Ahead: > 0 } or { Behind: > 0 } or { Detached: true } or { Upstream: null };

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
		var schema = withStatus ? StatusSchema : ListSchema;
		void Json(IReadOnlyList<JsonMount> mounts, IReadOnlyList<FxDiagnostic> diagnostics)
			=> console.WriteJson(new MountsDocument(schema, FxConsole.Ok(diagnostics), mounts, JsonDiagnostic.From(diagnostics)), c => c.MountsDocument);
		if (Manifest(console, errors => Json([], errors)) is not { } manifest)
			return 1;
		var repositories = console.WithProgress((progress, cancel) => MountsModule.Repositories(manifest, withStatus, progress, cancel, MountCopies.Survey(manifest.Root)), cancellationToken)
			.Where(r => !withStatus || r.Mounted)
			.ToList();
		if (console.Settings.IsMachine)
		{
			Json(repositories.Select(r => new JsonMount(
				r.Repository.Name,
				r.Repository.Path,
				r.Repository.Required,
				MountsManifest.ModeText(r.Repository.Mode),
				r.Mounted,
				r.Present,
				r.Parked,
				r.SupersededBy?.FullName,
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
				var table = Table(console, Texts.Get("mount.column.mount"), Texts.Get("mount.column.branch"), Texts.Get("mount.column.ahead"),
					Texts.Get("mount.column.behind"), Texts.Get("mount.column.changes"));
				foreach (var r in shown)
					table.AddRow(
						Cell(console, r.Repository.Name),
						Cell(console, r.Status?.Branch ?? Texts.Get(r.Present ? "mount.detached" : Where(r)), r.Present ? null : NeedsAttention(r) ? "red" : "gray"),
						Cell(console, r.Status?.Ahead.ToString() ?? "", r.Status?.Ahead > 0 ? "yellow" : null),
						Cell(console, r.Status?.Behind.ToString() ?? "", r.Status?.Behind > 0 ? "yellow" : null),
						Cell(console, r.Status?.Changes.ToString() ?? "", r.Status?.Changes > 0 ? "yellow" : null));
				console.Write(table);
			}
			var clean = repositories.Count - repositories.Count(NeedsAttention);
			if (!all)
				console.Line(clean == repositories.Count
					? $"[green]✓[/] {FxConsole.Escape(Texts.Get("mount.all-clean", repositories.Count))}"
					: $"[gray]{FxConsole.Escape(Texts.Get("mount.some-clean", clean, repositories.Count))}[/]");
			return 0;
		}

		var repoTable = Table(console, Texts.Get("mount.column.mount"), Texts.Get("mount.column.path"), Texts.Get("mount.column.mode"),
			Texts.Get("mount.column.required"), Texts.Get("mount.column.mounted"), Texts.Get("mount.column.cloned"));
		foreach (var r in repositories)
			repoTable.AddRow(
				Cell(console, r.Repository.Name),
				Cell(console, r.Repository.Path),
				Cell(console, MountsManifest.ModeText(r.Repository.Mode), r.Repository.Mode == MountMode.Write ? null : "yellow"),
				Cell(console, Texts.Get(r.Repository.Required ? "yes" : "no")),
				Cell(console, Texts.Get(r.Mounted ? "yes" : "no"), r.Mounted ? "green" : "gray"),
				Cell(console, Texts.Get(r.Present ? "yes" : r.Parked || r.SupersededBy is not null ? Where(r) : "no"), r.Present ? "green" : NeedsAttention(r) && r.Mounted ? "red" : "gray"));
		console.Write(repoTable);
		return 0;
	}

	/// <summary>Where a mount that is not at its place is: parked (and why), or not cloned.</summary>
	static string Where(RepositoryState r) => r.SupersededBy is not null ? "mount.superseded" : r.Parked ? "mount.parked" : "mount.not-cloned";

	static int Park(FxConsole console, IReadOnlyList<string> names, bool parked, CancellationToken cancellationToken)
	{
		void Json(IReadOnlyList<RepositoryAction> actions, IReadOnlyList<FxDiagnostic> diagnostics)
			=> console.WriteJson(new ParkDocument(ParkSchema, FxConsole.Ok(diagnostics),
				actions.Select(a => new JsonRepositoryAction(a.Repository, a.Action, a.Detail?.English)).ToList(), JsonDiagnostic.From(diagnostics)), c => c.ParkDocument);
		if (Manifest(console, errors => Json([], errors)) is not { } manifest)
			return 1;
		if (names.Count == 0)
		{
			var verb = parked ? "park" : "unpark";
			if (!console.Capabilities.CanPrompt)
				return console.Missing("<names>", $"fx mount {verb} <names>");
			var candidates = MountsModule.Repositories(manifest, withStatus: false)
				.Where(r => parked ? r.Mounted && !r.Repository.Required : !r.Mounted)
				.ToList();
			if (candidates.Count == 0)
			{
				console.Line($"[gray]{FxConsole.Escape(Texts.Get(parked ? "mount.nothing-to-park" : "mount.nothing-to-unpark"))}[/]");
				return 0;
			}
			names = Ask.Many(console, Texts.Get(parked ? "mount.ask.park" : "mount.ask.unpark"), candidates, r => r.Repository.Name, cancellationToken)
				.Select(r => r.Repository.Name)
				.ToList();
			Ask.Equivalent(console, $"fx mount {verb} {string.Join(' ', names)}");
		}
		var result = MountsModule.Park(manifest, names, parked);
		if (console.Settings.IsMachine)
			Json(result.Actions, result.Diagnostics);
		else
			foreach (var a in result.Actions)
			{
				var color = a.Action is "superseded" || a.Detail is null && a.Action == "parked" ? "gray" : "green";
				console.Line($"[{color}]{FxConsole.Escape(Texts.Get($"action.{a.Action}")),-10}[/] {FxConsole.Escape(a.Repository)}{(a.Detail is null ? "" : $" [gray]({FxConsole.Escape(a.Detail.ToString())})[/]")}");
			}
		return console.Report(result.Diagnostics);
	}

	static int Pull(FxConsole console, IReadOnlyCollection<string> only, CancellationToken cancellationToken)
	{
		void Json(IReadOnlyList<RepositoryAction> actions, IReadOnlyList<FxDiagnostic> diagnostics)
			=> console.WriteJson(new PullDocument(PullSchema, FxConsole.Ok(diagnostics),
				actions.Select(a => new JsonRepositoryAction(a.Repository, a.Action, a.Detail?.English)).ToList(), JsonDiagnostic.From(diagnostics)), c => c.PullDocument);
		if (Manifest(console, errors => Json([], errors)) is not { } manifest)
			return 1;
		var result = console.WithProgress((progress, cancel) => MountsModule.Pull(manifest, only, progress, cancel), cancellationToken);
		if (console.Settings.IsMachine)
			Json(result.Actions, result.Diagnostics);
		else
			foreach (var a in console.NotShown(result.Actions))
			{
				var color = a.Action switch { "pulled" => "green", "failed" => "red", _ => "gray" };
				console.Line($"[{color}]{FxConsole.Escape(Texts.Get($"action.{a.Action}")),-8}[/] {FxConsole.Escape(a.Repository)}{(a.Detail is null ? "" : $" [gray]({FxConsole.Escape(a.Detail.ToString())})[/]")}");
			}
		return console.Report(result.Diagnostics);
	}
}
