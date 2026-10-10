using System.CommandLine;
using System.IO;

namespace Fuxion.Tools.Cli;

public enum OutputFormat
{
	Human,
	Json
}

/// <summary>The options every command accepts (design §10.2), already bound.</summary>
/// <param name="Directory">Where the command acts: <c>--root</c>, or the current directory.</param>
public sealed record GlobalSettings(string Directory, OutputFormat Output, bool Verbose, bool Plain);

/// <summary>The global options, declared once and recursive, and their binder.</summary>
public sealed class GlobalOptions
{
	public Option<string?> Root { get; } = new("--root")
	{
		Description = Texts.Get("option.root"),
		HelpName = Texts.Get("option.root.name"),
		Recursive = true
	};

	public Option<string> Output { get; } = CreateOutput();

	public Option<bool> Verbose { get; } = new("--verbose")
	{
		Description = Texts.Get("option.verbose"),
		Recursive = true
	};

	public Option<bool> Plain { get; } = new("--plain")
	{
		Description = Texts.Get("option.plain"),
		Recursive = true
	};

	static Option<string> CreateOutput()
	{
		var option = new Option<string>("--output")
		{
			Description = Texts.Get("option.output"),
			HelpName = Texts.Get("option.output.name"),
			Recursive = true,
			DefaultValueFactory = _ => "human"
		};
		option.AcceptOnlyFromAmong("human", "json");
		return option;
	}

	public Option<bool> Workspace { get; } = new("--workspace", "-w")
	{
		Description = Texts.Get("option.workspace"),
		Recursive = true
	};

	/// <summary><c>--lang en|es</c>: read before the commands are built (<see cref="FxLanguage"/>); here for the help and to validate it.</summary>
	public Option<string> Lang { get; } = CreateLang();

	static Option<string> CreateLang()
	{
		var option = new Option<string>("--lang")
		{
			Description = Texts.Get("option.lang"),
			HelpName = Texts.Get("option.lang.name"),
			Recursive = true
		};
		option.AcceptOnlyFromAmong([.. FxLanguage.Supported]);
		return option;
	}

	public void AddTo(Command command)
	{
		command.Add(Root);
		command.Add(Workspace);
		command.Add(Output);
		command.Add(Verbose);
		command.Add(Plain);
		command.Add(Lang);
	}

	public GlobalSettings Bind(ParseResult result, string currentDirectory)
	{
		var root = result.GetValue(Root);
		var directory = string.IsNullOrWhiteSpace(root) ? currentDirectory : Path.GetFullPath(root, currentDirectory);
		if (result.GetValue(Workspace) && Fuxion.Tools.Core.Workspace.WorkspaceManifest.FindRoot(directory) is { } workspace)
			directory = workspace;
		return new(
			directory,
			result.GetValue(Output) == "json" ? OutputFormat.Json : OutputFormat.Human,
			result.GetValue(Verbose),
			result.GetValue(Plain));
	}
}
