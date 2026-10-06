using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Fuxion.Tools.Cli;
using Fuxion.Tools.Core.Diagnostics;
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

	static IEnumerable<string> Codes(WorkspaceResult result) => result.Diagnostics.Select(d => d.Code);

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
		Assert.Contains(ex.Diagnostics, d => d.Code == WorkspaceManifest.InvalidManifest && d.Message.Contains(message) && d.Line is > 0);
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

	// Sync

	[Fact(DisplayName = "sync: clones the mandatory repos, generates .gitignore and Fuxion.slnx, saves the state")]
	public void Sync()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "manual"));
		var result = WorkspaceModule.Sync(ws.Root, new());
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
		Assert.Contains("<FuxionWorkspaceProject Include=\"plus\" Repository=\"plus\"", map);
		Assert.Contains("<ProjectReference Include=\"@(FuxionReference->WithMetadataValue('Identity', 'plus')->'", map);
		var again = WorkspaceModule.Sync(ws.Root, new());
		Assert.All(again.Files, f => Assert.Equal(SyncFileStatus.Unchanged, f.Status));
	}

	[Fact(DisplayName = "sync: fetches the mounted repos (online), never pulls")]
	public void Sync_Fetch()
	{
		using var ws = new Workspace(("plus", "mandatory"));
		WorkspaceModule.Sync(ws.Root, new());
		ws.Origins["plus"].Commit("c2");
		var result = WorkspaceModule.Sync(ws.Root, new());
		Assert.Contains(new RepositoryAction("plus", "fetched"), result.Actions);
		var status = WorkspaceModule.Repositories(WorkspaceManifest.Load(ws.Root), withStatus: true).Single().Status!;
		Assert.Equal(1, status.Behind);
	}

	[Fact(DisplayName = "sync --dry-run: no clone, no file written; the missing repo is an error")]
	public void Sync_DryRun()
	{
		using var ws = new Workspace(("plus", "mandatory"));
		var result = WorkspaceModule.Sync(ws.Root, new(DryRun: true, Offline: true));
		Assert.Contains(WorkspaceModule.Missing, Codes(result));
		Assert.False(Directory.Exists(ws.RepoPath("plus")));
		Assert.False(File.Exists(Path.Combine(ws.Root, ".gitignore")));
	}

	[Fact(DisplayName = "a .gitignore written by hand is not touched without --adopt")]
	public void Adopt()
	{
		using var ws = new Workspace(("plus", "mandatory"));
		ws.Meta.WriteFile(".gitignore", "/*\n!/_fx/\n");
		var refused = WorkspaceModule.Sync(ws.Root, new());
		Assert.Contains(refused.Files, f => f.Path == ".gitignore" && f.Status == SyncFileStatus.Refused);
		Assert.Contains(WorkspaceModule.NotGenerated, Codes(refused));
		Assert.Equal("/*\n!/_fx/\n", ws.Read(".gitignore"));

		var adopted = WorkspaceModule.Sync(ws.Root, new(Adopt: true));
		Assert.Contains(adopted.Files, f => f.Path == ".gitignore" && f.Status == SyncFileStatus.Written);
		Assert.StartsWith("# GENERATED by fx", ws.Read(".gitignore"));
	}

	[Fact(DisplayName = "Fuxion.slnx: solutionExclude leaves projects out; a project two repos bring appears once")]
	public void Solution_ExcludeAndDedupe()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("tools", "mandatory"));
		ws.WriteManifest(("plus", "mandatory", "    solutionExclude: [solution/tools]\n"), ("tools", "mandatory", ""));
		Assert.Empty(WorkspaceModule.Sync(ws.Root, new()).Diagnostics);
		var solution = ws.Read("Fuxion.slnx");
		Assert.DoesNotContain("plus/repo/solution/tools", solution);
		Assert.Contains("tools/repo/solution/tools/Tool/Tool.csproj", solution);
		Assert.DoesNotContain("<Folder Name=\"/plus/tools/\"", solution);
		Assert.Contains("<Folder Name=\"/tools/tools/\">", solution);
		Assert.Contains("<FuxionWorkspaceProject Include=\"Tool\" Repository=\"tools\"", ws.Read("_fx/~$workspace.props"));
	}

	[Fact(DisplayName = "doctor: a standalone repo cannot reference a project of a repo that is not (§6)")]
	public void Doctor_StandaloneReference()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("next", "mandatory"));
		ws.WriteManifest(("plus", "mandatory", "    standalone: true\n"), ("next", "mandatory", ""));
		WorkspaceModule.Sync(ws.Root, new());
		File.WriteAllText(Path.Combine(ws.RepoPath("plus"), "uses.props"), """
			<Project>
			  <ItemGroup>
			    <FuxionReference Include="next" />
			    <FuxionReference Include="NotMounted;plus" />
			    <FuxionReference Include="next" Package="false" />
			  </ItemGroup>
			</Project>
			""");
		var diagnostic = Assert.Single(WorkspaceModule.Doctor(ws.Root, offline: true).Diagnostics, d => d.Code == WorkspaceModule.StandaloneReference);
		Assert.Contains("references next of 'next'", diagnostic.Message);
		Assert.Equal("plus/repo/uses.props", diagnostic.File);
		Assert.Equal(3, diagnostic.Line);
	}

	[Fact(DisplayName = "fx repo unmount: fx stops managing it, nothing is deleted; mount brings it back")]
	public void MountUnmount()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "manual"));
		var manifest = WorkspaceManifest.Load(ws.Root);
		Assert.Empty(WorkspaceModule.SetMount(manifest, ["lab"], mounted: true));
		Assert.Contains(new RepositoryAction("lab", "cloned"), WorkspaceModule.Sync(ws.Root, new()).Actions);
		Assert.Empty(WorkspaceModule.SetMount(manifest, ["lab"], mounted: false));
		WorkspaceModule.Sync(ws.Root, new());
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
		WorkspaceModule.Sync(ws.Root, new());
		Assert.Empty(WorkspaceModule.Doctor(ws.Root, offline: false).Diagnostics);
	}

	[Fact(DisplayName = "doctor: changes, commits not pushed, detached HEAD, orphans, origin and outdated files")]
	public void Doctor_Problems()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "mandatory"));
		WorkspaceModule.Sync(ws.Root, new());
		var plus = new TempGitRepository(init: false, path: ws.RepoPath("plus"));
		plus.Commit("local");
		plus.WriteFile("new.txt", "x");
		var lab = new TempGitRepository(init: false, path: ws.RepoPath("lab"));
		lab.Checkout(lab.Head());
		Directory.CreateDirectory(Path.Combine(ws.Root, "stray", "repo", ".git"));
		ws.WriteManifest(("plus", "mandatory", ""), ("lab", "mandatory", "    tags: [x]\n"));
		File.AppendAllText(Path.Combine(ws.Root, "_fx", "workspace.yaml"), "");
		ws.Meta.Git("-C", ws.RepoPath("plus"), "remote", "set-url", "origin", "https://example.invalid/other.git");

		var codes = Codes(WorkspaceModule.Doctor(ws.Root, offline: true)).ToList();
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
		WorkspaceModule.Sync(ws.Root, new());
		File.WriteAllText(Path.Combine(ws.Root, ".gitignore"), "# GENERATED by fx\n!/everything\n");
		var result = WorkspaceModule.Doctor(ws.Root, offline: true);
		Assert.Contains(result.Diagnostics, d => d.Code == WorkspaceModule.NotIgnored && d.Severity == FxSeverity.Error);
		Assert.Contains(result.Diagnostics, d => d.Code == WorkspaceModule.Outdated && d.File == ".gitignore");
	}

	// fx repo pull

	[Fact(DisplayName = "fx repo pull: fast-forward; a repo with changes is left alone")]
	public void Pull()
	{
		using var ws = new Workspace(("plus", "mandatory"), ("lab", "mandatory"));
		WorkspaceModule.Sync(ws.Root, new());
		ws.Origins["plus"].Commit("c2");
		ws.Origins["lab"].Commit("c2");
		new TempGitRepository(init: false, path: ws.RepoPath("lab")).WriteFile("dirty.txt", "x");
		var actions = WorkspaceModule.Pull(WorkspaceManifest.Load(ws.Root), []);
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
		Assert.True(JsonDocument.Parse(stdout.ToString()).RootElement.TryGetProperty("workspace", out _));
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
