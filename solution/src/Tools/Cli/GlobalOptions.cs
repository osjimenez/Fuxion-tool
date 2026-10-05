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
		Description = "Folder to act on (the current one by default).",
		HelpName = "path",
		Recursive = true
	};

	public Option<string> Output { get; } = CreateOutput();

	public Option<bool> Verbose { get; } = new("--verbose")
	{
		Description = "More detail, on stderr.",
		Recursive = true
	};

	public Option<bool> Plain { get; } = new("--plain")
	{
		Description = "No colors or borders.",
		Recursive = true
	};

	static Option<string> CreateOutput()
	{
		var option = new Option<string>("--output")
		{
			Description = "human (default) or json: a stable document on stdout.",
			HelpName = "format",
			Recursive = true,
			DefaultValueFactory = _ => "human"
		};
		option.AcceptOnlyFromAmong("human", "json");
		return option;
	}

	public void AddTo(Command command)
	{
		command.Add(Root);
		command.Add(Output);
		command.Add(Verbose);
		command.Add(Plain);
	}

	public GlobalSettings Bind(ParseResult result, string currentDirectory)
	{
		var root = result.GetValue(Root);
		return new(
			string.IsNullOrWhiteSpace(root) ? currentDirectory : Path.GetFullPath(root, currentDirectory),
			result.GetValue(Output) == "json" ? OutputFormat.Json : OutputFormat.Human,
			result.GetValue(Verbose),
			result.GetValue(Plain));
	}
}
