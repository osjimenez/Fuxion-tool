using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Fuxion.Tool.Cli;
using Fuxion.Tool.Core.Git;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Sync;
using Fuxion.Tool.Core.Worktree;
using Xunit;
using static Fuxion.Tool.Test.Worktrees;

namespace Fuxion.Tool.Test;

/// <summary>The worktrees of fx (plan P, P7), on the sample tree: unmounting and removing them, the doctor and the command line.</summary>
public sealed class WorktreeRemoveTest : SampleTree
{
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
		Assert.False(Directory.Exists(Path.Combine(acme.Path, "_fx", "~$worktrees")));
		Assert.Empty(acme.Git("branch", "--list", "feature/x").Trim());
		Assert.Empty(acme.Git("-C", Path.Combine(acme.Path, "libs", "shared"), "branch", "--list", "feature/x").Trim());
		Assert.Empty(WorktreeModule.List(acme.Path));
		Assert.Contains(WorktreeModule.Remove(acme.Path, "feature/x", cancellationToken: Token).Diagnostics, d => d.Code == WorktreeModule.NotFound);
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
		Assert.Equal(0, InProcess.Run(["worktree", "add", "fix/y", "--output", "json"], stdout, new StringWriter(), Path.Combine(acme.Path, "libs", "shared")));
		var json = JsonDocument.Parse(stdout.ToString()).RootElement;
		Assert.Equal("fx-worktree-add/1", json.GetProperty("schema").GetString());
		Assert.True(json.GetProperty("ok").GetBoolean());
		Assert.Equal(["acme", "shared"], json.GetProperty("repositories").EnumerateArray().Select(r => r.GetProperty("name").GetString()));
		Assert.EndsWith("fix-y", json.GetProperty("path").GetString()!.TrimEnd('\\', '/'));

		stdout = new StringWriter();
		Assert.Equal(0, InProcess.Run(["worktree", "list", "--output", "json"], stdout, new StringWriter(), acme.Path));
		var worktree = Assert.Single(JsonDocument.Parse(stdout.ToString()).RootElement.GetProperty("worktrees").EnumerateArray());
		Assert.Equal("fix/y", worktree.GetProperty("branch").GetString());
		Assert.Equal(["_docs/"], worktree.GetProperty("leftOut").EnumerateArray().Select(p => p.GetString()));
		Assert.Equal(0, worktree.GetProperty("repositories")[1].GetProperty("changes").GetInt32());

		stdout = new StringWriter();
		var stderr = new StringWriter();
		var listed = InProcess.Run(["worktree", "list"], stdout, stderr, acme.Path);
		Assert.True(listed == 0, $"exit {listed}: {stderr} | {stdout}");
		Assert.Contains("fix/y", stdout.ToString());
		stdout = new StringWriter();
		Assert.Equal(0, InProcess.Run(["worktree", "remove", "fix/y"], stdout, new StringWriter(), acme.Path));
		Assert.Contains("Worktree 'fix/y' removed.", stdout.ToString());
	}
}
