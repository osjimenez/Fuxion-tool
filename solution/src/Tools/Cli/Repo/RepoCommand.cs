using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using Fuxion.Tools.Core.Workspace;
using Spectre.Console;

namespace Fuxion.Tools.Cli.Repo;

/// <summary><c>fx repo list|status|mount|unmount|pull</c> (design §10.2): the repositories of the workspace.</summary>
public static class RepoCommand
{
	public static Command Create(GlobalOptions global, string currentDirectory)
	{
		var command = new Command("repo", "The repositories of the workspace.");

		var list = new Command("list", "The repositories of the manifest: mount point, policy and whether they are mounted.");
		list.SetAction(result => List(Console(result, global, currentDirectory), withStatus: false));
		command.Add(list);

		var status = new Command("status", "The mounted repositories: branch, commits ahead and behind, changes.");
		status.SetAction(result => List(Console(result, global, currentDirectory), withStatus: true));
		command.Add(status);

		foreach (var mounted in new[] { true, false })
		{
			var names = new Argument<string[]>("names") { Description = "Repositories of the manifest.", Arity = ArgumentArity.OneOrMore };
			var mount = new Command(mounted ? "mount" : "unmount",
				mounted ? "Mount repositories: fx sync clones what is missing." : "Unmount repositories: fx stops managing them; nothing is deleted.")
			{
				names
			};
			mount.SetAction(result => Mount(Console(result, global, currentDirectory), result.GetValue(names) ?? [], mounted));
			command.Add(mount);
		}

		var only = new Option<string[]>("--repo") { Description = "Only these repositories.", AllowMultipleArgumentsPerToken = true };
		var pull = new Command("pull", "Fast-forward the mounted repositories that have a clean working tree.") { only };
		pull.SetAction(result => Pull(Console(result, global, currentDirectory), result.GetValue(only) ?? []));
		command.Add(pull);
		return command;
	}

	static FxConsole Console(ParseResult result, GlobalOptions global, string currentDirectory)
		=> new(result.InvocationConfiguration.Output, result.InvocationConfiguration.Error, global.Bind(result, currentDirectory));

	static WorkspaceManifest? Manifest(FxConsole console, string schema)
	{
		var (manifest, error) = WorkspaceModule.Find(console.Settings.Directory);
		if (manifest is not null)
			return manifest;
		Diagnostic[] errors = [Diagnostic.From(error!)];
		if (console.Settings.Output == OutputFormat.Json)
			console.WriteJson(schema, errors);
		console.Report(errors);
		return null;
	}

	static int List(FxConsole console, bool withStatus)
	{
		var schema = withStatus ? "fx-repo-status/1" : "fx-repo-list/1";
		if (Manifest(console, schema) is not { } manifest)
			return 1;
		var repositories = WorkspaceModule.Repositories(manifest, withStatus).Where(r => !withStatus || r.Mounted).ToList();
		if (console.Settings.Output == OutputFormat.Json)
		{
			console.WriteJson(schema, [], json =>
			{
				json.WriteStartArray("repositories");
				foreach (var r in repositories)
				{
					json.WriteStartObject();
					json.WriteString("name", r.Repository.Name);
					json.WriteString("path", r.Repository.Path);
					json.WriteString("mount", r.Repository.Mount == MountPolicy.Mandatory ? "mandatory" : "manual");
					json.WriteBoolean("mounted", r.Mounted);
					json.WriteBoolean("present", r.Present);
					if (r.Status is { } s)
					{
						json.WriteString("branch", s.Branch);
						json.WriteBoolean("detached", s.Detached);
						json.WriteString("upstream", s.Upstream);
						json.WriteNumber("ahead", s.Ahead);
						json.WriteNumber("behind", s.Behind);
						json.WriteNumber("changes", s.Changes);
					}
					json.WriteEndObject();
				}
				json.WriteEndArray();
			});
			return 0;
		}

		var table = new Table();
		if (console.Settings.Plain)
			table.Border(TableBorder.None);
		if (withStatus)
		{
			table.AddColumns("Repository", "Branch", "Ahead", "Behind", "Changes");
			foreach (var r in repositories)
				table.AddRow(r.Repository.Name, r.Status?.Branch ?? (r.Present ? "(detached)" : "(not cloned)"),
					r.Status?.Ahead.ToString() ?? "", r.Status?.Behind.ToString() ?? "", r.Status?.Changes.ToString() ?? "");
		}
		else
		{
			table.AddColumns("Repository", "Path", "Mount", "Mounted", "Cloned");
			foreach (var r in repositories)
				table.AddRow(r.Repository.Name, r.Repository.Path, r.Repository.Mount == MountPolicy.Mandatory ? "mandatory" : "manual",
					r.Mounted ? "yes" : "no", r.Present ? "yes" : "no");
		}
		console.Out.Write(table);
		return 0;
	}

	static int Mount(FxConsole console, IReadOnlyList<string> names, bool mounted)
	{
		const string schema = "fx-repo-mount/1";
		if (Manifest(console, schema) is not { } manifest)
			return 1;
		var diagnostics = WorkspaceModule.SetMount(manifest, names, mounted).Select(Diagnostic.From).ToList();
		if (console.Settings.Output == OutputFormat.Json)
			console.WriteJson(schema, diagnostics);
		else if (diagnostics.Count == 0)
			console.Out.WriteLine(mounted
				? $"Mounted: {string.Join(", ", names)}. Run fx sync to clone what is missing."
				: $"Unmounted: {string.Join(", ", names)}. Nothing was deleted.");
		return console.Report(diagnostics);
	}

	static int Pull(FxConsole console, IReadOnlyCollection<string> only)
	{
		const string schema = "fx-repo-pull/1";
		if (Manifest(console, schema) is not { } manifest)
			return 1;
		var actions = WorkspaceModule.Pull(manifest, only);
		var failed = actions.Where(a => a.Action == "failed")
			.Select(a => new Diagnostic("workspace.pull-failed", "error", $"'{a.Repository}': {a.Detail}")).ToList();
		if (console.Settings.Output == OutputFormat.Json)
			console.WriteJson(schema, failed, json =>
			{
				json.WriteStartArray("repositories");
				foreach (var a in actions)
				{
					json.WriteStartObject();
					json.WriteString("name", a.Repository);
					json.WriteString("action", a.Action);
					if (a.Detail is not null)
						json.WriteString("detail", a.Detail);
					json.WriteEndObject();
				}
				json.WriteEndArray();
			});
		else
			foreach (var a in actions)
				console.Out.WriteLine($"{a.Action,-8} {a.Repository}{(a.Detail is null ? "" : $" ({a.Detail})")}");
		return console.Report(failed);
	}
}
