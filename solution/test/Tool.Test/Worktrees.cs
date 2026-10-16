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

namespace Fuxion.Tool.Test;

/// <summary>What the tests of the worktrees share.</summary>
static class Worktrees
{
	internal static System.Threading.CancellationToken Token => TestContext.Current.CancellationToken;

	internal static WorktreeResult Ok(WorktreeResult result)
	{
		var errors = result.AllDiagnostics.Where(d => d.Severity == FxSeverity.Error).ToList();
		Assert.True(errors.Count == 0, string.Join("; ", errors.Select(d => d.Where)));
		return result;
	}

	internal static string? Branch(string path) => GitClient.Discover(path)!.CurrentBranch();

	/// <summary>A worktree: its <c>.git</c> is a file.</summary>
	internal static bool IsWorktree(string path) => File.Exists(Path.Combine(path, ".git"));

	internal static string[] Paths(string root) => WorktreeCard.Load(root)!.Worktrees.Select(w => w.Path).ToArray();
}
