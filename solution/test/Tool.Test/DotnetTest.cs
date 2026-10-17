using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Fuxion.Tool.Cli;
using Fuxion.Tool.Core.Dotnet;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Sync;
using Xunit;

namespace Fuxion.Tool.Test;

/// <summary>The .NET module: dotnet.yaml, its generation and the doctor (plan K, K4; plan P).</summary>
public sealed class DotnetTest
{
	const string DesignYaml = """
		schema: 1.0.0
		sdks:
		  Fuxion.Tool.Sdk: 0.1.0

		variables:
		  - name: CoreFrameworks
		    value: net10.0;net11.0
		  - name: CoreWindowsFrameworks
		    value: net10.0-windows10.0.19041;net11.0-windows10.0.19041
		    applies-to:
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

	[Fact(DisplayName = "reader: sections, variables, packages and applies-to")]
	public void Read()
	{
		var config = DotnetYamlReader.Read(DesignYaml, "dotnet.yaml");
		Assert.Equal("0.1.0", config.Sdks["Fuxion.Tool.Sdk"]);
		Assert.Equal(5, config.Variables.Count);
		Assert.Equal("net10.0;net11.0", config.Variables[0].Value);
		Assert.Equal(["windows"], config.Variables[1].AppliesTo.Tags);
		Assert.True(config.Variables[2].IsWhen);
		Assert.Equal(["Microsoft.Extensions.Http", "NotUsed.Package"], config.Packages[1].Ids);
		Assert.Equal(2, config.Packages[0].Versions.Count);
		Assert.Null(config.AppliesTo);
		Assert.True(config.Enabled);
	}

	[Fact(DisplayName = "reader: an empty file is an empty configuration")]
	public void Read_Empty() => Assert.Empty(DotnetYamlReader.Read("", "dotnet.yaml").Variables);

	[Theory(DisplayName = "reader: every problem, with its line")]
	[InlineData("variables:\n  - name: X\n", "needs exactly one of 'value' or 'when'", 2)]
	[InlineData("variables:\n  - name: X\n    value: a\n    when: b\n", "needs exactly one of 'value' or 'when'", 2)]
	[InlineData("variables:\n  - name: 1X\n    value: a\n", "valid MSBuild property name", 2)]
	[InlineData("packages:\n  - ids: [A]\n    versions:\n      - version: 1.0.0\n      - version: 2.0.0\n        when: x\n", "each needs 'when'", 4)]
	[InlineData("packages:\n  - versions:\n      - when: x\n        version: 1\n", "needs 'ids'", 2)]
	[InlineData("other: 1\n", "Unknown key 'other'", 1)]
	[InlineData("variables:\n  - name: A\n    value: 1\n  - name: a\n    value: 2\n", "defined more than once", 4)]
	[InlineData("variables: [\n", "", 2)]
	[InlineData("variables:\n  - name: X\n    value: a\n    define: X\n", "'define' needs 'when'", 2)]
	[InlineData("variables:\n  - name: X\n    when: a\n    define: OLD-FRAMEWORKS\n", "compilation constants separated by ';'", 2)]
	[InlineData("imports:\n  - when: x\n", "needs 'props' or 'targets'", 2)]
	[InlineData("imports:\n  - props: ../outside.props\n", "without '..'", 2)]
	[InlineData("imports:\n  - props: a.props\n    tags: [x]\n", "Unknown key 'tags'", 3)]
	[InlineData("solutions:\n  - path: All.slnx\n    folders:\n      - name: /\n        files: [README.md]\n", "a file cannot go in the root", 4)]
	[InlineData("solutions:\n  - path: All.sln\n", "a .slnx relative to this root", 2)]
	public void Read_Invalid(string yaml, string message, int line)
	{
		var ex = Assert.Throws<DotnetConfigException>(() => DotnetYamlReader.Read("schema: 1.0.0\n" + yaml, "dotnet.yaml"));
		var diagnostic = ex.Diagnostics.First(d => d.Severity == FxSeverity.Error);
		Assert.Equal(DotnetYamlReader.InvalidYaml, diagnostic.Code);
		Assert.Contains(message, diagnostic.Message.English);
		Assert.Equal(line + 1, diagnostic.Line);
	}

	[Fact(DisplayName = "reader: solutions with their folders, standalone, enabled and the module's applies-to")]
	public void Read_Solutions()
	{
		var config = DotnetYamlReader.Read("""
			schema: 1.0.0
			enabled: false
			standalone: true
			applies-to:
			  tags: [dotnet]
			  repositories: [shared, /acme/repo-w1]
			solutions:
			  - path: Acme.slnx
			    folders:
			      - name: /acme
			        files:
			          - README.md
			      - name: /products/w1
			        solutions:
			          - repo-w1/W1.slnx
			""", "dotnet.yaml");
		Assert.False(config.Enabled);
		Assert.True(config.Standalone);
		Assert.Equal(["shared", "/acme/repo-w1"], config.AppliesTo!.Repositories);
		Assert.Equal(["/acme/shared", "/acme/repo-w1"], config.Absolute("/acme").AppliesTo!.Repositories);
		var solution = Assert.Single(config.Solutions);
		Assert.Equal("Acme.slnx", solution.Path);
		Assert.Equal(["/acme", "/products/w1"], solution.Folders.Select(f => f.Name));
		Assert.Equal(["repo-w1/W1.slnx"], solution.Folders[1].Solutions);
	}

	[Fact(DisplayName = "combine: the nearest wins by sdk, variable name, package id and imported file; the solutions do not go down")]
	public void Combine()
	{
		var inherited = DotnetYamlReader.Read(DesignYaml + "\nimports:\n  - targets: test.targets\n  - props: default.props\n", "ws");
		var own = DotnetYamlReader.Read("""
			sdks:
			  Fuxion.Tool.Sdk: 0.2.0
			variables:
			  - name: CoreFrameworks
			    value: net8.0
			imports:
			  - targets: test.targets
			    when: "'$(IsTestProject)' == 'true'"
			packages:
			  - ids: [NotUsed.Package]
			    versions:
			      - when: "true"
			        version: 9.9.9
			solutions:
			  - path: Own.slnx
			""", "repo");
		var combined = DotnetConfig.Combine(inherited, own);
		Assert.Equal("0.2.0", combined.Sdks["Fuxion.Tool.Sdk"]);
		Assert.Equal("net8.0", combined.Variables.Single(v => v.Name == "CoreFrameworks").Value);
		Assert.Equal(5, combined.Variables.Count);
		Assert.Equal(["Microsoft.Extensions.Http"], combined.Packages.Single(p => p.Ids.Contains("Microsoft.Extensions.Http")).Ids);
		Assert.Equal("9.9.9", combined.Packages.Single(p => p.Ids.Contains("NotUsed.Package")).Versions[0].Version);
		Assert.Single(combined.Imports, i => i.Targets.Contains("test.targets"));
		Assert.Equal("'$(IsTestProject)' == 'true'", combined.Imports.Single(i => i.Targets.Contains("test.targets")).When);
		Assert.Single(combined.Solutions);
	}

	[Fact(DisplayName = "applies-to: what has none goes to every repo; the rest, by tag or by name")]
	public void AppliesTo()
	{
		var config = DotnetYamlReader.Read(DesignYaml, "x");
		Assert.DoesNotContain(config.ForTarget(new("plus", "/test/plus", [])).Variables, v => v.Name == "CoreWindowsFrameworks");
		Assert.Contains(config.ForTarget(new("plus", "/test/plus", ["Windows"])).Variables, v => v.Name == "CoreWindowsFrameworks");
		var byName = new FxAppliesTo([], ["/test/plus"]);
		Assert.True(byName.Matches(new("plus", "/test/plus", [])));
		Assert.True(byName.Matches(new("plus2", "/test/plus2", [], ["/test/plus"])));
		Assert.False(byName.Matches(new("lab", "/test/lab", [])));
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

	// Module, on a repository with fx and no workspace

	internal sealed class Repo : IDisposable
	{
		public readonly TempGitRepository Git = new();

		public Repo(string yaml = DesignYaml, string frameworks = "$(CoreFrameworks);netstandard2.0", string centralPackages = "")
		{
			Git.WriteFile(Path.Combine("_fx", "fx.yaml"), "schema: 1.0.0\nname: lib\n");
			Git.WriteFile(Path.Combine("_fx", "dotnet", "dotnet.yaml"), yaml);
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

	static FxModuleResult Sync(string directory, bool dryRun = false)
		=> Assert.Single(SyncEngine.Sync(directory, new(DryRun: dryRun), cancellationToken: TestContext.Current.CancellationToken), r => r.Module == DotnetModule.Name);

	static IReadOnlyList<FxDiagnostic> Doctor(string directory)
		=> SyncEngine.Doctor(directory, new(Offline: true), cancellationToken: TestContext.Current.CancellationToken).SelectMany(r => r.Diagnostics).ToList();

	static IEnumerable<string> Codes(IEnumerable<FxDiagnostic> diagnostics) => diagnostics.Select(d => d.Code);

	[Fact(DisplayName = "sync: the three generated files, global.json and the Import; doctor all right after it")]
	public void Sync_Repo()
	{
		using var repo = new Repo();
		var result = Sync(repo.Git.Path);
		Assert.Empty(result.Diagnostics);
		Assert.All(result.Files, f => Assert.Equal(SyncFileStatus.Written, f.Status));
		Assert.Equal(["_fx/dotnet/dotnet.g.props", "_fx/dotnet/dotnet.g.targets", "_fx/dotnet/packages.g.props", "_fx/dotnet/projects.g.props", "global.json",
			"solution/Directory.Packages.props"], result.Files.Select(f => f.Path));
		// its projects by name, for an FxReference inside the repository (plan R, decision 1)
		Assert.Contains("<FxRepositoryProject Include=\"Lib\" Path=\"$(MSBuildThisFileDirectory)../../solution/src/Lib/Lib.csproj\" />", repo.Read("_fx/dotnet/projects.g.props"));

		var props = repo.Read("_fx/dotnet/dotnet.g.props");
		Assert.StartsWith($"<!-- {FxHeader.Title} -->\n<!-- Generated from: _fx/dotnet/dotnet.yaml -->\n<!-- Regenerated with: fx sync -->\n<Project>\n", props);
		Assert.Contains("<FxRepositoryName>lib</FxRepositoryName>", props);
		Assert.Contains("<FxRepositoryFullName>/lib</FxRepositoryFullName>", props);
		Assert.Contains("<CoreFrameworks>net10.0;net11.0</CoreFrameworks>", props);
		Assert.DoesNotContain("CoreWindowsFrameworks", props);
		Assert.Contains("<IsNet11 Condition=\"'$(TargetFramework)' == 'net11.0'\">true</IsNet11>", repo.Read("_fx/dotnet/dotnet.g.targets"));
		var packages = repo.Read("_fx/dotnet/packages.g.props");
		Assert.Contains("<PackageVersion Include=\"MongoDB.EntityFrameworkCore\" Version=\"9.1.3\" Condition=\"('$(TargetFramework)' == 'netstandard2.0' Or '$(TargetFramework)' == 'net472')\" />", packages);
		Assert.DoesNotContain("NotUsed.Package", packages);
		var global = JsonDocument.Parse(repo.Read("global.json")).RootElement;
		Assert.Equal("0.1.0", global.GetProperty("msbuild-sdks").GetProperty("Fuxion.Tool.Sdk").GetString());
		Assert.Equal("Microsoft.Testing.Platform", global.GetProperty("test").GetProperty("runner").GetString());
		Assert.Contains("<Import Project=\"$(MSBuildThisFileDirectory)../_fx/dotnet/packages.g.props\"", repo.Read("solution/Directory.Packages.props"));

		var again = Sync(repo.Git.Path);
		Assert.All(again.Files, f => Assert.Equal(SyncFileStatus.Unchanged, f.Status));
		Assert.Empty(Doctor(repo.Git.Path));
	}

	[Fact(DisplayName = "sync: msbuild-sdks is fx's, exactly the sdks of dotnet.yaml: one renamed or removed goes from global.json")]
	public void Sync_GlobalJsonExactly()
	{
		using var repo = new Repo();
		repo.Git.WriteFile("global.json",
			"{\n  \"msbuild-sdks\": { \"Fuxion.Tools.Sdk\": \"0.5.0\", \"Fuxion.Tool.Sdk\": \"0.1.0\" },\n  \"test\": { \"runner\": \"Microsoft.Testing.Platform\" }\n}\n");
		Assert.Contains(Codes(Doctor(repo.Git.Path)), c => c == DotnetModule.Outdated);
		Sync(repo.Git.Path);
		var global = JsonDocument.Parse(repo.Read("global.json")).RootElement;
		Assert.Equal(["Fuxion.Tool.Sdk"], global.GetProperty("msbuild-sdks").EnumerateObject().Select(p => p.Name));
		Assert.Equal("0.1.0", global.GetProperty("msbuild-sdks").GetProperty("Fuxion.Tool.Sdk").GetString());
		Assert.Equal("Microsoft.Testing.Platform", global.GetProperty("test").GetProperty("runner").GetString());
		Assert.Equal(SyncFileStatus.Unchanged, Assert.Single(Sync(repo.Git.Path).Files, f => f.Path == "global.json").Status);
	}

	[Fact(DisplayName = "sync: the Import of an older layout in Directory.Packages.props is moved, not added again")]
	public void ImportMoved()
	{
		using var repo = new Repo();
		repo.Git.WriteFile(Path.Combine("solution", "Directory.Packages.props"), """
			<Project>

				<!-- The packages governed by _fx/dotnet.yaml (fx sync dotnet, design §7.3) -->
				<Import Project="$(MSBuildThisFileDirectory)../_fx/packages.g.props" Condition="Exists('$(MSBuildThisFileDirectory)../_fx/packages.g.props')" />
				<PropertyGroup>
					<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
				</PropertyGroup>
			</Project>
			""");
		Sync(repo.Git.Path);
		var text = repo.Read("solution/Directory.Packages.props");
		Assert.Contains("<Import Project=\"$(MSBuildThisFileDirectory)../_fx/dotnet/packages.g.props\"", text);
		Assert.DoesNotContain("_fx/packages.g.props", text);
		Assert.DoesNotContain("design", text);
		Assert.Single(text.Split('\n'), l => l.Contains("<Import"));
	}

	[Fact(DisplayName = "sync: a build file deleted and not committed yet is not read")]
	public void DeletedFile()
	{
		using var repo = new Repo();
		repo.Git.WriteFile(Path.Combine("solution", "src", "Directory.Build.targets"), "<Project />");
		repo.Git.CommitAll("c1");
		File.Delete(repo.At(Path.Combine("solution", "src", "Directory.Build.targets")));
		Assert.Empty(Sync(repo.Git.Path).Diagnostics);
	}

	[Fact(DisplayName = "sync --dry-run writes nothing; doctor reports every outdated file")]
	public void DryRunAndDoctor()
	{
		using var repo = new Repo();
		var dry = Sync(repo.Git.Path, dryRun: true);
		Assert.All(dry.Files, f => Assert.Equal(SyncFileStatus.WouldWrite, f.Status));
		Assert.False(File.Exists(repo.At("_fx/dotnet/dotnet.g.props")));
		Assert.Equal(6, Codes(Doctor(repo.Git.Path)).Count(c => c == DotnetModule.Outdated));
	}

	[Fact(DisplayName = "a file without the GENERATED header is not fx's: not written, and an error")]
	public void NotGenerated()
	{
		using var repo = new Repo();
		repo.Git.WriteFile(Path.Combine("_fx", "dotnet", "dotnet.g.props"), "<Project />");
		var result = Sync(repo.Git.Path);
		Assert.Contains(result.Files, f => f.Path == "_fx/dotnet/dotnet.g.props" && f.Status == SyncFileStatus.Refused);
		Assert.Contains(FxOutputs.NotGenerated, Codes(result.Diagnostics));
		Assert.Equal("<Project />", repo.Read("_fx/dotnet/dotnet.g.props"));
	}

	[Fact(DisplayName = "a file with the header of fx before 0.6.0 (Fuxion.Tools) is fx's: rewritten with the current one")]
	public void FormerHeader()
	{
		using var repo = new Repo();
		repo.Git.WriteFile(Path.Combine("_fx", "dotnet", "dotnet.g.props"),
			$"<!-- ===      {FxHeader.FormerMark} - DO NOT EDIT      === -->\n<!-- Generated from: _fx/dotnet/dotnet.yaml -->\n<!-- Regenerated with: fx sync -->\n<Project />\n");
		var result = Sync(repo.Git.Path);
		Assert.Contains(result.Files, f => f.Path == "_fx/dotnet/dotnet.g.props" && f.Status == SyncFileStatus.Written);
		Assert.DoesNotContain(FxOutputs.NotGenerated, Codes(result.Diagnostics));
		var props = repo.Read("_fx/dotnet/dotnet.g.props");
		Assert.StartsWith($"<!-- {FxHeader.Title} -->\n", props);
		Assert.DoesNotContain(FxHeader.FormerMark, props);
		// and what fx copies down without its header loses the former one too
		Assert.DoesNotContain(FxHeader.FormerMark, Encoding.UTF8.GetString(FxHeader.Strip(Encoding.UTF8.GetBytes(
			$"# ===      {FxHeader.FormerMark} - DO NOT EDIT      ===\n# Generated from: x\n# Regenerated with: fx sync\nkey: value\n"))));
	}

	[Fact(DisplayName = "doctor: a package governed by dotnet.yaml and by Directory.Packages.props (D-16)")]
	public void Doctor_DuplicateOwner()
	{
		using var repo = new Repo(centralPackages: "\n\t\t<PackageVersion Include=\"MongoDB.EntityFrameworkCore\" Version=\"1.0.0\" />");
		Sync(repo.Git.Path);
		var diagnostic = Assert.Single(Doctor(repo.Git.Path));
		Assert.Equal(DotnetModule.DuplicateOwner, diagnostic.Code);
		Assert.Equal("solution/Directory.Packages.props", diagnostic.File);
	}

	[Fact(DisplayName = "doctor: a framework no version covers (a new net12.0) is a gap (D-32)")]
	public void Doctor_Gap()
	{
		using var repo = new Repo(frameworks: "$(CoreFrameworks);net12.0");
		Sync(repo.Git.Path);
		var gaps = Doctor(repo.Git.Path).Where(d => d.Code == DotnetModule.CoverageGap).ToList();
		Assert.Equal(2, gaps.Count);
		Assert.All(gaps, g => Assert.Contains("net12.0", g.Message.English));
	}

	[Fact(DisplayName = "doctor: two versions for the same framework is an overlap (D-32)")]
	public void Doctor_Overlap()
	{
		using var repo = new Repo(yaml: DesignYaml.Replace("when: \"$(IsOldFramework)\"", "when: \"$(IsOldFramework) Or $(IsNet11)\""));
		Sync(repo.Git.Path);
		var overlap = Assert.Single(Doctor(repo.Git.Path));
		Assert.Equal(DotnetModule.CoverageOverlap, overlap.Code);
		Assert.Contains("'MongoDB.EntityFrameworkCore' on net11.0: several versions apply (10.0.3, 9.1.3)", overlap.Message.English);
	}

	[Fact(DisplayName = "doctor: the incomplete condition of the design's MongoDB example cannot be verified (warning)")]
	public void Doctor_Unverifiable()
	{
		using var repo = new Repo(yaml: DesignYaml.Replace("when: \"$(IsOldFramework)\"", "when: \"'$(TargetFramework)' == 'net472' Or '$(TargetFramework)'\""));
		Sync(repo.Git.Path);
		Assert.Contains(Doctor(repo.Git.Path), d => d.Code == DotnetModule.Unverifiable && d.Severity == FxSeverity.Warning);
	}

	const string ImportsYaml = """
		schema: 1.0.0
		variables:
		  - name: IsOldFramework
		    when: "'$(TargetFramework)' == 'net472'"
		imports:
		  - props: default.props
		  - props: [packable.props]
		    targets: packable.targets
		    when: "'$(IsPackable)' == 'true'"
		  - targets: test.targets
		    when: "'$(IsTestProject)' == 'true'"
		  - targets: old.targets
		    when: "$(IsOldFramework)"
		    applies-to:
		      tags: [windows]
		packages:
		  - ids: [xunit.v3]
		    versions:
		      - when: "'$(TargetFramework)' != ''"
		        version: 4.0.0
		""";

	[Fact(DisplayName = "reader: imports, with one path or a list, when and applies-to")]
	public void Read_Imports()
	{
		var imports = DotnetYamlReader.Read(ImportsYaml, "dotnet.yaml").Imports;
		Assert.Equal(4, imports.Count);
		Assert.Equal(["default.props"], imports[0].Props);
		Assert.Null(imports[0].When);
		Assert.Equal(["packable.props"], imports[1].Props);
		Assert.Equal(["packable.targets"], imports[1].Targets);
		Assert.Equal(["windows"], imports[3].AppliesTo.Tags);
	}

	[Fact(DisplayName = "sync: the imports in dotnet.g.props (props) and dotnet.g.targets (targets), relative to the module folder, conditions expanded")]
	public void Sync_Imports()
	{
		using var repo = new Repo(ImportsYaml);
		foreach (var file in new[] { "default.props", "packable.props", "packable.targets", "old.targets" })
			repo.Git.WriteFile(Path.Combine("_fx", "dotnet", file), "<Project />");
		// a package only an imported file uses is still the repo's: it gets its version
		repo.Git.WriteFile(Path.Combine("_fx", "dotnet", "test.targets"), """<Project><ItemGroup><PackageReference Include="xunit.v3" /></ItemGroup></Project>""");
		var result = Sync(repo.Git.Path);
		Assert.Empty(result.Diagnostics);

		var props = repo.Read("_fx/dotnet/dotnet.g.props");
		Assert.Contains("<Import Project=\"$(MSBuildThisFileDirectory)default.props\" />", props);
		Assert.Contains("<Import Project=\"$(MSBuildThisFileDirectory)packable.props\" Condition=\"'$(IsPackable)' == 'true'\" />", props);
		Assert.DoesNotContain("test.targets", props);
		var targets = repo.Read("_fx/dotnet/dotnet.g.targets");
		Assert.Contains("<Import Project=\"$(MSBuildThisFileDirectory)packable.targets\" Condition=\"'$(IsPackable)' == 'true'\" />", targets);
		Assert.Contains("<Import Project=\"$(MSBuildThisFileDirectory)test.targets\" Condition=\"'$(IsTestProject)' == 'true'\" />", targets);
		Assert.DoesNotContain("old.targets", targets); // for [windows], and the repo has no tags
		Assert.Contains("Include=\"xunit.v3\"", repo.Read("_fx/dotnet/packages.g.props"));
		Assert.Empty(Doctor(repo.Git.Path));

		File.Delete(repo.At(Path.Combine("_fx", "dotnet", "default.props")));
		Assert.Contains(DotnetModule.MissingImport, Codes(Doctor(repo.Git.Path)));
	}

	[Fact(DisplayName = "a clone on its own: who it is and what it inherits come from _fx/.inherited/")]
	public void Inherited()
	{
		using var repo = new Repo("");
		File.Delete(repo.At(Path.Combine("_fx", "dotnet", "dotnet.yaml")));
		repo.Git.WriteFile(Path.Combine("_fx", ".inherited", "dotnet", "dotnet.yaml"), ImportsYaml);
		repo.Git.WriteFile(Path.Combine("_fx", ".inherited", "inherited.yaml"),
			"schema: 1.0.0\nrepository:\n  name: plus\n  full-name: /fuxion/plus\n  tags: [dotnet, windows]\nparent:\n  full-name: /fuxion\n");
		foreach (var file in new[] { "default.props", "packable.props", "packable.targets", "test.targets", "old.targets" })
			repo.Git.WriteFile(Path.Combine("_fx", ".inherited", "dotnet", file), "<Project />");
		Assert.Empty(Sync(repo.Git.Path).Diagnostics);
		// the imports from _fx/.inherited/; the one for windows applies (its tags, from inherited.yaml)
		var targets = repo.Read("_fx/dotnet/dotnet.g.targets");
		Assert.Contains("<Import Project=\"$(MSBuildThisFileDirectory)../.inherited/dotnet/old.targets\" Condition=\"('$(TargetFramework)' == 'net472')\" />", targets);
		Assert.StartsWith($"<!-- {FxHeader.Title} -->\n<!-- Generated from: _fx/.inherited/dotnet/dotnet.yaml -->", targets);
		Assert.Contains("<FxRepositoryFullName>/fuxion/plus</FxRepositoryFullName>", repo.Read("_fx/dotnet/dotnet.g.props"));
	}

	[Fact(DisplayName = "enabled: false turns the module off: what fx generated for it goes")]
	public void Disabled()
	{
		using var repo = new Repo();
		Sync(repo.Git.Path);
		repo.Git.WriteFile(Path.Combine("_fx", "dotnet", "dotnet.yaml"), "schema: 1.0.0\nenabled: false\n" + DesignYaml["schema: 1.0.0\n".Length..]);
		Assert.All(Sync(repo.Git.Path).Files, f => Assert.Equal(SyncFileStatus.Written, f.Status));
		Assert.False(File.Exists(repo.At("_fx/dotnet/dotnet.g.props")));
		Assert.True(File.Exists(repo.At("_fx/dotnet/dotnet.yaml")));
	}

	[Fact(DisplayName = "one version without when: for every framework, without a condition")]
	public void SingleVersion()
	{
		using var repo = new Repo("schema: 1.0.0\npackages:\n  - ids: [Spectre.Console]\n    versions:\n      - version: 0.57.2\n");
		repo.Git.WriteFile(Path.Combine("solution", "src", "Lib", "Lib.csproj"),
			"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks>net10.0;net472</TargetFrameworks></PropertyGroup><ItemGroup><PackageReference Include="Spectre.Console" /></ItemGroup></Project>""");
		repo.Git.WriteFile(Path.Combine("solution", "Directory.Packages.props"),
			"<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>");
		Assert.Empty(Sync(repo.Git.Path).Diagnostics);
		Assert.Contains("<PackageVersion Include=\"Spectre.Console\" Version=\"0.57.2\" />", repo.Read("_fx/dotnet/packages.g.props"));
		Assert.Empty(Doctor(repo.Git.Path));
	}

	[Fact(DisplayName = "a package used only through FxReference gets its version: outside the workspace it is a package")]
	public void FxReferenceVersion()
	{
		using var repo = new Repo("schema: 1.0.0\npackages:\n  - ids: [Fuxion]\n    versions:\n      - version: 11.0.0\n");
		repo.Git.WriteFile(Path.Combine("solution", "src", "Lib", "Lib.csproj"),
			"""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><FxReference Include="Fuxion" /><FxReference Include="Other.Lib" /></ItemGroup></Project>""");
		repo.Git.WriteFile(Path.Combine("solution", "Directory.Packages.props"),
			"<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include=\"Other.Lib\" Version=\"1.0.0\" /></ItemGroup></Project>");
		repo.Git.WriteFile(Path.Combine("solution", "src", "Lib", "obj", "project.assets.json"), """{ "libraries": { } }""");
		Assert.Empty(Sync(repo.Git.Path).Diagnostics);
		Assert.Contains("<PackageVersion Include=\"Fuxion\" Version=\"11.0.0\" />", repo.Read("_fx/dotnet/packages.g.props"));
		Assert.DoesNotContain(DotnetModule.UnusedPackage, Codes(Doctor(repo.Git.Path)));
	}

	[Fact(DisplayName = "without central package management: PackageReference Update after the imports, no packages.g.props")]
	public void WithoutCentralManagement()
	{
		using var repo = new Repo(ImportsYaml);
		File.Delete(repo.At(Path.Combine("solution", "Directory.Packages.props")));
		foreach (var file in new[] { "default.props", "packable.props", "packable.targets", "old.targets" })
			repo.Git.WriteFile(Path.Combine("_fx", "dotnet", file), "<Project />");
		repo.Git.WriteFile(Path.Combine("_fx", "dotnet", "test.targets"), """<Project><ItemGroup><PackageReference Include="xunit.v3" /></ItemGroup></Project>""");
		Assert.Empty(Sync(repo.Git.Path).Diagnostics);

		var targets = repo.Read("_fx/dotnet/dotnet.g.targets");
		var update = targets.IndexOf("<PackageReference Update=\"xunit.v3\" Version=\"4.0.0\" Condition=\"'$(TargetFramework)' != ''\" />", StringComparison.Ordinal);
		Assert.True(update > targets.IndexOf("test.targets", StringComparison.Ordinal), targets); // after the import that adds it
		Assert.False(File.Exists(repo.At("_fx/dotnet/packages.g.props")));

		// a Version in a project is fx's to override: the doctor says so
		repo.Git.WriteFile(Path.Combine("solution", "test", "T", "T.csproj"),
			"""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="xunit.v3" Version="3.0.0" /></ItemGroup></Project>""");
		Assert.Contains(DotnetModule.IgnoredVersion, Codes(Doctor(repo.Git.Path)));
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
		Sync(repo.Git.Path);

		// a fresh clone: what is transitive cannot be told, so nothing is reported
		Assert.DoesNotContain(DotnetModule.UnusedPackage, Codes(Doctor(repo.Git.Path)));

		// restored: Transitive.Package comes through another package, Spectre.Console and Unused.Package nobody uses
		repo.Git.WriteFile(Path.Combine("solution", "src", "Lib", "obj", "project.assets.json"),
			"""{ "libraries": { "Direct.Package/1.0.0": { "type": "package" }, "Transitive.Package/1.0.0": { "type": "package" } } }""");
		var unused = Doctor(repo.Git.Path).Where(d => d.Code == DotnetModule.UnusedPackage).ToList();
		Assert.Equal(["'Spectre.Console'", "'Unused.Package'"], unused.Select(d => d.Message.English.Split(' ')[0]));
		Assert.All(unused, d => Assert.Equal(FxSeverity.Warning, d.Severity));
		Assert.Equal("solution/Directory.Packages.props", unused[1].File);
		Assert.Equal(repo.Read("solution/Directory.Packages.props").Split('\n').ToList().FindIndex(l => l.Contains("Unused.Package")) + 1, unused[1].Line);
	}

	[Fact(DisplayName = "no .NET configuration: the module has nothing to do here")]
	public void NoConfig()
	{
		using var git = new TempGitRepository();
		Assert.DoesNotContain(SyncEngine.Sync(git.Path, new(), cancellationToken: TestContext.Current.CancellationToken), r => r.Module == DotnetModule.Name);
	}

	[Fact(DisplayName = "an invalid dotnet.yaml: the reader's diagnostics, relative to the repo")]
	public void InvalidConfig()
	{
		using var repo = new Repo(yaml: "schema: 1.0.0\nvariables:\n  - name: X\n");
		var diagnostic = Assert.Single(Sync(repo.Git.Path).Diagnostics);
		Assert.Equal(DotnetYamlReader.InvalidYaml, diagnostic.Code);
		Assert.Equal("_fx/dotnet/dotnet.yaml", diagnostic.File);
		Assert.Equal(3, diagnostic.Line);
	}

	[Fact(DisplayName = "a root without a name: an error, and its folder's name meanwhile")]
	public void NameMissing()
	{
		using var repo = new Repo();
		File.Delete(repo.At(Path.Combine("_fx", "fx.yaml")));
		var results = SyncEngine.Sync(repo.Git.Path, new(), cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(results.SelectMany(r => r.Diagnostics), d => d.Code == FxNode.NameMissing);
	}

	// CLI

	[Fact(DisplayName = "fx sync and fx doctor: fx-sync/1 and fx-doctor/1, exit code 1 only with errors")]
	public void Cli()
	{
		using var repo = new Repo();
		var stdout = new StringWriter();
		var stderr = new StringWriter();
		Assert.Equal(1, InProcess.Run(["doctor", "dotnet"], stdout, stderr, repo.Git.Path));
		// for people, as flutter doctor and on stdout (plan O, decision 8): the module, each problem and its fix, the summary
		var doctor = stdout.ToString();
		Assert.Contains("[✗] dotnet     6 error(s)", doctor);
		Assert.Contains("Out of date: fx sync would change it. (_fx/dotnet/dotnet.g.props · dotnet.outdated)", doctor);
		Assert.Contains("      → fx sync", doctor);
		Assert.Contains("Doctor found issues in 1 module(s).", doctor);
		Assert.Empty(stderr.ToString());

		// The diagnostics, with their scope and fix (plan O, decision 13)
		stdout = new StringWriter();
		Assert.Equal(1, InProcess.Run(["doctor", "dotnet", "--output", "json"], stdout, new StringWriter(), repo.Git.Path));
		var outdated = JsonDocument.Parse(stdout.ToString()).RootElement.GetProperty("diagnostics").EnumerateArray().First();
		Assert.Equal("dotnet.outdated", outdated.GetProperty("code").GetString());
		Assert.Equal("dotnet", outdated.GetProperty("module").GetString());
		Assert.Equal("lib", outdated.GetProperty("repository").GetString());
		Assert.Equal("fx sync", outdated.GetProperty("fix").GetProperty("command").GetString());

		stdout = new StringWriter();
		Assert.Equal(0, InProcess.Run(["sync", "dotnet", "--output", "json"], stdout, new StringWriter(), repo.Git.Path));
		var json = JsonDocument.Parse(stdout.ToString()).RootElement;
		Assert.Equal("fx-sync/1", json.GetProperty("schema").GetString());
		Assert.True(json.GetProperty("ok").GetBoolean());
		var dotnet = Assert.Single(json.GetProperty("modules").EnumerateArray());
		Assert.Equal("dotnet", dotnet.GetProperty("module").GetString());
		Assert.Equal(6, dotnet.GetProperty("files").EnumerateArray().Count(f => f.GetProperty("status").GetString() == "written"));

		stdout = new StringWriter();
		Assert.Equal(0, InProcess.Run(["doctor", "dotnet", "--output", "json"], stdout, new StringWriter(), repo.Git.Path));
		Assert.Equal("fx-doctor/1", JsonDocument.Parse(stdout.ToString()).RootElement.GetProperty("schema").GetString());
	}

	[Fact(DisplayName = "a composed solution keeps the paths as its solutions write them, also under a '~' (a worktree), whatever the case on disk")]
	public void Solution_PathsAsWritten()
	{
		// .NET takes a path with '~' for an 8.3 short name and, on Windows, gives it the case it has on disk
		var root = TempGitRepository.TempFolder("fx~");
		try
		{
			Directory.CreateDirectory(Path.Combine(root, "sub", "src", "xUnit"));
			File.WriteAllText(Path.Combine(root, "sub", "src", "xUnit", "A.csproj"), "<Project />");
			File.WriteAllText(Path.Combine(root, "sub", "Sub.slnx"), "<Solution>\n  <Project Path=\"src/Xunit/A.csproj\" />\n  <Project Path=\"./src/../src/Xunit/A.csproj\" />\n</Solution>\n");
			var solution = new DotnetSolution("build/All.slnx", [new DotnetSolutionFolder("/sub", ["sub/Sub.slnx"], ["sub/Sub.slnx"], 1)], 1);
			var composed = SolutionComposer.Compose(root, solution, null, new FxDiagnostics(), p => p);
			Assert.Contains("<Project Path=\"../sub/src/Xunit/A.csproj\" />", composed);
			Assert.Contains("<File Path=\"../sub/Sub.slnx\" />", composed);
			Assert.Single(composed.Split('\n'), l => l.Contains("A.csproj"));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}
}
