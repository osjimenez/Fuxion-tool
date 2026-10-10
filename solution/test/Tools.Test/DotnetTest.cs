using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Fuxion.Tools.Cli;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Dotnet;
using Xunit;

namespace Fuxion.Tools.Test;

/// <summary>The .NET module: dotnet.yaml, its generation and the doctor (plan K, K4).</summary>
public sealed class DotnetTest
{
	const string DesignYaml = """
		sdks:
		  Fuxion.Tools.Sdk: 0.1.0

		variables:
		  - name: CoreFrameworks
		    value: net10.0;net11.0
		  - name: CoreWindowsFrameworks
		    value: net10.0-windows10.0.19041;net11.0-windows10.0.19041
		    tags: [windows]
		  - name: IsOldFramework
		    when: "'$(TargetFramework)' == 'netstandard2.0' Or '$(TargetFramework)' == 'net472'"
		  - name: IsNet10
		    when: "'$(TargetFramework)' == 'net10.0'"
		  - name: IsNet11
		    when: "'$(TargetFramework)' == 'net11.0'"

		packages:
		  - ids: [MongoDB.EntityFrameworkCore]
		    versions:
		      - when: "$(IsNet10) Or $(IsNet11)"
		        version: 10.0.3
		      - when: "$(IsOldFramework)"
		        version: 9.1.3
		  - ids: [Microsoft.Extensions.Http, NotUsed.Package]
		    versions:
		      - when: "$(IsNet11)"
		        version: 11.0.0-rc.1
		      - when: "$(IsOldFramework) Or $(IsNet10)"
		        version: 10.0.12
		""";

	// Reader

	[Fact(DisplayName = "reader: sections, variables, packages and tags")]
	public void Read()
	{
		var config = DotnetYamlReader.Read(DesignYaml, "dotnet.yaml");
		Assert.Equal("0.1.0", config.Sdks["Fuxion.Tools.Sdk"]);
		Assert.Equal(5, config.Variables.Count);
		Assert.Equal("net10.0;net11.0", config.Variables[0].Value);
		Assert.Equal(["windows"], config.Variables[1].Tags);
		Assert.True(config.Variables[2].IsWhen);
		Assert.Equal(["Microsoft.Extensions.Http", "NotUsed.Package"], config.Packages[1].Ids);
		Assert.Equal(2, config.Packages[0].Versions.Count);
	}

	[Fact(DisplayName = "reader: an empty file is an empty configuration")]
	public void Read_Empty() => Assert.Empty(DotnetYamlReader.Read("", "dotnet.yaml").Variables);

	[Theory(DisplayName = "reader: every problem, with its line")]
	[InlineData("variables:\n  - name: X\n", "needs exactly one of 'value' or 'when'", 2)]
	[InlineData("variables:\n  - name: X\n    value: a\n    when: b\n", "needs exactly one of 'value' or 'when'", 2)]
	[InlineData("variables:\n  - name: 1X\n    value: a\n", "valid MSBuild property name", 2)]
	[InlineData("packages:\n  - ids: [A]\n    versions:\n      - version: 1.0.0\n      - version: 2.0.0\n        when: x\n", "each needs 'when' (design D-32)", 4)]
	[InlineData("packages:\n  - versions:\n      - when: x\n        version: 1\n", "needs 'ids'", 2)]
	[InlineData("other: 1\n", "Unknown section 'other'", 1)]
	[InlineData("variables:\n  - name: A\n    value: 1\n  - name: a\n    value: 2\n", "defined more than once", 4)]
	[InlineData("variables: [\n", "", 2)]
	[InlineData("variables:\n  - name: X\n    value: a\n    define: X\n", "'define' needs 'when'", 2)]
	[InlineData("variables:\n  - name: X\n    when: a\n    define: OLD-FRAMEWORKS\n", "compilation constants separated by ';'", 2)]
	[InlineData("imports:\n  - when: x\n", "needs 'props' or 'targets'", 2)]
	[InlineData("imports:\n  - props: ../outside.props\n", "without '..'", 2)]
	[InlineData("imports:\n  - props: a.props\n    other: 1\n", "unknown key 'other'", 2)]
	public void Read_Invalid(string yaml, string message, int line)
	{
		var ex = Assert.Throws<DotnetConfigException>(() => DotnetYamlReader.Read(yaml, "dotnet.yaml"));
		var diagnostic = ex.Diagnostics[0];
		Assert.Equal(DotnetYamlReader.InvalidYaml, diagnostic.Code);
		Assert.Contains(message, diagnostic.Message.English);
		Assert.Equal(line, diagnostic.Line);
	}

	[Fact(DisplayName = "combine: the repo wins by sdk, variable name and package id")]
	public void Combine()
	{
		var workspace = DotnetYamlReader.Read(DesignYaml, "ws");
		var repo = DotnetYamlReader.Read("""
			sdks:
			  Fuxion.Tools.Sdk: 0.2.0
			variables:
			  - name: CoreFrameworks
			    value: net8.0
			packages:
			  - ids: [NotUsed.Package]
			    versions:
			      - when: "true"
			        version: 9.9.9
			""", "repo");
		var combined = DotnetConfig.Combine(workspace, repo);
		Assert.Equal("0.2.0", combined.Sdks["Fuxion.Tools.Sdk"]);
		Assert.Equal("net8.0", combined.Variables.Single(v => v.Name == "CoreFrameworks").Value);
		Assert.Equal(5, combined.Variables.Count);
		Assert.Equal(["Microsoft.Extensions.Http"], combined.Packages.Single(p => p.Ids.Contains("Microsoft.Extensions.Http")).Ids);
		Assert.Equal("9.9.9", combined.Packages.Single(p => p.Ids.Contains("NotUsed.Package")).Versions[0].Version);
	}

	[Fact(DisplayName = "tags: what has no tags goes to every repo; tagged things only to repos with the tag")]
	public void Tags()
	{
		var config = DotnetYamlReader.Read(DesignYaml, "x");
		Assert.DoesNotContain(config.ForTags([]).Variables, v => v.Name == "CoreWindowsFrameworks");
		Assert.Contains(config.ForTags(["Windows"]).Variables, v => v.Name == "CoreWindowsFrameworks");
	}

	// Conditions

	static readonly Dictionary<string, string> When = new()
	{
		["IsNet10"] = "'$(TargetFramework)' == 'net10.0'",
		["IsNet11"] = "'$(TargetFramework)' == 'net11.0'",
		["IsCore"] = "$(IsNet10) Or $(IsNet11)"
	};

	[Theory(DisplayName = "expand: when variables replaced by their conditions, recursively")]
	[InlineData("$(IsNet11)", "('$(TargetFramework)' == 'net11.0')")]
	[InlineData("'$(IsNet11)' == 'true'", "('$(TargetFramework)' == 'net11.0')")]
	[InlineData("'$(IsNet11)' != 'true'", "!('$(TargetFramework)' == 'net11.0')")]
	[InlineData("'$(IsNet11)' == 'false'", "!('$(TargetFramework)' == 'net11.0')")]
	[InlineData("$(IsCore)", "(('$(TargetFramework)' == 'net10.0') Or ('$(TargetFramework)' == 'net11.0'))")]
	[InlineData("'$(Other)' == 'x'", "'$(Other)' == 'x'")]
	public void Expand(string condition, string expected) => Assert.Equal(expected, MSBuildConditions.Expand(condition, When));

	[Fact(DisplayName = "expand: a cycle is an error")]
	public void Expand_Cycle()
		=> Assert.Throws<InvalidOperationException>(() => MSBuildConditions.Expand("$(A)", new Dictionary<string, string> { ["A"] = "$(B)", ["B"] = "$(A)" }));

	[Theory(DisplayName = "evaluate: the subset the coverage check understands")]
	[InlineData("'$(TargetFramework)' == 'net10.0'", "net10.0", true)]
	[InlineData("'$(TargetFramework)' == 'NET10.0'", "net10.0", true)]
	[InlineData("'$(TargetFramework)' != 'net10.0'", "net10.0", false)]
	[InlineData("('$(TargetFramework)' == 'net10.0') Or ('$(TargetFramework)' == 'net11.0')", "net11.0", true)]
	[InlineData("'$(TargetFramework)' == 'net10.0' And '$(TargetFramework)' == 'net11.0'", "net10.0", false)]
	[InlineData("!('$(TargetFramework)' == 'net10.0')", "net472", true)]
	[InlineData("true", "x", true)]
	[InlineData("'$(TargetFramework)' == 'net472' Or '$(TargetFramework)'", "net472", null)]
	[InlineData("'$(Configuration)' == 'Debug'", "net10.0", null)]
	[InlineData("$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net8.0'))", "net10.0", null)]
	public void Evaluate(string condition, string tfm, bool? expected) => Assert.Equal(expected, MSBuildConditions.Evaluate(condition, tfm));

	[Fact(DisplayName = "frameworks: value variables expanded; unknown ones make it unverifiable")]
	public void Frameworks()
	{
		var values = new Dictionary<string, string> { ["Core"] = "net10.0;net11.0", ["All"] = "$(Core);net472" };
		Assert.Equal(["net10.0", "net11.0", "net472"], MSBuildConditions.Frameworks("$(All)", values));
		Assert.Null(MSBuildConditions.Frameworks("$(Unknown)", values));
	}

	// Module, on a repository

	sealed class Repo : IDisposable
	{
		public readonly TempGitRepository Git = new();

		public Repo(string yaml = DesignYaml, string frameworks = "$(CoreFrameworks);netstandard2.0", string centralPackages = "")
		{
			Git.WriteFile(Path.Combine("_fx", "dotnet.yaml"), yaml);
			Git.WriteFile("global.json", "{\n  // comment\n  \"test\": { \"runner\": \"Microsoft.Testing.Platform\" },\n}\n");
			Git.WriteFile(Path.Combine("solution", "Directory.Packages.props"), $"""
				<Project>
					<PropertyGroup>
						<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
					</PropertyGroup>
					<ItemGroup>
						<PackageVersion Include="Spectre.Console" Version="0.57.2" />{centralPackages}
					</ItemGroup>
				</Project>
				""");
			Git.WriteFile(Path.Combine("solution", "src", "Lib", "Lib.csproj"), $"""
				<Project Sdk="Microsoft.NET.Sdk">
					<PropertyGroup><TargetFrameworks>{frameworks}</TargetFrameworks></PropertyGroup>
					<ItemGroup>
						<PackageReference Include="MongoDB.EntityFrameworkCore" />
						<PackageReference Include="Microsoft.Extensions.Http" />
					</ItemGroup>
				</Project>
				""");
		}

		public string At(string relative) => System.IO.Path.Combine(Git.Path, relative);

		public string Read(string relative) => File.ReadAllText(At(relative)).Replace("\r\n", "\n");

		public void Dispose() => Git.Dispose();
	}

	static IEnumerable<string> Codes(FxModuleResult result) => result.Diagnostics.Select(d => d.Code);

	[Fact(DisplayName = "sync: the three generated files, global.json and the Import; doctor all right after it")]
	public void Sync()
	{
		using var repo = new Repo();
		var result = DotnetModule.Sync(repo.Git.Path, dryRun: false);
		Assert.Empty(result.Diagnostics);
		Assert.All(result.Files, f => Assert.Equal(SyncFileStatus.Written, f.Status));
		Assert.Equal(["_fx/dotnet.g.props", "_fx/dotnet.g.targets", "_fx/packages.g.props", "global.json", "solution/Directory.Packages.props"],
			result.Files.Select(f => f.Path));

		var props = repo.Read("_fx/dotnet.g.props");
		Assert.StartsWith("<!-- GENERATED by fx from _fx/dotnet.yaml.", props);
		Assert.Contains("<CoreFrameworks>net10.0;net11.0</CoreFrameworks>", props);
		Assert.DoesNotContain("CoreWindowsFrameworks", props);
		Assert.Contains("<IsNet11 Condition=\"'$(TargetFramework)' == 'net11.0'\">true</IsNet11>", repo.Read("_fx/dotnet.g.targets"));
		var packages = repo.Read("_fx/packages.g.props");
		Assert.Contains("<PackageVersion Include=\"MongoDB.EntityFrameworkCore\" Version=\"9.1.3\" Condition=\"('$(TargetFramework)' == 'netstandard2.0' Or '$(TargetFramework)' == 'net472')\" />", packages);
		Assert.DoesNotContain("NotUsed.Package", packages);
		var global = JsonDocument.Parse(repo.Read("global.json")).RootElement;
		Assert.Equal("0.1.0", global.GetProperty("msbuild-sdks").GetProperty("Fuxion.Tools.Sdk").GetString());
		Assert.Equal("Microsoft.Testing.Platform", global.GetProperty("test").GetProperty("runner").GetString());
		Assert.Contains("<Import Project=\"$(MSBuildThisFileDirectory)../_fx/packages.g.props\"", repo.Read("solution/Directory.Packages.props"));

		var again = DotnetModule.Sync(repo.Git.Path, dryRun: false);
		Assert.All(again.Files, f => Assert.Equal(SyncFileStatus.Unchanged, f.Status));
		Assert.Empty(DotnetModule.Doctor(repo.Git.Path).Diagnostics);
	}

	[Fact(DisplayName = "sync: a build file deleted and not committed yet is not read")]
	public void DeletedFile()
	{
		using var repo = new Repo();
		repo.Git.WriteFile(Path.Combine("solution", "src", "Directory.Build.targets"), "<Project />");
		repo.Git.CommitAll("c1");
		File.Delete(repo.At(Path.Combine("solution", "src", "Directory.Build.targets")));
		Assert.Empty(DotnetModule.Sync(repo.Git.Path, dryRun: false).Diagnostics);
	}

	[Fact(DisplayName = "sync --dry-run writes nothing; doctor reports every outdated file")]
	public void DryRunAndDoctor()
	{
		using var repo = new Repo();
		var dry = DotnetModule.Sync(repo.Git.Path, dryRun: true);
		Assert.All(dry.Files, f => Assert.Equal(SyncFileStatus.WouldWrite, f.Status));
		Assert.False(File.Exists(repo.At("_fx/dotnet.g.props")));
		Assert.Equal(5, Codes(DotnetModule.Doctor(repo.Git.Path)).Count(c => c == DotnetModule.Outdated));
	}

	[Fact(DisplayName = "a file without the GENERATED header is not fx's: not written, and an error")]
	public void NotGenerated()
	{
		using var repo = new Repo();
		repo.Git.WriteFile(Path.Combine("_fx", "dotnet.g.props"), "<Project />");
		var result = DotnetModule.Sync(repo.Git.Path, dryRun: false);
		Assert.Contains(result.Files, f => f.Path == "_fx/dotnet.g.props" && f.Status == SyncFileStatus.Refused);
		Assert.Contains(DotnetModule.NotGenerated, Codes(result));
		Assert.Equal("<Project />", repo.Read("_fx/dotnet.g.props"));
	}

	[Fact(DisplayName = "doctor: a package governed by dotnet.yaml and by Directory.Packages.props (D-16)")]
	public void Doctor_DuplicateOwner()
	{
		using var repo = new Repo(centralPackages: "\n\t\t<PackageVersion Include=\"MongoDB.EntityFrameworkCore\" Version=\"1.0.0\" />");
		DotnetModule.Sync(repo.Git.Path, dryRun: false);
		var diagnostic = Assert.Single(DotnetModule.Doctor(repo.Git.Path).Diagnostics);
		Assert.Equal(DotnetModule.DuplicateOwner, diagnostic.Code);
		Assert.Equal("solution/Directory.Packages.props", diagnostic.File);
	}

	[Fact(DisplayName = "doctor: a framework no version covers (a new net12.0) is a gap (D-32)")]
	public void Doctor_Gap()
	{
		using var repo = new Repo(frameworks: "$(CoreFrameworks);net12.0");
		DotnetModule.Sync(repo.Git.Path, dryRun: false);
		var gaps = DotnetModule.Doctor(repo.Git.Path).Diagnostics.Where(d => d.Code == DotnetModule.CoverageGap).ToList();
		Assert.Equal(2, gaps.Count);
		Assert.All(gaps, g => Assert.Contains("net12.0", g.Message.English));
	}

	[Fact(DisplayName = "doctor: two versions for the same framework is an overlap (D-32)")]
	public void Doctor_Overlap()
	{
		using var repo = new Repo(yaml: DesignYaml.Replace("when: \"$(IsOldFramework)\"", "when: \"$(IsOldFramework) Or $(IsNet11)\""));
		DotnetModule.Sync(repo.Git.Path, dryRun: false);
		var overlap = Assert.Single(DotnetModule.Doctor(repo.Git.Path).Diagnostics);
		Assert.Equal(DotnetModule.CoverageOverlap, overlap.Code);
		Assert.Contains("'MongoDB.EntityFrameworkCore' on net11.0: several versions apply (10.0.3, 9.1.3)", overlap.Message.English);
	}

	[Fact(DisplayName = "doctor: the incomplete condition of the design's MongoDB example cannot be verified (warning)")]
	public void Doctor_Unverifiable()
	{
		using var repo = new Repo(yaml: DesignYaml.Replace("when: \"$(IsOldFramework)\"", "when: \"'$(TargetFramework)' == 'net472' Or '$(TargetFramework)'\""));
		DotnetModule.Sync(repo.Git.Path, dryRun: false);
		var diagnostics = DotnetModule.Doctor(repo.Git.Path).Diagnostics;
		Assert.Contains(diagnostics, d => d.Code == DotnetModule.Unverifiable && d.Severity == FxSeverity.Warning);
	}

	const string ImportsYaml = """
		variables:
		  - name: IsOldFramework
		    when: "'$(TargetFramework)' == 'net472'"
		imports:
		  - props: dotnet/default.props
		  - props: [dotnet/packable.props]
		    targets: dotnet/packable.targets
		    when: "'$(IsPackable)' == 'true'"
		  - targets: dotnet/test.targets
		    when: "'$(IsTestProject)' == 'true'"
		  - targets: dotnet/old.targets
		    when: "$(IsOldFramework)"
		    tags: [windows]
		packages:
		  - ids: [xunit.v3]
		    versions:
		      - when: "'$(TargetFramework)' != ''"
		        version: 4.0.0
		""";

	[Fact(DisplayName = "reader: imports, with one path or a list, when and tags")]
	public void Read_Imports()
	{
		var imports = DotnetYamlReader.Read(ImportsYaml, "dotnet.yaml").Imports;
		Assert.Equal(4, imports.Count);
		Assert.Equal(["dotnet/default.props"], imports[0].Props);
		Assert.Null(imports[0].When);
		Assert.Equal(["dotnet/packable.props"], imports[1].Props);
		Assert.Equal(["dotnet/packable.targets"], imports[1].Targets);
		Assert.Equal(["windows"], imports[3].Tags);
	}

	[Fact(DisplayName = "sync: the imports in dotnet.g.props (props) and dotnet.g.targets (targets), conditions expanded")]
	public void Sync_Imports()
	{
		using var repo = new Repo(ImportsYaml);
		foreach (var file in new[] { "default.props", "packable.props", "packable.targets", "old.targets" })
			repo.Git.WriteFile(Path.Combine("_fx", "dotnet", file), "<Project />");
		// a package only an imported file uses is still the repo's: it gets its version
		repo.Git.WriteFile(Path.Combine("_fx", "dotnet", "test.targets"), """<Project><ItemGroup><PackageReference Include="xunit.v3" /></ItemGroup></Project>""");
		var result = DotnetModule.Sync(repo.Git.Path, dryRun: false);
		Assert.Empty(result.Diagnostics);

		var props = repo.Read("_fx/dotnet.g.props");
		Assert.Contains("<Import Project=\"$(MSBuildThisFileDirectory)dotnet/default.props\" />", props);
		Assert.Contains("<Import Project=\"$(MSBuildThisFileDirectory)dotnet/packable.props\" Condition=\"'$(IsPackable)' == 'true'\" />", props);
		Assert.DoesNotContain("test.targets", props);
		var targets = repo.Read("_fx/dotnet.g.targets");
		Assert.Contains("<Import Project=\"$(MSBuildThisFileDirectory)dotnet/packable.targets\" Condition=\"'$(IsPackable)' == 'true'\" />", targets);
		Assert.Contains("<Import Project=\"$(MSBuildThisFileDirectory)dotnet/test.targets\" Condition=\"'$(IsTestProject)' == 'true'\" />", targets);
		Assert.DoesNotContain("old.targets", targets); // tagged [windows], and the repo has no tags
		Assert.Contains("Include=\"xunit.v3\"", repo.Read("_fx/packages.g.props"));
		Assert.Empty(DotnetModule.Doctor(repo.Git.Path).Diagnostics);

		File.Delete(repo.At(Path.Combine("_fx", "dotnet", "default.props")));
		Assert.Contains(DotnetModule.MissingImport, Codes(DotnetModule.Doctor(repo.Git.Path)));
	}

	[Fact(DisplayName = "outside the workspace, the tags come from _fx/.workspace/repository.yaml")]
	public void RepositoryTags()
	{
		using var repo = new Repo("");
		repo.Git.WriteFile(Path.Combine("_fx", ".workspace", "dotnet.yaml"), ImportsYaml);
		repo.Git.WriteFile(Path.Combine("_fx", ".workspace", "repository.yaml"), "# GENERATED by fx\nname: plus\ntags: [dotnet, windows]\nfiles: []\n");
		foreach (var file in new[] { "default.props", "packable.props", "packable.targets", "test.targets", "old.targets" })
			repo.Git.WriteFile(Path.Combine("_fx", ".workspace", "dotnet", file), "<Project />");
		Assert.Empty(DotnetModule.Sync(repo.Git.Path, dryRun: false).Diagnostics);
		// the workspace's imports, from _fx/.workspace/; the tagged one applies (windows)
		Assert.Contains("<Import Project=\"$(MSBuildThisFileDirectory).workspace/dotnet/old.targets\" Condition=\"('$(TargetFramework)' == 'net472')\" />",
			repo.Read("_fx/dotnet.g.targets"));
	}

	[Fact(DisplayName = "one version without when: for every framework, without a condition")]
	public void SingleVersion()
	{
		using var repo = new Repo("packages:\n  - ids: [Spectre.Console]\n    versions:\n      - version: 0.57.2\n");
		repo.Git.WriteFile(Path.Combine("solution", "src", "Lib", "Lib.csproj"),
			"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks>net10.0;net472</TargetFrameworks></PropertyGroup><ItemGroup><PackageReference Include="Spectre.Console" /></ItemGroup></Project>""");
		repo.Git.WriteFile(Path.Combine("solution", "Directory.Packages.props"),
			"<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>");
		Assert.Empty(DotnetModule.Sync(repo.Git.Path, dryRun: false).Diagnostics);
		Assert.Contains("<PackageVersion Include=\"Spectre.Console\" Version=\"0.57.2\" />", repo.Read("_fx/packages.g.props"));
		Assert.Empty(DotnetModule.Doctor(repo.Git.Path).Diagnostics);
	}

	[Fact(DisplayName = "without central package management: PackageReference Update after the imports, no packages.g.props")]
	public void WithoutCentralManagement()
	{
		using var repo = new Repo(ImportsYaml);
		File.Delete(repo.At(Path.Combine("solution", "Directory.Packages.props")));
		foreach (var file in new[] { "default.props", "packable.props", "packable.targets", "old.targets" })
			repo.Git.WriteFile(Path.Combine("_fx", "dotnet", file), "<Project />");
		repo.Git.WriteFile(Path.Combine("_fx", "dotnet", "test.targets"), """<Project><ItemGroup><PackageReference Include="xunit.v3" /></ItemGroup></Project>""");
		Assert.Empty(DotnetModule.Sync(repo.Git.Path, dryRun: false).Diagnostics);

		var targets = repo.Read("_fx/dotnet.g.targets");
		var update = targets.IndexOf("<PackageReference Update=\"xunit.v3\" Version=\"4.0.0\" Condition=\"'$(TargetFramework)' != ''\" />", StringComparison.Ordinal);
		Assert.True(update > targets.IndexOf("test.targets", StringComparison.Ordinal), targets); // after the import that adds it
		Assert.False(File.Exists(repo.At("_fx/packages.g.props")));

		// a Version in a project is fx's to override: the doctor says so
		repo.Git.WriteFile(Path.Combine("solution", "test", "T", "T.csproj"),
			"""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="xunit.v3" Version="3.0.0" /></ItemGroup></Project>""");
		Assert.Contains(DotnetModule.IgnoredVersion, Codes(DotnetModule.Doctor(repo.Git.Path)));
	}

	[Fact(DisplayName = "doctor: a version no project uses, directly or transitively, is reported; nothing without a restore")]
	public void UnusedPackages()
	{
		using var repo = new Repo("", centralPackages: """

				<PackageVersion Include="Direct.Package" Version="1.0.0" />
				<PackageVersion Include="Transitive.Package" Version="1.0.0" />
				<PackageVersion Include="Global.Package" Version="1.0.0" />
				<PackageVersion Include="Unused.Package" Version="1.0.0" />
				<GlobalPackageReference Include="Global.Package" />
			""");
		repo.Git.WriteFile(Path.Combine("solution", "src", "Lib", "Lib.csproj"),
			"""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Direct.Package" /></ItemGroup></Project>""");
		DotnetModule.Sync(repo.Git.Path, dryRun: false);

		// a fresh clone: what is transitive cannot be told, so nothing is reported
		Assert.DoesNotContain(DotnetModule.UnusedPackage, Codes(DotnetModule.Doctor(repo.Git.Path)));

		// restored: Transitive.Package comes through another package, Spectre.Console and Unused.Package nobody uses
		repo.Git.WriteFile(Path.Combine("solution", "src", "Lib", "obj", "project.assets.json"),
			"""{ "libraries": { "Direct.Package/1.0.0": { "type": "package" }, "Transitive.Package/1.0.0": { "type": "package" } } }""");
		var unused = DotnetModule.Doctor(repo.Git.Path).Diagnostics.Where(d => d.Code == DotnetModule.UnusedPackage).ToList();
		Assert.Equal(["'Spectre.Console'", "'Unused.Package'"], unused.Select(d => d.Message.English.Split(' ')[0]));
		Assert.All(unused, d => Assert.Equal(FxSeverity.Warning, d.Severity));
		Assert.Equal("solution/Directory.Packages.props", unused[1].File);
		Assert.Equal(repo.Read("solution/Directory.Packages.props").Split('\n').ToList().FindIndex(l => l.Contains("Unused.Package")) + 1, unused[1].Line);
	}

	[Fact(DisplayName = "the metarepo's dotnet.yaml is the workspace layer: fx sync dotnet does not apply there")]
	public void WorkspaceLayer()
	{
		using var repo = new Repo();
		repo.Git.WriteFile(Path.Combine("_fx", "workspace.yaml"), "version: 1\n");
		Assert.False(DotnetModule.Applies(repo.Git.Path));
		Assert.Contains(DotnetModule.WorkspaceLayer, Codes(DotnetModule.Sync(repo.Git.Path, dryRun: false)));
	}

	[Fact(DisplayName = "no dotnet.yaml: an error with its code")]
	public void NoConfig()
	{
		using var git = new TempGitRepository();
		Assert.Equal(DotnetModule.NoConfig, Assert.Single(DotnetModule.Sync(git.Path, dryRun: false).Diagnostics).Code);
		Assert.False(DotnetModule.Applies(git.Path));
	}

	[Fact(DisplayName = "an invalid dotnet.yaml: the reader's diagnostics, relative to the repo")]
	public void InvalidConfig()
	{
		using var repo = new Repo(yaml: "variables:\n  - name: X\n");
		var diagnostic = Assert.Single(DotnetModule.Sync(repo.Git.Path, dryRun: false).Diagnostics);
		Assert.Equal(DotnetYamlReader.InvalidYaml, diagnostic.Code);
		Assert.Equal("_fx/dotnet.yaml", diagnostic.File);
		Assert.Equal(2, diagnostic.Line);
	}

	[Fact(DisplayName = "the workspace manifest gives the repo its tags")]
	public void WorkspaceTags()
	{
		var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fx-test-" + Guid.NewGuid().ToString("N")[..12])).FullName;
		try
		{
			Directory.CreateDirectory(Path.Combine(workspace, "_fx"));
			File.WriteAllText(Path.Combine(workspace, "_fx", "workspace.yaml"), "version: 1\nrepositories:\n  - name: plus\n    path: plus/repo\n    tags: [dotnet, windows]\n");
			using var git = new TempGitRepository(path: Path.Combine(workspace, "plus", "repo"));
			git.WriteFile(Path.Combine("_fx", "dotnet.yaml"), DesignYaml);
			DotnetModule.Sync(git.Path, dryRun: false);
			Assert.Contains("CoreWindowsFrameworks", File.ReadAllText(Path.Combine(git.Path, "_fx", "dotnet.g.props")));
		}
		finally
		{
			Directory.Delete(workspace, true);
		}
	}

	// CLI

	[Fact(DisplayName = "fx sync and fx doctor: fx-sync/1 and fx-doctor/1, exit code 1 only with errors")]
	public void Cli()
	{
		using var repo = new Repo();
		var stdout = new StringWriter();
		var stderr = new StringWriter();
		Assert.Equal(1, FxApp.Run(["doctor"], stdout, stderr, repo.Git.Path));
		Assert.Contains("error dotnet.outdated: _fx/dotnet.g.props: Out of date with _fx/dotnet.yaml. → fx sync dotnet", stderr.ToString());

		// The diagnostics, with their scope and fix (plan O, decision 13)
		stdout = new StringWriter();
		Assert.Equal(1, FxApp.Run(["doctor", "--output", "json"], stdout, new StringWriter(), repo.Git.Path));
		var outdated = JsonDocument.Parse(stdout.ToString()).RootElement.GetProperty("diagnostics").EnumerateArray().First();
		Assert.Equal("dotnet.outdated", outdated.GetProperty("code").GetString());
		Assert.Equal("dotnet", outdated.GetProperty("module").GetString());
		Assert.False(string.IsNullOrEmpty(outdated.GetProperty("repository").GetString()));
		Assert.Equal("fx sync dotnet", outdated.GetProperty("fix").GetProperty("command").GetString());

		stdout = new StringWriter();
		Assert.Equal(0, FxApp.Run(["sync", "dotnet", "--output", "json"], stdout, new StringWriter(), repo.Git.Path));
		var json = JsonDocument.Parse(stdout.ToString()).RootElement;
		Assert.Equal("fx-sync/1", json.GetProperty("schema").GetString());
		Assert.True(json.GetProperty("ok").GetBoolean());
		var dotnet = Assert.Single(json.GetProperty("modules").EnumerateArray());
		Assert.Equal("dotnet", dotnet.GetProperty("module").GetString());
		Assert.Equal(5, dotnet.GetProperty("files").EnumerateArray().Count(f => f.GetProperty("status").GetString() == "written"));

		stdout = new StringWriter();
		Assert.Equal(0, FxApp.Run(["doctor", "--output", "json"], stdout, new StringWriter(), repo.Git.Path));
		Assert.Equal("fx-doctor/1", JsonDocument.Parse(stdout.ToString()).RootElement.GetProperty("schema").GetString());
	}

	[Fact(DisplayName = "fx in Spanish: the messages and the help are; codes, commands and JSON are not (plan O, decision 7)")]
	public void Cli_Spanish()
	{
		using var repo = new Repo();
		try
		{
			var stderr = new StringWriter();
			Assert.Equal(1, FxApp.Run(["doctor", "--lang", "es"], new StringWriter(), stderr, repo.Git.Path));
			Assert.Contains("error dotnet.outdated: _fx/dotnet.g.props: Desactualizado respecto a _fx/dotnet.yaml. → fx sync dotnet", stderr.ToString());

			var stdout = new StringWriter();
			FxApp.Run(["doctor", "--lang", "es", "--output", "json"], stdout, new StringWriter(), repo.Git.Path);
			var outdated = JsonDocument.Parse(stdout.ToString()).RootElement.GetProperty("diagnostics").EnumerateArray().First();
			Assert.Equal("Out of date with _fx/dotnet.yaml.", outdated.GetProperty("message").GetString());

			stdout = new StringWriter();
			Assert.Equal(0, FxApp.Run(["sync", "--help", "--lang", "es"], stdout, new StringWriter(), repo.Git.Path));
			Assert.Contains("Genera y actualiza lo que es de fx aquí", stdout.ToString());
			Assert.Contains("--dry-run", stdout.ToString());

			// FX_LANG, and back to English
			stderr = new StringWriter();
			Environment.SetEnvironmentVariable(FxLanguage.Variable, "es");
			FxApp.Run(["doctor"], new StringWriter(), stderr, repo.Git.Path);
			Assert.Contains("Desactualizado", stderr.ToString());
		}
		finally
		{
			Environment.SetEnvironmentVariable(FxLanguage.Variable, "en");
			FxLanguage.Apply("en");
		}
		var english = new StringWriter();
		FxApp.Run(["doctor"], new StringWriter(), english, repo.Git.Path);
		Assert.Contains("Out of date with _fx/dotnet.yaml.", english.ToString());
	}
}
