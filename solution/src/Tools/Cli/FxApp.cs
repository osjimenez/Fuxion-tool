using System.CommandLine;
using System.IO;
using Fuxion.Tools.Cli.Repo;
using Fuxion.Tools.Cli.Sync;
using Fuxion.Tools.Cli.Version;

namespace Fuxion.Tools.Cli;

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
			VersionCommand.Create(global, currentDirectory)
		};
		global.AddTo(root);
		return root;
	}

	/// <summary>Runs fx in the language of the arguments, FX_LANG or the system (decided before the help is built).</summary>
	public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string currentDirectory)
	{
		FxLanguage.Apply(FxLanguage.Resolve(args));
		return Build(currentDirectory).Parse(args).Invoke(new() { Output = stdout, Error = stderr });
	}
}
