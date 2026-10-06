using System.CommandLine;
using System.IO;
using Fuxion.Tools.Cli.Sync;
using Fuxion.Tools.Cli.Version;

namespace Fuxion.Tools.Cli;

/// <summary>The <c>fx</c> command line (design §10.2). Writers and directory come in, so tests run it in process.</summary>
public static class FxApp
{
	public static RootCommand Build(string currentDirectory)
	{
		var global = new GlobalOptions();
		var root = new RootCommand("fx: the Fuxion workspace tool.")
		{
			SyncCommand.CreateSync(global, currentDirectory),
			SyncCommand.CreateDoctor(global, currentDirectory),
			VersionCommand.Create(global, currentDirectory)
		};
		global.AddTo(root);
		return root;
	}

	public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string currentDirectory)
		=> Build(currentDirectory).Parse(args).Invoke(new() { Output = stdout, Error = stderr });
}
