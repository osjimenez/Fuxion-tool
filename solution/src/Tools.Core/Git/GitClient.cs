using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fuxion.Tools.Core.Processes;

namespace Fuxion.Tools.Core.Git;

public sealed record GitTag(string Name, string Commit);

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

	/// <summary>Tracked files (and untracked ones not ignored) matching the pathspecs, relative to the root, with '/'.</summary>
	public IReadOnlyList<string> ListFiles(params string[] pathspecs)
		=> Required(["ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", .. pathspecs])
			.Split('\0', StringSplitOptions.RemoveEmptyEntries)
			.Distinct(StringComparer.Ordinal)
			.ToList();

	/// <summary>Committer date of a commit.</summary>
	public DateTimeOffset? CommitDate(string commit)
		=> Optional("show", "-s", "--format=%cI", commit) is { } date
		   && DateTimeOffset.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
			? parsed
			: null;

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
