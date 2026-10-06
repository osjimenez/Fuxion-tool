using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Fuxion.Tools.Core.Processes;
using Xunit;

namespace Fuxion.Tools.Test;

/// <summary>
/// Packs Fuxion.Tools.Sdk once (from the build of the test run, with a version of its own) into a temporary feed, and
/// builds sample repositories that use it with dotnet build or with the MSBuild of Visual Studio.
/// </summary>
public sealed class SdkFixture : IDisposable
{
	static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);

	public SdkFixture()
	{
		Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fx-sdk-" + Guid.NewGuid().ToString("N")[..12])).FullName;
		Feed = Directory.CreateDirectory(Path.Combine(Root, "feed")).FullName;
		Packages = Path.Combine(Root, "packages");
		Version = $"0.0.0-test.{DateTime.UtcNow:yyyyMMddHHmmss}";

		var repository = FindRepository();
		var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
		var pack = Dotnet(repository, "pack", Path.Combine(repository, "solution", "src", "Tools.Sdk", "Fuxion.Tools.Sdk.csproj"),
			"-c", configuration, "--no-build", "-o", Feed, $"-p:Version={Version}", "-nologo");
		if (pack.ExitCode != 0)
			throw new InvalidOperationException("Packing Fuxion.Tools.Sdk failed:\n" + pack.StandardOutput + pack.StandardError);

		MSBuildExe = FindVisualStudioMSBuild();
	}

	/// <summary>Temporary folder of the fixture: feed, packages and workspaces.</summary>
	public string Root { get; }

	public string Feed { get; }

	public string Packages { get; }

	/// <summary>Version of the packed SDK.</summary>
	public string Version { get; }

	/// <summary>MSBuild.exe of the newest Visual Studio (the .NET Framework MSBuild the IDE uses), if there is one.</summary>
	public string? MSBuildExe { get; }

	/// <summary>global.json, nuget.config and Directory.Build.props of a repository that uses the SDK.</summary>
	public void SetUp(TempGitRepository repo, string directoryBuildProps)
	{
		repo.WriteFile("global.json", $$"""
			{
			  "msbuild-sdks": { "Fuxion.Tools.Sdk": "{{Version}}" }
			}
			""");
		var userPackages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
		repo.WriteFile("nuget.config", $"""
			<?xml version="1.0" encoding="utf-8"?>
			<configuration>
				<config><add key="globalPackagesFolder" value="{Packages}" /></config>
				<fallbackPackageFolders><add key="user" value="{userPackages}" /></fallbackPackageFolders>
				<packageSources>
					<clear />
					<add key="sdk" value="{Feed}" />
					<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
				</packageSources>
			</configuration>
			""");
		repo.WriteFile(Path.Combine("solution", "Directory.Build.props"), directoryBuildProps);
	}

	public ProcessResult Build(string directory, params string[] args)
		=> Dotnet(directory, ["build", .. args, "-nologo", "-nodeReuse:false", "--disable-build-servers"]);

	public ProcessResult BuildWithVisualStudio(string directory, params string[] args)
		=> ProcessRunner.Run(MSBuildExe!, directory, [.. args, "-restore", "-nologo", "-nodeReuse:false"], BuildTimeout, CleanEnvironment());

	static ProcessResult Dotnet(string directory, params string[] args)
		=> ProcessRunner.Run("dotnet", directory, args, BuildTimeout, CleanEnvironment());

	// The test host runs under dotnet test: its MSBuild and Microsoft.Testing.Platform variables must not leak into
	// the processes it starts (a nested dotnet test would try to talk to the outer one), and neither must the CI
	// variables (the versioning reads them).
	public static IReadOnlyDictionary<string, string?> CleanEnvironment()
	{
		var environment = new Dictionary<string, string?>
		{
			["CI"] = null,
			["GITHUB_ACTIONS"] = null,
			["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
			["DOTNET_NOLOGO"] = "1"
		};
		foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>())
			if (name.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase) || name.StartsWith("MSBUILD", StringComparison.Ordinal)
			    || name.StartsWith("TESTINGPLATFORM", StringComparison.OrdinalIgnoreCase))
				environment[name] = null;
		// No MSBuild node outlives a build. Through the environment because dotnet test passes the arguments it does
		// not know (like -nodeReuse:false) on to the test application, which rejects them.
		environment["MSBUILDDISABLENODEREUSE"] = "1";
		return environment;
	}

	static string FindRepository()
	{
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
			if (File.Exists(Path.Combine(dir.FullName, "FxTool.slnx")))
				return dir.FullName;
		throw new InvalidOperationException("FxTool.slnx not found above " + AppContext.BaseDirectory);
	}

	static string? FindVisualStudioMSBuild()
	{
		var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
		if (!OperatingSystem.IsWindows() || !File.Exists(vswhere))
			return null;
		var result = ProcessRunner.Run(vswhere, Path.GetTempPath(),
			["-latest", "-prerelease", "-requires", "Microsoft.Component.MSBuild", "-find", @"MSBuild\**\Bin\MSBuild.exe"], TimeSpan.FromSeconds(30));
		return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.FirstOrDefault(File.Exists);
	}

	public void Dispose()
	{
		// git writes read-only files, and an MSBuild process may still hold the task assembly for a moment
		for (var attempt = 0; attempt < 5 && Directory.Exists(Root); attempt++)
		{
			try
			{
				foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
					File.SetAttributes(file, FileAttributes.Normal);
				Directory.Delete(Root, true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				Thread.Sleep(1000);
			}
		}
	}
}
