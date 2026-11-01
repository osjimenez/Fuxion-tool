using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Fuxion;
using Fuxion.Tool.Core.Git;
using Fuxion.Tool.Core.Results;

namespace Fuxion.Tool.Core.Versioning;

public static class VersioningInputsFactory
{
	/// <summary>The branch name of a detached HEAD (the name LibGit2Sharp gave it, kept so versions do not change).</summary>
	const string DetachedBranchName = "(no branch)";

	/// <summary>The inputs of the version from git (<see cref="Calculate"/>).</summary>
	public static Response<VersioningInputs> Create(GitVersioningOptions options)
	{
		var calculated = Calculate(options);
		return calculated is VersioningResult result ? FxResponse.Success(result.Inputs, calculated.Diagnostics) : (Error)calculated;
	}

	/// <summary>
	/// The version from git and how it was calculated. What keeps it from being calculated is an error when the options
	/// ask to fail (<see cref="GitVersioningOptions.FailOnError"/>); otherwise the default version, with that as its
	/// warning, and no details. A version tag that is not valid is always an error.
	/// </summary>
	public static Response<VersioningResult> Calculate(GitVersioningOptions cfg)
	{
		var suffix = ResolveInformationalSuffix(cfg.InformationalSuffix, cfg.ForceCi);
		var git = GitClient.Discover(string.IsNullOrWhiteSpace(cfg.RepositoryPath) ? Directory.GetCurrentDirectory() : Path.GetFullPath(cfg.RepositoryPath));
		if (git is null)
			return FailOrDefault(cfg, suffix, VersioningErrorCodes.RepositoryNotFound, new("version.reason.repository-not-found"));

		var head = git.Head();
		if (head is null)
			return FailOrDefault(cfg, suffix, VersioningErrorCodes.NoCommits, new("version.reason.no-commits"));

		var branchName = git.CurrentBranch() ?? DetachedBranchName;
		// The stable branch: local; otherwise origin's (a clone of one branch, a CI checkout), so the same commit gets the
		// same version however it was cloned; without either, HEAD (a repo that has no stable branch yet)
		var stableTip = git.BranchTip("master") ?? git.BranchTip("main")
		                ?? git.RemoteBranchTip("origin", "master") ?? git.RemoteBranchTip("origin", "main") ?? head;

		Response<VersioningResult> Result(string version, GitVersionRule rule, LastVersionTag tag, int commitsSinceTag, string? mergeBase = null, int? branchCommits = null)
			=> new VersioningResult(new(version, suffix), new(git.Root, branchName, rule, head, stableTip, mergeBase, tag.Name, tag.Commit, commitsSinceTag, branchCommits));

		Response<VersioningResult> NoTag(string key, params object?[] args) => FailOrDefault(cfg, suffix, VersioningErrorCodes.NoTag, new($"version.reason.no-tag-{key}", args));

		// what git could not answer stops the calculation: an invalid tag, as it is; git, as a failure of the versioning
		Response<VersioningResult> Failed(Error error)
			=> error.Diagnostics.Any(d => d.Code == VersioningErrorCodes.InvalidTag) ? error
				: FailOrDefault(cfg, suffix, VersioningErrorCodes.GitFailed, error.Diagnostics[0].Message);

		if (IsPreviewBranch(branchName) || IsReleaseBranch(branchName))
		{
			var preview = IsPreviewBranch(branchName);
			var found = FindLastVersionTag(git, head);
			if (found is Error notFound)
				return Failed(notFound);
			if (found is not LastVersionTag tag)
				return NoTag(preview ? "preview" : "release", branchName, git.Root);
			var id = GetBranchSuffix(branchName, preview ? "preview/" : "release/");
			if (CountCommits(git, head, tag.Commit) is not int commitCount)
				return Failed((Error)CountCommits(git, head, tag.Commit));
			var baseVersion = $"{tag.Version.Major}.{tag.Version.Minor}.{tag.Version.Patch}";
			return preview
				? Result($"{baseVersion}-preview.{id}.{commitCount}", GitVersionRule.Preview, tag, commitCount)
				: Result($"{baseVersion}-rc.{id}.{commitCount}", GitVersionRule.Release, tag, commitCount);
		}

		if (IsFeatureBranch(branchName))
		{
			var mergeBase = git.MergeBase(head, stableTip) ?? stableTip;
			var fromBase = FindLastVersionTag(git, mergeBase);
			if (fromBase is Error baseFailed)
				return Failed(baseFailed);
			if (fromBase is not LastVersionTag tag)
				return NoTag("feature", branchName, git.Root);
			if (CountCommits(git, mergeBase, tag.Commit) is not int stablePatch)
				return Failed((Error)CountCommits(git, mergeBase, tag.Commit));
			var baseVersion = $"{tag.Version.Major}.{tag.Version.Minor}.{stablePatch}";
			var featureName = GetBranchSuffix(branchName, "feature/");
			if (CountCommitsSinceLastStableMerge(git, head, stableTip, mergeBase) is not int delta)
				return Failed((Error)CountCommitsSinceLastStableMerge(git, head, stableTip, mergeBase));
			return Result($"{baseVersion}-feature.{featureName}.{delta}", GitVersionRule.Feature, tag, stablePatch, mergeBase, delta);
		}

		var (rule, key) = IsDevelopBranch(branchName) ? (GitVersionRule.Develop, "develop")
			: IsStableBranch(branchName) ? (GitVersionRule.Stable, "stable")
			: (GitVersionRule.Other, "other");
		var last = FindLastVersionTag(git, head);
		if (last is Error lastFailed)
			return Failed(lastFailed);
		if (last is not LastVersionTag lastTag)
			return rule == GitVersionRule.Other ? NoTag(key, branchName, git.Root) : NoTag(key, git.Root);
		if (CountCommits(git, head, lastTag.Commit) is not int patch)
			return Failed((Error)CountCommits(git, head, lastTag.Commit));
		var version = $"{lastTag.Version.Major}.{lastTag.Version.Minor}.{patch}";
		return rule switch
		{
			GitVersionRule.Develop => Result($"{version}-alpha", rule, lastTag, patch),
			GitVersionRule.Stable => Result(version, rule, lastTag, patch),
			_ => Result($"{version}-{SanitizeIdentifier(branchName)}", rule, lastTag, patch)
		};
	}


	static string ResolveInformationalSuffix(string? configured, bool forceCi)
	{
		if (!string.IsNullOrWhiteSpace(configured))
			return configured;
		return IsCiBuild(forceCi) ? string.Empty : VersioningInputs.Default.InformationalSuffix;
	}

	static bool IsCiBuild(bool forceCi)
	{
		if (forceCi)
			return true;
		var ci = Environment.GetEnvironmentVariable("CI");
		if (string.Equals(ci, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(ci, "1", StringComparison.OrdinalIgnoreCase))
			return true;
		var githubActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
		return string.Equals(githubActions, "true", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// What keeps the version from being calculated: an error, if the options ask to fail; otherwise the default version,
	/// with it as a warning (<c>Git versioning fallback: …</c>).
	/// </summary>
	static Response<VersioningResult> FailOrDefault(GitVersioningOptions cfg, string suffix, string code, FxText reason)
	{
		if (!cfg.FailOnError)
			return FxResponse.Success(new VersioningResult(new(VersioningInputs.Default.BaseVersion, suffix), null),
				[VersioningErrors.Diagnostic(code, new("version.fallback", reason), FxSeverity.Warning)]);
		return VersioningErrors.Of(code, new("version.failed", reason));
	}

	static bool IsStableBranch(string branchName)
		=> string.Equals(branchName, "master", StringComparison.OrdinalIgnoreCase)
		   || string.Equals(branchName, "main", StringComparison.OrdinalIgnoreCase);

	static bool IsDevelopBranch(string branchName)
		=> string.Equals(branchName, "develop", StringComparison.OrdinalIgnoreCase);

	static bool IsFeatureBranch(string branchName)
		=> branchName.StartsWith("feature/", StringComparison.OrdinalIgnoreCase);

	static bool IsPreviewBranch(string branchName)
		=> branchName.StartsWith("preview/", StringComparison.OrdinalIgnoreCase);

	static bool IsReleaseBranch(string branchName)
		=> branchName.StartsWith("release/", StringComparison.OrdinalIgnoreCase);

	static string GetBranchSuffix(string branchName, string prefix)
	{
		var raw = branchName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
			? branchName[prefix.Length..]
			: branchName;
		var normalized = NormalizeBranchName(raw);
		return string.IsNullOrWhiteSpace(normalized) ? "1" : normalized;
	}

	static string NormalizeBranchName(string name)
	{
		var segments = name
			.Replace('\\', '/')
			.Split('/', StringSplitOptions.RemoveEmptyEntries);
		return string.Join('.', segments.Select(SanitizeIdentifier));
	}

	static string SanitizeIdentifier(string value)
	{
		var cleaned = new string(value
			.ToLowerInvariant()
			.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-')
			.ToArray())
			.Trim('-');
		return string.IsNullOrWhiteSpace(cleaned) ? "branch" : cleaned;
	}

	/// <summary>
	/// Commits on the branch since main was last merged into it (the merge included), or since it left main if it
	/// never was. Commits only reachable from main do not count.
	/// </summary>
	static Response<int> CountCommitsSinceLastStableMerge(GitClient git, string branchTip, string stableTip, string mergeBase)
	{
		if (branchTip == stableTip)
			return 0;
		var lastMerge = FindLastStableMergeCommit(git, branchTip, stableTip);
		if (lastMerge is Error failed)
			return failed;
		var exclude = new List<string> { stableTip };
		if (lastMerge is ValueTuple<string, string[]> merge)
			exclude.AddRange(merge.Item2);
		else
			exclude.Add(mergeBase);
		return git.Count(branchTip, exclude);
	}

	/// <summary>The newest merge commit with a parent that is already in main.</summary>
	static ResponseMaybe<(string Commit, string[] Parents)> FindLastStableMergeCommit(GitClient git, string branchTip, string stableTip)
	{
		if (git.MergesByDate(branchTip) is not IReadOnlyList<(string Commit, string[] Parents)> merges)
			return (Error)git.MergesByDate(branchTip);
		foreach (var merge in merges)
			foreach (var parent in merge.Parents)
				switch (git.IsAncestor(parent, stableTip))
				{
					case true:
						return merge;
					case Error failed:
						return failed;
				}
		return None.Value;
	}

	static Response<int> CountCommits(GitClient git, string tip, string baseCommit)
		=> tip == baseCommit ? 0 : git.Count(tip, baseCommit);

	sealed record LastVersionTag(string Name, SemanticVersion Version, string Commit);

	/// <summary>
	/// The nearest commit, walking back from <paramref name="tip"/> by date, with a <c>version/X.Y.0</c> tag; the highest
	/// version if it has several; none if there is none. A version tag with a patch other than 0 is an error.
	/// </summary>
	static ResponseMaybe<LastVersionTag> FindLastVersionTag(GitClient git, string tip)
	{
		const string prefix = "version/";
		if (git.Tags() is not IReadOnlyList<GitTag> tags)
			return (Error)git.Tags();
		var tagVersions = new Dictionary<string, LastVersionTag>();
		foreach (var tag in tags)
		{
			if (!tag.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				continue;
			var value = tag.Name[prefix.Length..];
			if (!SemanticVersion.TryParse(value, out var semver))
				continue;
			if (semver.Patch != 0)
				return VersioningErrors.Of(VersioningErrorCodes.InvalidTag, new("version.failed", new FxText("version.reason.invalid-tag", tag.Name)));
			if (!tagVersions.TryGetValue(tag.Commit, out var current) || semver.CompareTo(current.Version) > 0)
				tagVersions[tag.Commit] = new(tag.Name, semver, tag.Commit);
		}

		if (tagVersions.Count == 0)
			return None.Value;
		if (git.CommitsByDate(tip) is not IReadOnlyList<string> commits)
			return (Error)git.CommitsByDate(tip);
		foreach (var commit in commits)
			if (tagVersions.TryGetValue(commit, out var found))
				return found;
		return None.Value;
	}
}
