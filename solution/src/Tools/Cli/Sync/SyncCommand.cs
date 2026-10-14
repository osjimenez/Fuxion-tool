using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Fuxion.Tools.Core.Dotnet;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Sync;
using Fuxion.Tools.Core.Workspace;

namespace Fuxion.Tools.Cli.Sync;

public sealed record ModuleSettings(GlobalSettings Global, string? Module, bool DryRun, bool Offline = false, bool Adopt = false, bool OnlyHere = false,
	string? ParentRef = null);

/// <summary>
/// <c>fx sync [module]</c> and <c>fx doctor [module]</c> (plan P, decision 17 and proposal B): the root of fx where
/// they run and everything mounted below; with a module, only that one.
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
		var (only, parentRef) = LevelOptions();
		var command = new Command("sync", Texts.Get("sync")) { module, dryRun, offline, adopt, only, parentRef }.Describe(SyncSchema, writes: true, network: true);
		command.SetAction((result, cancellationToken) =>
		{
			var settings = global.Bind(result, currentDirectory);
			return Task.FromResult(Run(new(settings, result.GetValue(module), result.GetValue(dryRun), result.GetValue(offline), result.GetValue(adopt), result.GetValue(only),
				result.GetValue(parentRef)),
				new(result.InvocationConfiguration.Output, result.InvocationConfiguration.Error, settings), doctor: false, cancellationToken));
		});
		return command;
	}

	public static Command CreateDoctor(GlobalOptions global, string currentDirectory)
	{
		var module = ModuleArgument();
		var offline = new Option<bool>("--offline") { Description = Texts.Get("doctor.offline") };
		var (only, parentRef) = LevelOptions();
		var command = new Command("doctor", Texts.Get("doctor")) { module, offline, only, parentRef }.Describe(DoctorSchema, network: true);
		command.SetAction((result, cancellationToken) =>
		{
			var settings = global.Bind(result, currentDirectory);
			return Task.FromResult(Run(new(settings, result.GetValue(module), DryRun: true, Offline: result.GetValue(offline), OnlyHere: result.GetValue(only),
				ParentRef: result.GetValue(parentRef)),
				new(result.InvocationConfiguration.Output, result.InvocationConfiguration.Error, settings), doctor: true, cancellationToken));
		});
		return command;
	}

	/// <summary><c>--only</c> (this root alone) and <c>--parent-ref</c> (the branch of the parent, from its remote).</summary>
	static (Option<bool> Only, Option<string?> ParentRef) LevelOptions()
		=> (new("--only") { Description = Texts.Get("sync.only") }, new("--parent-ref") { Description = Texts.Get("sync.parent-ref"), HelpName = "branch" });

	static Argument<string?> ModuleArgument()
	{
		var argument = new Argument<string?>("module") { Description = Texts.Get("module"), Arity = ArgumentArity.ZeroOrOne };
		argument.AcceptOnlyFromAmong(WorkspaceModule.Name, DotnetModule.Name);
		return argument;
	}

	public static int Run(ModuleSettings settings, FxConsole console, bool doctor, CancellationToken cancellationToken = default)
		=> console.Run(token => Execute(settings, console, doctor, token), cancellationToken);

	/// <summary>The version of fx, for <c>requires</c> (plan P, decision 4) and what <c>fx describe</c> says.</summary>
	internal static string? Version => typeof(SyncCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];

	static int Execute(ModuleSettings settings, FxConsole console, bool doctor, CancellationToken cancellationToken)
	{
		var schema = doctor ? DoctorSchema : SyncSchema;
		var directory = settings.Global.Directory;
		var options = new SyncOptions(settings.DryRun, settings.Offline, settings.Adopt, settings.Module, Version, settings.OnlyHere, settings.ParentRef);
		var modules = console.WithProgress((progress, cancel) => doctor
			? SyncEngine.Doctor(directory, options, progress, cancel)
			: SyncEngine.Sync(directory, options, progress, cancel), cancellationToken).ToList();
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
		foreach (var action in console.NotShown(module.Actions))
		{
			var color = action.Action.EndsWith("failed") || action.Action == "missing" ? "red" : action.Action == "superseded" ? "gray" : "green";
			var detail = action.Detail is null ? "" : $" [gray]({FxConsole.Escape(action.Detail.ToString())})[/]";
			console.Line($"[{color}]{FxConsole.Escape(Texts.Get($"action.{action.Action}")),-11}[/] {FxConsole.Escape(action.Repository)}{detail}");
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
