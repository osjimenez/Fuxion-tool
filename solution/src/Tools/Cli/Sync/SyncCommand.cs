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
		var dryRun = new Option<bool>("--dry-run") { Description = "Show what would change, without writing." };
		var offline = new Option<bool>("--offline") { Description = "No network: do not fetch the repositories." };
		var adopt = new Option<bool>("--adopt") { Description = "Take over files written by hand (without the GENERATED header)." };
		var command = new Command("sync", "Generate and update what fx owns here, from the _fx configuration.") { module, dryRun, offline, adopt };
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
		var offline = new Option<bool>("--offline") { Description = "No network: do not ask the remotes." };
		var command = new Command("doctor", "Check, without changing anything, that what fx owns here is right.") { module, offline };
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
		var argument = new Argument<string?>("module") { Description = "Only this module: workspace or dotnet.", Arity = ArgumentArity.ZeroOrOne };
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
			diagnostics.Add(FxDiagnostic.Info(NothingToDo, "No module applies here (no workspace manifest at this level, no _fx/dotnet.yaml)."));

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
			console.Out.WriteLine(problems == 0 ? $"{module.Module}: all right" : $"{module.Module}: {problems} problem(s)");
			return;
		}
		foreach (var action in module.Actions)
			console.Out.WriteLine($"{action.Action,-11} {action.Repository}");
		foreach (var file in module.Files.Where(f => f.Status != SyncFileStatus.Unchanged || console.Settings.Verbose))
			console.Out.WriteLine($"{JsonModule.FileStatus(file.Status),-11} {file.Path}");
		var changed = module.Files.Count(f => f.Status is SyncFileStatus.Written or SyncFileStatus.WouldWrite);
		var refused = module.Files.Count(f => f.Status == SyncFileStatus.Refused);
		var summary = changed == 0 ? "up to date" : dryRun ? $"{changed} file(s) would change" : $"{changed} file(s) written";
		console.Out.WriteLine(refused == 0 ? $"{module.Module}: {summary}" : $"{module.Module}: {summary}, {refused} refused");
	}
}
