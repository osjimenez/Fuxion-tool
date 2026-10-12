using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Fuxion.Tools.Cli;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Sync;
using Fuxion.Tools.Core.Workspace;
using Xunit;

namespace Fuxion.Tools.Test;

/// <summary>The workspace module and the sync of a tree: manifest, local state, inheritance, sync, doctor and fx repo (plans M and P).</summary>
public sealed class WorkspaceTest
{
	/// <summary>
	/// A workspace (a git repository with <c>_fx/workspace/</c>) and, for each repository of the manifest, an "origin": a
	/// local repository with a commit, two projects and a solution, cloned by URL (its path). The workspace composes
	/// <c>Test.slnx</c> from their solutions; its .NET module goes down to the repositories tagged <c>dotnet</c>.
	/// </summary>
	sealed class Workspace : IDisposable
	{
		public readonly TempGitRepository Meta = new();
		public readonly Dictionary<string, TempGitRepository> Origins = [];

		public Workspace(params (string Name, string Mount)[] repositories)
		{
			foreach (var (name, _) in repositories)
				Origins[name] = Origin(name);
			Meta.WriteFile(Path.Combine("_fx", "fx.yaml"), "schema: 1.0.0\nname: test\ntags:\n  - name: dotnet\n  - name: x\n  - name: other\n");
			WriteManifest(repositories.Select(r => (r.Name, r.Mount, "")).ToArray());
			WriteDotnet("", repositories.Select(r => r.Name).ToArray());
			Meta.CommitAll("workspace");
		}

		/// <summary>A repository to clone: its solution, two projects and a .gitignore that ignores fx's local files.</summary>
		public static TempGitRepository Origin(string name)
		{
			var origin = new TempGitRepository();
			origin.WriteFile(".gitignore", "~$*\n");
			origin.WriteFile($"Fx{name}.slnx", $"""
				<Solution>
				  <Configurations>
				    <Platform Name="Any CPU" />
				  </Configurations>
				  <Folder Name="/src/">
				    <Project Path="solution/src/{name}/{name}.csproj" />
				  </Folder>
				  <Folder Name="/tools/">
				    <Project Path="solution/tools/Tool/Tool.csproj" />
				  </Folder>
				</Solution>
				""");
			origin.WriteFile(Path.Combine("solution", "src", name, $"{name}.csproj"), "<Project />\n");
			origin.WriteFile(Path.Combine("solution", "tools", "Tool", "Tool.csproj"), "<Project />\n");
			origin.CommitAll("c1");
			return origin;
		}

		public void WriteManifest(params (string Name, string Mount, string Extra)[] repositories)
			=> Meta.WriteFile(Path.Combine("_fx", "workspace", "workspace.yaml"), "schema: 1.0.0\npaths:\n  - path: _docs/\nrepositories:\n" + string.Concat(repositories.Select(r => $"""
				  - name: {r.Name}
				    path: {r.Name}/repo
				    url: {Origins[r.Name].Path.Replace('\\', '/')}
				    mount: {r.Mount}
				{r.Extra}
				""")));

		/// <summary>The .NET module of the workspace: <paramref name="extra"/> and <c>Test.slnx</c> with a folder per repository.</summary>
		public void WriteDotnet(string extra, params string[] solutions)
			=> Meta.WriteFile(Path.Combine("_fx", "dotnet", "dotnet.yaml"), "schema: 1.0.0\napplies-to:\n  tags: [dotnet]\n" + extra
				+ "solutions:\n  - path: Test.slnx\n    folders:\n      - name: /test\n        files: [_fx/workspace/workspace.yaml]\n"
				+ string.Concat(solutions.Select(s => $"      - name: /{s}\n        solutions: [{s}/repo/Fx{s}.slnx]\n")));

		public string Root => Meta.Path;

		public string RepoPath(string name) => Path.Combine(Root, name, "repo");

		public string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative)).Replace("\r\n", "\n");

		public void Dispose()
		{
			foreach (var dir in Directory.EnumerateDirectories(Root).Where(d => Directory.Exists(Path.Combine(d, "repo"))))
				new TempGitRepository(init: false, path: Path.Combine(dir, "repo")).Dispose();
			Meta.Dispose();
			foreach (var origin in Origins.Values)
				origin.Dispose();
		}
	}

	static IReadOnlyList<FxModuleResult> Sync(string directory, SyncOptions? options = null, IFxProgress? progress = null)
		=> SyncEngine.Sync(directory, options ?? new(), progress, TestContext.Current.CancellationToken);

	static IReadOnlyList<FxModuleResult> Doctor(string directory, bool offline = true, IFxProgress? progress = null)
		=> SyncEngine.Doctor(directory, new(Offline: offline), progress, TestContext.Current.CancellationToken);

	static IEnumerable<FxDiagnostic> Diagnostics(IEnumerable<FxModuleResult> results) => results.SelectMany(r => r.Diagnostics);

	static IEnumerable<string> Codes(IEnumerable<FxModuleResult> results) => Diagnostics(results).Select(d => d.Code);

	static IEnumerable<SyncFile> Files(IEnumerable<FxModuleResult> results) => results.SelectMany(r => r.Files);

	static IEnumerable<RepositoryAction> Actions(IEnumerable<FxModuleResult> results) => results.SelectMany(r => r.Actions);

	static IEnumerable<FxDiagnostic> Errors(IEnumerable<FxModuleResult> results) => Diagnostics(results).Where(d => d.Severity == FxSeverity.Error);

	// Manifest

	[Theory(DisplayName = "manifest: every problem, with its line")]
	[InlineData("  - name: Plus\n    path: plus/repo\n    url: x\n", "lowercase letters, digits and '-'")]
	[InlineData("  - name: plus\n    path: ../plus\n    url: x\n", "without '.' or '..'")]
	[InlineData("  - name: plus\n    path: _fx/repo\n    url: x\n", "is fx's or git's")]
	[InlineData("  - name: plus\n    path: libs/~$old\n    url: x\n", "is fx's or git's")]
	[InlineData("  - name: plus\n    path: plus/repo\n", "needs 'url'")]
	[InlineData("  - name: plus\n    path: plus/repo\n    url: x\n    mount: always\n", "'mandatory' or 'manual'")]
	[InlineData("  - name: plus\n    path: plus/repo\n    url: x\n    depends-on: [oss]\n", "depends on 'oss'")]
	[InlineData("  - name: plus\n    path: plus/repo\n    url: x\n    dependsOn: [oss]\n", "Unknown key 'dependsOn'")]
	[InlineData("  - name: a\n    path: a/repo\n    url: x\n  - name: a\n    path: b/repo\n    url: y\n", "used more than once")]
	[InlineData("  - name: a\n    path: Libs/A\n    url: x\n  - name: b\n    path: libs/a\n    url: y\n", "used more than once")]
	[InlineData("  - name: a\n    path: a/repo\n    url: x\n  - name: b\n    path: a/repo/b\n    url: y\n", "mounted inside 'a'")]
	public void Manifest_Invalid(string repositories, string message)
	{
		var ex = Assert.Throws<WorkspaceManifestException>(() => WorkspaceManifest.Read("root", "schema: 1.0.0\nrepositories:\n" + repositories, strict: true));
		Assert.Contains(ex.Diagnostics, d => d.Severity == FxSeverity.Error && d.Message.English.Contains(message) && d.Line is > 0);
	}

	[Theory(DisplayName = "manifest: a share comes from inside the workspace and goes inside the repository, out of what is fx's or git's; one per destination")]
	[InlineData("  - path: ../x\n", "has to come from a path inside the workspace")]
	[InlineData("  - path: _fx/.inherited/dotnet/x.props\n", "has to come from a path inside the workspace")]
	[InlineData("  - path: x\n    destination: _fx/dotnet/x\n", "cannot go to '_fx/dotnet/x'")]
	[InlineData("  - path: x\n    destination: .git/hooks/x\n", "cannot go to '.git/hooks/x'")]
	[InlineData("  - path: x\n    destination: ~$x\n", "cannot go to '~$x'")]
	[InlineData("  - path: a\n    destination: x\n  - path: b\n    destination: x\n", "Two shares go to 'x'")]
	public void Manifest_InvalidShare(string shares, string message)
	{
		var ex = Assert.Throws<WorkspaceManifestException>(() => WorkspaceManifest.Read("root", "schema: 1.0.0\nrepositories: []\nshares:\n" + shares, strict: true));
		Assert.Contains(ex.Diagnostics, d => d.Severity == FxSeverity.Error && d.Message.English.Contains(message) && d.Line is > 0);
	}

	[Fact(DisplayName = "manifest: repositories anywhere, paths as objects, shares; nothing about solutions")]
	public void Manifest_Read()
	{
		var manifest = WorkspaceManifest.Read("root", """
			schema: 1.0.0
			repositories:
			  - name: repo-w1
			    aliases: [w1]
			    url: https://git.acme.example/acme/product-w1.git
			    path: repo-w1
			    mount: mandatory
			    tags: [dotnet, product]
			  - name: shared
			    url: https://git.acme.example/acme/shared.git
			    path: libs/shared
			    inherits: false
			    depends-on: [repo-w1]
			paths:
			  - path: README.md
			  - path: _docs/
			    worktree: false
			  - path: _docs/drafts/
			    exclude: true
			  - path: libs/
			shares:
			  - path: res/icon.png
			    applies-to:
			      tags: [nuget-package]
			  - path: res/
			    destination: icons
			  - path: LICENSE
			    destination: legal/
			""", strict: true);
		var w1 = manifest.Repositories[0];
		Assert.Equal(MountPolicy.Mandatory, w1.Mount);
		Assert.Equal(["w1"], w1.Aliases);
		Assert.Equal(["dotnet", "product"], w1.Tags);
		var shared = manifest.Repositories[1];
		Assert.False(shared.Inherits);
		Assert.Equal(["repo-w1"], shared.DependsOn);
		Assert.Equal(4, manifest.Paths!.Count);
		Assert.False(manifest.Paths[1].Worktree);
		Assert.True(manifest.Paths[2].Exclude);
		Assert.True(manifest.Versions("libs/README.md"));
		Assert.True(manifest.Versions("_docs/plans/a.md"));
		Assert.False(manifest.Versions("_docs/drafts/a.md"));
		Assert.False(manifest.Versions("notes/"));
		var share = manifest.Shares![0];
		Assert.Equal("res/icon.png", share.Destination);
		Assert.Equal(["nuget-package"], share.AppliesTo.Tags);
		// a folder goes to a folder; a file to a folder keeps its name
		Assert.Equal(["res/icon.png", "icons/", "legal/LICENSE"], manifest.Shares.Select(s => s.Destination));
	}

	[Fact(DisplayName = "schema: a newer major is not read; a newer minor, unknown keys only warn; none, a warning")]
	public void Manifest_Schema()
	{
		var newer = Assert.Throws<WorkspaceManifestException>(() => WorkspaceManifest.Read("root", "schema: 2.0.0\nrepositories: []\n", strict: true));
		Assert.Contains(newer.Diagnostics, d => d.Code == FxYamlReader.SchemaNewer);

		var minor = WorkspaceManifest.Read("root", "schema: 1.3.0\nfuture: 1\nrepositories: []\n", strict: true);
		Assert.Empty(minor.Repositories);
		var unknown = Assert.Throws<WorkspaceManifestException>(() => WorkspaceManifest.Read("root", "schema: 1.0.0\nfuture: 1\n", strict: true));
		Assert.Contains(unknown.Diagnostics, d => d.Message.English.Contains("Unknown key 'future'"));

		var reader = new FxYamlReader("x", "file.yaml");
		Assert.NotNull(reader.Load("repositories: []\n"));
		Assert.Contains(reader.Diagnostics, d => d.Code == FxYamlReader.SchemaMissing && d.Severity == FxSeverity.Warning);
	}

	// State

	[Fact(DisplayName = "mount rules: mandatory always; a clone already there is adopted; the intent wins")]
	public void State()
	{
		var mandatory = new WorkspaceRepository("a", "a/repo", [], "x", MountPolicy.Mandatory);
		var manual = new WorkspaceRepository("b", "b/repo", [], "x");
		Assert.True(WorkspaceState.Empty.IsMounted(mandatory, cloneExists: false));
		Assert.False(WorkspaceState.Empty.IsMounted(manual, cloneExists: false));
		Assert.True(WorkspaceState.Empty.IsMounted(manual, cloneExists: true));
		Assert.False(WorkspaceState.Empty.With("b", false).IsMounted(manual, cloneExists: true));
		Assert.True(WorkspaceState.Empty.With("a", false).IsMounted(mandatory, cloneExists: true));
	}

	// Progress (plan O, decision 13)

	/// <summary>The events of the progress channel, from any thread.</summary>
	sealed class Recorder : IFxProgress
	{
		readonly List<FxEvent> _events = [];

		public IReadOnlyList<FxEvent> Events
		{
			get
			{
				lock (_events)
					return _events.ToList();
			}
		}

		public void Report(FxEvent e)
		{
			lock (_events)
				_events.Add(e);
		}
	}

	[Fact(DisplayName = "progress: each repository's steps start and finish; each diagnostic is reported as soon as it is found")]
	public void Progress()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "mandatory"));
		var sync = new Recorder();
		Sync(ws.Root, progress: sync);
		foreach (var repo in new[] { "plus", "lab" })
		{
			Assert.Contains(sync.Events, e => e is FxStepStarted { Module: "workspace", Step: "clone" } s && s.Repository == repo);
			Assert.Contains(sync.Events, e => e is FxStepFinished { Module: "workspace", Step: "clone", Succeeded: true } f && f.Repository == repo);
		}

		new TempGitRepository(init: false, path: ws.RepoPath("lab")).WriteFile("dirty.txt", "x");
		var doctor = new Recorder();
		var results = Doctor(ws.Root, progress: doctor);
		Assert.Contains(doctor.Events, e => e is FxStepFinished { Step: "status", Repository: "lab", Succeeded: true });
		var dirty = Assert.Single(Diagnostics(results), d => d.Code == WorkspaceModule.Dirty);
		Assert.Equal("lab", dirty.Repository);
		Assert.Equal("workspace", dirty.Module);
		// every diagnostic of the result went through the channel, once
		Assert.Equal(Diagnostics(results).Count(), doctor.Events.OfType<FxDiagnosticFound>().Count());
		Assert.Contains(doctor.Events, e => e is FxDiagnosticFound { Diagnostic.Code: WorkspaceModule.Dirty, Repository: "lab" });
	}

	[Fact(DisplayName = "progress: a cancelled operation stops")]
	public void Cancellation()
	{
		using var ws = new Workspace(("plus", "mandatory"));
		using var cancelled = new System.Threading.CancellationTokenSource();
		cancelled.Cancel();
		Assert.ThrowsAny<OperationCanceledException>(() => SyncEngine.Sync(ws.Root, new(), cancellationToken: cancelled.Token));
		Assert.False(Directory.Exists(ws.RepoPath("plus")));
	}

	// Sync

	[Fact(DisplayName = "sync: clones the mandatory repos, generates .gitignore, the solution and the map, saves the state")]
	public void Sync_Workspace()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "manual"));
		var results = Sync(ws.Root);
		Assert.Empty(Errors(results));
		// lab is declared in the solution but not mounted: nothing to take its projects from yet
		Assert.Contains("dotnet.missing-solution", Codes(results));
		Assert.Contains(new RepositoryAction("plus", "cloned"), Actions(results));
		Assert.True(File.Exists(Path.Combine(ws.RepoPath("plus"), "Fxplus.slnx")));
		Assert.False(Directory.Exists(ws.RepoPath("lab")));

		var gitignore = ws.Read(".gitignore");
		Assert.StartsWith($"# {FxHeader.Title}\n", gitignore);
		Assert.Contains("\n*\n", gitignore);
		Assert.Contains("!/_docs/\n!/_docs/**\n", gitignore);
		Assert.Contains("!/Test.slnx\n", gitignore);
		Assert.EndsWith("~$*\n", gitignore);
		Assert.True(ws.Meta.Git("check-ignore", "plus/repo/").Length > 0);

		var solution = ws.Read("Test.slnx");
		Assert.Contains(FxHeader.Title, solution);
		Assert.Contains("<Folder Name=\"/plus/\" />", solution);
		Assert.Contains("<Folder Name=\"/plus/src/\">", solution);
		Assert.Contains("<Project Path=\"plus/repo/solution/src/plus/plus.csproj\" />", solution);
		Assert.Contains("<File Path=\"_fx/workspace/workspace.yaml\" />", solution);
		Assert.Contains("<Platform Name=\"Any CPU\" />", solution);

		Assert.Contains("name: plus\n    mount: true", ws.Read("_fx/workspace/~$state.yaml"));

		var map = ws.Read("_fx/dotnet/~$workspace.props");
		Assert.Contains("<FxWorkspaceProject Include=\"plus\" Repository=\"plus\"", map);
		Assert.Contains("<ProjectReference Include=\"@(FxReference->WithMetadataValue('Identity', 'plus')->'", map);
		var again = Sync(ws.Root);
		Assert.All(Files(again), f => Assert.Equal(SyncFileStatus.Unchanged, f.Status));
	}

	[Fact(DisplayName = "sync: fetches the mounted repos (online), never pulls")]
	public void Sync_Fetch()
	{
		using var ws = new Workspace(("plus", "mandatory"));
		Sync(ws.Root);
		ws.Origins["plus"].Commit("c2");
		Assert.Contains(new RepositoryAction("plus", "fetched"), Actions(Sync(ws.Root)));
		var status = WorkspaceModule.Repositories(WorkspaceManifest.Load(ws.Root), withStatus: true, cancellationToken: TestContext.Current.CancellationToken).Single().Status!;
		Assert.Equal(1, status.Behind);
	}

	[Fact(DisplayName = "sync --dry-run: no clone, no file written; the missing repo is an error")]
	public void Sync_DryRun()
	{
		using var ws = new Workspace(("plus", "mandatory"));
		var results = Sync(ws.Root, new(DryRun: true, Offline: true));
		Assert.Contains(WorkspaceModule.Missing, Codes(results));
		Assert.False(Directory.Exists(ws.RepoPath("plus")));
		Assert.False(File.Exists(Path.Combine(ws.Root, ".gitignore")));
	}

	[Fact(DisplayName = "a .gitignore written by hand is not touched without --adopt")]
	public void Adopt()
	{
		using var ws = new Workspace(("plus", "mandatory"));
		ws.Meta.WriteFile(".gitignore", "/*\n!/_fx/\n");
		var refused = Sync(ws.Root);
		Assert.Contains(Files(refused), f => f.Path == ".gitignore" && f.Status == SyncFileStatus.Refused);
		Assert.Contains(FxOutputs.NotGenerated, Codes(refused));
		Assert.Equal("/*\n!/_fx/\n", ws.Read(".gitignore"));

		var adopted = Sync(ws.Root, new(Adopt: true));
		Assert.Contains(Files(adopted), f => f.Path == ".gitignore" && f.Status == SyncFileStatus.Written);
		Assert.StartsWith($"# {FxHeader.Title}", ws.Read(".gitignore"));
	}

	[Fact(DisplayName = ".gitignore: paths at any depth, exclusions, a repository inside a versioned folder; as git reads it")]
	public void Gitignore()
	{
		var manifest = WorkspaceManifest.Read("root", """
			schema: 1.0.0
			repositories:
			  - name: shared
			    url: x
			    path: libs/shared
			  - name: app
			    url: y
			    path: apps/app
			paths:
			  - path: README.md
			  - path: libs/
			  - path: docs/guides/
			  - path: docs/guides/drafts/
			    exclude: true
			""", strict: true);
		using var repo = new TempGitRepository();
		repo.WriteFile(".gitignore", GitignoreGenerator.Generate(manifest, ["build/All.slnx"]));
		bool Ignored(string path)
		{
			try
			{
				repo.Git("check-ignore", "--quiet", "--no-index", path);
				return true;
			}
			catch (InvalidOperationException)
			{
				return false;
			}
		}
		Assert.False(Ignored("README.md"));
		Assert.False(Ignored("libs/README.md"));
		Assert.True(Ignored("libs/shared/"));
		Assert.True(Ignored("libs/shared/x.cs"));
		Assert.False(Ignored("docs/guides/a.md"));
		Assert.True(Ignored("docs/guides/drafts/a.md"));
		Assert.True(Ignored("docs/other.md"));
		Assert.False(Ignored("build/All.slnx"));
		Assert.True(Ignored("apps/app/"));
		Assert.True(Ignored("notes/todo.md"));
		Assert.False(Ignored("_fx/workspace/workspace.yaml"));
		Assert.True(Ignored("_fx/workspace/~$state.yaml"));
		Assert.True(Ignored("libs/~$tmp"));
		Assert.False(Ignored(".gitignore"));
	}

	[Fact(DisplayName = "solutions: composed from others; a repository not on disk keeps what the generated one had of it")]
	public void Solution_Compose()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "manual"));
		Assert.Empty(WorkspaceModule.SetMount(WorkspaceManifest.Load(ws.Root), ["lab"], mounted: true).Diagnostics);
		Assert.DoesNotContain(Errors(Sync(ws.Root)), _ => true);
		Assert.Contains("lab/repo/solution/src/lab/lab.csproj", ws.Read("Test.slnx"));
		// an empty folder and a build type only lab has
		var labSolution = Path.Combine(ws.RepoPath("lab"), "Fxlab.slnx");
		File.WriteAllText(labSolution, File.ReadAllText(labSolution).Replace("<Configurations>", "<Configurations>\n    <BuildType Name=\"Lab\" />")
			.Replace("</Solution>", "  <Folder Name=\"/empty/\" />\n</Solution>"));
		Sync(ws.Root);
		var before = ws.Read("Test.slnx");
		Assert.Contains("<BuildType Name=\"Lab\" />", before);
		Assert.Contains("<Folder Name=\"/lab/empty/\" />", before);

		// lab unmounted, so parked: the solution does not change, its projects are still there (unavailable in Visual Studio)
		Assert.Empty(WorkspaceModule.SetMount(WorkspaceManifest.Load(ws.Root), ["lab"], mounted: false).Diagnostics);
		var results = Sync(ws.Root);
		Assert.DoesNotContain(Codes(results), c => c == "dotnet.missing-solution");
		var solution = ws.Read("Test.slnx");
		Assert.Equal(before, solution);
		Assert.Contains("<Project Path=\"lab/repo/solution/src/lab/lab.csproj\" />", solution);
		Assert.Contains("<Folder Name=\"/lab/src/\">", solution);
		Assert.DoesNotContain("lab.csproj", ws.Read("_fx/dotnet/~$workspace.props"));
	}

	[Fact(DisplayName = "solutions: two different projects with the same name in one folder are an error")]
	public void Solution_DuplicateName()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "mandatory"));
		ws.Meta.WriteFile(Path.Combine("_fx", "dotnet", "dotnet.yaml"),
			"schema: 1.0.0\nsolutions:\n  - path: All.slnx\n    folders:\n      - name: /all\n        solutions: [plus/repo/Fxplus.slnx, lab/repo/Fxlab.slnx]\n");
		var error = Assert.Single(Errors(Sync(ws.Root)));
		Assert.Equal("dotnet.duplicate-project-name", error.Code);
		Assert.Contains("'Tool' in the folder /all/tools/", error.Message.English);
	}

	[Fact(DisplayName = "inheritance: the .NET module goes down to the repos of applies-to, merged, with its folder, in _fx/.inherited/")]
	public void Sync_Inheritance()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("docs", "mandatory"));
		ws.WriteManifest(("plus", "mandatory", "    tags: [dotnet]\n"), ("docs", "mandatory", ""));
		ws.WriteDotnet("imports:\n  - props: default.props\n", "plus");
		ws.Meta.WriteFile(Path.Combine("_fx", "dotnet", "default.props"), "<Project />\n");
		ws.Meta.WriteFile(Path.Combine("_fx", "dotnet", "unused.targets"), "<Project />\n");
		var icon = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF };
		Directory.CreateDirectory(Path.Combine(ws.Root, "_fx", "dotnet", "assets"));
		File.WriteAllBytes(Path.Combine(ws.Root, "_fx", "dotnet", "assets", "icon.png"), icon);
		Assert.Empty(Errors(Sync(ws.Root)));

		// the yaml merged and without the solutions; the folder whole, binaries byte for byte, text with the header
		var inherited = Path.Combine(ws.RepoPath("plus"), "_fx", ".inherited");
		var yaml = ws.Read("plus/repo/_fx/.inherited/dotnet/dotnet.yaml");
		Assert.StartsWith($"# {FxHeader.Title}\n# Generated from: workspace /test (_fx/dotnet/dotnet.yaml)\n", yaml);
		Assert.Contains("applies-to:\n  tags: [dotnet]\nimports:\n  - props: default.props\n", yaml);
		Assert.DoesNotContain("solutions", yaml);
		Assert.Contains(FxHeader.Title, ws.Read("plus/repo/_fx/.inherited/dotnet/unused.targets"));
		Assert.Equal(icon, File.ReadAllBytes(Path.Combine(inherited, "dotnet", "assets", "icon.png")));
		var card = FxInherited.Load(ws.RepoPath("plus"))!;
		Assert.Equal("/test/plus", card.Repository.FullName);
		Assert.Equal(["dotnet"], card.Repository.Tags);
		Assert.Equal("/test", card.Parent.FullName);
		Assert.Contains(card.Files, f => f.Path == "dotnet/assets/icon.png" && f.From.SequenceEqual(["/test"]));
		Assert.Contains("name: dotnet", ws.Read("plus/repo/_fx/.inherited/fx.yaml"));
		var props = ws.Read("plus/repo/_fx/dotnet/dotnet.g.props");
		Assert.Contains("$(MSBuildThisFileDirectory)../.inherited/dotnet/default.props", props);
		Assert.Contains("<FxRepositoryFullName>/test/plus</FxRepositoryFullName>", props);
		Assert.False(Directory.Exists(Path.Combine(ws.RepoPath("docs"), "_fx"))); // nothing applies to it
		Assert.DoesNotContain(Codes(Doctor(ws.Root)), c => c is WorkspaceModule.Outdated or "dotnet.outdated");

		// a change in the workspace is out of date in the repo until the next sync; what goes, goes from the repo
		ws.Meta.WriteFile(Path.Combine("_fx", "dotnet", "default.props"), "<Project><PropertyGroup><X>1</X></PropertyGroup></Project>\n");
		Assert.Contains(Diagnostics(Doctor(ws.Root)), d => d.Code == WorkspaceModule.Outdated && d.File == "plus/repo/_fx/.inherited/dotnet/default.props");
		File.Delete(Path.Combine(ws.Root, "_fx", "dotnet", "unused.targets"));
		Sync(ws.Root);
		Assert.False(File.Exists(Path.Combine(inherited, "dotnet", "unused.targets")));
		Assert.Contains("<X>1</X>", ws.Read("plus/repo/_fx/.inherited/dotnet/default.props"));

		// a repo the module no longer applies to loses all of it
		ws.WriteManifest(("plus", "mandatory", "    tags: [other]\n"), ("docs", "mandatory", ""));
		Sync(ws.Root);
		Assert.False(Directory.Exists(inherited));
	}

	[Fact(DisplayName = "nested workspaces: each level inherits from its parent, merged; the solutions are composed from the bottom up")]
	public void Sync_Nested()
	{
		using var c1 = new TempGitRepository();
		c1.WriteFile(".gitignore", "~$*\n");
		c1.WriteFile(Path.Combine("src", "App", "C1.App.csproj"), "<Project><ItemGroup><PackageReference Include=\"xunit.v3\" /><PackageReference Include=\"Serilog\" /></ItemGroup></Project>\n");
		c1.WriteFile("C1.slnx", "<Solution>\n  <Folder Name=\"/src/\">\n    <Project Path=\"src/App/C1.App.csproj\" />\n  </Folder>\n</Solution>\n");
		c1.CommitAll("c1");
		using var w1 = new TempGitRepository();
		w1.WriteFile(Path.Combine("_fx", "fx.yaml"), "schema: 1.0.0\ntags:\n  - name: mobile\n");
		w1.WriteFile(Path.Combine("_fx", "workspace", "workspace.yaml"),
			$"schema: 1.0.0\nrepositories:\n  - name: c1\n    url: {c1.Path.Replace('\\', '/')}\n    path: c1\n    mount: mandatory\n    tags: [dotnet, mobile]\n");
		w1.WriteFile(Path.Combine("_fx", "dotnet", "dotnet.yaml"),
			"schema: 1.0.0\npackages:\n  - ids: [Serilog]\n    versions:\n      - version: 4.0.0\nsolutions:\n  - path: W1.slnx\n    folders:\n      - name: /C1\n        solutions: [c1/C1.slnx]\n");
		w1.CommitAll("w1");
		using var acme = new TempGitRepository();
		acme.WriteFile(Path.Combine("_fx", "fx.yaml"), "schema: 1.0.0\nname: acme\nrequires: 0.1.0\ntags:\n  - name: dotnet\n  - name: product\n");
		acme.WriteFile(Path.Combine("_fx", "workspace", "workspace.yaml"),
			$"schema: 1.0.0\nrepositories:\n  - name: w1\n    url: {w1.Path.Replace('\\', '/')}\n    path: products/w1\n    mount: mandatory\n    tags: [product]\n");
		acme.WriteFile(Path.Combine("_fx", "dotnet", "dotnet.yaml"),
			"schema: 1.0.0\napplies-to:\n  tags: [dotnet]\npackages:\n  - ids: [xunit.v3]\n    versions:\n      - version: 4.0.0\n"
			+ "solutions:\n  - path: Acme.slnx\n    folders:\n      - name: /products/w1\n        solutions: [products/w1/W1.slnx]\n");
		acme.CommitAll("acme");
		Assert.Empty(Errors(Sync(acme.Path)));
		string Read(string relative) => File.ReadAllText(Path.Combine(acme.Path, relative)).Replace("\r\n", "\n");

		// w1 is a conduit: it gets acme's .NET although it is not tagged dotnet, to pass it on
		Assert.Contains("ids: [xunit.v3]", Read("products/w1/_fx/.inherited/dotnet/dotnet.yaml"));
		Assert.False(File.Exists(Path.Combine(acme.Path, "products", "w1", "_fx", "dotnet", "dotnet.g.props"))); // no projects of its own
		// c1: acme's and w1's, merged; its full name, the chain from the root; the catalog of both levels
		var card = FxInherited.Load(Path.Combine(acme.Path, "products", "w1", "c1"))!;
		Assert.Equal("/acme/w1/c1", card.Repository.FullName);
		Assert.Equal("/acme/w1", card.Parent.FullName);
		Assert.Contains(card.Files, f => f.Path == "dotnet/dotnet.yaml" && f.From.SequenceEqual(["/acme", "/acme/w1"]));
		var yaml = Read("products/w1/c1/_fx/.inherited/dotnet/dotnet.yaml");
		Assert.Contains("ids: [xunit.v3]", yaml);
		Assert.Contains("ids: [Serilog]", yaml);
		Assert.Contains("name: mobile", Read("products/w1/c1/_fx/.inherited/fx.yaml"));
		Assert.Contains("requires: 0.1.0", Read("products/w1/c1/_fx/.inherited/fx.yaml"));
		var targets = Read("products/w1/c1/_fx/dotnet/dotnet.g.targets");
		Assert.Contains("<PackageReference Update=\"xunit.v3\" Version=\"4.0.0\" />", targets);
		Assert.Contains("<PackageReference Update=\"Serilog\" Version=\"4.0.0\" />", targets);
		Assert.Contains("<FxRepositoryFullName>/acme/w1/c1</FxRepositoryFullName>", Read("products/w1/c1/_fx/dotnet/dotnet.g.props"));
		// the solutions from the bottom up, and the map of the highest workspace
		Assert.Contains("<Project Path=\"c1/src/App/C1.App.csproj\" />", Read("products/w1/W1.slnx"));
		Assert.Contains("<Folder Name=\"/products/w1/C1/src/\">", Read("Acme.slnx"));
		Assert.Contains("<Project Path=\"products/w1/c1/src/App/C1.App.csproj\" />", Read("Acme.slnx"));
		Assert.Contains("Include=\"C1.App\" Repository=\"c1\"", Read("_fx/dotnet/~$workspace.props"));
		Assert.False(File.Exists(Path.Combine(acme.Path, "products", "w1", "_fx", "dotnet", "~$workspace.props")));

		// the same sync from c1 alone: it reads its parent on disk and finds nothing to change
		Assert.All(Files(Sync(Path.Combine(acme.Path, "products", "w1", "c1"))), f => Assert.Equal(SyncFileStatus.Unchanged, f.Status));
	}

	[Fact(DisplayName = "requires: a fx older than the tree requires reads, but writes nothing")]
	public void Requires()
	{
		using var ws = new Workspace(("plus", "mandatory"));
		ws.Meta.WriteFile(Path.Combine("_fx", "fx.yaml"), "schema: 1.0.0\nname: test\nrequires: 9.0.0\n");
		var results = Sync(ws.Root, new(FxVersion: "1.2.0"));
		Assert.Contains(SyncEngine.Requires, Codes(results));
		Assert.False(Directory.Exists(ws.RepoPath("plus")));
		Assert.False(File.Exists(Path.Combine(ws.Root, ".gitignore")));
		Assert.DoesNotContain(SyncEngine.Requires, Codes(Sync(ws.Root, new(FxVersion: "9.0.1-local"))));
	}

	[Fact(DisplayName = "doctor: a standalone repo cannot reference a project of a repo that is not")]
	public void Doctor_StandaloneReference()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("next", "mandatory"));
		Sync(ws.Root);
		new TempGitRepository(init: false, path: ws.RepoPath("plus")).WriteFile(Path.Combine("_fx", "dotnet", "dotnet.yaml"), "schema: 1.0.0\nstandalone: true\n");
		File.WriteAllText(Path.Combine(ws.RepoPath("plus"), "uses.props"), """
			<Project>
			  <ItemGroup>
			    <FxReference Include="next" />
			    <FxReference Include="NotMounted;plus" />
			    <FxReference Include="next" WorkspaceOnly="true" />
			  </ItemGroup>
			</Project>
			""");
		var diagnostic = Assert.Single(Diagnostics(Doctor(ws.Root)), d => d.Code == SyncEngine.StandaloneReference);
		Assert.Contains("references next of 'next'", diagnostic.Message.English);
		Assert.Equal("plus/repo/uses.props", diagnostic.File);
		Assert.Equal(3, diagnostic.Line);
	}

	[Fact(DisplayName = "fx repo unmount parks the clone, with its work, never deletes it; mount brings it back without cloning")]
	public void MountUnmount()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "manual"));
		var manifest = WorkspaceManifest.Load(ws.Root);
		var mount = WorkspaceModule.SetMount(manifest, ["lab"], mounted: true);
		Assert.Empty(mount.Diagnostics);
		Assert.Equal("mounted", Assert.Single(mount.Actions).Action);
		Assert.Contains(new RepositoryAction("lab", "cloned"), Actions(Sync(ws.Root)));
		Assert.Contains("lab.csproj", ws.Read("_fx/dotnet/~$workspace.props"));
		File.WriteAllText(Path.Combine(ws.RepoPath("lab"), "work.txt"), "not committed");

		// parked in _fx/~$parking/ under its path; the folder it leaves empty goes
		var unmount = WorkspaceModule.SetMount(manifest, ["lab"], mounted: false);
		Assert.Empty(unmount.Diagnostics);
		var parked = Assert.Single(unmount.Actions);
		Assert.Equal("parked", parked.Action);
		Assert.Equal("in _fx/~$parking/lab/repo", parked.Detail!.English);
		var parking = Path.Combine(ws.Root, "_fx", "~$parking", "lab", "repo");
		Assert.True(File.Exists(Path.Combine(parking, "work.txt")));
		Assert.False(Directory.Exists(Path.Combine(ws.Root, "lab")));
		Assert.True(ws.Meta.Git("check-ignore", "_fx/~$parking/lab/repo/").Length > 0);
		Assert.DoesNotContain(Actions(Sync(ws.Root)), a => a.Repository == "lab");
		Assert.DoesNotContain("lab.csproj", ws.Read("_fx/dotnet/~$workspace.props"));
		Assert.True(Assert.Single(WorkspaceModule.Repositories(manifest, withStatus: false, cancellationToken: TestContext.Current.CancellationToken), r => r.Repository.Name == "lab").Parked);

		// back, without cloning: the work is there, and the parking goes
		Assert.Equal("unparked", Assert.Single(WorkspaceModule.SetMount(manifest, ["lab"], mounted: true).Actions).Action);
		Assert.True(File.Exists(Path.Combine(ws.RepoPath("lab"), "work.txt")));
		Assert.False(Directory.Exists(Path.Combine(ws.Root, "_fx", "~$parking")));
		Sync(ws.Root);
		Assert.Contains("lab.csproj", ws.Read("_fx/dotnet/~$workspace.props"));

		// a mandatory one cannot be unmounted, nor an unknown one mounted: nothing changes
		Assert.Single(WorkspaceModule.SetMount(manifest, ["plus", "lab"], mounted: false).Diagnostics);
		Assert.True(FxLayout.IsGitRoot(ws.RepoPath("lab")));
		Assert.Single(WorkspaceModule.SetMount(manifest, ["nope"], mounted: true).Diagnostics);
	}

	[Fact(DisplayName = "fx sync parks what is unmounted and still there, and brings back what is mounted and parked; the doctor says it first")]
	public void Sync_Parking()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "manual"));
		var manifest = WorkspaceManifest.Load(ws.Root);
		WorkspaceModule.SetMount(manifest, ["lab"], mounted: true);
		Sync(ws.Root);
		void State(bool mounted)
			=> File.WriteAllText(Path.Combine(ws.Root, WorkspaceState.RelativePath), WorkspaceState.Load(ws.Root).With("lab", mounted).ToYaml());

		// unmounted only in the state, as fx 0.1 did
		State(false);
		var notParked = Assert.Single(Diagnostics(Doctor(ws.Root)), d => d.Code == WorkspaceModule.NotParked);
		Assert.Equal("'lab' is unmounted but still at lab/repo: fx sync parks it.", notParked.Message.English);
		Assert.Equal(FxSeverity.Warning, notParked.Severity);
		Assert.Contains(Actions(Sync(ws.Root)), a => a is { Repository: "lab", Action: "parked" });
		Assert.True(FxLayout.IsGitRoot(Path.Combine(ws.Root, "_fx", "~$parking", "lab", "repo")));

		// mounted only in the state: parked, so missing; sync brings it back, also offline
		State(true);
		var missing = Assert.Single(Diagnostics(Doctor(ws.Root)), d => d.Code == WorkspaceModule.Missing);
		Assert.Equal("'lab' is mounted but parked in _fx/~$parking/lab/repo: fx sync brings it back to lab/repo.", missing.Message.English);
		Assert.Contains(Actions(Sync(ws.Root, new(Offline: true))), a => a is { Repository: "lab", Action: "unparked" });
		Assert.True(FxLayout.IsGitRoot(ws.RepoPath("lab")));
		Assert.DoesNotContain(Diagnostics(Doctor(ws.Root)), d => d.Code is WorkspaceModule.Missing or WorkspaceModule.NotParked);
	}

	[Fact(DisplayName = "parking: if something has the clone open, or the parking is taken, nothing moves and it says so")]
	public void Parking_Blocked()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "manual"));
		var manifest = WorkspaceManifest.Load(ws.Root);
		WorkspaceModule.SetMount(manifest, ["lab"], mounted: true);
		Sync(ws.Root);

		// the place in the parking is taken
		var parking = Path.Combine(ws.Root, "_fx", "~$parking", "lab", "repo");
		Directory.CreateDirectory(parking);
		File.WriteAllText(Path.Combine(parking, "other.txt"), "");
		var taken = Assert.Single(WorkspaceModule.SetMount(manifest, ["lab"], mounted: false).Diagnostics);
		Assert.Equal(WorkspaceModule.MoveFailed, taken.Code);
		Assert.Contains("there is already something there", taken.Message.English);
		Assert.True(FxLayout.IsGitRoot(ws.RepoPath("lab")));
		Assert.True(WorkspaceState.Load(ws.Root).Mount["lab"]);
		Directory.Delete(Path.Combine(ws.Root, "_fx", "~$parking"), recursive: true);

		// a file open inside it (Visual Studio, a terminal): Windows does not move the folder
		Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows keeps an open folder from moving");
		using (File.Open(Path.Combine(ws.RepoPath("lab"), "Fxlab.slnx"), FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			var open = Assert.Single(WorkspaceModule.SetMount(manifest, ["lab"], mounted: false).Diagnostics);
			Assert.Equal(WorkspaceModule.MoveFailed, open.Code);
			Assert.Equal($"{WorkspaceModule.MoveFailed}.fix", open.Fix!.Hint!.Key);
		}
		Assert.True(FxLayout.IsGitRoot(ws.RepoPath("lab")));
		Assert.False(Directory.Exists(Path.Combine(ws.Root, "_fx", "~$parking")));
		Assert.True(WorkspaceState.Load(ws.Root).Mount["lab"]);
	}

	[Fact(DisplayName = "parking a clone with worktrees: git still links them after the move, both ways")]
	public void Parking_Worktrees()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "manual"));
		var manifest = WorkspaceManifest.Load(ws.Root);
		WorkspaceModule.SetMount(manifest, ["lab"], mounted: true);
		Sync(ws.Root);
		var lab = new TempGitRepository(init: false, path: ws.RepoPath("lab"));
		using var worktree = new TempGitRepository(init: false);
		lab.Git("worktree", "add", "--quiet", "-b", "feature/x", worktree.Path);

		WorkspaceModule.SetMount(manifest, ["lab"], mounted: false);
		var parked = new TempGitRepository(init: false, path: Path.Combine(ws.Root, "_fx", "~$parking", "lab", "repo"));
		Assert.Equal("feature/x", worktree.Git("rev-parse", "--abbrev-ref", "HEAD").Trim());
		Assert.Contains(FxLayout.Normalize(worktree.Path).TrimEnd('\\', '/').Replace('\\', '/'), parked.Git("worktree", "list", "--porcelain").Replace('\\', '/'),
			StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("prunable", parked.Git("worktree", "list", "--porcelain"));
	}

	// Doctor

	[Fact(DisplayName = "doctor: all right after a sync")]
	public void Doctor_AllRight()
	{
		using var ws = new Workspace(("plus", "mandatory"));
		Sync(ws.Root);
		Assert.DoesNotContain(Diagnostics(Doctor(ws.Root, offline: false)), d => d.Severity != FxSeverity.Info);
	}

	[Fact(DisplayName = "doctor: changes, commits not pushed, detached HEAD, undeclared repos, origin, unknown tags, local files not ignored")]
	public void Doctor_Problems()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "mandatory"));
		Sync(ws.Root);
		var plus = new TempGitRepository(init: false, path: ws.RepoPath("plus"));
		plus.Commit("local");
		plus.WriteFile("new.txt", "x");
		File.Delete(Path.Combine(ws.RepoPath("plus"), ".gitignore"));
		var lab = new TempGitRepository(init: false, path: ws.RepoPath("lab"));
		lab.Checkout(lab.Head());
		Directory.CreateDirectory(Path.Combine(ws.Root, "stray", "repo", ".git"));
		ws.Meta.WriteFile(Path.Combine("notes", "todo.md"), "mine");
		ws.WriteManifest(("plus", "mandatory", ""), ("lab", "mandatory", "    tags: [unheard]\n"));
		ws.Meta.Git("-C", ws.RepoPath("plus"), "remote", "set-url", "origin", "https://example.invalid/other.git");

		var diagnostics = Diagnostics(Doctor(ws.Root)).ToList();
		var codes = diagnostics.Select(d => d.Code).ToList();
		Assert.Contains(WorkspaceModule.Dirty, codes);
		Assert.Contains(WorkspaceModule.Unpushed, codes);
		Assert.Contains(WorkspaceModule.Detached, codes);
		Assert.Contains(WorkspaceModule.OriginMismatch, codes);
		Assert.Contains(diagnostics, d => d.Code == WorkspaceModule.Orphan && d.Message.English.StartsWith("stray/repo "));
		Assert.Contains(diagnostics, d => d.Code == WorkspaceModule.NotVersioned && d.Severity == FxSeverity.Info && d.Message.English.StartsWith("notes/ "));
		Assert.Contains(diagnostics, d => d.Code == WorkspaceModule.UnknownTag && d.Repository == "lab");
		Assert.Contains(diagnostics, d => d.Code == WorkspaceModule.LocalNotIgnored && d.Repository == "plus");
		// a folder that only holds a repository is not reported
		Assert.DoesNotContain(diagnostics, d => d.Code == WorkspaceModule.NotVersioned && d.Message.English.StartsWith("plus/"));
	}

	[Fact(DisplayName = "doctor: a mounted repo the workspace does not ignore is an error")]
	public void Doctor_NotIgnored()
	{
		using var ws = new Workspace(("plus", "mandatory"));
		Sync(ws.Root);
		File.WriteAllText(Path.Combine(ws.Root, ".gitignore"), $"# {FxHeader.Title}\n!/everything\n");
		var diagnostics = Diagnostics(Doctor(ws.Root)).ToList();
		Assert.Contains(diagnostics, d => d.Code == WorkspaceModule.NotIgnored && d.Severity == FxSeverity.Error);
		Assert.Contains(diagnostics, d => d.Code == WorkspaceModule.Outdated && d.File == ".gitignore");
	}

	// fx repo pull

	[Fact(DisplayName = "fx repo pull: fast-forward; a repo with changes is left alone")]
	public void Pull()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "mandatory"));
		Sync(ws.Root);
		ws.Origins["plus"].Commit("c2");
		ws.Origins["lab"].Commit("c2");
		new TempGitRepository(init: false, path: ws.RepoPath("lab")).WriteFile("dirty.txt", "x");
		var actions = WorkspaceModule.Pull(WorkspaceManifest.Load(ws.Root), [], cancellationToken: TestContext.Current.CancellationToken).Actions;
		Assert.Contains(new RepositoryAction("plus", "pulled"), actions);
		Assert.Contains(actions, a => a.Repository == "lab" && a.Action == "skipped");
		Assert.Equal(ws.Origins["plus"].Head(), new TempGitRepository(init: false, path: ws.RepoPath("plus")).Head());
	}

	// CLI

	[Fact(DisplayName = "fx sync in the workspace clones; fx doctor inside a repo; -w from inside a repo checks the workspace")]
	public void Cli_Scope()
	{
		using var ws = new Workspace(("plus", "mandatory"));
		var stdout = new StringWriter();
		Assert.Equal(0, FxApp.Run(["sync"], stdout, new StringWriter(), ws.Root));
		Assert.Contains("cloned      plus", stdout.ToString());

		Assert.Equal(0, FxApp.Run(["doctor", "--offline"], new StringWriter(), new StringWriter(), ws.RepoPath("plus")));
		stdout = new StringWriter();
		Assert.Equal(0, FxApp.Run(["doctor", "-w", "--offline", "--output", "json"], stdout, new StringWriter(), ws.RepoPath("plus")));
		Assert.Contains(JsonDocument.Parse(stdout.ToString()).RootElement.GetProperty("modules").EnumerateArray(), m => m.GetProperty("module").GetString() == "workspace");
	}

	[Fact(DisplayName = "fx repo list --output json: fx-repo-list/1")]
	public void Cli_RepoList()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "manual"));
		var stdout = new StringWriter();
		Assert.Equal(0, FxApp.Run(["repo", "list", "--output", "json"], stdout, new StringWriter(), ws.Root));
		var json = JsonDocument.Parse(stdout.ToString()).RootElement;
		Assert.Equal("fx-repo-list/1", json.GetProperty("schema").GetString());
		var repos = json.GetProperty("repositories").EnumerateArray().ToList();
		Assert.Equal("mandatory", repos[0].GetProperty("mount").GetString());
		Assert.False(repos[1].GetProperty("mounted").GetBoolean());
	}

	[Fact(DisplayName = "fx repo unmount|mount: what happened to each repository (fx-repo-mount/1); fx repo list says which is parked")]
	public void Cli_MountParking()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "mandatory"));
		Assert.Equal(0, FxApp.Run(["sync"], new StringWriter(), new StringWriter(), ws.Root));
		ws.WriteManifest(("plus", "mandatory", ""), ("lab", "manual", ""));

		var stdout = new StringWriter();
		Assert.Equal(0, FxApp.Run(["repo", "unmount", "lab", "--output", "json"], stdout, new StringWriter(), ws.Root));
		var json = JsonDocument.Parse(stdout.ToString()).RootElement;
		Assert.Equal("fx-repo-mount/1", json.GetProperty("schema").GetString());
		var lab = Assert.Single(json.GetProperty("repositories").EnumerateArray());
		Assert.Equal("parked", lab.GetProperty("action").GetString());
		Assert.Equal("in _fx/~$parking/lab/repo", lab.GetProperty("detail").GetString());

		stdout = new StringWriter();
		Assert.Equal(0, FxApp.Run(["repo", "list", "--output", "json"], stdout, new StringWriter(), ws.Root));
		var repos = JsonDocument.Parse(stdout.ToString()).RootElement.GetProperty("repositories").EnumerateArray().ToList();
		Assert.True(repos[1].GetProperty("parked").GetBoolean());
		Assert.False(repos[0].GetProperty("parked").GetBoolean());

		stdout = new StringWriter();
		Assert.Equal(0, FxApp.Run(["repo", "mount", "lab"], stdout, new StringWriter(), ws.Root));
		Assert.Equal("unparked   lab", stdout.ToString().Trim());
	}

	[Theory(DisplayName = "fx repo mount|unmount without names and no terminal to ask for them: what is missing, exit code 2 (plan O, O3.3)")]
	[InlineData("mount")]
	[InlineData("unmount")]
	public void Cli_MountMissing(string verb)
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "manual"));
		var stderr = new StringWriter();
		Assert.Equal(FxConsole.UsageError, FxApp.Run(["repo", verb], new StringWriter(), stderr, ws.Root));
		Assert.Contains($"fx repo {verb} <names>", stderr.ToString());
	}

	[Fact(DisplayName = "fx repo status: only the repositories that ask for attention; --all, every one (plan O, decision 8)")]
	public void Cli_RepoStatus()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "mandatory"));
		Assert.Equal(0, FxApp.Run(["sync"], new StringWriter(), new StringWriter(), ws.Root));
		var stdout = new StringWriter();
		Assert.Equal(0, FxApp.Run(["repo", "status"], stdout, new StringWriter(), ws.Root));
		Assert.Equal("✓ All 2 repositories are clean.", stdout.ToString().Trim());

		File.WriteAllText(Path.Combine(ws.RepoPath("lab"), "new.txt"), "x");
		stdout = new StringWriter();
		Assert.Equal(0, FxApp.Run(["repo", "status"], stdout, new StringWriter(), ws.Root));
		var text = stdout.ToString();
		Assert.Contains("lab", text);
		Assert.DoesNotContain("plus", text);
		Assert.Contains("1 of 2 repositories are clean (--all to see them).", text);

		stdout = new StringWriter();
		Assert.Equal(0, FxApp.Run(["repo", "status", "--all"], stdout, new StringWriter(), ws.Root));
		text = stdout.ToString();
		Assert.Contains("lab", text);
		Assert.Contains("plus", text);
		Assert.DoesNotContain("--all", text);
		// without a terminal, no colours
		Assert.DoesNotContain("\x1b[", text);
	}
}
