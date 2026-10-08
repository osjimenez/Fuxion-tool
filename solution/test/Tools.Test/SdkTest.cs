using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Fuxion.Tools.Cli;
using Fuxion.Tools.Core.Processes;
using Xunit;

namespace Fuxion.Tools.Test;

/// <summary>Fuxion.Tools.Sdk used by real projects (plan K, K3.2): each test builds a sample repository.</summary>
public sealed class SdkTest(SdkFixture fixture) : IClassFixture<SdkFixture>
{
	const string UsesMetadata = """
		namespace Lib;

		/// <summary>Sample.</summary>
		public static class Info
		{
			/// <summary>From the static metadata.</summary>
			public static string Version => Fuxion.Metadata.Lib.Versioning.InformationalVersion + " " + Fuxion.Metadata.Lib.Repository.Branch;
		}
		""";

	const string Plain = """
		namespace Lib;

		/// <summary>Sample.</summary>
		public static class Info;
		""";

	/// <summary>
	/// A repository with a packable library (net10.0 and net11.0) that uses the SDK and publishes locally
	/// (FxLocalPublish); tagged version/2.3.0 on its first commit and with one more commit, so its version is 2.3.1.
	/// </summary>
	TempGitRepository Sample(string? path = null, bool tag = true, string properties = "", string code = UsesMetadata)
	{
		var repo = new TempGitRepository(path: path);
		fixture.SetUp(repo, $"""
			<Project>
				<PropertyGroup>
					<IsPackable>true</IsPackable>
					<GenerateDocumentationFile>true</GenerateDocumentationFile>
					<IncludeSymbols>true</IncludeSymbols>
					<SymbolPackageFormat>snupkg</SymbolPackageFormat>
					<FxLocalPublish>true</FxLocalPublish>
					{properties}
				</PropertyGroup>
				<Import Project="Sdk.props" Sdk="Fuxion.Tools.Sdk" />
			</Project>
			""");
		repo.WriteFile(Path.Combine("solution", "src", "Lib", "Lib.csproj"), """
			<Project Sdk="Microsoft.NET.Sdk">
				<PropertyGroup>
					<TargetFrameworks>net10.0;net11.0</TargetFrameworks>
					<Authors>Fuxion</Authors>
				</PropertyGroup>
			</Project>
			""");
		repo.WriteFile(Path.Combine("solution", "src", "Lib", "Info.cs"), code);
		repo.CommitAll("c1");
		if (tag)
			repo.Tag("version/2.3.0");
		repo.Commit("c2");
		return repo;
	}

	static string Lib(TempGitRepository repo) => Path.Combine(repo.Path, "solution", "src", "Lib");

	static string MetadataFile(TempGitRepository repo, string tfm = "net10.0") => Path.Combine(Lib(repo), "obj", "Debug", tfm, "Fuxion.StaticMetadata.g.cs");

	static FileVersionInfo Assembly(TempGitRepository repo, string tfm = "net10.0") => FileVersionInfo.GetVersionInfo(Path.Combine(Lib(repo), "bin", "Debug", tfm, "Lib.dll"));

	static void Succeeded(ProcessResult result) => Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);

	[Fact(DisplayName = "build: the version from git, in the assembly and in the static metadata")]
	public void Build()
	{
		using var repo = Sample();
		Succeeded(fixture.Build(Lib(repo)));

		var assembly = Assembly(repo);
		Assert.Equal("2.3.1.0", assembly.FileVersion);
		Assert.StartsWith("2.3.1+local", assembly.ProductVersion);
		foreach (var tfm in new[] { "net10.0", "net11.0" })
		{
			var metadata = File.ReadAllText(MetadataFile(repo, tfm));
			Assert.Contains("public static readonly string Version = \"2.3.1\";", metadata);
			Assert.Contains("public static readonly string Branch = \"main\";", metadata);
			Assert.Contains($"public static readonly string TargetFramework = \"{tfm}\";", metadata);
			// the build date is the date of the commit: the same commit, the same file
			Assert.Contains($"new global::System.DateTime({repo.LastCommitDate.UtcDateTime.Ticks}L, global::System.DateTimeKind.Utc)", metadata);
		}
	}

	[Fact(DisplayName = "build again without changes: the static metadata is not rewritten")]
	public void Build_Incremental()
	{
		using var repo = Sample();
		Succeeded(fixture.Build(Lib(repo)));
		var written = File.GetLastWriteTimeUtc(MetadataFile(repo));
		Succeeded(fixture.Build(Lib(repo)));
		Assert.Equal(written, File.GetLastWriteTimeUtc(MetadataFile(repo)));
	}

	[Fact(DisplayName = "FxLocalPublish outside a workspace: <repo>/~$publish/nupkgs, with the version and the docs")]
	public void Pack()
	{
		using var repo = Sample();
		Succeeded(fixture.Build(Lib(repo), "-t:Pack"));

		var package = Path.Combine(repo.Path, "~$publish", "nupkgs", "Lib.2.3.1.nupkg");
		Assert.True(File.Exists(package), string.Join(", ", Directory.EnumerateFiles(repo.Path, "*.nupkg", SearchOption.AllDirectories)));
		Assert.True(File.Exists(Path.ChangeExtension(package, ".snupkg")));
		using var zip = ZipFile.OpenRead(package);
		var entries = zip.Entries.Select(e => e.FullName).ToList();
		Assert.Contains("lib/net10.0/Lib.dll", entries);
		Assert.Contains("lib/net11.0/Lib.xml", entries);
	}

	[Fact(DisplayName = "without FxLocalPublish the SDK leaves PackageOutputPath alone (design D-37)")]
	public void Pack_NotLocal()
	{
		using var repo = Sample(properties: "<FxLocalPublish>false</FxLocalPublish>");
		Succeeded(fixture.Build(Lib(repo), "-t:Pack"));
		Assert.True(File.Exists(Path.Combine(Lib(repo), "bin", "Release", "Lib.2.3.1.nupkg")) || File.Exists(Path.Combine(Lib(repo), "bin", "Debug", "Lib.2.3.1.nupkg")),
			string.Join(", ", Directory.EnumerateFiles(repo.Path, "*.nupkg", SearchOption.AllDirectories)));
		Assert.False(Directory.Exists(Path.Combine(repo.Path, "~$publish")));
	}

	[Fact(DisplayName = "FxLocalPublish in a workspace: <workspace>/~$publish/<repo>/nupkgs")]
	public void Pack_Workspace()
	{
		var workspace = Directory.CreateDirectory(Path.Combine(fixture.Root, "ws-" + Guid.NewGuid().ToString("N")[..8])).FullName;
		Directory.CreateDirectory(Path.Combine(workspace, "_fx"));
		File.WriteAllText(Path.Combine(workspace, "_fx", "workspace.yaml"), "version: 1\n");
		using var repo = Sample(path: Path.Combine(workspace, "lib", "repo"));
		Succeeded(fixture.Build(Lib(repo), "-t:Pack"));

		Assert.True(File.Exists(Path.Combine(workspace, "~$publish", "lib", "nupkgs", "Lib.2.3.1.nupkg")));
		Assert.False(Directory.Exists(Path.Combine(repo.Path, "~$publish")));
	}

	[Fact(DisplayName = "FxLocalPublish in a workspace from a repo not at <name>/repo (a submodule): <workspace>/~$publish/<folder>/nupkgs")]
	public void Pack_WorkspaceSubmodule()
	{
		var workspace = Directory.CreateDirectory(Path.Combine(fixture.Root, "ws-" + Guid.NewGuid().ToString("N")[..8])).FullName;
		Directory.CreateDirectory(Path.Combine(workspace, "_fx"));
		File.WriteAllText(Path.Combine(workspace, "_fx", "workspace.yaml"), "version: 1\n");
		using var repo = Sample(path: Path.Combine(workspace, "plus", "repo", "lib"));
		Succeeded(fixture.Build(Lib(repo), "-t:Pack"));

		Assert.True(File.Exists(Path.Combine(workspace, "~$publish", "lib", "nupkgs", "Lib.2.3.1.nupkg")));
	}

	[Fact(DisplayName = "FxDocumentationLanguages: the English XML copied to ~$docs/en, the translations packed")]
	public void TranslatedDocumentation()
	{
		using var repo = Sample(properties: "<FxDocumentationLanguages>es</FxDocumentationLanguages>");
		repo.WriteFile(Path.Combine("solution", "src", "Lib", "~$docs", "es", "Lib.xml"), "<doc>es</doc>");
		Succeeded(fixture.Build(Lib(repo), "-t:Pack"));

		Assert.True(File.Exists(Path.Combine(Lib(repo), "~$docs", "en", "Lib.xml")));
		using var zip = ZipFile.OpenRead(Path.Combine(repo.Path, "~$publish", "nupkgs", "Lib.2.3.1.nupkg"));
		var entries = zip.Entries.Select(e => e.FullName).ToList();
		Assert.Contains("lib/net10.0/es/Lib.xml", entries);
		Assert.Contains("lib/net11.0/es/Lib.xml", entries);
	}

	[Fact(DisplayName = "FxVersioningEnabled=false: the SDK leaves the version and the metadata alone")]
	public void VersioningDisabled()
	{
		using var repo = Sample(properties: "<FxVersioningEnabled>false</FxVersioningEnabled>", code: Plain);
		Succeeded(fixture.Build(Lib(repo)));
		Assert.Equal("1.0.0.0", Assembly(repo).FileVersion);
		Assert.False(File.Exists(MetadataFile(repo)));
	}

	[Fact(DisplayName = "no version tag: the build fails with the error code")]
	public void NoTag()
	{
		using var repo = Sample(tag: false);
		var result = fixture.Build(Lib(repo));
		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("error version.no-tag", result.StandardOutput);
	}

	/// <summary>A repository with central package management and the versions the SDK's packages need.</summary>
	TempGitRepository CentralPackages(string properties, string? path = null, string packages = "")
	{
		var repo = new TempGitRepository(path: path);
		fixture.SetUp(repo, $"""
			<Project>
				<PropertyGroup>
					{properties}
				</PropertyGroup>
				<Import Project="Sdk.props" Sdk="Fuxion.Tools.Sdk" />
			</Project>
			""");
		repo.WriteFile(Path.Combine("solution", "Directory.Packages.props"), $"""
			<Project>
				<PropertyGroup>
					<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
				</PropertyGroup>
				<ItemGroup>
					<PackageVersion Include="Microsoft.Testing.Extensions.CodeCoverage" Version="18.11.2" />
					<PackageVersion Include="PolySharp" Version="1.16.0" />
					<PackageVersion Include="xunit.v3" Version="4.0.0" />
					{packages}
				</ItemGroup>
			</Project>
			""");
		repo.WriteFile("global.json", $$"""
			{
			  "msbuild-sdks": { "Fuxion.Tools.Sdk": "{{fixture.Version}}" },
			  "test": { "runner": "Microsoft.Testing.Platform" }
			}
			""");
		return repo;
	}

	[Fact(DisplayName = "dotnet.yaml imports: a project that only declares IsTestProject is an xUnit v3 test; PolySharp where a variable says")]
	public void Imports()
	{
		// The repo's conventions are plain MSBuild files; dotnet.yaml says where they go (plan N, decision 15)
		using var repo = CentralPackages("");
		repo.WriteFile(Path.Combine("_fx", "dotnet.yaml"), """
			variables:
			  - name: IsOldFramework
			    when: "'$(TargetFramework)' == 'netstandard2.0'"
			imports:
			  - props: dotnet/default.props
			  - targets: dotnet/test.targets
			    when: "'$(IsTestProject)' == 'true'"
			  - targets: dotnet/polysharp.targets
			    when: "$(IsOldFramework)"
			""");
		repo.WriteFile(Path.Combine("_fx", "dotnet", "default.props"), """
			<Project>
				<PropertyGroup>
					<LangVersion>latest</LangVersion>
				</PropertyGroup>
			</Project>
			""");
		// Right after the project file: it sees IsTestProject and can still make it an executable
		repo.WriteFile(Path.Combine("_fx", "dotnet", "test.targets"), """
			<Project>
				<PropertyGroup>
					<OutputType>Exe</OutputType>
				</PropertyGroup>
				<ItemGroup>
					<PackageReference Include="xunit.v3" />
					<PackageReference Include="Microsoft.Testing.Extensions.CodeCoverage" />
				</ItemGroup>
			</Project>
			""");
		repo.WriteFile(Path.Combine("_fx", "dotnet", "polysharp.targets"), """
			<Project>
				<ItemGroup>
					<PackageReference Include="PolySharp" PrivateAssets="all" IncludeAssets="runtime; build; native; contentfiles; analyzers; buildtransitive" />
				</ItemGroup>
			</Project>
			""");
		repo.WriteFile(Path.Combine("solution", "test", "Tests", "Tests.csproj"), """
			<Project Sdk="Microsoft.NET.Sdk">
				<PropertyGroup>
					<TargetFramework>net10.0</TargetFramework>
					<IsTestProject>true</IsTestProject>
				</PropertyGroup>
			</Project>
			""");
		repo.WriteFile(Path.Combine("solution", "test", "Tests", "SampleTest.cs"), """
			using Xunit;

			namespace Tests;

			public sealed class SampleTest
			{
				[Fact]
				public void Passes() => Assert.Equal(2, 1 + 1);
			}
			""");
		repo.WriteFile(Path.Combine("solution", "src", "Old", "Old.csproj"), """
			<Project Sdk="Microsoft.NET.Sdk">
				<PropertyGroup>
					<TargetFramework>netstandard2.0</TargetFramework>
				</PropertyGroup>
			</Project>
			""");
		repo.WriteFile(Path.Combine("solution", "src", "Old", "Point.cs"), """
			namespace Old;

			/// <summary>Needs IsExternalInit, which netstandard2.0 lacks: PolySharp gives it.</summary>
			public sealed record Point(int X, int Y)
			{
				/// <summary>An init accessor.</summary>
				public string Name { get; init; } = "";
			}
			""");
		Assert.Equal(0, FxApp.Run(["sync", "dotnet"], new StringWriter(), new StringWriter(), repo.Path));
		repo.CommitAll("c1");
		repo.Tag("version/1.0.0");

		var test = ProcessRunner.Run("dotnet", repo.Path, ["test", "--project", Path.Combine("solution", "test", "Tests")],
			TimeSpan.FromMinutes(5), SdkFixture.CleanEnvironment());
		Succeeded(test);
		Assert.Contains("succeeded: 1", test.StandardOutput);
		Succeeded(fixture.Build(Path.Combine(repo.Path, "solution", "src", "Old")));
		Assert.Equal(0, FxApp.Run(["doctor"], new StringWriter(), new StringWriter(), repo.Path));
	}

	[Fact(DisplayName = "without central package management: the version of dotnet.yaml reaches the project's reference")]
	public void WithoutCentralManagement()
	{
		using var repo = new TempGitRepository();
		fixture.SetUp(repo, """
			<Project>
				<Import Project="Sdk.props" Sdk="Fuxion.Tools.Sdk" />
			</Project>
			""");
		repo.WriteFile(Path.Combine("_fx", "dotnet.yaml"), """
			packages:
			  - ids: [Spectre.Console]
			    versions:
			      - version: 0.57.2
			""");
		repo.WriteFile(Path.Combine("solution", "src", "App", "App.csproj"), """
			<Project Sdk="Microsoft.NET.Sdk">
				<PropertyGroup>
					<TargetFramework>net10.0</TargetFramework>
				</PropertyGroup>
				<ItemGroup>
					<PackageReference Include="Spectre.Console" />
				</ItemGroup>
			</Project>
			""");
		Assert.Equal(0, FxApp.Run(["sync", "dotnet"], new StringWriter(), new StringWriter(), repo.Path));
		Assert.False(File.Exists(Path.Combine(repo.Path, "_fx", "packages.g.props")));
		repo.CommitAll("c1");
		repo.Tag("version/1.0.0");

		Succeeded(fixture.Build(Path.Combine(repo.Path, "solution", "src", "App")));
		Assert.Contains("\"Spectre.Console/0.57.2\"", File.ReadAllText(Path.Combine(repo.Path, "solution", "src", "App", "obj", "project.assets.json")));
	}

	[Fact(DisplayName = "the SDK keeps a BeforeMicrosoftNETSdkTargets the repo had, and the .NET SDK still imports it")]
	public void BeforeMicrosoftNETSdkTargets()
	{
		using var repo = CentralPackages("<BeforeMicrosoftNETSdkTargets>$(MSBuildThisFileDirectory)mine.targets</BeforeMicrosoftNETSdkTargets>");
		repo.WriteFile(Path.Combine("solution", "mine.targets"), """
			<Project>
				<PropertyGroup>
					<MineImported>true</MineImported>
				</PropertyGroup>
			</Project>
			""");
		repo.WriteFile(Path.Combine("solution", "src", "App", "App.csproj"), """
			<Project Sdk="Microsoft.NET.Sdk">
				<PropertyGroup>
					<TargetFramework>net10.0</TargetFramework>
				</PropertyGroup>
			</Project>
			""");
		repo.CommitAll("c1");
		repo.Tag("version/1.0.0");
		var result = ProcessRunner.Run("dotnet", repo.Path,
			["msbuild", Path.Combine("solution", "src", "App", "App.csproj"), "-getProperty:MineImported", "-getProperty:FxProjectTargetsImported", "-nologo"],
			TimeSpan.FromMinutes(5), SdkFixture.CleanEnvironment());
		Succeeded(result);
		Assert.Contains("\"MineImported\": \"true\"", result.StandardOutput);
		// If a new .NET SDK stops importing BeforeMicrosoftNETSdkTargets, the targets: imports of dotnet.yaml are lost
		Assert.Contains("\"FxProjectTargetsImported\": \"true\"", result.StandardOutput);
	}

	[Fact(DisplayName = "dotnet.yaml: fx sync generates it and the SDK builds with its frameworks and package versions")]
	public void DotnetYaml()
	{
		using var repo = CentralPackages("");
		repo.WriteFile(Path.Combine("_fx", "dotnet.yaml"), $"""
			sdks:
			  Fuxion.Tools.Sdk: {fixture.Version}
			variables:
			  - name: CoreFrameworks
			    value: net10.0;net11.0
			  - name: IsNet10
			    when: "'$(TargetFramework)' == 'net10.0'"
			  - name: IsNet11
			    when: "'$(TargetFramework)' == 'net11.0'"
			    define: NET11_ONLY;PREVIEW_FRAMEWORK
			packages:
			  - ids: [Spectre.Console]
			    versions:
			      - when: "$(IsNet10) Or $(IsNet11)"
			        version: 0.57.2
			""");
		repo.WriteFile(Path.Combine("solution", "src", "App", "App.csproj"), """
			<Project Sdk="Microsoft.NET.Sdk">
				<PropertyGroup>
					<TargetFrameworks>$(CoreFrameworks)</TargetFrameworks>
				</PropertyGroup>
				<ItemGroup>
					<PackageReference Include="Spectre.Console" />
				</ItemGroup>
				<Target Name="ShowNet11" BeforeTargets="CoreCompile">
					<Message Importance="high" Text="IsNet11[$(TargetFramework)]=$(IsNet11)" />
				</Target>
			</Project>
			""");
		repo.WriteFile(Path.Combine("solution", "src", "App", "Hello.cs"), """
			namespace App;

			/// <summary>Uses the governed package.</summary>
			public static class Hello
			{
				/// <summary>Markup.</summary>
				public static string Text => Spectre.Console.Markup.Escape("[hello]");
			#if NET11_ONLY && PREVIEW_FRAMEWORK
				/// <summary>Only where dotnet.yaml defines the constants.</summary>
				public const string Framework = "net11";
			#endif
			}
			""");
		repo.WriteFile(Path.Combine("solution", "src", "App", "Check.cs"), """
			namespace App;

			/// <summary>The constants of dotnet.yaml, per framework.</summary>
			public static class Check
			{
			#if NET11_0
				/// <summary>Defined on net11.0 only.</summary>
				public const string Framework = Hello.Framework;
			#elif NET11_ONLY
				#error NET11_ONLY outside net11.0
			#endif
			}
			""");

		var stdout = new StringWriter();
		Assert.Equal(0, FxApp.Run(["sync"], stdout, new StringWriter(), repo.Path));
		Assert.Contains("written     _fx/packages.g.props", stdout.ToString());
		repo.CommitAll("c1");
		repo.Tag("version/1.0.0");

		var result = fixture.Build(Path.Combine(repo.Path, "solution", "src", "App"));
		Succeeded(result);
		Assert.Contains("IsNet11[net11.0]=true", result.StandardOutput);
		Assert.Contains("IsNet11[net10.0]=false", result.StandardOutput);
		Assert.Contains("\"Spectre.Console/0.57.2\"", File.ReadAllText(Path.Combine(repo.Path, "solution", "src", "App", "obj", "project.assets.json")));
		Assert.Equal(0, FxApp.Run(["doctor"], new StringWriter(), new StringWriter(), repo.Path));
	}

	[Fact(DisplayName = "FxReference: a project in the workspace, a package outside it, and a clear error when it needs the workspace")]
	public void FxReference()
	{
		using var workspace = new TempGitRepository(path: Path.Combine(fixture.Root, "ws-" + Guid.NewGuid().ToString("N")[..8]));
		workspace.WriteFile(Path.Combine("_fx", "workspace.yaml"), """
			version: 1
			repositories:
			  - name: lib
			    path: lib/repo
			    url: https://example.invalid/lib.git
			    mount: mandatory
			    solution: Lib.slnx
			  - name: app
			    path: app/repo
			    url: https://example.invalid/app.git
			    mount: mandatory
			    solution: App.slnx
			""");
		using var lib = Sample(path: Path.Combine(workspace.Path, "lib", "repo"));
		lib.WriteFile("Lib.slnx", """<Solution><Project Path="solution/src/Lib/Lib.csproj" /></Solution>""");
		using var app = CentralPackages("", Path.Combine(workspace.Path, "app", "repo"), """<PackageVersion Include="Lib" Version="2.3.1" />""");
		app.WriteFile("App.slnx", """<Solution><Project Path="solution/src/App/App.csproj" /></Solution>""");
		app.WriteFile(Path.Combine("solution", "src", "App", "App.csproj"), """
			<Project Sdk="Microsoft.NET.Sdk">
				<PropertyGroup>
					<TargetFramework>net10.0</TargetFramework>
				</PropertyGroup>
				<ItemGroup>
					<FxReference Include="Lib" />
					<FxReference Include="OnlyInTheWorkspace" WorkspaceOnly="true" />
				</ItemGroup>
			</Project>
			""");
		app.WriteFile(Path.Combine("solution", "src", "App", "Use.cs"), """
			namespace App;

			/// <summary>Uses the library of the other repository.</summary>
			public static class Use
			{
				/// <summary>Its version.</summary>
				public static string Version => Lib.Info.Version;
			}
			""");
		app.CommitAll("c1");
		app.Tag("version/1.0.0");
		var appProject = Path.Combine(app.Path, "solution", "src", "App");

		// In the workspace: fx sync writes the map, and Lib is a project
		Assert.Equal(0, FxApp.Run(["sync", "workspace", "--offline"], new StringWriter(), new StringWriter(), workspace.Path));
		Assert.Contains("Include=\"Lib\" Repository=\"lib\"", File.ReadAllText(Path.Combine(workspace.Path, "_fx", "~$workspace.props")));
		Succeeded(fixture.Build(appProject));
		Assert.Equal("project", LibraryType(app, "Lib"));
		Assert.True(File.Exists(Path.Combine(appProject, "bin", "Debug", "net10.0", "Lib.dll")));

		// Outside it (FxUsePackages, as a CI would): Lib is the package
		Succeeded(fixture.Build(Lib(lib), "-t:Pack", $"-p:PackageOutputPath={fixture.Feed}"));
		Succeeded(fixture.Build(appProject, "-p:FxUsePackages=true"));
		Assert.Equal("package", LibraryType(app, "Lib"));

		// A repo that only builds in the workspace says so, instead of NuGet not finding a package
		var result = fixture.Build(appProject, "-p:FxUsePackages=true", "-p:FxRequiresWorkspace=true");
		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("FX0001", result.StandardOutput);
		// WorkspaceOnly="true" (the analyzers of oss): only in the workspace; never a package, never the error
		Assert.DoesNotContain("OnlyInTheWorkspace", result.StandardOutput);
	}

	static string LibraryType(TempGitRepository repo, string library)
	{
		var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo.Path, "solution", "src", "App", "obj", "project.assets.json")));
		return assets.RootElement.GetProperty("libraries").EnumerateObject()
			.Single(l => l.Name.StartsWith(library + "/", StringComparison.Ordinal)).Value.GetProperty("type").GetString()!;
	}

	[Fact(DisplayName = "the MSBuild of Visual Studio (.NET Framework): the tasks run on the .NET task host")]
	public void VisualStudioMSBuild()
	{
		Assert.SkipWhen(fixture.MSBuildExe is null, "Visual Studio (MSBuild.exe) is not installed");
		using var repo = Sample();
		// net10.0 only: the stable Visual Studio of a CI runner uses the stable .NET SDK, which cannot build net11.0
		Succeeded(fixture.BuildWithVisualStudio(Lib(repo), "-p:TargetFrameworks=net10.0"));
		Assert.Equal("2.3.1.0", Assembly(repo).FileVersion);
		Assert.Contains("public static readonly string Version = \"2.3.1\";", File.ReadAllText(MetadataFile(repo)));
	}
}
