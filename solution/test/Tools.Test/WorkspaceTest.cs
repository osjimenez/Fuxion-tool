using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Fuxion.Tools.Cli;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Dotnet;
using Fuxion.Tools.Core.Workspace;
using Xunit;

namespace Fuxion.Tools.Test;

/// <summary>The workspace module: manifest, local state, sync, doctor and fx repo (plan M).</summary>
public sealed class WorkspaceTest
{
	/// <summary>
	/// A metarepo (a git repository with <c>_fx/workspace.yaml</c>) and, for each repository of the manifest, an
	/// "origin": a local repository with a commit and a solution, cloned by URL (its path).
	/// </summary>
	sealed class Workspace : IDisposable
	{
		public readonly TempGitRepository Meta = new();
		public readonly Dictionary<string, TempGitRepository> Origins = [];

		public Workspace(params (string Name, string Mount)[] repositories)
		{
			foreach (var (name, _) in repositories)
			{
				var origin = new TempGitRepository();
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
				origin.CommitAll("c1");
				Origins[name] = origin;
			}
			WriteManifest(repositories.Select(r => (r.Name, r.Mount, "")).ToArray());
			Meta.CommitAll("metarepo");
		}

		public void WriteManifest(params (string Name, string Mount, string Extra)[] repositories)
			=> Meta.WriteFile(Path.Combine("_fx", "workspace.yaml"), "version: 1\nworkspace:\n  name: test\nfolders: [_docs]\nrepositories:\n" + string.Concat(repositories.Select(r => $"""
				  - name: {r.Name}
				    path: {r.Name}/repo
				    url: {Origins[r.Name].Path.Replace('\\', '/')}
				    mount: {r.Mount}
				    solution: Fx{r.Name}.slnx
				{r.Extra}
				""")));

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

	static IEnumerable<string> Codes(FxModuleResult result) => result.Diagnostics.Select(d => d.Code);

	// Manifest

	[Theory(DisplayName = "manifest: every problem, with its line")]
	[InlineData("  - name: Plus\n    path: plus/repo\n    url: x\n", "lowercase letters, digits and '-'")]
	[InlineData("  - name: plus\n    path: Plus/Repo\n    url: x\n", "must be lowercase")]
	[InlineData("  - name: plus\n    path: ../plus\n    url: x\n", "without '.' or '..'")]
	[InlineData("  - name: plus\n    path: _fx/repo\n    url: x\n", "reserved for the metarepo")]
	[InlineData("  - name: plus\n    path: _docs/repo\n    url: x\n", "reserved for the metarepo")]
	[InlineData("  - name: plus\n    path: plus/repo\n", "needs 'url'")]
	[InlineData("  - name: plus\n    path: plus/repo\n    url: x\n    mount: always\n", "'mandatory' or 'manual'")]
	[InlineData("  - name: plus\n    path: plus/repo\n    url: x\n    dependsOn: [oss]\n", "depends on 'oss'")]
	[InlineData("  - name: a\n    path: a/repo\n    url: x\n  - name: a\n    path: b/repo\n    url: y\n", "used more than once")]
	[InlineData("  - name: a\n    path: a/repo\n    url: x\n  - name: b\n    path: a/repo/b\n    url: y\n", "mounted inside 'a'")]
	public void Manifest_Invalid(string repositories, string message)
	{
		var ex = Assert.Throws<WorkspaceManifestException>(() => WorkspaceManifest.Read("root", "version: 1\nfolders: [_docs]\nrepositories:\n" + repositories, strict: true));
		Assert.Contains(ex.Diagnostics, d => d.Code == WorkspaceManifest.InvalidManifest && d.Message.English.Contains(message) && d.Line is > 0);
	}

	[Fact(DisplayName = "manifest: the design's example reads (§5)")]
	public void Manifest_Read()
	{
		var manifest = WorkspaceManifest.Read("root", """
			version: 1
			workspace:
			  name: fuxion
			  major: 11
			  fx: 1.4.0
			folders: [_docs]
			repositories:
			  - name: oss
			    path: oss/repo
			    url: https://github.com/osjimenez/Fuxion.git
			    mount: mandatory
			    standalone: true
			    solution: Fuxion.slnx
			    tags: [dotnet, public]
			  - name: next
			    path: next/repo
			    url: https://github.com/osjimenez/Fuxion-next.git
			    dependsOn: [oss]
			    solution: FxNext.slnx
			    solutionExclude: [solution/old]
			""", strict: true);
		Assert.Equal("fuxion", manifest.Name);
		Assert.Equal(11, manifest.Major);
		Assert.Equal("1.4.0", manifest.MinimumFx);
		var oss = manifest.Repositories[0];
		Assert.Equal(MountPolicy.Mandatory, oss.Mount);
		Assert.True(oss.Standalone);
		Assert.Equal(["dotnet", "public"], oss.Tags);
		Assert.Equal(["oss"], manifest.Repositories[1].DependsOn);
		Assert.Equal(["solution/old"], manifest.Repositories[1].SolutionExclude);
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
		WorkspaceModule.Sync(ws.Root, new(), sync, TestContext.Current.CancellationToken);
		foreach (var repo in new[] { "plus", "lab" })
		{
			Assert.Contains(sync.Events, e => e is FxStepStarted { Module: "workspace", Step: "clone" } s && s.Repository == repo);
			Assert.Contains(sync.Events, e => e is FxStepFinished { Module: "workspace", Step: "clone", Succeeded: true } f && f.Repository == repo);
		}

		new TempGitRepository(init: false, path: ws.RepoPath("lab")).WriteFile("dirty.txt", "x");
		var doctor = new Recorder();
		var result = WorkspaceModule.Doctor(ws.Root, offline: true, doctor, TestContext.Current.CancellationToken);
		Assert.Contains(doctor.Events, e => e is FxStepFinished { Step: "status", Repository: "lab", Succeeded: true });
		var dirty = Assert.Single(result.Diagnostics, d => d.Code == WorkspaceModule.Dirty);
		Assert.Equal("lab", dirty.Repository);
		Assert.Equal("workspace", dirty.Module);
		// every diagnostic of the result went through the channel, once
		Assert.Equal(result.Diagnostics.Count, doctor.Events.OfType<FxDiagnosticFound>().Count());
		Assert.Contains(doctor.Events, e => e is FxDiagnosticFound { Diagnostic.Code: WorkspaceModule.Dirty, Repository: "lab" });
	}

	[Fact(DisplayName = "progress: a cancelled operation stops")]
	public void Cancellation()
	{
		using var ws = new Workspace(("plus", "mandatory"));
		using var cancelled = new System.Threading.CancellationTokenSource();
		cancelled.Cancel();
		Assert.ThrowsAny<OperationCanceledException>(() => WorkspaceModule.Sync(ws.Root, new(), cancellationToken: cancelled.Token));
		Assert.False(Directory.Exists(ws.RepoPath("plus")));
	}

	// Sync

	[Fact(DisplayName = "sync: clones the mandatory repos, generates .gitignore and Fuxion.slnx, saves the state")]
	public void Sync()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "manual"));
		var result = WorkspaceModule.Sync(ws.Root, new(), cancellationToken: TestContext.Current.CancellationToken);
		Assert.Empty(result.Diagnostics);
		Assert.Contains(new RepositoryAction("plus", "cloned"), result.Actions);
		Assert.True(File.Exists(Path.Combine(ws.RepoPath("plus"), "Fxplus.slnx")));
		Assert.False(Directory.Exists(ws.RepoPath("lab")));

		var gitignore = ws.Read(".gitignore");
		Assert.StartsWith("# GENERATED by fx", gitignore);
		Assert.Contains("/*\n", gitignore);
		Assert.Contains("!/_docs/\n", gitignore);
		Assert.Contains("!/global.json\n", gitignore);
		Assert.True(ws.Meta.Git("check-ignore", "plus/repo/").Length > 0);

		var solution = ws.Read("Fuxion.slnx");
		Assert.Contains("GENERATED by fx", solution);
		Assert.Contains("<Folder Name=\"/plus/\" />", solution);
		Assert.Contains("<Folder Name=\"/plus/src/\">", solution);
		Assert.Contains("<Project Path=\"plus/repo/solution/src/plus/plus.csproj\" />", solution);
		Assert.Contains("<File Path=\"_fx/workspace.yaml\" />", solution);
		Assert.Contains("<Platform Name=\"Any CPU\" />", solution);

		Assert.Contains("name: plus\n    mount: true", ws.Read("_fx/~$workspace.yaml"));

		var map = ws.Read("_fx/~$workspace.props");
		Assert.Contains("<FxWorkspaceProject Include=\"plus\" Repository=\"plus\"", map);
		Assert.Contains("<ProjectReference Include=\"@(FxReference->WithMetadataValue('Identity', 'plus')->'", map);
		var again = WorkspaceModule.Sync(ws.Root, new(), cancellationToken: TestContext.Current.CancellationToken);
		Assert.All(again.Files, f => Assert.Equal(SyncFileStatus.Unchanged, f.Status));
	}

	[Fact(DisplayName = "sync: fetches the mounted repos (online), never pulls")]
	public void Sync_Fetch()
	{
		using var ws = new Workspace(("plus", "mandatory"));
		WorkspaceModule.Sync(ws.Root, new(), cancellationToken: TestContext.Current.CancellationToken);
		ws.Origins["plus"].Commit("c2");
		var result = WorkspaceModule.Sync(ws.Root, new(), cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(new RepositoryAction("plus", "fetched"), result.Actions);
		var status = WorkspaceModule.Repositories(WorkspaceManifest.Load(ws.Root), withStatus: true, cancellationToken: TestContext.Current.CancellationToken).Single().Status!;
		Assert.Equal(1, status.Behind);
	}

	[Fact(DisplayName = "sync --dry-run: no clone, no file written; the missing repo is an error")]
	public void Sync_DryRun()
	{
		using var ws = new Workspace(("plus", "mandatory"));
		var result = WorkspaceModule.Sync(ws.Root, new(DryRun: true, Offline: true), cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(WorkspaceModule.Missing, Codes(result));
		Assert.False(Directory.Exists(ws.RepoPath("plus")));
		Assert.False(File.Exists(Path.Combine(ws.Root, ".gitignore")));
	}

	[Fact(DisplayName = "a .gitignore written by hand is not touched without --adopt")]
	public void Adopt()
	{
		using var ws = new Workspace(("plus", "mandatory"));
		ws.Meta.WriteFile(".gitignore", "/*\n!/_fx/\n");
		var refused = WorkspaceModule.Sync(ws.Root, new(), cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(refused.Files, f => f.Path == ".gitignore" && f.Status == SyncFileStatus.Refused);
		Assert.Contains(WorkspaceModule.NotGenerated, Codes(refused));
		Assert.Equal("/*\n!/_fx/\n", ws.Read(".gitignore"));

		var adopted = WorkspaceModule.Sync(ws.Root, new(Adopt: true), cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(adopted.Files, f => f.Path == ".gitignore" && f.Status == SyncFileStatus.Written);
		Assert.StartsWith("# GENERATED by fx", ws.Read(".gitignore"));
	}

	[Fact(DisplayName = "Fuxion.slnx: solutionExclude leaves projects out; a project two repos bring appears once")]
	public void Solution_ExcludeAndDedupe()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("tools", "mandatory"));
		ws.WriteManifest(("plus", "mandatory", "    solutionExclude: [solution/tools]\n"), ("tools", "mandatory", ""));
		Assert.Empty(WorkspaceModule.Sync(ws.Root, new(), cancellationToken: TestContext.Current.CancellationToken).Diagnostics);
		var solution = ws.Read("Fuxion.slnx");
		Assert.DoesNotContain("plus/repo/solution/tools", solution);
		Assert.Contains("tools/repo/solution/tools/Tool/Tool.csproj", solution);
		Assert.DoesNotContain("<Folder Name=\"/plus/tools/\"", solution);
		Assert.Contains("<Folder Name=\"/tools/tools/\">", solution);
		Assert.Contains("<FxWorkspaceProject Include=\"Tool\" Repository=\"tools\"", ws.Read("_fx/~$workspace.props"));
	}

	[Fact(DisplayName = "sync: the dotnet module gives its repos _fx/dotnet.yaml and the folder _fx/dotnet/ whole, in _fx/.workspace/")]
	public void Sync_ModulePropagation()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("docs", "mandatory"));
		ws.WriteManifest(("plus", "mandatory", "    tags: [dotnet]\n"), ("docs", "mandatory", ""));
		File.AppendAllText(Path.Combine(ws.Root, "_fx", "workspace.yaml"), "modules:\n  dotnet:\n    tags: [dotnet]\n");
		ws.Meta.WriteFile(Path.Combine("_fx", "dotnet.yaml"), "imports:\n  - props: dotnet/default.props\n");
		ws.Meta.WriteFile(Path.Combine("_fx", "dotnet", "default.props"), "<Project />\n");
		ws.Meta.WriteFile(Path.Combine("_fx", "dotnet", "unused.targets"), "<Project />\n");
		var icon = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF };
		Directory.CreateDirectory(Path.Combine(ws.Root, "_fx", "dotnet", "assets"));
		File.WriteAllBytes(Path.Combine(ws.Root, "_fx", "dotnet", "assets", "icon.png"), icon);
		Assert.DoesNotContain(WorkspaceModule.Sync(ws.Root, new(), cancellationToken: TestContext.Current.CancellationToken).Diagnostics, d => d.Severity == FxSeverity.Error);

		// the folder whole, binaries byte for byte; the yaml as it is
		var copy = Path.Combine(ws.RepoPath("plus"), "_fx", ".workspace");
		Assert.Equal("imports:\n  - props: dotnet/default.props\n", ws.Read("plus/repo/_fx/.workspace/dotnet.yaml"));
		Assert.True(File.Exists(Path.Combine(copy, "dotnet", "unused.targets")));
		Assert.Equal(icon, File.ReadAllBytes(Path.Combine(copy, "dotnet", "assets", "icon.png")));
		var repository = ws.Read("plus/repo/_fx/.workspace/repository.yaml");
		Assert.Contains("name: plus\ntags: [dotnet]\n", repository);
		Assert.Contains("  - dotnet/assets/icon.png\n", repository);
		Assert.Equal(["dotnet"], ModulePropagation.RepositoryTags(ws.RepoPath("plus")));
		Assert.Contains("$(MSBuildThisFileDirectory).workspace/dotnet/default.props", ws.Read("plus/repo/_fx/dotnet.g.props"));
		Assert.False(Directory.Exists(Path.Combine(ws.RepoPath("docs"), "_fx"))); // no dotnet tag
		Assert.DoesNotContain(WorkspaceModule.Doctor(ws.Root, offline: true, cancellationToken: TestContext.Current.CancellationToken).Diagnostics, d => d.Code == WorkspaceModule.Outdated);

		// a change in the workspace is outdated in the repo until the next sync; what goes, goes from the repo
		ws.Meta.WriteFile(Path.Combine("_fx", "dotnet", "default.props"), "<Project><PropertyGroup><X>1</X></PropertyGroup></Project>\n");
		Assert.Contains(WorkspaceModule.Doctor(ws.Root, offline: true, cancellationToken: TestContext.Current.CancellationToken).Diagnostics,
			d => d.Code == WorkspaceModule.Outdated && d.File == "plus/repo/_fx/.workspace/dotnet/default.props");
		File.Delete(Path.Combine(ws.Root, "_fx", "dotnet", "unused.targets"));
		WorkspaceModule.Sync(ws.Root, new(), cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(File.Exists(Path.Combine(copy, "dotnet", "unused.targets")));
		Assert.Contains("<X>1</X>", ws.Read("plus/repo/_fx/.workspace/dotnet/default.props"));

		// a repo that stops using the module loses its copy
		ws.WriteManifest(("plus", "mandatory", "    tags: [other]\n"), ("docs", "mandatory", ""));
		File.AppendAllText(Path.Combine(ws.Root, "_fx", "workspace.yaml"), "modules:\n  dotnet:\n    tags: [dotnet]\n");
		WorkspaceModule.Sync(ws.Root, new(), cancellationToken: TestContext.Current.CancellationToken);
		Assert.Empty(Directory.EnumerateFiles(copy, "*", SearchOption.AllDirectories));
	}

	[Fact(DisplayName = "manifest: modules, with their tags; an unknown module is an error")]
	public void Manifest_Modules()
	{
		var manifest = WorkspaceManifest.Read("root", "version: 1\nrepositories: []\nmodules:\n  dotnet:\n    tags: [dotnet, windows]\n", strict: true);
		Assert.Equal(["dotnet", "windows"], manifest.Module("dotnet")!.Tags);
		var ex = Assert.Throws<WorkspaceManifestException>(() => WorkspaceManifest.Read("root", "version: 1\nmodules:\n  angular: {}\n", strict: true));
		Assert.Contains("Unknown module 'angular'", ex.Diagnostics[0].Message.English);
	}

	[Fact(DisplayName = "doctor: a standalone repo cannot reference a project of a repo that is not (§6)")]
	public void Doctor_StandaloneReference()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("next", "mandatory"));
		ws.WriteManifest(("plus", "mandatory", "    standalone: true\n"), ("next", "mandatory", ""));
		WorkspaceModule.Sync(ws.Root, new(), cancellationToken: TestContext.Current.CancellationToken);
		File.WriteAllText(Path.Combine(ws.RepoPath("plus"), "uses.props"), """
			<Project>
			  <ItemGroup>
			    <FxReference Include="next" />
			    <FxReference Include="NotMounted;plus" />
			    <FxReference Include="next" WorkspaceOnly="true" />
			  </ItemGroup>
			</Project>
			""");
		var diagnostic = Assert.Single(WorkspaceModule.Doctor(ws.Root, offline: true, cancellationToken: TestContext.Current.CancellationToken).Diagnostics, d => d.Code == WorkspaceModule.StandaloneReference);
		Assert.Contains("references next of 'next'", diagnostic.Message.English);
		Assert.Equal("plus/repo/uses.props", diagnostic.File);
		Assert.Equal(3, diagnostic.Line);
	}

	[Fact(DisplayName = "fx repo unmount: fx stops managing it, nothing is deleted; mount brings it back")]
	public void MountUnmount()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "manual"));
		var manifest = WorkspaceManifest.Load(ws.Root);
		Assert.Empty(WorkspaceModule.SetMount(manifest, ["lab"], mounted: true));
		Assert.Contains(new RepositoryAction("lab", "cloned"), WorkspaceModule.Sync(ws.Root, new(), cancellationToken: TestContext.Current.CancellationToken).Actions);
		Assert.Empty(WorkspaceModule.SetMount(manifest, ["lab"], mounted: false));
		WorkspaceModule.Sync(ws.Root, new(), cancellationToken: TestContext.Current.CancellationToken);
		Assert.True(Directory.Exists(ws.RepoPath("lab")));
		Assert.DoesNotContain("lab/repo", ws.Read("Fuxion.slnx"));
		Assert.Single(WorkspaceModule.SetMount(manifest, ["plus"], mounted: false));
		Assert.Single(WorkspaceModule.SetMount(manifest, ["nope"], mounted: true));
	}

	// Doctor

	[Fact(DisplayName = "doctor: all right after a sync")]
	public void Doctor_AllRight()
	{
		using var ws = new Workspace(("plus", "mandatory"));
		WorkspaceModule.Sync(ws.Root, new(), cancellationToken: TestContext.Current.CancellationToken);
		Assert.Empty(WorkspaceModule.Doctor(ws.Root, offline: false, cancellationToken: TestContext.Current.CancellationToken).Diagnostics);
	}

	[Fact(DisplayName = "doctor: changes, commits not pushed, detached HEAD, orphans, origin and outdated files")]
	public void Doctor_Problems()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "mandatory"));
		WorkspaceModule.Sync(ws.Root, new(), cancellationToken: TestContext.Current.CancellationToken);
		var plus = new TempGitRepository(init: false, path: ws.RepoPath("plus"));
		plus.Commit("local");
		plus.WriteFile("new.txt", "x");
		var lab = new TempGitRepository(init: false, path: ws.RepoPath("lab"));
		lab.Checkout(lab.Head());
		Directory.CreateDirectory(Path.Combine(ws.Root, "stray", "repo", ".git"));
		ws.WriteManifest(("plus", "mandatory", ""), ("lab", "mandatory", "    tags: [x]\n"));
		File.AppendAllText(Path.Combine(ws.Root, "_fx", "workspace.yaml"), "");
		ws.Meta.Git("-C", ws.RepoPath("plus"), "remote", "set-url", "origin", "https://example.invalid/other.git");

		var codes = Codes(WorkspaceModule.Doctor(ws.Root, offline: true, cancellationToken: TestContext.Current.CancellationToken)).ToList();
		Assert.Contains(WorkspaceModule.Dirty, codes);
		Assert.Contains(WorkspaceModule.Unpushed, codes);
		Assert.Contains(WorkspaceModule.Detached, codes);
		Assert.Contains(WorkspaceModule.Orphan, codes);
		Assert.Contains(WorkspaceModule.OriginMismatch, codes);
	}

	[Fact(DisplayName = "doctor: a mounted repo the metarepo does not ignore is an error")]
	public void Doctor_NotIgnored()
	{
		using var ws = new Workspace(("plus", "mandatory"));
		WorkspaceModule.Sync(ws.Root, new(), cancellationToken: TestContext.Current.CancellationToken);
		File.WriteAllText(Path.Combine(ws.Root, ".gitignore"), "# GENERATED by fx\n!/everything\n");
		var result = WorkspaceModule.Doctor(ws.Root, offline: true, cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(result.Diagnostics, d => d.Code == WorkspaceModule.NotIgnored && d.Severity == FxSeverity.Error);
		Assert.Contains(result.Diagnostics, d => d.Code == WorkspaceModule.Outdated && d.File == ".gitignore");
	}

	// fx repo pull

	[Fact(DisplayName = "fx repo pull: fast-forward; a repo with changes is left alone")]
	public void Pull()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "mandatory"));
		WorkspaceModule.Sync(ws.Root, new(), cancellationToken: TestContext.Current.CancellationToken);
		ws.Origins["plus"].Commit("c2");
		ws.Origins["lab"].Commit("c2");
		new TempGitRepository(init: false, path: ws.RepoPath("lab")).WriteFile("dirty.txt", "x");
		var actions = WorkspaceModule.Pull(WorkspaceManifest.Load(ws.Root), [], cancellationToken: TestContext.Current.CancellationToken).Actions;
		Assert.Contains(new RepositoryAction("plus", "pulled"), actions);
		Assert.Contains(actions, a => a.Repository == "lab" && a.Action == "skipped");
		Assert.Equal(ws.Origins["plus"].Head(), new TempGitRepository(init: false, path: ws.RepoPath("plus")).Head());
	}

	// CLI

	[Fact(DisplayName = "fx sync in the metarepo runs the workspace module; -w from inside a repo too")]
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
}
