using System;
using System.CommandLine;
using System.Net.Http;
using System.Threading;
using Fuxion.Tool.Cli.Sync;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Self;

namespace Fuxion.Tool.Cli.Self;

public sealed record SelfUpdateSettings(GlobalSettings Global, string? Version, bool Check);

/// <summary>
/// <c>fx self update [--version &lt;X.Y.Z&gt;] [--check]</c> (plan S): fx updated as it was installed. From a release
/// (the install scripts), it updates itself; with dotnet or dnx, it says the command that does.
/// </summary>
public static class SelfCommand
{
	public const string Schema = "fx-self-update/1";

	public static Command Create(GlobalOptions global, string currentDirectory)
	{
		var version = new Option<string?>("--version") { Description = Texts.Get("self.update.version"), HelpName = "X.Y.Z" };
		var check = new Option<bool>("--check") { Description = Texts.Get("self.update.check") };
		var update = new Command("update", Texts.Get("self.update"))
		{
			version,
			check
		}.Describe(Schema, writes: true, network: true);
		update.SetAction(result =>
		{
			var settings = new SelfUpdateSettings(global.Bind(result, currentDirectory), result.GetValue(version), result.GetValue(check));
			var console = new FxConsole(result.InvocationConfiguration.Output, result.InvocationConfiguration.Error, settings.Global);
			return console.Run(token => Run(settings, console, Environment.ProcessPath ?? "", token));
		});
		return new Command("self", Texts.Get("self")) { update };
	}

	/// <param name="executable">The executable of fx that runs: where it is says how it was installed.</param>
	public static int Run(SelfUpdateSettings settings, FxConsole console, string executable, CancellationToken cancellationToken = default)
	{
		var current = SyncCommand.Informational ?? "0.0.0";
		var dotnetHome = Environment.GetEnvironmentVariable("DOTNET_CLI_HOME") is { Length: > 0 } home ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var install = FxInstall.Detect(executable, current, dotnetHome);
		using var http = new HttpClient();
		var result = SelfUpdater.Run(install, current, settings.Version, settings.Check, http,
			settings.Global.IsMachine ? null : (release, rid) => console.ErrorLine($"[gray]{FxConsole.Escape(Texts.Get("self.update.downloading", release, rid))}[/]"),
			cancellationToken);
		if (settings.Global.IsMachine)
			console.WriteJson(new SelfUpdateDocument(Schema, FxConsole.Ok(result.Diagnostics), install.Name, install.Executable, result.Current, result.Release, result.Updated,
				result.Command, JsonDiagnostic.From(result.Diagnostics)), c => c.SelfUpdateDocument);
		else
			WriteHuman(console, settings, result);
		return console.Report(result.Diagnostics);
	}

	static void WriteHuman(FxConsole console, SelfUpdateSettings settings, SelfUpdateResult result)
	{
		if (result.Command is { } command)
			console.Line(FxConsole.Escape(Texts.Get(result.Install.Kind == FxInstallKind.DotnetTool ? "self.update.dotnet-tool" : "self.update.package", command)));
		else if (result.Updated)
			console.Line($"[green]✓[/] {FxConsole.Escape(Texts.Get("self.update.updated", result.Current, result.Release, result.Install.Executable))}");
		else if (result.Release is { } release && FxConsole.Ok(result.Diagnostics))
		{
			var order = FxToolVersion.Compare(release, result.Current);
			console.Line(order == 0 && settings.Version is null ? $"[green]✓[/] {FxConsole.Escape(Texts.Get("self.update.up-to-date", release))}"
				: order == 0 ? $"[green]✓[/] {FxConsole.Escape(Texts.Get("self.update.installed", release))}"
				: order < 0 && settings.Version is null ? FxConsole.Escape(Texts.Get("self.update.newer", result.Current, release))
				: $"[yellow]{FxConsole.Escape(Texts.Get("self.update.available", release, result.Current))}[/]");
		}
	}
}
