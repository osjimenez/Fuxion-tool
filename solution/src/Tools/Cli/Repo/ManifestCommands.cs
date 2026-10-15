using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Sync;
using Fuxion.Tools.Core.Text;
using Fuxion.Tools.Core.Workspace;

namespace Fuxion.Tools.Cli.Repo;

/// <summary>
/// The commands that edit the configuration (plan O, O3.4; plan P): <c>fx repo add|remove|tag|set</c> and <c>fx path
/// add|remove</c> (the manifest of the workspace), <c>fx module enable|disable</c> (the yaml of a module, in any root of
/// fx). They show the change as a diff and write it, or only show it (<c>--dry-run</c>); what is missing is asked, with
/// a person in front (<see cref="Ask"/>). They never touch clones nor folders: <c>fx sync</c> does the rest, and each
/// one says so.
/// </summary>
public static class ManifestCommands
{
	public const string Schema = "fx-manifest-edit/1";

	const string Sync = "fx sync";

	/// <summary>What a command does once it has its values: the change, or null if something is missing with no one to ask.</summary>
	delegate ManifestChange? Edit(ParseResult result, FxConsole console, WorkspaceManifest manifest, CancellationToken cancellationToken);

	/// <summary>The same, for a command that changes several files (<c>fx repo rename</c>).</summary>
	delegate IReadOnlyList<ManifestChange>? EditMany(ParseResult result, FxConsole console, WorkspaceManifest manifest, CancellationToken cancellationToken);

	/// <summary>The same, for a command that edits any root of fx (not only a workspace).</summary>
	delegate ManifestChange? RootEdit(ParseResult result, FxConsole console, string root, CancellationToken cancellationToken);

	/// <summary><c>fx repo add|remove|tag|set</c>, into <c>fx repo</c>.</summary>
	public static void AddTo(Command repo, GlobalOptions global, string currentDirectory)
	{
		var url = new Argument<string?>("url") { Description = Texts.Get("repo.add.url"), Arity = ArgumentArity.ZeroOrOne };
		var path = new Argument<string?>("path") { Description = Texts.Get("repo.add.path"), Arity = ArgumentArity.ZeroOrOne };
		var name = new Option<string?>("--name") { Description = Texts.Get("repo.add.name") };
		var mandatory = new Option<bool>("--mandatory") { Description = Texts.Get("repo.add.mandatory") };
		var guest = new Option<bool>("--guest") { Description = Texts.Get("repo.add.guest") };
		var dependsOn = new Option<string[]>("--depends-on") { Description = Texts.Get("repo.add.depends-on"), AllowMultipleArgumentsPerToken = true };
		var tags = new Option<string[]>("--tag") { Description = Texts.Get("repo.add.tag"), AllowMultipleArgumentsPerToken = true };
		var add = new Command("add", Texts.Get("repo.add")) { url, path, name, mandatory, guest, dependsOn, tags };
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
				result.GetValue(dependsOn) ?? [], result.GetValue(tags) ?? [], inherits: !result.GetValue(guest));
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
					"inherits" => [Ask.One(console, question, ["true", "false"], v => v, token)],
					"depends-on" => Ask.Many(console, question, manifest.Repositories.Where(r => r.Name != n).Select(r => r.Name).ToList(), r => r, token),
					_ => Ask.Text(console, question, token).Split(' ', StringSplitOptions.RemoveEmptyEntries)
				};
				asked = true;
			}
			if (asked)
				Ask.Equivalent(console, $"fx repo set {n} {k} {(removing ? "--unset" : string.Join(' ', given))}");
			return ManifestEditor.SetRepository(manifest.Root, n, k, given);
		});
		repo.Add(set);

		var renamed = RepositoryArgument();
		var newName = new Argument<string?>("new-name") { Description = Texts.Get("repo.rename.new-name"), Arity = ArgumentArity.ZeroOrOne };
		var rename = new Command("rename", Texts.Get("repo.rename")) { renamed, newName };
		WireMany(rename, global, currentDirectory, (result, console, manifest, token) =>
		{
			var asked = string.IsNullOrWhiteSpace(result.GetValue(renamed));
			if (Repository(result, console, manifest, renamed, "fx repo rename <name> <new-name>", Texts.Get("repo.ask.repository"), token) is not { } n)
				return null;
			var to = result.GetValue(newName);
			if (string.IsNullOrWhiteSpace(to))
			{
				if (!console.Capabilities.CanPrompt)
				{
					console.Missing("<new-name>", $"fx repo rename {n} <new-name>");
					return null;
				}
				to = Ask.Text(console, Texts.Get("repo.ask.new-name"), token);
				asked = true;
			}
			if (asked)
				Ask.Equivalent(console, $"fx repo rename {n} {to}");
			return ManifestEditor.RenameRepository(manifest.Root, n, to);
		});
		repo.Add(rename);
	}

	/// <summary><c>fx module enable|disable</c>, in the root of fx where it runs.</summary>
	public static Command CreateModule(GlobalOptions global, string currentDirectory)
	{
		var command = new Command("module", Texts.Get("modules"));

		var enabled = ModuleArgument();
		var tags = new Option<string[]>("--tag") { Description = Texts.Get("module.tag"), AllowMultipleArgumentsPerToken = true };
		var enable = new Command("enable", Texts.Get("module.enable")) { enabled, tags };
		WireRoot(enable, global, currentDirectory, (result, console, root, token) =>
		{
			var choices = ManifestEditor.KnownModules.Where(m => !Active(root, m)).ToList();
			return Given(result, console, result.GetValue(enabled), "<module>", "fx module enable <module>", () => Ask.One(console, Texts.Get("module.ask.enable"), choices, m => m, token),
				choices.Count > 0) is { } module
				? ManifestEditor.EnableModule(root, module, result.GetValue(tags) ?? [])
				: null;
		});
		command.Add(enable);

		var disabled = ModuleArgument();
		var disable = new Command("disable", Texts.Get("module.disable")) { disabled };
		WireRoot(disable, global, currentDirectory, (result, console, root, token) =>
		{
			var choices = ManifestEditor.KnownModules.Where(m => Active(root, m)).ToList();
			return Given(result, console, result.GetValue(disabled), "<module>", "fx module disable <module>", () => Ask.One(console, Texts.Get("module.ask.disable"), choices, m => m, token),
				choices.Count > 0) is { } module
				? ManifestEditor.DisableModule(root, module)
				: null;
		});
		command.Add(disable);
		return command;
	}

	/// <summary>Whether a module is on in a root: its folder exists and its yaml does not say <c>enabled: false</c>.</summary>
	static bool Active(string root, string module)
	{
		var file = FxLayout.Full(root, FxLayout.ModuleFile(module));
		return Directory.Exists(FxLayout.Full(root, FxLayout.ModuleFolder(module)))
		       && !(File.Exists(file) && File.ReadAllText(file).Contains("enabled: false", StringComparison.Ordinal));
	}

	/// <summary><c>fx path add|remove</c>: what the git of the workspace versions.</summary>
	public static Command CreatePath(GlobalOptions global, string currentDirectory)
	{
		var command = new Command("path", Texts.Get("path"));

		var added = PathArgument();
		var exclude = new Option<bool>("--exclude") { Description = Texts.Get("path.exclude") };
		var noWorktree = new Option<bool>("--no-worktree") { Description = Texts.Get("path.no-worktree") };
		var add = new Command("add", Texts.Get("path.add")) { added, exclude, noWorktree };
		Wire(add, global, currentDirectory, (result, console, manifest, token) =>
			Given(result, console, result.GetValue(added), "<path>", "fx path add <path>", () => Ask.Text(console, Texts.Get("path.ask.add"), token)) is { } path
				? ManifestEditor.AddPath(manifest.Root, path, result.GetValue(exclude), !result.GetValue(noWorktree))
				: null);
		command.Add(add);

		var removed = PathArgument();
		var remove = new Command("remove", Texts.Get("path.remove")) { removed };
		Wire(remove, global, currentDirectory, (result, console, manifest, token) =>
		{
			var choices = (manifest.Paths ?? []).Select(p => p.Path).ToList();
			return Given(result, console, result.GetValue(removed), "<path>", "fx path remove <path>", () => Ask.One(console, Texts.Get("path.ask.remove"), choices, f => f, token),
				choices.Count > 0) is { } path
				? ManifestEditor.RemovePath(manifest.Root, path)
				: null;
		});
		command.Add(remove);
		return command;
	}

	// optional for the parser: asked if missing (plan O, O3.3)
	static Argument<string?> RepositoryArgument() => new("name") { Description = Texts.Get("repo.name"), Arity = ArgumentArity.ZeroOrOne };

	static Argument<string?> ModuleArgument() => new("module") { Description = Texts.Get("module.name"), Arity = ArgumentArity.ZeroOrOne };

	static Argument<string?> PathArgument() => new("path") { Description = Texts.Get("path.name"), Arity = ArgumentArity.ZeroOrOne };

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
		=> WireMany(command, global, currentDirectory, (result, console, manifest, token) => edit(result, console, manifest, token) is { } change ? [change] : null, next);

	/// <summary>The action of an editing command that may change several files.</summary>
	static void WireMany(Command command, GlobalOptions global, string currentDirectory, EditMany edit, Func<ParseResult, string>? next = null)
	{
		command.Describe(Schema, writes: true);
		var dryRun = new Option<bool>("--dry-run") { Description = Texts.Get("manifest.dry-run-option") };
		command.Add(dryRun);
		command.SetAction((result, cancellationToken) =>
		{
			var console = RepoCommand.Console(result, global, currentDirectory);
			return Task.FromResult(console.Run(token =>
			{
				void Json(IReadOnlyList<FxDiagnostic> diagnostics)
					=> console.WriteJson(new ManifestEditDocument(Schema, false, false, false, [], JsonDiagnostic.From(diagnostics)), c => c.ManifestEditDocument);
				if (RepoCommand.Manifest(console, Json) is not { } manifest)
					return 1;
				if (edit(result, console, manifest, token) is not { } changes)
					return FxConsole.UsageError;
				return Apply(console, manifest.Root, changes, result.GetValue(dryRun), next?.Invoke(result) ?? Sync);
			}, cancellationToken));
		});
	}

	/// <summary>The action of a command that edits any root of fx: the root around the directory (its <c>_fx/</c>, or its git).</summary>
	static void WireRoot(Command command, GlobalOptions global, string currentDirectory, RootEdit edit)
	{
		command.Describe(Schema, writes: true);
		var dryRun = new Option<bool>("--dry-run") { Description = Texts.Get("manifest.dry-run-option") };
		command.Add(dryRun);
		command.SetAction((result, cancellationToken) =>
		{
			var console = RepoCommand.Console(result, global, currentDirectory);
			return Task.FromResult(console.Run(token =>
			{
				if (FxLayout.FindRoot(console.Settings.Directory) is not { } root)
				{
					var error = FxDiagnostic.Error(FxNode.NotARoot, new(FxNode.NotARoot, console.Settings.Directory));
					if (console.Settings.IsMachine)
						console.WriteJson(new ManifestEditDocument(Schema, false, false, false, [], JsonDiagnostic.From([error])), c => c.ManifestEditDocument);
					return console.Report([error]);
				}
				if (edit(result, console, root, token) is not { } change)
					return FxConsole.UsageError;
				return Apply(console, root, [change], result.GetValue(dryRun), Sync);
			}, cancellationToken));
		});
	}

	/// <summary>
	/// Writes the changes (unless <c>--dry-run</c> or with errors in any of them: all or nothing) and shows them: the
	/// diff of each file, or why nothing changes. With <c>--dry-run</c>, also what <c>fx sync</c> would do with them,
	/// worked out with the changes in place of the files, without writing them (<see cref="FxFiles"/>).
	/// </summary>
	static int Apply(FxConsole console, string root, IReadOnlyList<ManifestChange> changes, bool dryRun, string next)
	{
		var diagnostics = changes.SelectMany(c => c.Diagnostics).ToList();
		var ok = changes.All(c => c.Ok);
		var changed = changes.Where(c => c.Changed).ToList();
		var written = ok && changed.Count > 0 && !dryRun;
		if (written)
			foreach (var change in changed)
				ManifestEditor.Write(root, change);
		IReadOnlyList<FxModuleResult>? sync = null;
		if (dryRun && ok && changed.Count > 0)
			using (FxFiles.Overlay(changed.Select(c => KeyValuePair.Create(FxLayout.Full(root, c.File), c.After.Text))))
				sync = SyncEngine.Sync(root, new(DryRun: true, Skill: Describe.FxSkill.Text));
		var problems = diagnostics.Where(d => d.Severity != FxSeverity.Info).ToList();
		if (console.Settings.IsMachine)
		{
			console.WriteJson(new ManifestEditDocument(Schema, ok, ok && changed.Count > 0, written,
				ok ? changed.Select(c => new JsonFileEdit(c.File, LineDiff.Unified(c.Diff))).ToList() : [], JsonDiagnostic.From(diagnostics),
				sync?.Select(JsonModule.From).ToList()), c => c.ManifestEditDocument);
			return FxConsole.ExitCode(diagnostics);
		}
		if (ok && changed.Count == 0)
		{
			var reasons = diagnostics.Where(d => d.Severity == FxSeverity.Info).Select(d => d.Message.ToString()).ToList();
			console.Line($"[gray]{FxConsole.Escape(reasons.Count > 0 ? string.Join(" ", reasons) : Texts.Get("manifest.unchanged"))}[/]");
		}
		else if (ok)
		{
			foreach (var change in changed)
				WriteDiff(console, change.File, change.Diff);
			console.Line(dryRun
				? $"[yellow]{FxConsole.Escape(Texts.Get("manifest.dry-run"))}[/]"
				: $"[green]✓[/] {FxConsole.Escape(Texts.Get("manifest.written", string.Join(", ", changed.Select(c => c.File))))} [gray]{FxConsole.Escape(Texts.Get("manifest.next", next))}[/]");
			if (sync is not null)
			{
				console.Line($"[bold]{FxConsole.Escape(Texts.Get("manifest.sync-would"))}[/]");
				foreach (var module in sync)
					global::Fuxion.Tools.Cli.Sync.SyncCommand.WriteHuman(console, module, dryRun: true);
			}
		}
		var code = console.Report(problems);
		// what sync would find is shown, but the exit code is the edit's: the change itself is valid
		if (sync is not null)
			console.Report(sync.SelectMany(m => m.Diagnostics).Where(d => d.Severity != FxSeverity.Info).ToList());
		return code;
	}

	static void WriteDiff(FxConsole console, string file, IReadOnlyList<DiffHunk> hunks)
	{
		console.Line($"[bold]{FxConsole.Escape(file)}[/]");
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
