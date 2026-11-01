using System;
using System.Net;
using Fuxion.Tool.Core.Git;
using Fuxion.Tool.Core.Processes;
using Fuxion.Tool.Core.Results;
using Xunit;

namespace Fuxion.Tool.Test;

/// <summary>
/// The processes and git on the responses (plan <i>fx sobre Responses</i>, phase 3): a process that does not start or
/// does not end in time is an error; its exit code, data; git that fails, an error of what it was asked.
/// </summary>
public sealed class ProcessRunnerTest
{
	[Fact(DisplayName = "a process that does not start: unavailable, with what was run and where")]
	public void NotStarted()
	{
		var run = ProcessRunner.Run("fx-no-such-program", TempGitRepository.TempRoot, [], TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
		Assert.True(run is Error { Type: HttpStatusCode.ServiceUnavailable });
		var diagnostic = Assert.Single(run.Diagnostics);
		Assert.Equal(ProcessRunner.NotStarted, diagnostic.Code);
		Assert.Contains("fx-no-such-program", diagnostic.Message.English);
		Assert.Equal(-1, ProcessResult.Of(run).ExitCode);
	}

	[Fact(DisplayName = "a process that does not end in time: a timeout; one that ends with an error, its exit code as data")]
	public void TimedOut_Or_ExitCode()
	{
		var slow = ProcessRunner.Run("git", TempGitRepository.TempRoot, ["--version"], TimeSpan.Zero, cancellationToken: TestContext.Current.CancellationToken);
		Assert.True(slow is Error { Type: HttpStatusCode.RequestTimeout });
		Assert.Equal(ProcessRunner.TimedOut, Assert.Single(slow.Diagnostics).Code);

		var failed = ProcessRunner.Run("git", TempGitRepository.TempRoot, ["no-such-command"], TimeSpan.FromSeconds(30), cancellationToken: TestContext.Current.CancellationToken);
		Assert.True(failed is ProcessResult { ExitCode: not 0 });
	}

	[Fact(DisplayName = "git that fails: an error with the command, the repository, its exit code and what it said; a query, nothing")]
	public void Git_Failed()
	{
		using var repo = new TempGitRepository();
		repo.Commit("c1");
		var git = GitClient.Discover(repo.Path)!;
		var count = git.Count("no-such-ref");
		Assert.True(count is Error { Type: HttpStatusCode.ServiceUnavailable });
		var diagnostic = Assert.Single(count.Diagnostics);
		Assert.Equal(GitClient.Failed, diagnostic.Code);
		Assert.Contains("git rev-list --count no-such-ref", diagnostic.Message.English);
		Assert.True(git.Count("HEAD") is 1);
		Assert.Null(git.MergeBase("HEAD", "no-such-ref"));
		Assert.True(git.IsAncestor("HEAD", "HEAD") is true);
		Assert.True(git.FirstCommit("HEAD") is string);
	}
}
