using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Fuxion.Tool.Cli;
using Fuxion.Tool.Core.Dotnet;
using Fuxion.Tool.Core.Git;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Mounts;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Sync;
using Xunit;

namespace Fuxion.Tool.Test;

/// <summary>The workspace module and the sync of a tree: manifest, local state, inheritance, sync, doctor and fx repo (plans M and P).</summary>
public sealed class WorkspaceTest
{
	/// <summary>
	/// A workspace (a git repository with <c>_fx/mounts/</c>) and, for each repository of the manifest, an "origin": a
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
			Meta.WriteFile(Path.Combine("_fx", "space.yaml"), "schema: 1.0.0\nname: test\ntags:\n  - name: dotnet\n  - name: x\n  - name: other\npaths:\n  - path: _docs/\n");
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
			=> Meta.WriteFile(Path.Combine("_fx", "mounts", "mounts.yaml"), "schema: 1.0.0\nmounts:\n" + string.Concat(repositories.Select(r => $"""
				  - name: {r.Name}
				    path: {r.Name}/repo
				    url: {Origins[r.Name].Path.Replace('\\', '/')}
				    required: {(r.Mount == "mandatory" ? "true" : "false")}
				{r.Extra}
				""")));

		/// <summary>The .NET module of the workspace: <paramref name="extra"/> and <c>Test.slnx</c> with a folder per repository.</summary>
		public void WriteDotnet(string extra, params string[] solutions)
			=> Meta.WriteFile(Path.Combine("_fx", "dotnet", "dotnet.yaml"), "schema: 1.0.0\napplies-to:\n  tags: [dotnet]\n" + extra
				+ "solutions:\n  - path: Test.slnx\n    folders:\n      - name: /test\n        files: [_fx/mounts/mounts.yaml]\n"
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
	[InlineData("  - name: plus\n    path: plus/repo\n    url: x\n    mode: always\n", "'write', 'read-only' or 'propose-only'")]
	[InlineData("  - name: plus\n    path: plus/repo\n    url: x\n    required: always\n", "'required' must be true or false")]
	[InlineData("  - name: plus\n    path: plus/repo\n    url: x\n    depends-on: [oss]\n", "depends on 'oss'")]
	[InlineData("  - name: plus\n    path: plus/repo\n    url: x\n    dependsOn: [oss]\n", "Unknown key 'dependsOn'")]
	[InlineData("  - name: a\n    path: a/repo\n    url: x\n  - name: a\n    path: b/repo\n    url: y\n", "used more than once")]
	[InlineData("  - name: a\n    path: Libs/A\n    url: x\n  - name: b\n    path: libs/a\n    url: y\n", "used more than once")]
	[InlineData("  - name: a\n    path: a/repo\n    url: x\n  - name: b\n    path: a/repo/b\n    url: y\n", "mounted inside 'a'")]
	public void Manifest_Invalid(string repositories, string message)
	{
		var ex = Assert.Throws<MountsManifestException>(() => MountsManifest.Read("root", "schema: 1.0.0\nmounts:\n" + repositories, strict: true));
		Assert.Contains(ex.Diagnostics, d => d.Severity == FxSeverity.Error && d.Message.English.Contains(message) && d.Line is > 0);
	}

	[Theory(DisplayName = "space.yaml: every problem, with its line; a share comes from inside the space and goes inside the repository, out of what is fx's or git's, one per destination")]
	[InlineData("name: Acme\n", "lowercase letters, digits and '-'")]
	[InlineData("visibility: internal\n", "private or public")]
	[InlineData("paths:\n  - path: ../x\n", "must be relative, inside the space")]
	[InlineData("paths:\n  - path: _fx/\n", "is fx's: it is always versioned")]
	[InlineData("paths:\n  - path: ~$x\n", "is fx's: it is always versioned")]
	[InlineData("paths:\n  - path: a/\n  - path: A/\n", "'a/' is twice in paths")]
	[InlineData("paths:\n  - path: a/\n    worktree: maybe\n", "'worktree' must be true or false")]
	[InlineData("paths: x\n", "'paths' must be a list")]
	[InlineData("shares:\n  - path: ../x\n", "has to come from a path inside the space")]
	[InlineData("shares:\n  - path: _fx/.inherited/dotnet/x.props\n", "has to come from a path inside the space")]
	[InlineData("shares:\n  - path: x\n    destination: _fx/dotnet/x\n", "cannot go to '_fx/dotnet/x'")]
	[InlineData("shares:\n  - path: x\n    destination: .git/hooks/x\n", "cannot go to '.git/hooks/x'")]
	[InlineData("shares:\n  - path: x\n    destination: ~$x\n", "cannot go to '~$x'")]
	[InlineData("shares:\n  - path: a\n    destination: x\n  - path: b\n    destination: x\n", "Two shares go to 'x'")]
	public void Space_Invalid(string yaml, string message)
	{
		var diagnostics = new List<FxDiagnostic>();
		SpaceConfig.Read("schema: 1.0.0\n" + yaml, FxLayout.ConfigFile, diagnostics);
		Assert.Contains(diagnostics, d => d.Severity == FxSeverity.Error && d.Message.English.Contains(message) && d.Line is > 0);
	}

	[Fact(DisplayName = "mounts.yaml: mounts anywhere, required or not, in their mode; nothing about solutions")]
	public void Manifest_Read()
	{
		var manifest = MountsManifest.Read("root", """
			schema: 1.0.0
			mounts:
			  - name: repo-w1
			    aliases: [w1]
			    url: https://git.acme.example/acme/product-w1.git
			    path: repo-w1
			    required: true
			    tags: [dotnet, product]
			  - name: shared
			    url: https://git.acme.example/acme/shared.git
			    path: libs/shared
			    mode: read-only
			    inherits: false
			    depends-on: [repo-w1]
			  - name: team
			    url: https://git.acme.example/acme/team.git
			    path: team
			    mode: propose-only
			""", strict: true);
		var w1 = manifest.Mounts[0];
		Assert.True(w1.Required);
		Assert.Equal(MountMode.Write, w1.Mode);
		Assert.Equal(["w1"], w1.Aliases);
		Assert.Equal(["dotnet", "product"], w1.Tags);
		var shared = manifest.Mounts[1];
		Assert.False(shared.Required);
		Assert.Equal(MountMode.ReadOnly, shared.Mode);
		Assert.False(shared.Inherits);
		Assert.Equal(["repo-w1"], shared.DependsOn);
		Assert.Equal(MountMode.ProposeOnly, manifest.Mounts[2].Mode);
	}

	[Fact(DisplayName = "space.yaml: its name and visibility, paths as objects, shares")]
	public void Space_Read()
	{
		var diagnostics = new List<FxDiagnostic>();
		var space = SpaceConfig.Read("""
			schema: 1.0.0
			name: acme
			visibility: public
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
			""", FxLayout.ConfigFile, diagnostics);
		Assert.Empty(diagnostics);
		Assert.Equal("acme", space.Name);
		Assert.Equal(SpaceVisibility.Public, space.Visibility);
		Assert.Equal(SpaceVisibility.Private, SpaceConfig.Read("schema: 1.0.0\n", FxLayout.ConfigFile, diagnostics).Visibility);
		Assert.Equal(4, space.Paths!.Count);
		Assert.False(space.Paths[1].Worktree);
		Assert.True(space.Paths[2].Exclude);
		Assert.True(space.Versions("libs/README.md"));
		Assert.True(space.Versions("_docs/plans/a.md"));
		Assert.False(space.Versions("_docs/drafts/a.md"));
		Assert.False(space.Versions("notes/"));
		var share = space.Shares![0];
		Assert.Equal("res/icon.png", share.Destination);
		Assert.Equal(["nuget-package"], share.AppliesTo.Tags);
		// a folder goes to a folder; a file to a folder keeps its name
		Assert.Equal(["res/icon.png", "icons/", "legal/LICENSE"], space.Shares.Select(s => s.Destination));
	}

	[Fact(DisplayName = "schema: a newer major is not read; a newer minor, unknown keys only warn; none, a warning")]
	public void Manifest_Schema()
	{
		var newer = Assert.Throws<MountsManifestException>(() => MountsManifest.Read("root", "schema: 2.0.0\nmounts: []\n", strict: true));
		Assert.Contains(newer.Diagnostics, d => d.Code == FxYamlReader.SchemaNewer);

		var minor = MountsManifest.Read("root", "schema: 1.3.0\nfuture: 1\nmounts: []\n", strict: true);
		Assert.Empty(minor.Mounts);
		var unknown = Assert.Throws<MountsManifestException>(() => MountsManifest.Read("root", "schema: 1.0.0\nfuture: 1\n", strict: true));
		Assert.Contains(unknown.Diagnostics, d => d.Message.English.Contains("Unknown key 'future'"));

		var reader = new FxYamlReader("x", "file.yaml");
		Assert.NotNull(reader.Load("repositories: []\n"));
		Assert.Contains(reader.Diagnostics, d => d.Code == FxYamlReader.SchemaMissing && d.Severity == FxSeverity.Warning);
	}

	// State

	[Fact(DisplayName = "mount rules: mandatory always; a clone already there is adopted; the intent wins")]
	public void State()
	{
		var mandatory = new Mount("a", "a/repo", [], "x", Required: true);
		var manual = new Mount("b", "b/repo", [], "x");
		Assert.True(MountsState.Empty.IsMounted(mandatory, cloneExists: false));
		Assert.False(MountsState.Empty.IsMounted(manual, cloneExists: false));
		Assert.True(MountsState.Empty.IsMounted(manual, cloneExists: true));
		Assert.False(MountsState.Empty.With("b", false).IsMounted(manual, cloneExists: true));
		Assert.True(MountsState.Empty.With("a", false).IsMounted(mandatory, cloneExists: true));
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
			Assert.Contains(sync.Events, e => e is FxStepStarted { Module: "mounts", Step: "clone" } s && s.Repository == repo);
			Assert.Contains(sync.Events, e => e is FxStepFinished { Module: "mounts", Step: "clone", Succeeded: true } f && f.Repository == repo);
		}

		new TempGitRepository(init: false, path: ws.RepoPath("lab")).WriteFile("dirty.txt", "x");
		var doctor = new Recorder();
		var results = Doctor(ws.Root, progress: doctor);
		Assert.Contains(doctor.Events, e => e is FxStepFinished { Step: "status", Repository: "lab", Succeeded: true });
		var dirty = Assert.Single(Diagnostics(results), d => d.Code == MountsModule.Dirty);
		Assert.Equal("lab", dirty.Repository);
		Assert.Equal("mounts", dirty.Module);
		// every diagnostic of the result went through the channel, once
		Assert.Equal(Diagnostics(results).Count(), doctor.Events.OfType<FxDiagnosticFound>().Count());
		Assert.Contains(doctor.Events, e => e is FxDiagnosticFound { Diagnostic.Code: MountsModule.Dirty, Repository: "lab" });
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
		Assert.Contains("<File Path=\"_fx/mounts/mounts.yaml\" />", solution);
		Assert.Contains("<Platform Name=\"Any CPU\" />", solution);

		Assert.Contains("name: plus\n    parked: false", ws.Read("_fx/mounts/~$state.yaml"));

		var map = ws.Read("_fx/dotnet/~$workspace.props");
		Assert.Contains("<FxWorkspaceProject Include=\"plus\" Repository=\"plus\"", map);
		Assert.Contains("<ProjectReference Include=\"@(_FxExternalReference->WithMetadataValue('Identity', 'plus')->'", map);
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
		var status = MountsModule.Repositories(MountsManifest.Load(ws.Root), withStatus: true, cancellationToken: TestContext.Current.CancellationToken).Single().Status!;
		Assert.Equal(1, status.Behind);
	}

	[Fact(DisplayName = "sync --dry-run: no clone, no file written; the missing repo is an error")]
	public void Sync_DryRun()
	{
		using var ws = new Workspace(("plus", "mandatory"));
		var results = Sync(ws.Root, new(DryRun: true, Offline: true));
		Assert.Contains(MountsModule.Missing, Codes(results));
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
		var manifest = MountsManifest.Read("root", """
			schema: 1.0.0
			mounts:
			  - name: shared
			    url: x
			    path: libs/shared
			  - name: app
			    url: y
			    path: apps/app
			""", strict: true);
		var space = SpaceConfig.Read("""
			schema: 1.0.0
			paths:
			  - path: README.md
			  - path: libs/
			  - path: docs/guides/
			  - path: docs/guides/drafts/
			    exclude: true
			""", FxLayout.ConfigFile, []);
		using var repo = new TempGitRepository();
		repo.WriteFile(".gitignore", GitignoreGenerator.Generate(space, manifest, ["build/All.slnx"]));
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
		Assert.False(Ignored("_fx/space.yaml"));
		Assert.False(Ignored("_fx/mounts/mounts.yaml"));
		Assert.True(Ignored("_fx/mounts/~$state.yaml"));
		Assert.True(Ignored("libs/~$tmp"));
		Assert.False(Ignored(".gitignore"));
	}

	[Fact(DisplayName = ".gitignore: a space that mounts nothing and declares paths gets its whitelist too; without paths, none")]
	public void Gitignore_WithoutMounts()
	{
		using var notes = new TempGitRepository();
		notes.WriteFile(Path.Combine("_fx", "space.yaml"), "schema: 1.0.0\nname: notes\n");
		notes.WriteFile(Path.Combine("ideas", "a.md"), "a\n");
		Assert.Empty(Errors(Sync(notes.Path)));
		Assert.False(File.Exists(Path.Combine(notes.Path, ".gitignore")));

		notes.WriteFile(Path.Combine("_fx", "space.yaml"), "schema: 1.0.0\nname: notes\npaths:\n  - path: ideas/\n");
		Assert.Empty(Errors(Sync(notes.Path)));
		Assert.StartsWith($"# {FxHeader.Title}", File.ReadAllText(Path.Combine(notes.Path, ".gitignore")));
		var git = GitClient.Discover(notes.Path)!;
		Assert.False(git.Ignores("ideas/a.md"));
		Assert.False(git.Ignores("_fx/space.yaml"));
		Assert.True(git.Ignores("drafts/b.md"));
	}

	[Fact(DisplayName = "solutions: composed from others; a repository not on disk keeps what the generated one had of it")]
	public void Solution_Compose()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "manual"));
		Assert.Empty(MountsModule.SetMount(MountsManifest.Load(ws.Root), ["lab"], mounted: true).Diagnostics);
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
		Assert.Empty(MountsModule.SetMount(MountsManifest.Load(ws.Root), ["lab"], mounted: false).Diagnostics);
		var results = Sync(ws.Root);
		Assert.DoesNotContain(Codes(results), c => c == "dotnet.missing-solution");
		var solution = ws.Read("Test.slnx");
		Assert.Equal(before, solution);
		Assert.Contains("<Project Path=\"lab/repo/solution/src/lab/lab.csproj\" />", solution);
		Assert.Contains("<Folder Name=\"/lab/src/\">", solution);
		Assert.DoesNotContain("lab.csproj", ws.Read("_fx/dotnet/~$workspace.props"));
	}

	[Fact(DisplayName = "solutions: the global.json of a workspace that composes them pins its sdks (MSBuild reads it from the folder of the solution)")]
	public void Solution_GlobalJson()
	{
		using var ws = new Workspace(("plus", "mandatory"));
		ws.Meta.WriteFile("global.json", "{\n  \"test\": {\n    \"runner\": \"Microsoft.Testing.Platform\"\n  }\n}\n");
		ws.WriteDotnet("sdks:\n  Fuxion.Tool.Sdk: 1.2.3\n", "plus");
		Assert.DoesNotContain(Errors(Sync(ws.Root)), _ => true);
		var json = ws.Read("global.json");
		Assert.Contains("\"Fuxion.Tool.Sdk\": \"1.2.3\"", json);
		Assert.Contains("\"runner\": \"Microsoft.Testing.Platform\"", json);

		// a new version: out of date until fx sync
		ws.WriteDotnet("sdks:\n  Fuxion.Tool.Sdk: 1.2.4\n", "plus");
		Assert.Contains(Diagnostics(Doctor(ws.Root)), d => d.Code == DotnetModule.Outdated && d.File == "global.json");
		Sync(ws.Root);
		Assert.Contains("\"Fuxion.Tool.Sdk\": \"1.2.4\"", ws.Read("global.json"));
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

	[Fact(DisplayName = "inheritance: the .NET module goes down to the repos of applies-to, merged, with the files of what applies, in _fx/.inherited/")]
	public void Sync_Inheritance()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("docs", "mandatory"));
		ws.WriteManifest(("plus", "mandatory", "    tags: [dotnet]\n"), ("docs", "mandatory", ""));
		ws.WriteDotnet("imports:\n  - props: default.props\n    files: [assets/icon.png]\n  - targets: other.targets\n    applies-to:\n      tags: [other]\n", "plus");
		ws.Meta.WriteFile(Path.Combine("_fx", "dotnet", "default.props"), "<Project />\n");
		ws.Meta.WriteFile(Path.Combine("_fx", "dotnet", "other.targets"), "<Project />\n");
		ws.Meta.WriteFile(Path.Combine("_fx", "dotnet", "unused.targets"), "<Project />\n");
		var icon = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF };
		Directory.CreateDirectory(Path.Combine(ws.Root, "_fx", "dotnet", "assets"));
		File.WriteAllBytes(Path.Combine(ws.Root, "_fx", "dotnet", "assets", "icon.png"), icon);
		Assert.Empty(Errors(Sync(ws.Root)));

		// the yaml merged, of what is for it and without the solutions; only the files of what applies (plan R, decision 2),
		// binaries byte for byte, text with the header
		var inherited = Path.Combine(ws.RepoPath("plus"), "_fx", ".inherited");
		var yaml = ws.Read("plus/repo/_fx/.inherited/dotnet/dotnet.yaml");
		Assert.StartsWith($"# {FxHeader.Title}\n# Generated from: workspace /test (_fx/dotnet/dotnet.yaml)\n", yaml);
		Assert.Contains("applies-to:\n  tags: [dotnet]\nimports:\n  - props: default.props\n    files: assets/icon.png\n", yaml);
		Assert.DoesNotContain("solutions", yaml);
		Assert.DoesNotContain("other.targets", yaml);
		Assert.Contains(FxHeader.Title, ws.Read("plus/repo/_fx/.inherited/dotnet/default.props"));
		Assert.False(File.Exists(Path.Combine(inherited, "dotnet", "other.targets")));
		Assert.False(File.Exists(Path.Combine(inherited, "dotnet", "unused.targets")));
		Assert.Equal(icon, File.ReadAllBytes(Path.Combine(inherited, "dotnet", "assets", "icon.png")));
		// a file of the workspace that no import names reaches nobody: the doctor says so
		Assert.Contains(Diagnostics(Doctor(ws.Root)), d => d.Code == DotnetModule.UnlistedFile && d.File == "_fx/dotnet/unused.targets");
		var card = FxInherited.Load(ws.RepoPath("plus"))!;
		Assert.Equal("/test/plus", card.Space.FullName);
		Assert.Equal(["dotnet"], card.Space.Tags);
		Assert.Equal("/test", card.Parent.FullName);
		Assert.Contains(card.Files, f => f.Path == "dotnet/assets/icon.png" && f.From.SequenceEqual(["/test"]));
		Assert.Contains("name: dotnet", ws.Read("plus/repo/_fx/.inherited/space.yaml"));
		var props = ws.Read("plus/repo/_fx/dotnet/dotnet.g.props");
		Assert.Contains("$(MSBuildThisFileDirectory)../.inherited/dotnet/default.props", props);
		Assert.Contains("<FxRepositoryFullName>/test/plus</FxRepositoryFullName>", props);
		// a repository of a workspace that does not say it builds on its own needs it (plan R, proposal 5)
		Assert.Contains("<FxRequiresWorkspace Condition=\"'$(FxRequiresWorkspace)' == ''\">true</FxRequiresWorkspace>", props);
		Assert.False(Directory.Exists(Path.Combine(ws.RepoPath("docs"), "_fx"))); // nothing applies to it
		Assert.DoesNotContain(Codes(Doctor(ws.Root)), c => c is MountsModule.Outdated or "dotnet.outdated");

		// a change in the workspace is out of date in the repo until the next sync; what goes, goes from the repo
		ws.Meta.WriteFile(Path.Combine("_fx", "dotnet", "default.props"), "<Project><PropertyGroup><X>1</X></PropertyGroup></Project>\n");
		Assert.Contains(Diagnostics(Doctor(ws.Root)), d => d.Code == MountsModule.Outdated && d.File == "plus/repo/_fx/.inherited/dotnet/default.props");
		ws.WriteDotnet("imports:\n  - props: default.props\n", "plus");
		Sync(ws.Root);
		Assert.False(File.Exists(Path.Combine(inherited, "dotnet", "assets", "icon.png")));
		Assert.Contains("<X>1</X>", ws.Read("plus/repo/_fx/.inherited/dotnet/default.props"));
		// one that builds on its own does not need it
		File.WriteAllText(Path.Combine(ws.RepoPath("plus"), "_fx", "dotnet", "dotnet.yaml"), "schema: 1.0.0\nstandalone: true\n");
		Sync(ws.Root);
		Assert.DoesNotContain("FxRequiresWorkspace", ws.Read("plus/repo/_fx/dotnet/dotnet.g.props"));

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
		w1.WriteFile(Path.Combine("_fx", "space.yaml"), "schema: 1.0.0\ntags:\n  - name: mobile\n");
		w1.WriteFile(Path.Combine("_fx", "mounts", "mounts.yaml"),
			$"schema: 1.0.0\nmounts:\n  - name: c1\n    url: {c1.Path.Replace('\\', '/')}\n    path: c1\n    required: true\n    tags: [dotnet, mobile]\n");
		w1.WriteFile(Path.Combine("_fx", "dotnet", "dotnet.yaml"),
			"schema: 1.0.0\npackages:\n  - ids: [Serilog]\n    versions:\n      - version: 4.0.0\nsolutions:\n  - path: W1.slnx\n    folders:\n      - name: /C1\n        solutions: [c1/C1.slnx]\n");
		w1.CommitAll("w1");
		using var acme = new TempGitRepository();
		acme.WriteFile(Path.Combine("_fx", "space.yaml"), "schema: 1.0.0\nname: acme\nrequires: 0.1.0\ntags:\n  - name: dotnet\n  - name: product\n");
		acme.WriteFile(Path.Combine("_fx", "mounts", "mounts.yaml"),
			$"schema: 1.0.0\nmounts:\n  - name: w1\n    url: {w1.Path.Replace('\\', '/')}\n    path: products/w1\n    required: true\n    tags: [product]\n");
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
		Assert.Equal("/acme/w1/c1", card.Space.FullName);
		Assert.Equal("/acme/w1", card.Parent.FullName);
		Assert.Contains(card.Files, f => f.Path == "dotnet/dotnet.yaml" && f.From.SequenceEqual(["/acme", "/acme/w1"]));
		var yaml = Read("products/w1/c1/_fx/.inherited/dotnet/dotnet.yaml");
		Assert.Contains("ids: [xunit.v3]", yaml);
		Assert.Contains("ids: [Serilog]", yaml);
		Assert.Contains("name: mobile", Read("products/w1/c1/_fx/.inherited/space.yaml"));
		Assert.Contains("requires: 0.1.0", Read("products/w1/c1/_fx/.inherited/space.yaml"));
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
		ws.Meta.WriteFile(Path.Combine("_fx", "space.yaml"), "schema: 1.0.0\nname: test\nrequires: 9.0.0\n");
		var results = Sync(ws.Root, new(FxVersion: "1.2.0"));
		Assert.Contains(SyncEngine.Requires, Codes(results));
		Assert.False(Directory.Exists(ws.RepoPath("plus")));
		Assert.False(File.Exists(Path.Combine(ws.Root, ".gitignore")));
		Assert.DoesNotContain(SyncEngine.Requires, Codes(Sync(ws.Root, new(FxVersion: "9.0.1-local"))));
	}

	[Fact(DisplayName = "the SDK the tree pins: newer than fx, it reads but writes nothing (also from a repository); older, the doctor says it, where it is raised")]
	public void SdkPinned()
	{
		using var ws = new Workspace(("plus", "mandatory"));
		ws.WriteDotnet("sdks:\n  Fuxion.Tool.Sdk: 1.2.3\n", "plus");
		var newer = Assert.Single(Diagnostics(Sync(ws.Root, new(FxVersion: "1.2.2+62d65aa"))), d => d.Code == SyncEngine.SdkNewer);
		Assert.Equal((FxSeverity.Error, "_fx/dotnet/dotnet.yaml", "fx self update"), (newer.Severity, newer.File, newer.Fix?.Command));
		Assert.Contains("1.2.2)", newer.Message.English);
		Assert.False(Directory.Exists(ws.RepoPath("plus")));
		Assert.Empty(Errors(Sync(ws.Root, new(FxVersion: "1.2.3+62d65aa"))));
		Assert.True(Directory.Exists(ws.RepoPath("plus")));

		// raised in the workspace, a sync from the repository sees it in its parent on disk
		ws.WriteDotnet("sdks:\n  Fuxion.Tool.Sdk: 1.2.4\n", "plus");
		var fromRepository = Assert.Single(Diagnostics(Sync(ws.RepoPath("plus"), new(FxVersion: "1.2.3+62d65aa"))), d => d.Code == SyncEngine.SdkNewer);
		Assert.Equal("../../_fx/dotnet/dotnet.yaml", fromRepository.File);

		// older than fx: a warning of the doctor only, not for a fx built from its sources
		DiagnosticsOf(new(FxVersion: "1.3.0+62d65aa"), doctor: false, none: true);
		var older = DiagnosticsOf(new(Offline: true, FxVersion: "1.3.0+62d65aa"), doctor: true, none: false)!;
		Assert.Equal((FxSeverity.Warning, "_fx/dotnet/dotnet.yaml"), (older.Severity, older.File));
		Assert.Contains("1.3.0", older.Fix!.Hint!.English);
		DiagnosticsOf(new(Offline: true, FxVersion: "1.3.0+local.62d65aa"), doctor: true, none: true);

		FxDiagnostic? DiagnosticsOf(SyncOptions options, bool doctor, bool none)
		{
			var results = doctor ? SyncEngine.Doctor(ws.Root, options, cancellationToken: TestContext.Current.CancellationToken) : Sync(ws.Root, options);
			var found = Diagnostics(results).Where(d => d.Code is SyncEngine.SdkOlder or SyncEngine.SdkNewer).ToList();
			if (none)
				Assert.Empty(found);
			return none ? null : Assert.Single(found);
		}
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
		var manifest = MountsManifest.Load(ws.Root);
		var mount = MountsModule.SetMount(manifest, ["lab"], mounted: true);
		Assert.Empty(mount.Diagnostics);
		Assert.Equal("mounted", Assert.Single(mount.Actions).Action);
		Assert.Contains(new RepositoryAction("lab", "cloned"), Actions(Sync(ws.Root)));
		Assert.Contains("lab.csproj", ws.Read("_fx/dotnet/~$workspace.props"));
		File.WriteAllText(Path.Combine(ws.RepoPath("lab"), "work.txt"), "not committed");

		// parked in _fx/~$parking/ under its path; the folder it leaves empty goes
		var unmount = MountsModule.SetMount(manifest, ["lab"], mounted: false);
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
		Assert.True(Assert.Single(MountsModule.Repositories(manifest, withStatus: false, cancellationToken: TestContext.Current.CancellationToken), r => r.Repository.Name == "lab").Parked);

		// back, without cloning: the work is there, and the parking goes
		Assert.Equal("unparked", Assert.Single(MountsModule.SetMount(manifest, ["lab"], mounted: true).Actions).Action);
		Assert.True(File.Exists(Path.Combine(ws.RepoPath("lab"), "work.txt")));
		Assert.False(Directory.Exists(Path.Combine(ws.Root, "_fx", "~$parking")));
		Sync(ws.Root);
		Assert.Contains("lab.csproj", ws.Read("_fx/dotnet/~$workspace.props"));

		// a mandatory one cannot be unmounted, nor an unknown one mounted: nothing changes
		Assert.Single(MountsModule.SetMount(manifest, ["plus", "lab"], mounted: false).Diagnostics);
		Assert.True(FxLayout.IsGitRoot(ws.RepoPath("lab")));
		Assert.Single(MountsModule.SetMount(manifest, ["nope"], mounted: true).Diagnostics);
	}

	[Fact(DisplayName = "fx sync parks what is unmounted and still there, and brings back what is mounted and parked; the doctor says it first")]
	public void Sync_Parking()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "manual"));
		var manifest = MountsManifest.Load(ws.Root);
		MountsModule.SetMount(manifest, ["lab"], mounted: true);
		Sync(ws.Root);
		void State(bool mounted)
			=> File.WriteAllText(Path.Combine(ws.Root, MountsState.RelativePath), MountsState.Load(ws.Root).With("lab", mounted).ToYaml());

		// unmounted only in the state, as fx 0.1 did
		State(false);
		var notParked = Assert.Single(Diagnostics(Doctor(ws.Root)), d => d.Code == MountsModule.NotParked);
		Assert.Equal("'lab' is unmounted but still at lab/repo: fx sync parks it.", notParked.Message.English);
		Assert.Equal(FxSeverity.Warning, notParked.Severity);
		Assert.Contains(Actions(Sync(ws.Root)), a => a is { Repository: "lab", Action: "parked" });
		Assert.True(FxLayout.IsGitRoot(Path.Combine(ws.Root, "_fx", "~$parking", "lab", "repo")));

		// mounted only in the state: parked, so missing; sync brings it back, also offline
		State(true);
		var missing = Assert.Single(Diagnostics(Doctor(ws.Root)), d => d.Code == MountsModule.Missing);
		Assert.Equal("'lab' is mounted but parked in _fx/~$parking/lab/repo: fx sync brings it back to lab/repo.", missing.Message.English);
		Assert.Contains(Actions(Sync(ws.Root, new(Offline: true))), a => a is { Repository: "lab", Action: "unparked" });
		Assert.True(FxLayout.IsGitRoot(ws.RepoPath("lab")));
		Assert.DoesNotContain(Diagnostics(Doctor(ws.Root)), d => d.Code is MountsModule.Missing or MountsModule.NotParked);
	}

	[Fact(DisplayName = "parking: if something has the clone open, or the parking is taken, nothing moves and it says so")]
	public void Parking_Blocked()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "manual"));
		var manifest = MountsManifest.Load(ws.Root);
		MountsModule.SetMount(manifest, ["lab"], mounted: true);
		Sync(ws.Root);

		// the place in the parking is taken
		var parking = Path.Combine(ws.Root, "_fx", "~$parking", "lab", "repo");
		Directory.CreateDirectory(parking);
		File.WriteAllText(Path.Combine(parking, "other.txt"), "");
		var taken = Assert.Single(MountsModule.SetMount(manifest, ["lab"], mounted: false).Diagnostics);
		Assert.Equal(MountsModule.MoveFailed, taken.Code);
		Assert.Contains("there is already something there", taken.Message.English);
		Assert.True(FxLayout.IsGitRoot(ws.RepoPath("lab")));
		Assert.False(MountsState.Load(ws.Root).Parked["lab"]);
		Directory.Delete(Path.Combine(ws.Root, "_fx", "~$parking"), recursive: true);

		// a file open inside it (Visual Studio, a terminal): Windows does not move the folder
		Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows keeps an open folder from moving");
		using (File.Open(Path.Combine(ws.RepoPath("lab"), "Fxlab.slnx"), FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			var open = Assert.Single(MountsModule.SetMount(manifest, ["lab"], mounted: false).Diagnostics);
			Assert.Equal(MountsModule.MoveFailed, open.Code);
			Assert.Equal($"{MountsModule.MoveFailed}.fix", open.Fix!.Hint!.Key);
		}
		Assert.True(FxLayout.IsGitRoot(ws.RepoPath("lab")));
		Assert.False(Directory.Exists(Path.Combine(ws.Root, "_fx", "~$parking")));
		Assert.False(MountsState.Load(ws.Root).Parked["lab"]);
	}

	[Fact(DisplayName = "parking a clone with worktrees: git still links them after the move, both ways")]
	public void Parking_Worktrees()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "manual"));
		var manifest = MountsManifest.Load(ws.Root);
		MountsModule.SetMount(manifest, ["lab"], mounted: true);
		Sync(ws.Root);
		var lab = new TempGitRepository(init: false, path: ws.RepoPath("lab"));
		using var worktree = new TempGitRepository(init: false);
		lab.Git("worktree", "add", "--quiet", "-b", "feature/x", worktree.Path);

		MountsModule.SetMount(manifest, ["lab"], mounted: false);
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
		Assert.Contains(MountsModule.Dirty, codes);
		Assert.Contains(MountsModule.Unpushed, codes);
		Assert.Contains(MountsModule.Detached, codes);
		Assert.Contains(MountsModule.OriginMismatch, codes);
		Assert.Contains(diagnostics, d => d.Code == MountsModule.Orphan && d.Message.English.StartsWith("stray/repo "));
		Assert.Contains(diagnostics, d => d.Code == MountsModule.NotVersioned && d.Severity == FxSeverity.Info && d.Message.English.StartsWith("notes/ "));
		Assert.Contains(diagnostics, d => d.Code == MountsModule.UnknownTag && d.Repository == "lab");
		Assert.Contains(diagnostics, d => d.Code == MountsModule.LocalNotIgnored && d.Repository == "plus");
		// a folder that only holds a repository is not reported
		Assert.DoesNotContain(diagnostics, d => d.Code == MountsModule.NotVersioned && d.Message.English.StartsWith("plus/"));
	}

	[Fact(DisplayName = "doctor: a mounted repo the workspace does not ignore is an error")]
	public void Doctor_NotIgnored()
	{
		using var ws = new Workspace(("plus", "mandatory"));
		Sync(ws.Root);
		File.WriteAllText(Path.Combine(ws.Root, ".gitignore"), $"# {FxHeader.Title}\n!/everything\n");
		var diagnostics = Diagnostics(Doctor(ws.Root)).ToList();
		Assert.Contains(diagnostics, d => d.Code == MountsModule.NotIgnored && d.Severity == FxSeverity.Error);
		Assert.Contains(diagnostics, d => d.Code == MountsModule.Outdated && d.File == ".gitignore");
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
		var actions = MountsModule.Pull(MountsManifest.Load(ws.Root), [], cancellationToken: TestContext.Current.CancellationToken).Actions;
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
		Assert.Equal(0, InProcess.Run(["sync"], stdout, new StringWriter(), ws.Root));
		Assert.Contains("cloned      plus", stdout.ToString());

		Assert.Equal(0, InProcess.Run(["doctor", "--offline"], new StringWriter(), new StringWriter(), ws.RepoPath("plus")));
		stdout = new StringWriter();
		Assert.Equal(0, InProcess.Run(["doctor", "-w", "--offline", "--output", "json"], stdout, new StringWriter(), ws.RepoPath("plus")));
		Assert.Contains(JsonDocument.Parse(stdout.ToString()).RootElement.GetProperty("modules").EnumerateArray(), m => m.GetProperty("module").GetString() == "mounts");
	}

	[Fact(DisplayName = "fx repo list --output json: fx-repo-list/1")]
	public void Cli_RepoList()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "manual"));
		var stdout = new StringWriter();
		Assert.Equal(0, InProcess.Run(["repo", "list", "--output", "json"], stdout, new StringWriter(), ws.Root));
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
		Assert.Equal(0, InProcess.Run(["sync"], new StringWriter(), new StringWriter(), ws.Root));
		ws.WriteManifest(("plus", "mandatory", ""), ("lab", "manual", ""));

		var stdout = new StringWriter();
		Assert.Equal(0, InProcess.Run(["repo", "unmount", "lab", "--output", "json"], stdout, new StringWriter(), ws.Root));
		var json = JsonDocument.Parse(stdout.ToString()).RootElement;
		Assert.Equal("fx-repo-mount/1", json.GetProperty("schema").GetString());
		var lab = Assert.Single(json.GetProperty("repositories").EnumerateArray());
		Assert.Equal("parked", lab.GetProperty("action").GetString());
		Assert.Equal("in _fx/~$parking/lab/repo", lab.GetProperty("detail").GetString());

		stdout = new StringWriter();
		Assert.Equal(0, InProcess.Run(["repo", "list", "--output", "json"], stdout, new StringWriter(), ws.Root));
		var repos = JsonDocument.Parse(stdout.ToString()).RootElement.GetProperty("repositories").EnumerateArray().ToList();
		Assert.True(repos[1].GetProperty("parked").GetBoolean());
		Assert.False(repos[0].GetProperty("parked").GetBoolean());

		stdout = new StringWriter();
		Assert.Equal(0, InProcess.Run(["repo", "mount", "lab"], stdout, new StringWriter(), ws.Root));
		Assert.Equal("unparked   lab", stdout.ToString().Trim());
	}

	[Theory(DisplayName = "fx repo mount|unmount without names and no terminal to ask for them: what is missing, exit code 2 (plan O, O3.3)")]
	[InlineData("mount")]
	[InlineData("unmount")]
	public void Cli_MountMissing(string verb)
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "manual"));
		var stderr = new StringWriter();
		Assert.Equal(FxConsole.UsageError, InProcess.Run(["repo", verb], new StringWriter(), stderr, ws.Root));
		Assert.Contains($"fx repo {verb} <names>", stderr.ToString());
	}

	[Fact(DisplayName = "fx repo status: only the repositories that ask for attention; --all, every one (plan O, decision 8)")]
	public void Cli_RepoStatus()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "mandatory"));
		Assert.Equal(0, InProcess.Run(["sync"], new StringWriter(), new StringWriter(), ws.Root));
		var stdout = new StringWriter();
		Assert.Equal(0, InProcess.Run(["repo", "status"], stdout, new StringWriter(), ws.Root));
		Assert.Equal("✓ All 2 repositories are clean.", stdout.ToString().Trim());

		File.WriteAllText(Path.Combine(ws.RepoPath("lab"), "new.txt"), "x");
		stdout = new StringWriter();
		Assert.Equal(0, InProcess.Run(["repo", "status"], stdout, new StringWriter(), ws.Root));
		var text = stdout.ToString();
		Assert.Contains("lab", text);
		Assert.DoesNotContain("plus", text);
		Assert.Contains("1 of 2 repositories are clean (--all to see them).", text);

		stdout = new StringWriter();
		Assert.Equal(0, InProcess.Run(["repo", "status", "--all"], stdout, new StringWriter(), ws.Root));
		text = stdout.ToString();
		Assert.Contains("lab", text);
		Assert.Contains("plus", text);
		Assert.DoesNotContain("--all", text);
		// without a terminal, no colours
		Assert.DoesNotContain("\x1b[", text);
	}
}
