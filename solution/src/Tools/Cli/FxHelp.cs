using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Linq;
using System.Reflection;

namespace Fuxion.Tools.Cli;

/// <summary>
/// The help of <c>fx</c> alone and of <c>fx --help</c> (plan O, O3.3): the commands by what they are about, each with
/// its arguments and what it does, the options every command takes and where to look next. Generated from the
/// commands themselves, so it does not go stale; the help of each command is still System.CommandLine's. On stdout,
/// with colours where they can be shown. <c>fx ui</c> will head it when it exists (O4).
/// </summary>
static class FxHelp
{
	// the commands by what they are about; one not listed goes last, under "other"
	static readonly (string Group, string[] Commands)[] Groups =
	[
		("help.group.workspace", ["sync", "doctor", "repo"]),
		("help.group.version", ["version"])
	];

	public static int Write(FxConsole console, RootCommand root, GlobalOptions global)
	{
		var groups = Groups
			.Select(g => (Title: Texts.Get(g.Group), Commands: g.Commands.Select(n => root.Subcommands.FirstOrDefault(c => c.Name == n)).OfType<Command>().ToList()))
			.Append((Title: Texts.Get("help.group.other"), Commands: root.Subcommands.Where(c => !c.Hidden && !Groups.Any(g => g.Commands.Contains(c.Name))).ToList()))
			.Where(g => g.Commands.Count > 0)
			.Select(g => (g.Title, Rows: g.Commands.SelectMany(c => Rows(c, "")).ToList()))
			.ToList();
		var options = global.All
			.Select(o => (Usage: string.Join(", ", o.Aliases.Order().Append(o.Name)) + (o.HelpName is { } name ? $" <{name}>" : ""), Description: o.Description ?? ""))
			.ToList();
		var width = groups.SelectMany(g => g.Rows).Concat(options).Max(r => r.Usage.Length) + 2;

		var version = typeof(FxHelp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
		console.Line($"[bold]fx[/] {FxConsole.Escape(version)} [gray]·[/] {FxConsole.Escape(Texts.Get("root"))}");
		console.Line();
		console.Line($"[bold]{FxConsole.Escape(Texts.Get("help.usage"))}[/] fx <{FxConsole.Escape(Texts.Get("help.command"))}> [[{FxConsole.Escape(Texts.Get("help.options"))}]]");
		foreach (var (title, rows) in groups)
			Section(console, title, rows, width);
		Section(console, Texts.Get("help.group.options"), options, width);
		console.Line();
		console.Line($"[gray]{FxConsole.Escape(Texts.Get("help.more"))}[/]");
		return 0;
	}

	/// <summary>A command and, after it, each subcommand; the arguments as System.CommandLine writes them.</summary>
	static IEnumerable<(string Usage, string Description)> Rows(Command command, string parent)
	{
		var path = parent.Length == 0 ? command.Name : $"{parent} {command.Name}";
		// a command that only groups others (fx repo) has no row of its own
		if (command.Action is not null || command.Subcommands.Count == 0)
			yield return (string.Join(' ', command.Arguments.Where(a => !a.Hidden).Select(Usage).Prepend(path)), command.Description ?? "");
		foreach (var sub in command.Subcommands.Where(c => !c.Hidden))
			foreach (var row in Rows(sub, path))
				yield return row;
	}

	static string Usage(Argument argument)
	{
		var name = $"<{argument.Name}>" + (argument.Arity.MaximumNumberOfValues > 1 ? "..." : "");
		return argument.Arity.MinimumNumberOfValues == 0 ? $"[{name}]" : name;
	}

	static void Section(FxConsole console, string title, IEnumerable<(string Usage, string Description)> rows, int width)
	{
		console.Line();
		console.Line($"[bold]{FxConsole.Escape(title)}[/]");
		foreach (var (usage, description) in rows)
			console.Line($"  [cyan]{FxConsole.Escape(usage)}[/]{new string(' ', width - usage.Length)}{FxConsole.Escape(description)}");
	}

	/// <summary>
	/// <c>--help</c>: on <c>fx</c> alone, this help; on a command, System.CommandLine's. It replaces the action of the
	/// help option, which is recursive: one option for every command.
	/// </summary>
	public sealed class Action(CommandLineAction standard, Func<ParseResult, int> curated) : SynchronousCommandLineAction
	{
		public override int Invoke(ParseResult parseResult)
			=> parseResult.CommandResult.Command is RootCommand ? curated(parseResult) : ((SynchronousCommandLineAction)standard).Invoke(parseResult);
	}
}
