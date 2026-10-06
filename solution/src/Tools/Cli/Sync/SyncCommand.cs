using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using System.Text.Json;
using Fuxion.Tools.Core.Dotnet;
using Spectre.Console;

namespace Fuxion.Tools.Cli.Sync;

public sealed record ModuleSettings(GlobalSettings Global, string? Module, bool DryRun);

/// <summary>
/// <c>fx sync [module] [--dry-run]</c> and <c>fx doctor [module]</c> (design §10.2): with a module, only that one (an
/// error if it does not apply); without one, every module that applies here. Only the .NET module exists yet (K4).
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
		var command = new Command("sync", "Generate and update what fx owns here, from the _fx configuration.") { module, dryRun };
		command.SetAction(result => Run(
			new(global.Bind(result, currentDirectory), result.GetValue(module), result.GetValue(dryRun)),
			new(result.InvocationConfiguration.Output, result.InvocationConfiguration.Error, global.Bind(result, currentDirectory)),
			doctor: false));
		return command;
	}

	public static Command CreateDoctor(GlobalOptions global, string currentDirectory)
	{
		var module = ModuleArgument();
		var command = new Command("doctor", "Check, without changing anything, that what fx owns here is right.") { module };
		command.SetAction(result => Run(
			new(global.Bind(result, currentDirectory), result.GetValue(module), DryRun: true),
			new(result.InvocationConfiguration.Output, result.InvocationConfiguration.Error, global.Bind(result, currentDirectory)),
			doctor: true));
		return command;
	}

	static Argument<string?> ModuleArgument()
	{
		var argument = new Argument<string?>("module") { Description = "Only this module: dotnet.", Arity = ArgumentArity.ZeroOrOne };
		argument.AcceptOnlyFromAmong("dotnet");
		return argument;
	}

	public static int Run(ModuleSettings settings, FxConsole console, bool doctor)
	{
		var schema = doctor ? DoctorSchema : SyncSchema;
		var runDotnet = settings.Module == "dotnet" || (settings.Module is null && DotnetModule.Applies(settings.Global.Directory));
		if (!runDotnet)
		{
			Diagnostic[] none = [new(NothingToDo, "warning", "No module applies here (there is no _fx/dotnet.yaml).")];
			if (settings.Global.Output == OutputFormat.Json)
				console.WriteJson(schema, none);
			console.Report(none);
			return 0;
		}

		var dotnet = doctor ? DotnetModule.Doctor(settings.Global.Directory) : DotnetModule.Sync(settings.Global.Directory, settings.DryRun);
		var diagnostics = dotnet.Diagnostics.Select(Diagnostic.From).ToList();

		if (settings.Global.Output == OutputFormat.Json)
			console.WriteJson(schema, diagnostics, json =>
			{
				json.WriteString("repository", dotnet.Repository);
				json.WriteStartObject("dotnet");
				json.WriteStartArray("files");
				foreach (var file in dotnet.Files)
				{
					json.WriteStartObject();
					json.WriteString("path", file.Path);
					json.WriteString("status", StatusName(file.Status));
					json.WriteEndObject();
				}
				json.WriteEndArray();
				json.WriteEndObject();
			});
		else
			WriteHuman(console, dotnet, doctor, settings.DryRun);
		return console.Report(diagnostics);
	}

	static void WriteHuman(FxConsole console, DotnetResult dotnet, bool doctor, bool dryRun)
	{
		if (doctor)
		{
			var problems = dotnet.Diagnostics.Count;
			console.Out.WriteLine(problems == 0 ? "dotnet: all right" : $"dotnet: {problems} problem(s)");
			return;
		}
		foreach (var file in dotnet.Files.Where(f => f.Status != SyncFileStatus.Unchanged || console.Settings.Verbose))
			console.Out.WriteLine($"{StatusName(file.Status),-11} {file.Path}");
		var changed = dotnet.Files.Count(f => f.Status is SyncFileStatus.Written or SyncFileStatus.WouldWrite);
		console.Out.WriteLine(changed == 0 ? "dotnet: up to date" : dryRun ? $"dotnet: {changed} file(s) would change" : $"dotnet: {changed} file(s) written");
	}

	public static string StatusName(SyncFileStatus status) => status switch
	{
		SyncFileStatus.Written => "written",
		SyncFileStatus.WouldWrite => "would-write",
		SyncFileStatus.Refused => "refused",
		_ => "unchanged"
	};
}
