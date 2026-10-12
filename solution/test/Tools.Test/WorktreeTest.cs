using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Fuxion.Tools.Cli;
using Fuxion.Tools.Core.Git;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Sync;
using Fuxion.Tools.Core.Worktree;
using Xunit;

namespace Fuxion.Tools.Test;

/// <summary>The worktrees of fx (plan P, P7), on the sample tree: the three of the plan (feature-x, fix-shared, fix-z), and removing them.</summary>
public sealed class WorktreeTest : SampleTree
{
	static System.Threading.CancellationToken Token => TestContext.Current.CancellationToken;

	static WorktreeResult Ok(WorktreeResult result)
	{
		var errors = result.AllDiagnostics.Where(d => d.Severity == FxSeverity.Error).ToList();
		Assert.True(errors.Count == 0, string.Join("; ", errors.Select(d => d.Where)));
		return result;
	}

	static string? Branch(string path) => GitClient.Discover(path)!.CurrentBranch();

	/// <summary>A worktree: its <c>.git</c> is a file.</summary>
	static bool IsWorktree(string path) => File.Exists(Path.Combine(path, ".git"));

	static readonly string[] Repositories = [".", "repo-w1", "repo-w1/repo-c1", "repo-w1/repo-c2", "repo-w2", "repo-w2/apps/c3", "libs/shared"];

	/// <summary>acme synced, and what sync wrote committed in every repository, as each team would have it.</summary>
	TempGitRepository AcmeCommitted()
	{
		var acme = Acme();
		foreach (var path in Repositories)
		{
			var full = Path.Combine(acme.Path, path);
			acme.Git("-C", full, "add", "-A");
			acme.Git("-C", full, "commit", "--quiet", "--allow-empty", "-m", "synced");
		}
		return acme;
	}

	static string[] Paths(string root) => WorktreeCard.Load(root)!.Worktrees.Select(w => w.Path).ToArray();

	[Fact(DisplayName = "add --mandatory in the root: the workspace in worktree mode (sparse), its mandatory repositories at every level, the highest copy; the main tree untouched")]
	public void Add_Mandatory()
	{
		var acme = AcmeCommitted();
		var result = Ok(WorktreeModule.Add(acme.Path, "feature/x", null, mandatory: true, cancellationToken: Token));
		var root = Path.Combine(acme.Path, "_fx", "~$worktrees", "feature-x");
		Assert.True(FxLayout.SameFolder(root, result.Root!));
		Assert.Equal("feature/x", Branch(root));
		Assert.True(File.Exists(Path.Combine(root, "Acme.slnx")));
		Assert.False(Directory.Exists(Path.Combine(root, "_docs")));
		Assert.True(File.Exists(Path.Combine(acme.Path, "_docs", "guide.md")));
		Assert.Equal("main", Branch(acme.Path));
		foreach (var path in Repositories.Skip(1))
		{
			Assert.True(IsWorktree(Path.Combine(root, path)), path);
			Assert.Equal("feature/x", Branch(Path.Combine(root, path)));
		}
		// repo-w2's guest shared: acme's is higher
		Assert.False(Directory.Exists(Path.Combine(root, "repo-w2", "libs", "shared")));
		Assert.Contains(result.Actions, a => a is { Repository: "shared", Action: "superseded" });
		var card = WorktreeCard.Load(root)!;
		Assert.Equal(("feature/x", "acme"), (card.Branch, card.From));
		Assert.Equal(["_docs/"], card.LeftOut);
		Assert.Equal([".", "repo-w1", "repo-w2", "libs/shared", "repo-w1/repo-c1", "repo-w1/repo-c2", "repo-w2/apps/c3"], Paths(root));
		Assert.All(card.Worktrees, w => Assert.True(w.CreatedBranch));

		// a closed tree: its own map, with its own projects
		var map = Read(root, "_fx/dotnet/~$workspace.props");
		Assert.Contains(Project(root, "libs", "shared", "Shared.csproj"), map);
		Assert.DoesNotContain(Project(acme.Path, "libs", "shared", "Shared.csproj"), map);
		// fx sync inside: nothing to clone, nothing missing; the doctor, all right; the main tree, as it was
		var sync = Sync(root);
		Assert.Empty(Errors(sync));
		Assert.DoesNotContain(sync.SelectMany(r => r.Actions), a => a.Action is "cloned" or "missing" or "parked");
		Assert.Empty(Errors(SyncEngine.Doctor(root, new(Offline: true), cancellationToken: Token)));
		Assert.DoesNotContain(SyncEngine.Doctor(acme.Path, new(Offline: true), cancellationToken: Token).SelectMany(r => r.Diagnostics),
			d => d.Severity == FxSeverity.Error || d.Code == WorktreeModule.Outside);
		Assert.Contains(Project(acme.Path, "libs", "shared", "Shared.csproj"), Read(acme.Path, "_fx/dotnet/~$workspace.props"));
	}

	[Fact(DisplayName = "add from a repository: its workspace in worktree mode with that repository alone; mount brings more, with the workspaces on the way, --mandatory, the paths left out")]
	public void Add_FromRepository_Mount()
	{
		var acme = AcmeCommitted();
		var shared = Path.Combine(acme.Path, "libs", "shared");
		Ok(WorktreeModule.Add(shared, "fix/shared", null, false, cancellationToken: Token));
		var root = Path.Combine(acme.Path, "_fx", "~$worktrees", "fix-shared");
		Assert.Equal([".", "libs/shared"], Paths(root));
		Assert.Equal("shared", WorktreeCard.Load(root)!.From);
		Assert.False(Directory.Exists(Path.Combine(root, "repo-w1")));

		// repo-c1 alone: repo-w1, that declares it, comes with it
		Ok(WorktreeModule.Mount(acme.Path, "fix/shared", ["repo-c1"], false, cancellationToken: Token));
		Assert.True(IsWorktree(Path.Combine(root, "repo-w1", "repo-c1")));
		Assert.False(Directory.Exists(Path.Combine(root, "repo-w1", "repo-c2")));
		// repo-w2 and its mandatory ones (from anywhere in the tree); its guest shared is lower than acme's
		Ok(WorktreeModule.Mount(shared, "fix/shared", ["repo-w2"], true, cancellationToken: Token));
		Assert.True(IsWorktree(Path.Combine(root, "repo-w2", "apps", "c3")));
		Assert.False(Directory.Exists(Path.Combine(root, "repo-w2", "libs", "shared")));
		// a path left out, brought back
		Ok(WorktreeModule.Mount(acme.Path, "fix/shared", ["_docs/"], false, cancellationToken: Token));
		Assert.Equal("guide\n", Read(root, "_docs/guide.md"));
		Assert.Empty(WorktreeCard.Load(root)!.LeftOut);
		// the code sees this worktree
		var map = Read(root, "_fx/dotnet/~$workspace.props");
		Assert.Contains(Project(root, "libs", "shared", "Shared.csproj"), map);
		Assert.Contains(Project(root, "repo-w1", "repo-c1", "src", "App", "C1.App.csproj"), map);
		Assert.DoesNotContain("C2.Lib", map);

		Assert.Contains(WorktreeModule.Mount(acme.Path, "fix/shared", ["nope"], false, cancellationToken: Token).Diagnostics, d => d.Code == WorktreeModule.UnknownTarget);
		Assert.Contains(WorktreeModule.Mount(acme.Path, "nope", ["repo-c1"], false, cancellationToken: Token).Diagnostics, d => d.Code == WorktreeModule.NotFound);
	}

	[Fact(DisplayName = "add from a repository of an intermediate workspace: the worktree goes there; its root inherits from the main tree, the rest inside; --in another one above")]
	public void Add_InIntermediateWorkspace()
	{
		var acme = AcmeCommitted();
		var c1 = Path.Combine(acme.Path, "repo-w1", "repo-c1");
		Ok(WorktreeModule.Add(c1, "fix/z", null, false, cancellationToken: Token));
		var root = Path.Combine(acme.Path, "repo-w1", "_fx", "~$worktrees", "fix-z");
		Assert.Equal([".", "repo-c1"], Paths(root));
		Assert.Equal(("repo-c1", "repo-w1"), (WorktreeCard.Load(root)!.From, WorktreeCard.Load(root)!.Worktrees[0].Repository));
		Assert.Equal("/acme", FxInherited.Load(root)!.Parent.FullName);
		Assert.Equal("/acme/repo-w1/repo-c1", FxInherited.Load(Path.Combine(root, "repo-c1"))!.Repository.FullName);

		// a change in the root of the worktree reaches its repo-c1, not the main tree's
		Append(Path.Combine(root, "_fx", "dotnet", "dotnet.yaml"), "packages:\n", "packages:\n  - ids: [Polly]\n    versions:\n      - version: 8.0.0\n");
		Assert.Empty(Errors(Sync(root)));
		Assert.Contains("Polly", Read(root, "repo-c1/_fx/.inherited/dotnet/dotnet.yaml"));
		Assert.DoesNotContain("Polly", Read(c1, "_fx/.inherited/dotnet/dotnet.yaml"));
		// its map: only what is in it (shared and repo-c2, packages)
		var map = Read(root, "_fx/dotnet/~$workspace.props");
		Assert.Contains("C1.App", map);
		Assert.DoesNotContain("Shared", map);
		Assert.DoesNotContain("C2.Lib", map);

		// --in acme: the root is acme, with repo-w1 because repo-c1 hangs from it
		Ok(WorktreeModule.Add(c1, "fix/zz", "acme", false, cancellationToken: Token));
		Assert.Equal([".", "repo-w1", "repo-w1/repo-c1"], Paths(Path.Combine(acme.Path, "_fx", "~$worktrees", "fix-zz")));
		Assert.Contains(WorktreeModule.Add(c1, "fix/q", "nope", false, cancellationToken: Token).Diagnostics, d => d.Code == WorktreeModule.UnknownWorkspace);
		// inside a worktree, no other one; and a branch of a repository is in one working tree only (git's limit): fix/z of
		// repo-w1 is the root of the first worktree
		Assert.Contains(WorktreeModule.Add(Path.Combine(root, "repo-c1"), "fix/q", null, false, cancellationToken: Token).Diagnostics, d => d.Code == WorktreeModule.InsideWorktree);
		var twice = WorktreeModule.Add(c1, "fix/z", "acme", false, cancellationToken: Token);
		Assert.Equal("repo-w1", Assert.Single(twice.Diagnostics, d => d.Code == WorktreeModule.AddFailed).Repository);
	}

	[Fact(DisplayName = "unmount and remove: not with uncommitted changes; the branches fx created go if git deletes them safely, one with new commits stays")]
	public void Remove()
	{
		var acme = AcmeCommitted();
		var root = Ok(WorktreeModule.Add(acme.Path, "feature/x", null, true, cancellationToken: Token)).Root!;
		var c1 = Path.Combine(root, "repo-w1", "repo-c1");
		File.WriteAllText(Path.Combine(c1, "work.txt"), "work\n");

		// repo-w1 and what is inside it: not with changes
		var dirty = WorktreeModule.Unmount(acme.Path, "feature/x", ["repo-w1"], cancellationToken: Token);
		Assert.Equal("repo-c1", Assert.Single(dirty.Diagnostics, d => d.Code == WorktreeModule.Dirty).Repository);
		Assert.True(IsWorktree(c1));
		// committed: repo-w1 and its repositories go; the branch of repo-c1, with a new commit, stays
		acme.Git("-C", c1, "add", "-A");
		acme.Git("-C", c1, "commit", "--quiet", "-m", "work");
		var unmount = Ok(WorktreeModule.Unmount(acme.Path, "feature/x", ["repo-w1"], cancellationToken: Token));
		Assert.False(Directory.Exists(Path.Combine(root, "repo-w1")));
		Assert.Equal("repo-c1", Assert.Single(unmount.Diagnostics, d => d.Code == WorktreeModule.BranchKept).Repository);
		Assert.NotEmpty(acme.Git("-C", Path.Combine(acme.Path, "repo-w1", "repo-c1"), "branch", "--list", "feature/x").Trim());
		Assert.Empty(acme.Git("-C", Path.Combine(acme.Path, "repo-w1"), "branch", "--list", "feature/x").Trim());
		Assert.Equal([".", "repo-w2", "libs/shared", "repo-w2/apps/c3"], Paths(root));
		Assert.Contains(WorktreeModule.Unmount(acme.Path, "feature/x", ["acme"], cancellationToken: Token).Diagnostics, d => d.Code == WorktreeModule.RootUnmount);

		// the whole worktree
		Ok(WorktreeModule.Remove(acme.Path, "feature/x", cancellationToken: Token));
		Assert.False(Directory.Exists(root));
		Assert.Empty(acme.Git("branch", "--list", "feature/x").Trim());
		Assert.Empty(acme.Git("-C", Path.Combine(acme.Path, "libs", "shared"), "branch", "--list", "feature/x").Trim());
		Assert.Empty(WorktreeModule.List(acme.Path));
		Assert.Contains(WorktreeModule.Remove(acme.Path, "feature/x", cancellationToken: Token).Diagnostics, d => d.Code == WorktreeModule.NotFound);
	}

	[Fact(DisplayName = "the branch: the local one if there is one; if not, one that follows origin; if not, a new one from HEAD")]
	public void Branches()
	{
		var acme = AcmeCommitted();
		var shared = Path.Combine(acme.Path, "libs", "shared");
		// fix/local exists in shared; fix/remote, only in its origin
		acme.Git("-C", shared, "branch", "fix/local");
		acme.Git("-C", shared, "push", "--quiet", "origin", "HEAD:refs/heads/fix/remote");
		acme.Git("-C", shared, "fetch", "--quiet", "origin");
		Ok(WorktreeModule.Add(shared, "fix/local", null, false, cancellationToken: Token));
		Assert.False(WorktreeCard.Load(Path.Combine(acme.Path, "_fx", "~$worktrees", "fix-local"))!.Worktrees[1].CreatedBranch);
		Ok(WorktreeModule.Add(shared, "fix/remote", null, false, cancellationToken: Token));
		var remote = Path.Combine(acme.Path, "_fx", "~$worktrees", "fix-remote", "libs", "shared");
		Assert.Equal("origin/fix/remote", acme.Git("-C", remote, "rev-parse", "--abbrev-ref", "@{upstream}").Trim());
		// removed: the local branch that was there stays
		Ok(WorktreeModule.Remove(acme.Path, "fix/local", cancellationToken: Token));
		Assert.NotEmpty(acme.Git("-C", shared, "branch", "--list", "fix/local").Trim());
	}

	[Fact(DisplayName = "a repository on its own: its worktrees in the folder of the user")]
	public void Loose()
	{
		var folder = Directory.CreateTempSubdirectory("fx-worktrees-").FullName;
		var before = Environment.GetEnvironmentVariable("FX_WORKTREES");
		Environment.SetEnvironmentVariable("FX_WORKTREES", folder);
		try
		{
			var loose = Clone(_sharedOrigin);
			var root = Ok(WorktreeModule.Add(loose.Path, "fix/l", null, false, cancellationToken: Token)).Root!;
			Assert.StartsWith(FxLayout.Normalize(folder), root, StringComparison.OrdinalIgnoreCase);
			Assert.True(File.Exists(Path.Combine(root, "Shared.csproj")));
			Assert.Equal("fix/l", Assert.Single(WorktreeModule.List(loose.Path)).Card.Branch);
			Ok(WorktreeModule.Remove(loose.Path, "fix/l", cancellationToken: Token));
			Assert.False(Directory.Exists(root));
		}
		finally
		{
			Environment.SetEnvironmentVariable("FX_WORKTREES", before);
			new TempGitRepository(init: false, path: folder).Dispose();
		}
	}

	[Fact(DisplayName = "doctor: a worktree out of its place (git worktree add, an agent) warns; a lost one, info; fx's are in their place")]
	public void Doctor()
	{
		var acme = AcmeCommitted();
		var c1 = Path.Combine(acme.Path, "repo-w1", "repo-c1");
		var outside = new TempGitRepository(init: false);
		acme.Git("-C", c1, "worktree", "add", "--quiet", "-b", "agent/x", outside.Path);
		Ok(WorktreeModule.Add(c1, "fix/q", null, false, cancellationToken: Token));
		var doctor = SyncEngine.Doctor(acme.Path, new(Offline: true), cancellationToken: Token).SelectMany(r => r.Diagnostics).ToList();
		var warning = Assert.Single(doctor, d => d.Code == WorktreeModule.Outside);
		Assert.Equal("repo-c1", warning.Repository);
		Assert.Contains("agent/x", warning.Message.English);

		outside.Dispose();
		doctor = SyncEngine.Doctor(acme.Path, new(Offline: true), cancellationToken: Token).SelectMany(r => r.Diagnostics).ToList();
		Assert.DoesNotContain(doctor, d => d.Code == WorktreeModule.Outside);
		Assert.Contains("worktree prune", Assert.Single(doctor, d => d.Code == WorktreeModule.Prunable).Fix!.Command);
	}

	[Fact(DisplayName = "fx worktree add|list|remove: fx-worktree-add/1, fx-worktree-list/1; in a terminal, what happened and where")]
	public void Cli()
	{
		var acme = AcmeCommitted();
		var stdout = new StringWriter();
		Assert.Equal(0, FxApp.Run(["worktree", "add", "fix/y", "--output", "json"], stdout, new StringWriter(), Path.Combine(acme.Path, "libs", "shared")));
		var json = JsonDocument.Parse(stdout.ToString()).RootElement;
		Assert.Equal("fx-worktree-add/1", json.GetProperty("schema").GetString());
		Assert.True(json.GetProperty("ok").GetBoolean());
		Assert.Equal(["acme", "shared"], json.GetProperty("repositories").EnumerateArray().Select(r => r.GetProperty("name").GetString()));
		Assert.EndsWith("fix-y", json.GetProperty("path").GetString()!.TrimEnd('\\', '/'));

		stdout = new StringWriter();
		Assert.Equal(0, FxApp.Run(["worktree", "list", "--output", "json"], stdout, new StringWriter(), acme.Path));
		var worktree = Assert.Single(JsonDocument.Parse(stdout.ToString()).RootElement.GetProperty("worktrees").EnumerateArray());
		Assert.Equal("fix/y", worktree.GetProperty("branch").GetString());
		Assert.Equal(["_docs/"], worktree.GetProperty("leftOut").EnumerateArray().Select(p => p.GetString()));
		Assert.Equal(0, worktree.GetProperty("repositories")[1].GetProperty("changes").GetInt32());

		stdout = new StringWriter();
		Assert.Equal(0, FxApp.Run(["worktree", "list"], stdout, new StringWriter(), acme.Path));
		Assert.Contains("fix/y", stdout.ToString());
		stdout = new StringWriter();
		Assert.Equal(0, FxApp.Run(["worktree", "remove", "fix/y"], stdout, new StringWriter(), acme.Path));
		Assert.Contains("Worktree 'fix/y' removed.", stdout.ToString());
	}
}
