using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Fuxion.Tools.Core.Processes;

namespace Fuxion.Tools.Core.Git;

public sealed record GitTag(string Name, string Commit);

/// <summary>The state of a working tree: branch (null if detached), upstream, commits ahead and behind it, and changes.</summary>
public sealed record GitStatus(string? Branch, bool Detached, string? Upstream, int Ahead, int Behind, int Changes);

/// <summary>A worktree of a repository: where, its branch (none if detached) and whether git takes it for lost (its folder is gone).</summary>
public sealed record GitWorktree(string Path, string? Branch, bool Prunable);

/// <summary>
/// Reads a repository with the git executable (design D-25): the same behavior as the user's git (configuration,
/// worktrees, credentials) and no native library. Every call runs <c>git -C &lt;root&gt;</c> with a timeout.
/// </summary>
public sealed class GitClient
{
	public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(30);

	// Never prompt, and never let variables of an outer git process (a hook, for example) pick the repository.
	static readonly IReadOnlyDictionary<string, string?> Environment = new Dictionary<string, string?>
	{
		["GIT_TERMINAL_PROMPT"] = "0",
		["GIT_DIR"] = null,
		["GIT_WORK_TREE"] = null,
		["GIT_INDEX_FILE"] = null,
		["GIT_OBJECT_DIRECTORY"] = null,
		["GIT_COMMON_DIR"] = null
	};

	readonly TimeSpan _timeout;

	GitClient(string root, TimeSpan timeout)
	{
		Root = root;
		_timeout = timeout;
	}

	/// <summary>Working tree root, full path with a trailing separator.</summary>
	public string Root { get; }

	/// <summary>The repository that contains <paramref name="path"/>, or <see langword="null"/> if there is none.</summary>
	public static GitClient? Discover(string path, TimeSpan? timeout = null)
	{
		if (!Directory.Exists(path))
			return null;
		var result = Run(path, timeout ?? DefaultTimeout, "rev-parse", "--show-toplevel");
		var top = result.ExitCode == 0 ? result.StandardOutput.Trim() : "";
		if (top.Length == 0)
			return null;
		var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(top)) + Path.DirectorySeparatorChar;
		return new(root, timeout ?? DefaultTimeout);
	}

	/// <summary>Commit of HEAD, or <see langword="null"/> if the repository has no commits.</summary>
	public string? Head() => Optional("rev-parse", "--verify", "--quiet", "HEAD^{commit}");

	/// <summary>Short name of the current branch, or <see langword="null"/> if HEAD is detached.</summary>
	public string? CurrentBranch() => Optional("symbolic-ref", "--quiet", "--short", "HEAD");

	/// <summary>Commit of a local branch, or <see langword="null"/> if it does not exist.</summary>
	public string? BranchTip(string branch) => Optional("rev-parse", "--verify", "--quiet", $"refs/heads/{branch}^{{commit}}");

	/// <summary>The commit a remote-tracking branch points to (<c>origin/main</c>), if it exists.</summary>
	public string? RemoteBranchTip(string remote, string branch) => Optional("rev-parse", "--verify", "--quiet", $"refs/remotes/{remote}/{branch}^{{commit}}");

	/// <summary>Every tag that points (directly or through an annotation) to a commit.</summary>
	public IReadOnlyList<GitTag> Tags()
	{
		var output = Required("for-each-ref", "--format=%(refname:short)%09%(objecttype)%09%(objectname)%09%(*objecttype)%09%(*objectname)", "refs/tags");
		var tags = new List<GitTag>();
		foreach (var line in Lines(output))
		{
			// name, type, object and, for an annotated tag, the type and object it points to (empty otherwise)
			var fields = line.Split('\t');
			if (fields.Length < 3)
				continue;
			var commit = fields.Length >= 5 && fields[3] == "commit" ? fields[4]
				: fields[1] == "commit" ? fields[2]
				: null;
			if (commit is not null)
				tags.Add(new(fields[0], commit));
		}
		return tags;
	}

	/// <summary>Commits reachable from <paramref name="tip"/>, children before parents and otherwise newest first.</summary>
	public IReadOnlyList<string> CommitsByDate(string tip) => Lines(Required("rev-list", "--date-order", tip));

	/// <summary>Merge commits reachable from <paramref name="tip"/> with their parents, in the same order.</summary>
	public IReadOnlyList<(string Commit, string[] Parents)> MergesByDate(string tip)
		=> Lines(Required("rev-list", "--date-order", "--merges", "--parents", tip))
			.Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
			.Select(fields => (fields[0], fields[1..]))
			.ToList();

	/// <summary>Number of commits reachable from <paramref name="tip"/> and from none of <paramref name="excluded"/>.</summary>
	public int Count(string tip, params IEnumerable<string> excluded)
		=> int.Parse(Required(["rev-list", "--count", tip, .. excluded.Select(e => "^" + e)]).Trim());

	public string? MergeBase(string a, string b) => Optional("merge-base", a, b);

	/// <summary>Whether <paramref name="ancestor"/> is <paramref name="commit"/> or one of its ancestors.</summary>
	public bool IsAncestor(string ancestor, string commit)
	{
		var result = Run(Root, _timeout, "merge-base", "--is-ancestor", ancestor, commit);
		return result.ExitCode switch
		{
			0 => true,
			1 => false,
			_ => throw Failure(result, ["merge-base", "--is-ancestor", ancestor, commit])
		};
	}

	public string? RemoteUrl(string remote) => Optional("config", "--get", $"remote.{remote}.url");

	/// <summary>
	/// The files of the working tree matching the pathspecs: tracked (except those deleted and not committed yet) and
	/// untracked ones not ignored; relative to the root, with '/'.
	/// </summary>
	public IReadOnlyList<string> ListFiles(params string[] pathspecs)
		=> Required(["ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", .. pathspecs])
			.Split('\0', StringSplitOptions.RemoveEmptyEntries)
			.Distinct(StringComparer.Ordinal)
			.Where(f => File.Exists(Path.Combine(Root, f)))
			.ToList();

	/// <summary>Committer date of a commit.</summary>
	public DateTimeOffset? CommitDate(string commit)
		=> Optional("show", "-s", "--format=%cI", commit) is { } date
		   && DateTimeOffset.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
			? parsed
			: null;

	/// <summary>Branch, changes and position against the upstream (<c>git status --porcelain --branch</c>).</summary>
	public GitStatus Status()
	{
		var lines = Required("status", "--porcelain=v1", "--branch").Split('\n', StringSplitOptions.RemoveEmptyEntries);
		var header = lines.FirstOrDefault()?.TrimEnd('\r') ?? "## HEAD (no branch)";
		var detached = header.StartsWith("## HEAD (no branch)", StringComparison.Ordinal);
		string? branch = null;
		string? upstream = null;
		int ahead = 0, behind = 0;
		if (!detached && header.Length > 3)
		{
			var info = header[3..];
			var bracket = info.IndexOf(" [", StringComparison.Ordinal);
			var refs = bracket < 0 ? info : info[..bracket];
			var dots = refs.IndexOf("...", StringComparison.Ordinal);
			branch = dots < 0 ? refs.Replace("No commits yet on ", "", StringComparison.Ordinal) : refs[..dots];
			upstream = dots < 0 ? null : refs[(dots + 3)..];
			if (bracket >= 0)
			{
				var counts = info[(bracket + 2)..].TrimEnd(']');
				foreach (var part in counts.Split(", "))
					if (part.StartsWith("ahead ", StringComparison.Ordinal)) ahead = int.Parse(part[6..]);
					else if (part.StartsWith("behind ", StringComparison.Ordinal)) behind = int.Parse(part[7..]);
			}
		}
		return new(branch, detached, upstream, ahead, behind, lines.Length - 1);
	}

	/// <summary><c>git fetch --all --prune</c>, with its own timeout (the network can be slow, or not there).</summary>
	public ProcessResult Fetch(TimeSpan timeout, CancellationToken cancellationToken = default)
		=> ProcessRunner.Run("git", Root, ["-C", Root, "fetch", "--all", "--prune", "--quiet"], timeout, Environment, cancellationToken);

	/// <summary><c>git pull --ff-only</c>: never a merge, never a rebase.</summary>
	public ProcessResult PullFastForward(TimeSpan timeout, CancellationToken cancellationToken = default)
		=> ProcessRunner.Run("git", Root, ["-C", Root, "pull", "--ff-only", "--quiet"], timeout, Environment, cancellationToken);

	/// <summary>Whether this repository ignores <paramref name="relativePath"/> (<c>git check-ignore</c>).</summary>
	public bool Ignores(string relativePath)
		=> Run(Root, _timeout, "check-ignore", "--quiet", "--no-index", relativePath).ExitCode == 0;

	/// <summary>
	/// <c>git worktree repair</c>: git keeps absolute paths between a repository and its worktrees; after one of them
	/// moves, this fixes the links from the end that moved.
	/// </summary>
	public ProcessResult RepairWorktrees() => Run(Root, _timeout, "worktree", "repair");

	/// <summary><c>git clone</c> (with long paths on), run in the parent folder of <paramref name="path"/>.</summary>
	public static ProcessResult Clone(string url, string path, TimeSpan timeout, CancellationToken cancellationToken = default)
	{
		var parent = System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(path))!;
		Directory.CreateDirectory(parent);
		return ProcessRunner.Run("git", parent, ["-c", "core.longpaths=true", "clone", "--quiet", url, path], timeout, Environment, cancellationToken);
	}

	/// <summary><c>git ls-remote</c> of a URL: whether it answers, without cloning anything.</summary>
	public static ProcessResult LsRemote(string url, TimeSpan timeout, CancellationToken cancellationToken = default)
		=> ProcessRunner.Run("git", System.IO.Path.GetTempPath(), ["ls-remote", "--quiet", url, "HEAD"], timeout, Environment, cancellationToken);

	/// <summary>The branch HEAD of a remote points to (its default branch), or null if it does not answer.</summary>
	public static string? DefaultBranch(string url, TimeSpan timeout, CancellationToken cancellationToken = default)
	{
		var result = ProcessRunner.Run("git", System.IO.Path.GetTempPath(), ["ls-remote", "--symref", url, "HEAD"], timeout, Environment, cancellationToken);
		if (result.ExitCode != 0)
			return null;
		// ref: refs/heads/main	HEAD
		return Lines(result.StandardOutput).Where(l => l.StartsWith("ref: refs/heads/", StringComparison.Ordinal))
			.Select(l => l["ref: refs/heads/".Length..].Split('\t')[0])
			.FirstOrDefault();
	}

	/// <summary>
	/// A clone of one branch with only its last commit and only <paramref name="folders"/> (and the files of the root) in
	/// the working tree: to read another repository's configuration without bringing the rest.
	/// </summary>
	public static ProcessResult CloneSparse(string url, string path, string branch, IReadOnlyList<string> folders, TimeSpan timeout, CancellationToken cancellationToken = default)
	{
		var parent = System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(path))!;
		Directory.CreateDirectory(parent);
		var clone = ProcessRunner.Run("git", parent,
			["-c", "core.longpaths=true", "clone", "--quiet", "--depth", "1", "--filter=blob:none", "--sparse", "--branch", branch, url, path],
			timeout, Environment, cancellationToken);
		return clone.ExitCode != 0
			? clone
			: ProcessRunner.Run("git", path, ["-C", path, "sparse-checkout", "set", .. folders], timeout, Environment, cancellationToken);
	}

	/// <summary>The main clone of this working tree: itself, or the one a worktree comes from (its common git folder).</summary>
	public string? MainRoot()
		=> Optional("rev-parse", "--path-format=absolute", "--git-common-dir") is { } common
			? Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(common)))!) + Path.DirectorySeparatorChar
			: null;

	/// <summary>
	/// <c>git worktree add</c> of <paramref name="branch"/> at <paramref name="path"/> (plan P, decision 26), with long
	/// paths on: the local branch, if there is one; if not, a new one that follows <c>origin/&lt;branch&gt;</c>, if that
	/// exists; if not, a new one from HEAD. Whether fx created the branch. Without <paramref name="checkout"/>, nothing
	/// is checked out yet (for a sparse working tree).
	/// </summary>
	public (ProcessResult Result, bool CreatedBranch) AddWorktree(string path, string branch, bool checkout, CancellationToken cancellationToken = default)
	{
		string[] args = BranchTip(branch) is not null ? [path, branch]
			: RemoteBranchTip("origin", branch) is not null ? ["--track", "-b", branch, path, $"origin/{branch}"]
			: ["-b", branch, path];
		var created = args[0] != path;
		var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path))!;
		Directory.CreateDirectory(parent);
		var result = ProcessRunner.Run("git", Root, ["-c", "core.longpaths=true", "-C", Root, "worktree", "add", "--quiet", .. checkout ? Array.Empty<string>() : ["--no-checkout"], .. args],
			TimeSpan.FromMinutes(10), Environment, cancellationToken);
		return (result, created);
	}

	/// <summary>
	/// What of this working tree is checked out (plan P, decision 26): all but <paramref name="leftOut"/> (paths
	/// relative to it, folders ending in '/'), with sparse patterns of this working tree only; nothing left out, no
	/// sparse checkout. Then it checks out what is missing.
	/// </summary>
	public ProcessResult SparseLeaveOut(IReadOnlyList<string> leftOut, CancellationToken cancellationToken = default)
	{
		var sparse = leftOut.Count == 0
			? ProcessRunner.Run("git", Root, ["-C", Root, "sparse-checkout", "disable"], TimeSpan.FromMinutes(10), Environment, cancellationToken)
			: ProcessRunner.Run("git", Root, ["-C", Root, "sparse-checkout", "set", "--no-cone", "/*", .. leftOut.Select(p => $"!/{p}")], TimeSpan.FromMinutes(10), Environment,
				cancellationToken);
		return sparse.ExitCode != 0 ? sparse : ProcessRunner.Run("git", Root, ["-c", "core.longpaths=true", "-C", Root, "checkout", "--quiet"], TimeSpan.FromMinutes(10), Environment,
			cancellationToken);
	}

	/// <summary><c>git worktree remove</c> of the worktree at <paramref name="path"/>, from this, its main clone (only a clean one).</summary>
	public ProcessResult RemoveWorktree(string path) => Run(Root, TimeSpan.FromMinutes(2), "worktree", "remove", path);

	/// <summary><c>git worktree prune</c>: forgets the worktrees whose folder is gone.</summary>
	public ProcessResult PruneWorktrees() => Run(Root, _timeout, "worktree", "prune");

	/// <summary><c>git branch -d</c>: only a branch with nothing that is not already in HEAD or in its upstream.</summary>
	public ProcessResult DeleteBranch(string branch) => Run(Root, _timeout, "branch", "--delete", "--quiet", branch);

	/// <summary>The worktrees of this repository, its main working tree first (<c>git worktree list --porcelain</c>).</summary>
	public IReadOnlyList<GitWorktree> Worktrees()
	{
		var result = new List<GitWorktree>();
		string? path = null, branch = null;
		var prunable = false;
		foreach (var line in Required("worktree", "list", "--porcelain").Split('\n').Select(l => l.TrimEnd('\r')).Append(""))
		{
			if (line.StartsWith("worktree ", StringComparison.Ordinal))
				path = Path.GetFullPath(line[9..]);
			else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal))
				branch = line[18..];
			else if (line.StartsWith("prunable", StringComparison.Ordinal))
				prunable = true;
			else if (line.Length == 0 && path is not null)
			{
				result.Add(new(path, branch, prunable));
				(path, branch, prunable) = (null, null, false);
			}
		}
		return result;
	}

	/// <summary>The folders of a sparse working tree (cone mode: they, entire, and the files of the root).</summary>
	public ProcessResult SparseCheckout(IReadOnlyList<string> folders, TimeSpan timeout, CancellationToken cancellationToken = default)
		=> ProcessRunner.Run("git", Root, ["-C", Root, "sparse-checkout", "set", .. folders], timeout, Environment, cancellationToken);

	/// <summary>The last commit of <paramref name="branch"/> of origin, checked out (detached).</summary>
	public ProcessResult FetchCheckout(string branch, TimeSpan timeout, CancellationToken cancellationToken = default)
	{
		var fetch = ProcessRunner.Run("git", Root, ["-C", Root, "fetch", "--quiet", "--depth", "1", "origin", branch], timeout, Environment, cancellationToken);
		return fetch.ExitCode != 0
			? fetch
			: ProcessRunner.Run("git", Root, ["-C", Root, "checkout", "--quiet", "--detach", "FETCH_HEAD"], timeout, Environment, cancellationToken);
	}

	/// <summary>The last <paramref name="count"/> commits of HEAD: short hash, date and subject (for <c>fx ui</c>).</summary>
	public IReadOnlyList<string> RecentCommits(int count)
		=> Lines(Optional("log", $"-{count}", "--format=%h %ad %s", "--date=short") ?? "");

	/// <summary>Creates a lightweight tag. Local only: nothing is pushed.</summary>
	public void CreateTag(string name, string commit) => Required("tag", name, commit);

	/// <summary>The commit a reverse topological walk from <paramref name="tip"/> starts with: a root commit.</summary>
	public string? FirstCommit(string tip) => Lines(Required("rev-list", "--topo-order", "--reverse", tip)).FirstOrDefault();

	string? Optional(params string[] args)
	{
		var result = Run(Root, _timeout, args);
		var output = result.StandardOutput.Trim();
		return result.ExitCode == 0 && output.Length > 0 ? output : null;
	}

	string Required(params string[] args)
	{
		var result = Run(Root, _timeout, args);
		return result.ExitCode == 0 ? result.StandardOutput : throw Failure(result, args);
	}

	static ProcessResult Run(string directory, TimeSpan timeout, params string[] args)
		=> ProcessRunner.Run("git", directory, ["-C", directory, .. args], timeout, Environment);

	ProcessException Failure(ProcessResult result, string[] args)
	{
		var command = "git " + string.Join(' ', args);
		return new($"'{command}' failed in '{Root}' ({result.ExitCode}): {result.StandardError.Trim()}", command, result.ExitCode, result.StandardError);
	}

	static List<string> Lines(string output)
		=> output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
