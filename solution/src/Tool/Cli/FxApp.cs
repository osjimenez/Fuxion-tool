using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using Fuxion.Tool.Cli.Repo;
using Fuxion.Tool.Cli.Sync;
using Fuxion.Tool.Cli.Version;

namespace Fuxion.Tool.Cli;

/// <summary>The <c>fx</c> command line (design §10.2). Writers and directory come in, so tests run it in process.</summary>
public static class FxApp
{
	public static RootCommand Build(string currentDirectory)
	{
		var global = new GlobalOptions();
		var root = new RootCommand(Texts.Get("root"))
		{
			SyncCommand.CreateSync(global, currentDirectory),
			SyncCommand.CreateDoctor(global, currentDirectory),
			RepoCommand.Create(global, currentDirectory),
			ManifestCommands.CreateModule(global, currentDirectory),
			ManifestCommands.CreatePath(global, currentDirectory),
			Worktree.WorktreeCommand.Create(global, currentDirectory),
			Ui.UiCommand.Create(global, currentDirectory),
			VersionCommand.Create(global, currentDirectory)
		};
		foreach (var command in Describe.DescribeCommand.Create(global, currentDirectory))
			root.Add(command);
		global.AddTo(root);
		// fx alone, and fx --help: fx's own help (plan O, O3.3); fx alone is not an error
		int Help(ParseResult result)
			=> FxHelp.Write(new(result.InvocationConfiguration.Output, result.InvocationConfiguration.Error, global.Bind(result, currentDirectory)), root, global);
		root.SetAction(Help);
		foreach (var help in root.Options.OfType<HelpOption>())
			help.Action = new FxHelp.Action(help.Action!, Help);
		return root;
	}

	/// <summary>Runs fx in the language of the arguments, FX_LANG or the system (decided before the help is built).</summary>
	public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string currentDirectory)
	{
		FxLanguage.Apply(FxLanguage.Resolve(args));
		var parse = Build(currentDirectory).Parse(args);
		// a usage error (an unknown command or option, a missing value): the error, where the help is, and exit code 2
		if (parse.Action is not ParseErrorAction error)
			return parse.Invoke(new() { Output = stdout, Error = stderr });
		error.ShowHelp = false;
		parse.Invoke(new() { Output = stdout, Error = stderr });
		stderr.WriteLine(Texts.Get("usage.help"));
		return FxConsole.UsageError;
	}
}
