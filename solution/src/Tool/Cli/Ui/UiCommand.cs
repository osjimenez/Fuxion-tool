using System.CommandLine;
using Fuxion.Tool.Cli.Mounts;
using Fuxion.Tool.Core.Explore;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using XenoAtom.Terminal.UI;

namespace Fuxion.Tool.Cli.Ui;

/// <summary>
/// <c>fx ui</c> (plan O, O4): the space in full screen. Only in a terminal with a person: without one (CI, a pipe,
/// an agent, <c>--output json</c>) it says so and ends with wrong usage. Ctrl+Q closes it.
/// </summary>
public static class UiCommand
{
	public const string NoTerminal = "ui.no-terminal";

	public static Command Create(GlobalOptions global, string currentDirectory)
	{
		var command = new Command("ui", Texts.Get("ui")).Describe(null, interactive: true);
		command.SetAction(result =>
		{
			var console = MountCommand.Console(result, global, currentDirectory);
			if (console.Settings.IsMachine || !console.Capabilities.CanPrompt || !console.Capabilities.CanAnimate)
			{
				console.ErrorLine($"[red]{FxConsole.Escape(Texts.Get(NoTerminal))}[/]");
				return FxConsole.UsageError;
			}
			if (FxTree.Build(console.Settings.Directory) is not { } model)
				return console.Report([FxDiagnostic.Error(FxNode.NotARoot, new(FxNode.NotARoot, console.Settings.Directory))]);
			var app = new UiApp(model, console.Settings.Directory);
			app.Start();
			try
			{
				using var session = XenoAtom.Terminal.Terminal.Open();
				XenoAtom.Terminal.Terminal.Run(app.Root, app.OnUpdate);
			}
			finally
			{
				app.Stop();
			}
			return 0;
		});
		return command;
	}
}
