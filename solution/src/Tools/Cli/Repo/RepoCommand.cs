using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Workspace;
using Spectre.Console;

namespace Fuxion.Tools.Cli.Repo;

/// <summary><c>fx repo list|status|mount|unmount|pull</c> (design §10.2): the repositories of the workspace.</summary>
public static class RepoCommand
{
	public static Command Create(GlobalOptions global, string currentDirectory)
	{
		var command = new Command("repo", Texts.Get("repo"));

		var list = new Command("list", Texts.Get("repo.list"));
		list.SetAction(result => List(Console(result, global, currentDirectory), withStatus: false));
		command.Add(list);

		var status = new Command("status", Texts.Get("repo.status"));
		status.SetAction(result => List(Console(result, global, currentDirectory), withStatus: true));
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
		pull.SetAction(result => Pull(Console(result, global, currentDirectory), result.GetValue(only) ?? []));
		command.Add(pull);
		return command;
	}

	static FxConsole Console(ParseResult result, GlobalOptions global, string currentDirectory)
		=> new(result.InvocationConfiguration.Output, result.InvocationConfiguration.Error, global.Bind(result, currentDirectory));

	/// <summary>The manifest, or the error written as the command's document.</summary>
	static WorkspaceManifest? Manifest(FxConsole console, System.Action<IReadOnlyList<FxDiagnostic>> writeJson)
	{
		var (manifest, error) = WorkspaceModule.Find(console.Settings.Directory);
		if (manifest is not null)
			return manifest;
		FxDiagnostic[] errors = [error!];
		if (console.Settings.Output == OutputFormat.Json)
			writeJson(errors);
		console.Report(errors);
		return null;
	}

	static int List(FxConsole console, bool withStatus)
	{
		var schema = withStatus ? "fx-repo-status/1" : "fx-repo-list/1";
		void Json(IReadOnlyList<JsonRepository> repositories, IReadOnlyList<FxDiagnostic> diagnostics)
			=> console.WriteJson(new RepositoriesDocument(schema, FxConsole.Ok(diagnostics), repositories, JsonDiagnostic.From(diagnostics)),
				FxConsole.JsonContext.RepositoriesDocument);
		if (Manifest(console, errors => Json([], errors)) is not { } manifest)
			return 1;
		var repositories = WorkspaceModule.Repositories(manifest, withStatus, console.Progress).Where(r => !withStatus || r.Mounted).ToList();
		if (console.Settings.Output == OutputFormat.Json)
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

		var table = new Table();
		if (console.Settings.Plain)
			table.Border(TableBorder.None);
		if (withStatus)
		{
			table.AddColumns(Texts.Get("repo.column.repository"), Texts.Get("repo.column.branch"), Texts.Get("repo.column.ahead"),
				Texts.Get("repo.column.behind"), Texts.Get("repo.column.changes"));
			foreach (var r in repositories)
				table.AddRow(r.Repository.Name, r.Status?.Branch ?? Texts.Get(r.Present ? "repo.detached" : "repo.not-cloned"),
					r.Status?.Ahead.ToString() ?? "", r.Status?.Behind.ToString() ?? "", r.Status?.Changes.ToString() ?? "");
		}
		else
		{
			table.AddColumns(Texts.Get("repo.column.repository"), Texts.Get("repo.column.path"), Texts.Get("repo.column.mount"),
				Texts.Get("repo.column.mounted"), Texts.Get("repo.column.cloned"));
			foreach (var r in repositories)
				table.AddRow(r.Repository.Name, r.Repository.Path, r.Repository.Mount == MountPolicy.Mandatory ? "mandatory" : "manual",
					Texts.Get(r.Mounted ? "yes" : "no"), Texts.Get(r.Present ? "yes" : "no"));
		}
		console.Out.Write(table);
		return 0;
	}

	static int Mount(FxConsole console, IReadOnlyList<string> names, bool mounted)
	{
		const string schema = "fx-repo-mount/1";
		void Json(IReadOnlyList<FxDiagnostic> diagnostics)
			=> console.WriteJson(new MountDocument(schema, FxConsole.Ok(diagnostics), JsonDiagnostic.From(diagnostics)), FxConsole.JsonContext.MountDocument);
		if (Manifest(console, Json) is not { } manifest)
			return 1;
		var diagnostics = WorkspaceModule.SetMount(manifest, names, mounted);
		if (console.Settings.Output == OutputFormat.Json)
			Json(diagnostics);
		else if (diagnostics.Count == 0)
			console.Out.WriteLine(Texts.Get(mounted ? "repo.mounted" : "repo.unmounted", string.Join(", ", names)));
		return console.Report(diagnostics);
	}

	static int Pull(FxConsole console, IReadOnlyCollection<string> only)
	{
		const string schema = "fx-repo-pull/1";
		void Json(IReadOnlyList<RepositoryAction> actions, IReadOnlyList<FxDiagnostic> diagnostics)
			=> console.WriteJson(new PullDocument(schema, FxConsole.Ok(diagnostics),
					actions.Select(a => new JsonRepositoryAction(a.Repository, a.Action, a.Detail?.English)).ToList(), JsonDiagnostic.From(diagnostics)),
				FxConsole.JsonContext.PullDocument);
		if (Manifest(console, errors => Json([], errors)) is not { } manifest)
			return 1;
		var result = WorkspaceModule.Pull(manifest, only, console.Progress);
		if (console.Settings.Output == OutputFormat.Json)
			Json(result.Actions, result.Diagnostics);
		else
			foreach (var a in result.Actions)
				console.Out.WriteLine($"{Texts.Get($"action.{a.Action}"),-8} {a.Repository}{(a.Detail is null ? "" : $" ({a.Detail})")}");
		return console.Report(result.Diagnostics);
	}
}
