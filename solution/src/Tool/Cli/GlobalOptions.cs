using System.Collections.Generic;
using System.CommandLine;
using System.IO;

namespace Fuxion.Tool.Cli;

public enum OutputFormat
{
	/// <summary>For people: rich in an interactive terminal, plain elsewhere.</summary>
	Human,

	/// <summary>One JSON document on stdout, with a versioned schema.</summary>
	Json,

	/// <summary>One JSON event per line as the work goes on; the last line is the document of <see cref="Json"/>.</summary>
	Ndjson
}

/// <summary>The options every command accepts (design §10.2), already bound.</summary>
/// <param name="Directory">Where the command acts: <c>--root</c>, or the current directory.</param>
public sealed record GlobalSettings(string Directory, OutputFormat Output, bool Verbose, bool Plain, bool NonInteractive = false)
{
	/// <summary>The output is for a machine (JSON or NDJSON): no human text on stdout.</summary>
	public bool IsMachine => Output != OutputFormat.Human;
}

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

	public Option<bool> Verbose { get; } = new("--verbose", "-v")
	{
		Description = Texts.Get("option.verbose"),
		Recursive = true
	};

	public Option<bool> Plain { get; } = new("--plain")
	{
		Description = Texts.Get("option.plain"),
		Recursive = true
	};

	/// <summary>No questions and no live region, whatever the terminal (plan O, decision 4).</summary>
	public Option<bool> NonInteractive { get; } = new("--non-interactive")
	{
		Description = Texts.Get("option.non-interactive"),
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
		option.AcceptOnlyFromAmong("human", "json", "ndjson");
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

	/// <summary>Every global option, in the order of the help.</summary>
	public IEnumerable<Option> All => [Root, Workspace, Output, Verbose, Plain, NonInteractive, Lang];

	public void AddTo(Command command)
	{
		foreach (var option in All)
			command.Add(option);
	}

	public GlobalSettings Bind(ParseResult result, string currentDirectory)
	{
		var root = result.GetValue(Root);
		// real (without symbolic links), as git names its folders: on macOS /var and /tmp are links
		var directory = Fuxion.Tool.Core.Layout.FxLayout.Real(string.IsNullOrWhiteSpace(root) ? currentDirectory : Path.GetFullPath(root, currentDirectory));
		if (result.GetValue(Workspace) && Fuxion.Tool.Core.Workspace.WorkspaceManifest.FindRoot(directory) is { } workspace)
			directory = workspace;
		return new(
			directory,
			result.GetValue(Output) switch
			{
				"json" => OutputFormat.Json,
				"ndjson" => OutputFormat.Ndjson,
				_ => OutputFormat.Human
			},
			result.GetValue(Verbose),
			result.GetValue(Plain),
			result.GetValue(NonInteractive));
	}
}
