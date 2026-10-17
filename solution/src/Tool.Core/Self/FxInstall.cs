using System;
using System.IO;
using Fuxion.Tool.Core.Layout;

namespace Fuxion.Tool.Core.Self;

/// <summary>How the fx that runs was installed, which says how it is updated (plan S).</summary>
public enum FxInstallKind
{
	/// <summary>From a release on GitHub (the install scripts, or its archive by hand): fx updates itself.</summary>
	Release,

	/// <summary>With <c>dotnet tool install</c>, global or in a folder: dotnet updates it.</summary>
	DotnetTool,

	/// <summary>Run from its NuGet package in the cache (<c>dnx</c>): there is nothing to update, dnx runs the version it is given.</summary>
	Package,

	/// <summary>Built from its sources: its version was never published.</summary>
	Local,

	/// <summary>Not an executable of fx (fx in process, as in its tests): nothing to update.</summary>
	Unknown
}

/// <summary>
/// The installation of the fx that runs, told by where its executable is: a tool of dotnet is in the <c>.store</c> of
/// its folder (<c>~/.dotnet/tools/.store/fuxion.tool/0.7.4/fuxion.tool.win-x64/0.7.4/tools/any/win-x64/fx.exe</c>),
/// and dnx runs it from the cache of NuGet with the same layout (<c>~/.nuget/packages/fuxion.tool.win-x64/0.7.4/tools/any/win-x64/</c>).
/// </summary>
/// <param name="ToolPath">The folder of a tool of dotnet (the one above <c>.store</c>).</param>
/// <param name="Global">Whether that folder is the one of the global tools (<c>-g</c>).</param>
public sealed record FxInstall(FxInstallKind Kind, string Executable, string? ToolPath = null, bool Global = false)
{
	/// <summary>The package of fx in NuGet.</summary>
	public const string Package = "Fuxion.Tool";

	/// <param name="executable">The executable that runs (<see cref="Environment.ProcessPath"/>).</param>
	/// <param name="informational">The informational version of fx (<see cref="FxToolVersion"/>).</param>
	/// <param name="dotnetHome">Where dotnet keeps its global tools (<c>DOTNET_CLI_HOME</c>, or the folder of the user).</param>
	public static FxInstall Detect(string executable, string informational, string dotnetHome)
	{
		// the same length, so a position in it is one in the executable
		var path = executable.Replace('\\', '/');
		var store = path.IndexOf($"/.store/{Package}/", StringComparison.OrdinalIgnoreCase);
		if (store > 0)
		{
			var toolPath = executable[..store];
			return new(FxInstallKind.DotnetTool, executable, toolPath, FxLayout.SameFolder(toolPath, Path.Combine(dotnetHome, ".dotnet", "tools")));
		}
		// a package of a platform (fuxion.tool.win-x64), with the layout of a tool: tools/any/<rid>/fx
		if (path.Contains($"/{Package}.", StringComparison.OrdinalIgnoreCase) && path.Contains("/tools/any/", StringComparison.OrdinalIgnoreCase))
			return new(FxInstallKind.Package, executable);
		if (FxToolVersion.IsLocal(informational))
			return new(FxInstallKind.Local, executable);
		return Path.GetFileNameWithoutExtension(executable).Equals("fx", StringComparison.OrdinalIgnoreCase)
			? new(FxInstallKind.Release, executable)
			: new(FxInstallKind.Unknown, executable);
	}

	/// <summary>The name of <see cref="Kind"/> in the JSON of fx.</summary>
	public string Name => Kind switch
	{
		FxInstallKind.Release => "release",
		FxInstallKind.DotnetTool => "dotnet-tool",
		FxInstallKind.Package => "package",
		FxInstallKind.Local => "local",
		_ => "unknown"
	};

	/// <summary>The command that updates it, when fx does not do it itself (a tool of dotnet, dnx); to <paramref name="version"/>, or the latest.</summary>
	public string? Command(string? version) => Kind switch
	{
		FxInstallKind.DotnetTool => (Global ? "dotnet tool update -g " : $"dotnet tool update --tool-path \"{ToolPath}\" ") + Package + (version is null ? "" : $" --version {version}"),
		FxInstallKind.Package => $"dnx {Package}" + (version is null ? "" : $"@{version}"),
		_ => null
	};
}
