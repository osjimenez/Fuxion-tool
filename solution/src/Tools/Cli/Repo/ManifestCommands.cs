using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Text;
using Fuxion.Tools.Core.Workspace;

namespace Fuxion.Tools.Cli.Repo;

/// <summary>
/// The commands that edit the manifest (plan O, O3.4): <c>fx repo add|remove|tag|set</c>, <c>fx module
/// enable|disable</c> and <c>fx folder add|remove</c>. They show the change as a diff and write it, or only show it
/// (<c>--dry-run</c>); what is missing is asked, with a person in front (<see cref="Ask"/>). They never touch clones
/// nor folders: <c>fx sync</c> does the rest, and each one says so.
/// </summary>
public static class ManifestCommands
{
	public const string Schema = "fx-manifest-edit/1";

	const string Sync = "fx sync";

	/// <summary>What a command does once it has its values: the change, or null if something is missing with no one to ask.</summary>
	delegate ManifestChange? Edit(ParseResult result, FxConsole console, WorkspaceManifest manifest, CancellationToken cancellationToken);

	/// <summary><c>fx repo add|remove|tag|set</c>, into <c>fx repo</c>.</summary>
	public static void AddTo(Command repo, GlobalOptions global, string currentDirectory)
	{
		var url = new Argument<string?>("url") { Description = Texts.Get("repo.add.url"), Arity = ArgumentArity.ZeroOrOne };
		var path = new Argument<string?>("path") { Description = Texts.Get("repo.add.path"), Arity = ArgumentArity.ZeroOrOne };
		var name = new Option<string?>("--name") { Description = Texts.Get("repo.add.name") };
		var mandatory = new Option<bool>("--mandatory") { Description = Texts.Get("repo.add.mandatory") };
		var standalone = new Option<bool>("--standalone") { Description = Texts.Get("repo.add.standalone") };
		var solution = new Option<string?>("--solution") { Description = Texts.Get("repo.add.solution") };
		var dependsOn = new Option<string[]>("--depends-on") { Description = Texts.Get("repo.add.depends-on"), AllowMultipleArgumentsPerToken = true };
		var tags = new Option<string[]>("--tag") { Description = Texts.Get("repo.add.tag"), AllowMultipleArgumentsPerToken = true };
		var add = new Command("add", Texts.Get("repo.add")) { url, path, name, mandatory, standalone, solution, dependsOn, tags };
		// the name of the added one, for the next step (it may come from an answer)
		var added = "";
		Wire(add, global, currentDirectory, (result, console, manifest, token) =>
		{
			// where it goes is the user's: no default (the layout of the workspace is free)
			string? u = result.GetValue(url), p = result.GetValue(path);
			if (string.IsNullOrWhiteSpace(u) || string.IsNullOrWhiteSpace(p))
			{
				if (!console.Capabilities.CanPrompt)
				{
					console.Missing(string.IsNullOrWhiteSpace(u) ? "<url>" : "<path>", "fx repo add <url> <path>");
					return null;
				}
				if (string.IsNullOrWhiteSpace(u))
					u = Ask.Text(console, Texts.Get("repo.ask.url"), token);
				if (string.IsNullOrWhiteSpace(p))
					p = Ask.Text(console, Texts.Get("repo.ask.path"), token);
				Ask.Equivalent(console, string.Join(' ', result.Tokens.Select(t => t.Value).Where(t => t != u && t != p).Prepend("fx").Append(u).Append(p)));
			}
			added = result.GetValue(name) ?? ManifestEditor.NameFromUrl(u);
			return ManifestEditor.AddRepository(manifest.Root, u, p, added, result.GetValue(mandatory),
				result.GetValue(standalone), result.GetValue(solution), result.GetValue(dependsOn) ?? [], result.GetValue(tags) ?? []);
		}, result => result.GetValue(mandatory) ? Sync : $"fx repo mount {added}");
		repo.Add(add);

		var removed = RepositoryArgument();
		var remove = new Command("remove", Texts.Get("repo.remove")) { removed };
		Wire(remove, global, currentDirectory, (result, console, manifest, token) =>
		{
			if (Repository(result, console, manifest, removed, "fx repo remove <name>", Texts.Get("repo.ask.remove"), token) is not { } n)
				return null;
			if (string.IsNullOrWhiteSpace(result.GetValue(removed)))
				Ask.Equivalent(console, $"fx repo remove {n}");
			return ManifestEditor.RemoveRepository(manifest.Root, n);
		});
		repo.Add(remove);

		var tagged = RepositoryArgument();
		var tagList = new Argument<string[]>("tags") { Description = Texts.Get("repo.tag.tags"), Arity = ArgumentArity.ZeroOrMore };
		var untag = new Option<bool>("--remove") { Description = Texts.Get("repo.tag.remove") };
		var tag = new Command("tag", Texts.Get("repo.tag")) { tagged, tagList, untag };
		Wire(tag, global, currentDirectory, (result, console, manifest, token) =>
		{
			var asked = string.IsNullOrWhiteSpace(result.GetValue(tagged));
			if (Repository(result, console, manifest, tagged, "fx repo tag <name> <tags>...", Texts.Get("repo.ask.repository"), token) is not { } n)
				return null;
			var removing = result.GetValue(untag);
			IReadOnlyList<string> values = result.GetValue(tagList) ?? [];
			if (values.Count == 0)
			{
				var current = manifest.Repositories.FirstOrDefault(r => r.Name == n)?.Tags ?? [];
				if (!console.Capabilities.CanPrompt || (removing && current.Count == 0))
				{
					console.Missing("<tags>", $"fx repo tag {n} <tags>...{(removing ? " --remove" : "")}");
					return null;
				}
				values = removing
					? Ask.Many(console, Texts.Get("repo.ask.tags-remove"), current, t => t, token)
					: Ask.Text(console, Texts.Get("repo.ask.tags-add"), token).Split(' ', StringSplitOptions.RemoveEmptyEntries);
				asked = true;
			}
			if (asked)
				Ask.Equivalent(console, $"fx repo tag {n} {string.Join(' ', values)}{(removing ? " --remove" : "")}");
			return ManifestEditor.TagRepository(manifest.Root, n, values, removing);
		});
		repo.Add(tag);

		var changed = RepositoryArgument();
		var key = new Argument<string?>("key") { Description = Texts.Get("repo.set.key"), Arity = ArgumentArity.ZeroOrOne };
		key.AcceptOnlyFromAmong([.. ManifestEditor.SettableKeys]);
		var values = new Argument<string[]>("values") { Description = Texts.Get("repo.set.values"), Arity = ArgumentArity.ZeroOrMore };
		var unset = new Option<bool>("--unset") { Description = Texts.Get("repo.set.unset") };
		var set = new Command("set", Texts.Get("repo.set")) { changed, key, values, unset };
		Wire(set, global, currentDirectory, (result, console, manifest, token) =>
		{
			var asked = string.IsNullOrWhiteSpace(result.GetValue(changed));
			if (Repository(result, console, manifest, changed, "fx repo set <name> <key> <values>...", Texts.Get("repo.ask.repository"), token) is not { } n)
				return null;
			var k = result.GetValue(key);
			if (string.IsNullOrWhiteSpace(k))
			{
				if (!console.Capabilities.CanPrompt)
				{
					console.Missing("<key>", $"fx repo set {n} <key> <values>...");
					return null;
				}
				k = Ask.One(console, Texts.Get("repo.ask.key"), ManifestEditor.SettableKeys, x => x, token);
				asked = true;
			}
			var removing = result.GetValue(unset);
			IReadOnlyList<string> given = removing ? [] : result.GetValue(values) ?? [];
			if (!removing && given.Count == 0)
			{
				if (!console.Capabilities.CanPrompt)
				{
					console.Missing("<values>", $"fx repo set {n} {k} <values>... (--unset to remove it)");
					return null;
				}
				var question = Texts.Get("repo.ask.value", k);
				given = k switch
				{
					"mount" => [Ask.One(console, question, ["mandatory", "manual"], v => v, token)],
					"standalone" => [Ask.One(console, question, ["true", "false"], v => v, token)],
					"dependsOn" => Ask.Many(console, question, manifest.Repositories.Where(r => r.Name != n).Select(r => r.Name).ToList(), r => r, token),
					_ => Ask.Text(console, question, token).Split(' ', StringSplitOptions.RemoveEmptyEntries)
				};
				asked = true;
			}
			if (asked)
				Ask.Equivalent(console, $"fx repo set {n} {k} {(removing ? "--unset" : string.Join(' ', given))}");
			return ManifestEditor.SetRepository(manifest.Root, n, k, given);
		});
		repo.Add(set);
	}

	/// <summary><c>fx module enable|disable</c>.</summary>
	public static Command CreateModule(GlobalOptions global, string currentDirectory)
	{
		var command = new Command("module", Texts.Get("modules"));

		var enabled = ModuleArgument();
		var tags = new Option<string[]>("--tag") { Description = Texts.Get("module.tag"), AllowMultipleArgumentsPerToken = true };
		var enable = new Command("enable", Texts.Get("module.enable")) { enabled, tags };
		Wire(enable, global, currentDirectory, (result, console, manifest, token) =>
		{
			var choices = WorkspaceManifest.KnownModules.Where(m => manifest.Module(m) is null).ToList();
			return Given(result, console, result.GetValue(enabled), "<module>", "fx module enable <module>", () => Ask.One(console, Texts.Get("module.ask.enable"), choices, m => m, token),
				choices.Count > 0) is { } module
				? ManifestEditor.EnableModule(manifest.Root, module, result.GetValue(tags) ?? [])
				: null;
		});
		command.Add(enable);

		var disabled = ModuleArgument();
		var disable = new Command("disable", Texts.Get("module.disable")) { disabled };
		Wire(disable, global, currentDirectory, (result, console, manifest, token) =>
		{
			var choices = (manifest.Modules ?? []).Select(m => m.Name).ToList();
			return Given(result, console, result.GetValue(disabled), "<module>", "fx module disable <module>", () => Ask.One(console, Texts.Get("module.ask.disable"), choices, m => m, token),
				choices.Count > 0) is { } module
				? ManifestEditor.DisableModule(manifest.Root, module)
				: null;
		});
		command.Add(disable);
		return command;
	}

	/// <summary><c>fx folder add|remove</c>.</summary>
	public static Command CreateFolder(GlobalOptions global, string currentDirectory)
	{
		var command = new Command("folder", Texts.Get("folder"));

		var added = FolderArgument();
		var add = new Command("add", Texts.Get("folder.add")) { added };
		Wire(add, global, currentDirectory, (result, console, manifest, token) =>
			Given(result, console, result.GetValue(added), "<name>", "fx folder add <name>", () => Ask.Text(console, Texts.Get("folder.ask.add"), token)) is { } folder
				? ManifestEditor.AddFolder(manifest.Root, folder)
				: null);
		command.Add(add);

		var removed = FolderArgument();
		var remove = new Command("remove", Texts.Get("folder.remove")) { removed };
		Wire(remove, global, currentDirectory, (result, console, manifest, token) =>
		{
			var choices = manifest.Folders ?? [];
			return Given(result, console, result.GetValue(removed), "<name>", "fx folder remove <name>", () => Ask.One(console, Texts.Get("folder.ask.remove"), choices, f => f, token),
				choices.Count > 0) is { } folder
				? ManifestEditor.RemoveFolder(manifest.Root, folder)
				: null;
		});
		command.Add(remove);
		return command;
	}

	// optional for the parser: asked if missing (plan O, O3.3)
	static Argument<string?> RepositoryArgument() => new("name") { Description = Texts.Get("repo.name"), Arity = ArgumentArity.ZeroOrOne };

	static Argument<string?> ModuleArgument() => new("module") { Description = Texts.Get("module.name"), Arity = ArgumentArity.ZeroOrOne };

	static Argument<string?> FolderArgument() => new("name") { Description = Texts.Get("folder.name"), Arity = ArgumentArity.ZeroOrOne };

	/// <summary>The value given; or asked, and then the command that does the same; or null, with what is missing said.</summary>
	static string? Given(ParseResult result, FxConsole console, string? given, string what, string usage, Func<string> ask, bool canAsk = true)
	{
		if (!string.IsNullOrWhiteSpace(given))
			return given;
		if (!console.Capabilities.CanPrompt || !canAsk)
		{
			console.Missing(what, usage);
			return null;
		}
		var answer = ask();
		Ask.Equivalent(console, string.Join(' ', result.Tokens.Select(t => t.Value).Prepend("fx").Append(answer)));
		return answer;
	}

	static string? Repository(ParseResult result, FxConsole console, WorkspaceManifest manifest, Argument<string?> argument, string usage, string question, CancellationToken cancellationToken)
	{
		var names = manifest.Repositories.Select(r => r.Name).ToList();
		var given = result.GetValue(argument);
		if (!string.IsNullOrWhiteSpace(given))
			return given;
		if (!console.Capabilities.CanPrompt || names.Count == 0)
		{
			console.Missing("<name>", usage);
			return null;
		}
		return Ask.One(console, question, names, n => n, cancellationToken);
	}

	/// <summary>The action of an editing command: the manifest, the values (given or asked), the change, and its result.</summary>
	static void Wire(Command command, GlobalOptions global, string currentDirectory, Edit edit, Func<ParseResult, string>? next = null)
	{
		var dryRun = new Option<bool>("--dry-run") { Description = Texts.Get("manifest.dry-run-option") };
		command.Add(dryRun);
		command.SetAction((result, cancellationToken) =>
		{
			var console = RepoCommand.Console(result, global, currentDirectory);
			return Task.FromResult(console.Run(token =>
			{
				void Json(IReadOnlyList<FxDiagnostic> diagnostics)
					=> console.WriteJson(new ManifestEditDocument(Schema, false, WorkspaceManifest.RelativePath, false, false, "", JsonDiagnostic.From(diagnostics)), c => c.ManifestEditDocument);
				if (RepoCommand.Manifest(console, Json) is not { } manifest)
					return 1;
				if (edit(result, console, manifest, token) is not { } change)
					return FxConsole.UsageError;
				return Apply(console, manifest.Root, change, result.GetValue(dryRun), next?.Invoke(result) ?? Sync);
			}, cancellationToken));
		});
	}

	/// <summary>Writes the change (unless <c>--dry-run</c> or with errors) and shows it: the diff, or why nothing changes.</summary>
	static int Apply(FxConsole console, string root, ManifestChange change, bool dryRun, string next)
	{
		var written = change.Ok && change.Changed && !dryRun;
		if (written)
			ManifestEditor.Write(root, change);
		var problems = change.Diagnostics.Where(d => d.Severity != FxSeverity.Info).ToList();
		if (console.Settings.IsMachine)
		{
			console.WriteJson(new ManifestEditDocument(Schema, change.Ok, WorkspaceManifest.RelativePath, change.Ok && change.Changed, written,
				change.Ok ? LineDiff.Unified(change.Diff) : "", JsonDiagnostic.From(change.Diagnostics)), c => c.ManifestEditDocument);
			return FxConsole.ExitCode(change.Diagnostics);
		}
		if (change.Ok && !change.Changed)
		{
			var reasons = change.Diagnostics.Where(d => d.Severity == FxSeverity.Info).Select(d => d.Message.ToString()).ToList();
			console.Line($"[gray]{FxConsole.Escape(reasons.Count > 0 ? string.Join(" ", reasons) : Texts.Get("manifest.unchanged"))}[/]");
		}
		else if (change.Ok)
		{
			WriteDiff(console, change.Diff);
			console.Line(dryRun
				? $"[yellow]{FxConsole.Escape(Texts.Get("manifest.dry-run"))}[/]"
				: $"[green]✓[/] {FxConsole.Escape(Texts.Get("manifest.written", WorkspaceManifest.RelativePath))} [gray]{FxConsole.Escape(Texts.Get("manifest.next", next))}[/]");
		}
		return console.Report(problems);
	}

	static void WriteDiff(FxConsole console, IReadOnlyList<DiffHunk> hunks)
	{
		console.Line($"[bold]{FxConsole.Escape(WorkspaceManifest.RelativePath)}[/]");
		foreach (var hunk in hunks)
		{
			console.Line($"[cyan]@@ -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} @@[/]");
			foreach (var line in hunk.Lines)
				console.Line(line.Kind switch
				{
					'-' => $"[red]-{FxConsole.Escape(line.Text)}[/]",
					'+' => $"[green]+{FxConsole.Escape(line.Text)}[/]",
					_ => $"[gray] {FxConsole.Escape(line.Text)}[/]"
				});
		}
	}
}
