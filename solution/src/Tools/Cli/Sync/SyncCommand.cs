using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using System.Text.Json;
using Fuxion.Tools.Core.Dotnet;
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
		argument.AcceptOnlyFromAmong("workspace", "dotnet");
		return argument;
	}

	public static int Run(ModuleSettings settings, FxConsole console, bool doctor)
	{
		var schema = doctor ? DoctorSchema : SyncSchema;
		var directory = settings.Global.Directory;
		var runWorkspace = settings.Module == "workspace" || (settings.Module is null && WorkspaceModule.Applies(directory));
		var runDotnet = settings.Module == "dotnet" || (settings.Module is null && DotnetModule.Applies(directory));
		if (!runWorkspace && !runDotnet)
		{
			Diagnostic[] none = [new(NothingToDo, "warning", "No module applies here (no workspace manifest at this level, no _fx/dotnet.yaml).")];
			if (settings.Global.Output == OutputFormat.Json)
				console.WriteJson(schema, none);
			console.Report(none);
			return 0;
		}

		var workspace = runWorkspace
			? doctor ? WorkspaceModule.Doctor(directory, settings.Offline) : WorkspaceModule.Sync(directory, new(settings.DryRun, settings.Offline, settings.Adopt))
			: null;
		var dotnet = runDotnet
			? doctor ? DotnetModule.Doctor(directory) : DotnetModule.Sync(directory, settings.DryRun)
			: null;
		var diagnostics = (workspace?.Diagnostics ?? []).Concat(dotnet?.Diagnostics ?? []).Select(Diagnostic.From).ToList();

		if (settings.Global.Output == OutputFormat.Json)
			console.WriteJson(schema, diagnostics, json =>
			{
				if (workspace is not null)
				{
					json.WriteStartObject("workspace");
					json.WriteString("root", workspace.Root);
					json.WriteStartArray("repositories");
					foreach (var action in workspace.Actions)
					{
						json.WriteStartObject();
						json.WriteString("name", action.Repository);
						json.WriteString("action", action.Action);
						json.WriteEndObject();
					}
					json.WriteEndArray();
					WriteFiles(json, workspace.Files);
					json.WriteEndObject();
				}
				if (dotnet is not null)
				{
					json.WriteString("repository", dotnet.Repository);
					json.WriteStartObject("dotnet");
					WriteFiles(json, dotnet.Files);
					json.WriteEndObject();
				}
			});
		else
		{
			if (workspace is not null)
				WriteHuman(console, "workspace", workspace.Files, workspace.Diagnostics.Count, doctor, settings.DryRun,
					workspace.Actions.Select(a => $"{a.Action,-11} {a.Repository}"));
			if (dotnet is not null)
				WriteHuman(console, "dotnet", dotnet.Files, dotnet.Diagnostics.Count, doctor, settings.DryRun, []);
		}
		return console.Report(diagnostics);
	}

	static void WriteFiles(Utf8JsonWriter json, IReadOnlyList<SyncFile> files)
	{
		json.WriteStartArray("files");
		foreach (var file in files)
		{
			json.WriteStartObject();
			json.WriteString("path", file.Path);
			json.WriteString("status", StatusName(file.Status));
			json.WriteEndObject();
		}
		json.WriteEndArray();
	}

	static void WriteHuman(FxConsole console, string module, IReadOnlyList<SyncFile> files, int problems, bool doctor, bool dryRun, IEnumerable<string> actions)
	{
		if (doctor)
		{
			console.Out.WriteLine(problems == 0 ? $"{module}: all right" : $"{module}: {problems} problem(s)");
			return;
		}
		foreach (var action in actions)
			console.Out.WriteLine(action);
		foreach (var file in files.Where(f => f.Status != SyncFileStatus.Unchanged || console.Settings.Verbose))
			console.Out.WriteLine($"{StatusName(file.Status),-11} {file.Path}");
		var changed = files.Count(f => f.Status is SyncFileStatus.Written or SyncFileStatus.WouldWrite);
		var refused = files.Count(f => f.Status == SyncFileStatus.Refused);
		var summary = changed == 0 ? "up to date" : dryRun ? $"{changed} file(s) would change" : $"{changed} file(s) written";
		console.Out.WriteLine(refused == 0 ? $"{module}: {summary}" : $"{module}: {summary}, {refused} refused");
	}

	public static string StatusName(SyncFileStatus status) => status switch
	{
		SyncFileStatus.Written => "written",
		SyncFileStatus.WouldWrite => "would-write",
		SyncFileStatus.Refused => "refused",
		_ => "unchanged"
	};
}
