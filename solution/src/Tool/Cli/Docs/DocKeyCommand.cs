using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using Fuxion.Tool.Cli.Mounts;
using Fuxion.Tool.Core.Docs;
using Fuxion.Tool.Core.Results;

namespace Fuxion.Tool.Cli.Docs;

/// <summary>
/// <c>fx doc key &lt;register&gt; [&lt;title&gt;]</c> (the SPEC of the module, 5.7): the next key of a register that uses
/// them, reserved by its provider; with <c>--confirm</c>, its provisional keys reserved now.
/// </summary>
public static class DocKeyCommand
{
	public const string Schema = "fx-doc-key/1";

	public static Command Create(GlobalOptions global, string currentDirectory)
	{
		var register = new Argument<string>("register") { Description = Texts.Get("doc.entry.register") };
		var title = new Argument<string?>("title") { Description = Texts.Get("doc.key.title"), Arity = ArgumentArity.ZeroOrOne };
		var confirm = new Option<bool>("--confirm") { Description = Texts.Get("doc.key.confirm") };
		var actor = new Option<string?>("--actor") { Description = Texts.Get("doc.new.actor", DocEditor.ActorVariable), HelpName = "actor" };
		var dryRun = new Option<bool>("--dry-run") { Description = Texts.Get("doc.key.dry-run") };
		var command = new Command("key", Texts.Get("doc.key")) { register, title, confirm, actor, dryRun }.Describe(Schema, writes: true, network: true);
		command.SetAction(result =>
		{
			var console = MountCommand.Console(result, global, currentDirectory);
			return console.Run(_ => Key(console, result.GetValue(register)!, result.GetValue(title), result.GetValue(confirm),
				result.GetValue(actor) ?? Environment.GetEnvironmentVariable(DocEditor.ActorVariable), result.GetValue(dryRun)));
		});
		return command;
	}

	static int Key(FxConsole console, string selector, string? title, bool confirm, string? actor, bool dryRun)
	{
		var diagnostics = new List<FxDiagnostic>();
		void Json(string? register, string? provider, IReadOnlyList<JsonKey> keys, bool written)
			=> console.WriteJson(new DocKeyDocument(Schema, FxConsole.Ok(diagnostics), register, provider, keys, written, JsonDiagnostic.From(diagnostics)), c => c.DocKeyDocument);
		if (DocsModule.Find(console.Settings.Directory, diagnostics) is not { } catalog)
		{
			if (console.Settings.IsMachine)
				Json(null, null, [], false);
			return console.Report(diagnostics);
		}
		if (DocCommand.One(console, catalog, selector) is not { } file)
			return FxConsole.UsageError;
		var display = DocCommand.Display(catalog);
		var keyed = DocKeys.Keyed(catalog, file);
		// the actor, as for every change of the catalog (plan U, decision 20)
		var check = new DocChange(catalog, display);
		if (keyed is null)
			diagnostics.Add(FxDiagnostic.Error(DocKeys.NotKeyed, new(DocKeys.NotKeyed, file.Path)));
		else if (DocEditor.Actor(check, actor))
		{
			if (confirm)
			{
				var change = DocEditor.ConfirmKeys(catalog, display, file, actor!, DateTimeOffset.UtcNow, !dryRun, out var confirmed);
				var written = !dryRun && change.Write();
				diagnostics.AddRange(change.Diagnostics);
				var keys = confirmed.Select(c => new JsonKey(c.To, false, c.From)).ToList();
				if (console.Settings.IsMachine)
				{
					Json(file.Path, keyed.Provider, keys, written);
					return FxConsole.ExitCode(diagnostics);
				}
				foreach (var key in keys)
					console.Line($"[green]{FxConsole.Escape(key.Replaces)} → {FxConsole.Escape(key.Key)}[/]");
				Info(diagnostics);
				return console.Report(diagnostics.Where(d => d.Severity != FxSeverity.Info).ToList());
			}
			var current = DocChecker.Read(catalog).FirstOrDefault(d => d.File == file)?.Sidecar.Entries.Select(e => e.Key).OfType<string>() ?? [];
			var reserved = DocKeys.Reserve(catalog, file, keyed, current, actor!, title, reserve: !dryRun);
			if (reserved.Problem is not null)
				diagnostics.Add(FxDiagnostic.Warning(DocKeys.ProvisionalKey, new(DocKeys.ProvisionalKey, reserved.Key, reserved.Problem)));
			if (console.Settings.IsMachine)
			{
				Json(file.Path, keyed.Provider, [new(reserved.Key, reserved.Provisional, null)], !dryRun && keyed.Provider == "git" && !reserved.Provisional);
				return FxConsole.ExitCode(diagnostics);
			}
			console.Line($"[green]{FxConsole.Escape(reserved.Key)}[/] [gray]{FxConsole.Escape(Texts.Get(reserved.Provisional ? "doc.key.provisional"
				: dryRun ? "doc.key.would" : $"doc.key.{keyed.Provider}"))}[/]");
			return console.Report(diagnostics);
		}
		diagnostics.AddRange(check.Diagnostics);
		if (console.Settings.IsMachine)
			Json(file.Path, keyed?.Provider, [], false);
		return console.Report(diagnostics);

		void Info(IEnumerable<FxDiagnostic> found)
		{
			foreach (var info in found.Where(d => d.Severity == FxSeverity.Info))
				console.Line($"[gray]{FxConsole.Escape(info.Message.ToString())}[/]");
		}
	}
}
