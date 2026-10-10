using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using Fuxion.Tools.Core.Dotnet;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Workspace;
using Spectre.Console;

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
		command.SetAction(result =>
		{
			var settings = global.Bind(result, currentDirectory);
			return Run(new(settings, result.GetValue(module), result.GetValue(dryRun), result.GetValue(offline), result.GetValue(adopt)),
				new(result.InvocationConfiguration.Output, result.InvocationConfiguration.Error, settings), doctor: false);
		});
		return command;
	}

	public static Command CreateDoctor(GlobalOptions global, string currentDirectory)
	{
		var module = ModuleArgument();
		var offline = new Option<bool>("--offline") { Description = Texts.Get("doctor.offline") };
		var command = new Command("doctor", Texts.Get("doctor")) { module, offline };
		command.SetAction(result =>
		{
			var settings = global.Bind(result, currentDirectory);
			return Run(new(settings, result.GetValue(module), DryRun: true, Offline: result.GetValue(offline)),
				new(result.InvocationConfiguration.Output, result.InvocationConfiguration.Error, settings), doctor: true);
		});
		return command;
	}

	static Argument<string?> ModuleArgument()
	{
		var argument = new Argument<string?>("module") { Description = Texts.Get("module"), Arity = ArgumentArity.ZeroOrOne };
		argument.AcceptOnlyFromAmong(WorkspaceModule.Name, DotnetModule.Name);
		return argument;
	}

	public static int Run(ModuleSettings settings, FxConsole console, bool doctor)
	{
		var schema = doctor ? DoctorSchema : SyncSchema;
		var directory = settings.Global.Directory;
		var runWorkspace = settings.Module == WorkspaceModule.Name || (settings.Module is null && WorkspaceModule.Applies(directory));
		var runDotnet = settings.Module == DotnetModule.Name || (settings.Module is null && DotnetModule.Applies(directory));
		var progress = console.Progress;
		var modules = new List<FxModuleResult>();
		if (runWorkspace)
			modules.Add(doctor
				? WorkspaceModule.Doctor(directory, settings.Offline, progress)
				: WorkspaceModule.Sync(directory, new(settings.DryRun, settings.Offline, settings.Adopt), progress));
		if (runDotnet)
			modules.Add(doctor ? DotnetModule.Doctor(directory, progress) : DotnetModule.Sync(directory, settings.DryRun, progress));
		var diagnostics = modules.SelectMany(m => m.Diagnostics).ToList();
		if (modules.Count == 0)
			diagnostics.Add(FxDiagnostic.Info(NothingToDo, new FxText(NothingToDo)));

		if (settings.Global.Output == OutputFormat.Json)
			console.WriteJson(new SyncDocument(schema, FxConsole.Ok(diagnostics), modules.Select(JsonModule.From).ToList(), JsonDiagnostic.From(diagnostics)),
				FxConsole.JsonContext.SyncDocument);
		else
			foreach (var module in modules)
				WriteHuman(console, module, doctor, settings.DryRun);
		return console.Report(diagnostics);
	}

	static void WriteHuman(FxConsole console, FxModuleResult module, bool doctor, bool dryRun)
	{
		if (doctor)
		{
			var problems = module.Diagnostics.Count;
			console.Out.WriteLine(problems == 0 ? Texts.Get("doctor.all-right", module.Module) : Texts.Get("doctor.problems", module.Module, problems));
			return;
		}
		foreach (var action in module.Actions)
			console.Out.WriteLine($"{Texts.Get($"action.{action.Action}"),-11} {action.Repository}");
		foreach (var file in module.Files.Where(f => f.Status != SyncFileStatus.Unchanged || console.Settings.Verbose))
			console.Out.WriteLine($"{Texts.Get($"file.{JsonModule.FileStatus(file.Status)}"),-11} {file.Path}");
		var changed = module.Files.Count(f => f.Status is SyncFileStatus.Written or SyncFileStatus.WouldWrite);
		var refused = module.Files.Count(f => f.Status == SyncFileStatus.Refused);
		var summary = changed == 0 ? Texts.Get("sync.up-to-date") : dryRun ? Texts.Get("sync.would-change", changed) : Texts.Get("sync.written", changed);
		console.Out.WriteLine(refused == 0 ? Texts.Get("sync.summary", module.Module, summary) : Texts.Get("sync.summary-refused", module.Module, summary, refused));
	}
}
