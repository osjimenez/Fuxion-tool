using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fuxion.Tools.Core.Dotnet;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Workspace;

namespace Fuxion.Tools.Cli.Sync;

public sealed record ModuleSettings(GlobalSettings Global, string? Module, bool DryRun, bool Offline = false, bool Adopt = false);

/// <summary>
/// <c>fx sync [module]</c> and <c>fx doctor [module]</c> (design §10.2): with a module, only that one; without one,
/// every module that applies here. <c>workspace</c> applies in the metarepo (or anywhere with <c>-w</c>); <c>dotnet</c>
/// in a repository with <c>_fx/dotnet.yaml</c>.
/// </summary>
public static class SyncCommand
{
	public const string SyncSchema = "fx-sync/1";
	public const string DoctorSchema = "fx-doctor/1";
	const string NothingToDo = "fx.nothing-to-do";

	public static Command CreateSync(GlobalOptions global, string currentDirectory)
	{
		var module = ModuleArgument();
		var dryRun = new Option<bool>("--dry-run") { Description = Texts.Get("sync.dry-run") };
		var offline = new Option<bool>("--offline") { Description = Texts.Get("sync.offline") };
		var adopt = new Option<bool>("--adopt") { Description = Texts.Get("sync.adopt") };
		var command = new Command("sync", Texts.Get("sync")) { module, dryRun, offline, adopt };
		command.SetAction((result, cancellationToken) =>
		{
			var settings = global.Bind(result, currentDirectory);
			return Task.FromResult(Run(new(settings, result.GetValue(module), result.GetValue(dryRun), result.GetValue(offline), result.GetValue(adopt)),
				new(result.InvocationConfiguration.Output, result.InvocationConfiguration.Error, settings), doctor: false, cancellationToken));
		});
		return command;
	}

	public static Command CreateDoctor(GlobalOptions global, string currentDirectory)
	{
		var module = ModuleArgument();
		var offline = new Option<bool>("--offline") { Description = Texts.Get("doctor.offline") };
		var command = new Command("doctor", Texts.Get("doctor")) { module, offline };
		command.SetAction((result, cancellationToken) =>
		{
			var settings = global.Bind(result, currentDirectory);
			return Task.FromResult(Run(new(settings, result.GetValue(module), DryRun: true, Offline: result.GetValue(offline)),
				new(result.InvocationConfiguration.Output, result.InvocationConfiguration.Error, settings), doctor: true, cancellationToken));
		});
		return command;
	}

	static Argument<string?> ModuleArgument()
	{
		var argument = new Argument<string?>("module") { Description = Texts.Get("module"), Arity = ArgumentArity.ZeroOrOne };
		argument.AcceptOnlyFromAmong(WorkspaceModule.Name, DotnetModule.Name);
		return argument;
	}

	public static int Run(ModuleSettings settings, FxConsole console, bool doctor, CancellationToken cancellationToken = default)
		=> console.Run(token => Execute(settings, console, doctor, token), cancellationToken);

	static int Execute(ModuleSettings settings, FxConsole console, bool doctor, CancellationToken cancellationToken)
	{
		var schema = doctor ? DoctorSchema : SyncSchema;
		var directory = settings.Global.Directory;
		var runWorkspace = settings.Module == WorkspaceModule.Name || (settings.Module is null && WorkspaceModule.Applies(directory));
		var runDotnet = settings.Module == DotnetModule.Name || (settings.Module is null && DotnetModule.Applies(directory));
		var modules = console.WithProgress((progress, cancel) =>
		{
			var results = new List<FxModuleResult>();
			if (runWorkspace)
				results.Add(doctor
					? WorkspaceModule.Doctor(directory, settings.Offline, progress, cancel)
					: WorkspaceModule.Sync(directory, new(settings.DryRun, settings.Offline, settings.Adopt), progress, cancel));
			if (runDotnet)
				results.Add(doctor ? DotnetModule.Doctor(directory, progress) : DotnetModule.Sync(directory, settings.DryRun, progress));
			return results;
		}, cancellationToken);
		var diagnostics = modules.SelectMany(m => m.Diagnostics).ToList();
		if (modules.Count == 0)
			diagnostics.Add(FxDiagnostic.Info(NothingToDo, new FxText(NothingToDo)));

		if (settings.Global.IsMachine)
			console.WriteJson(new SyncDocument(schema, FxConsole.Ok(diagnostics), modules.Select(JsonModule.From).ToList(), JsonDiagnostic.From(diagnostics)),
				c => c.SyncDocument);
		else if (doctor && modules.Count > 0)
		{
			DoctorView.Write(console, modules, diagnostics);
			return console.Report(diagnostics, write: false);
		}
		else
			foreach (var module in modules)
				WriteHuman(console, module, settings.DryRun);
		return console.Report(diagnostics);
	}

	static void WriteHuman(FxConsole console, FxModuleResult module, bool dryRun)
	{
		foreach (var action in module.Actions)
		{
			var color = action.Action.EndsWith("failed") || action.Action == "missing" ? "red" : "green";
			console.Line($"[{color}]{FxConsole.Escape(Texts.Get($"action.{action.Action}")),-11}[/] {FxConsole.Escape(action.Repository)}");
		}
		foreach (var file in module.Files.Where(f => f.Status != SyncFileStatus.Unchanged || console.Settings.Verbose))
		{
			var color = file.Status switch { SyncFileStatus.Refused => "red", SyncFileStatus.Unchanged => "gray", _ => "yellow" };
			console.Line($"[{color}]{FxConsole.Escape(Texts.Get($"file.{JsonModule.FileStatus(file.Status)}")),-11}[/] {FxConsole.Escape(file.Path)}");
		}
		var changed = module.Files.Count(f => f.Status is SyncFileStatus.Written or SyncFileStatus.WouldWrite);
		var refused = module.Files.Count(f => f.Status == SyncFileStatus.Refused);
		var summary = changed == 0 ? Texts.Get("sync.up-to-date") : dryRun ? Texts.Get("sync.would-change", changed) : Texts.Get("sync.written", changed);
		var line = refused == 0 ? Texts.Get("sync.summary", module.Module, summary) : Texts.Get("sync.summary-refused", module.Module, summary, refused);
		console.Line($"[{(refused > 0 ? "red" : changed > 0 ? "yellow" : "green")}]{FxConsole.Escape(line)}[/]");
	}
}
