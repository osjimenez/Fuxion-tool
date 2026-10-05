using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fuxion;
using Fuxion.Tools.Core.Configuration;
using Fuxion.Tools.Core.Git;

namespace Fuxion.Tools.Core.Versioning;

public static class VersioningInputsFactory
{
	/// <summary>The branch name of a detached HEAD (the name LibGit2Sharp gave it, kept so versions do not change).</summary>
	const string DetachedBranchName = "(no branch)";

	public static VersioningInputs Create(FuxionToolsConfig config, string? configPath = null)
		=> Calculate(config, configPath).Inputs;

	/// <summary>The version and, when it comes from git, how it was calculated.</summary>
	public static VersioningResult Calculate(FuxionToolsConfig config, string? configPath = null)
	{
		config ??= new();
		return config.Versioning switch
		{
			FixedVersioningConfig fixedCfg => new(CreateFixed(fixedCfg), null),
			GitVersioningConfig gitCfg => CreateGit(gitCfg, configPath),
			_ => new(VersioningInputs.Default, null)
		};
	}

	static VersioningInputs CreateFixed(FixedVersioningConfig cfg)
	{
		var version = string.IsNullOrWhiteSpace(cfg.Version) ? VersioningInputs.Default.BaseVersion : cfg.Version.Trim();
		var suffix = ResolveInformationalSuffix(cfg.InformationalSuffix, forceCi: false);
		return new(version, suffix);
	}

	static VersioningResult CreateGit(GitVersioningConfig cfg, string? configPath)
	{
		var suffix = ResolveInformationalSuffix(cfg.InformationalSuffix, cfg.ForceCIEnvironment);
		try
		{
			var git = GitClient.Discover(ResolveRepositoryPath(cfg.RepositoryPath, configPath));
			if (git is null)
				return FailOrDefault(cfg, suffix, VersioningErrorCodes.RepositoryNotFound, "Git repository not found.");

			var head = git.Head();
			if (head is null)
				return FailOrDefault(cfg, suffix, VersioningErrorCodes.NoCommits, "Git HEAD not available (repository has no commits).");

			var branchName = git.CurrentBranch() ?? DetachedBranchName;
			var stableTip = git.BranchTip("master") ?? git.BranchTip("main") ?? head;

			VersioningResult Result(string version, GitVersionRule rule, LastVersionTag tag, int commitsSinceTag, string? mergeBase = null, int? branchCommits = null)
				=> new(new(version, suffix), new(git.Root, branchName, rule, head, stableTip, mergeBase, tag.Name, tag.Commit, commitsSinceTag, branchCommits));

			VersioningResult NoTag(string reason) => FailOrDefault(cfg, suffix, VersioningErrorCodes.NoTag, reason);

			if (IsPreviewBranch(branchName))
			{
				if (FindLastVersionTag(git, head) is not { } tag)
					return NoTag($"No version tag found for preview branch '{branchName}' in repo '{git.Root}'.");
				var previewId = GetBranchSuffix(branchName, "preview/");
				var commitCount = CountCommits(git, head, tag.Commit);
				var baseVersion = $"{tag.Version.Major}.{tag.Version.Minor}.{tag.Version.Patch}";
				return Result($"{baseVersion}-preview.{previewId}.{commitCount}", GitVersionRule.Preview, tag, commitCount);
			}

			if (IsReleaseBranch(branchName))
			{
				if (FindLastVersionTag(git, head) is not { } tag)
					return NoTag($"No version tag found for release branch '{branchName}' in repo '{git.Root}'.");
				var releaseId = GetBranchSuffix(branchName, "release/");
				var commitCount = CountCommits(git, head, tag.Commit);
				var baseVersion = $"{tag.Version.Major}.{tag.Version.Minor}.{tag.Version.Patch}";
				return Result($"{baseVersion}-rc.{releaseId}.{commitCount}", GitVersionRule.Release, tag, commitCount);
			}

			if (IsFeatureBranch(branchName))
			{
				var mergeBase = git.MergeBase(head, stableTip) ?? stableTip;
				if (FindLastVersionTag(git, mergeBase) is not { } tag)
					return NoTag($"No version tag found on feature branch history for feature versioning (branch '{branchName}', repo '{git.Root}').");
				var stablePatch = CountCommits(git, mergeBase, tag.Commit);
				var baseVersion = $"{tag.Version.Major}.{tag.Version.Minor}.{stablePatch}";
				var featureName = GetBranchSuffix(branchName, "feature/");
				var delta = CountCommitsSinceLastStableMerge(git, head, stableTip, mergeBase);
				return Result($"{baseVersion}-feature.{featureName}.{delta}", GitVersionRule.Feature, tag, stablePatch, mergeBase, delta);
			}

			if (IsDevelopBranch(branchName))
			{
				if (FindLastVersionTag(git, head) is not { } tag)
					return NoTag($"No version tag found for develop branch in repo '{git.Root}'.");
				var patch = CountCommits(git, head, tag.Commit);
				return Result($"{tag.Version.Major}.{tag.Version.Minor}.{patch}-alpha", GitVersionRule.Develop, tag, patch);
			}

			if (IsStableBranch(branchName))
			{
				if (FindLastVersionTag(git, head) is not { } tag)
					return NoTag($"No version tag found for stable branch in repo '{git.Root}'.");
				var patch = CountCommits(git, head, tag.Commit);
				return Result($"{tag.Version.Major}.{tag.Version.Minor}.{patch}", GitVersionRule.Stable, tag, patch);
			}

			if (FindLastVersionTag(git, head) is not { } fallbackTag)
				return NoTag($"No version tag found for branch '{branchName}' in repo '{git.Root}'.");
			var fallbackPatch = CountCommits(git, head, fallbackTag.Commit);
			var fallbackBranch = SanitizeIdentifier(branchName);
			return Result($"{fallbackTag.Version.Major}.{fallbackTag.Version.Minor}.{fallbackPatch}-{fallbackBranch}", GitVersionRule.Other, fallbackTag, fallbackPatch);
		}
		catch (VersioningException)
		{
			throw;
		}
		catch (Exception ex)
		{
			return FailOrDefault(cfg, suffix, VersioningErrorCodes.GitFailed, ex.ToString());
		}
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

	static string ResolveRepositoryPath(string? repositoryPath, string? configPath)
	{
		if (string.IsNullOrWhiteSpace(repositoryPath))
			return Directory.GetCurrentDirectory();

		if (Path.IsPathRooted(repositoryPath))
			return repositoryPath;

		var baseDir = !string.IsNullOrWhiteSpace(configPath)
			? Path.GetDirectoryName(Path.GetFullPath(configPath))
			: Directory.GetCurrentDirectory();

		return string.IsNullOrWhiteSpace(baseDir)
			? Directory.GetCurrentDirectory()
			: Path.GetFullPath(Path.Combine(baseDir, repositoryPath));
	}

	static VersioningResult FailOrDefault(GitVersioningConfig cfg, string suffix, string code, string reason)
	{
		if (!cfg.FailOnError)
		{
			Console.Error.WriteLine($"[Warn] Git versioning fallback: {reason}");
			return new(new(VersioningInputs.Default.BaseVersion, suffix), null);
		}
		throw new VersioningException(code, $"Git versioning failed: {reason}");
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
	static int CountCommitsSinceLastStableMerge(GitClient git, string branchTip, string stableTip, string mergeBase)
	{
		if (branchTip == stableTip)
			return 0;
		var lastMerge = FindLastStableMergeCommit(git, branchTip, stableTip);
		var exclude = new List<string> { stableTip };
		if (lastMerge is null)
			exclude.Add(mergeBase);
		else
			exclude.AddRange(lastMerge.Value.Parents);
		return git.Count(branchTip, exclude);
	}

	/// <summary>The newest merge commit with a parent that is already in main.</summary>
	static (string Commit, string[] Parents)? FindLastStableMergeCommit(GitClient git, string branchTip, string stableTip)
	{
		foreach (var merge in git.MergesByDate(branchTip))
			foreach (var parent in merge.Parents)
				if (git.IsAncestor(parent, stableTip))
					return merge;
		return null;
	}

	static int CountCommits(GitClient git, string tip, string baseCommit)
		=> tip == baseCommit ? 0 : git.Count(tip, baseCommit);

	sealed record LastVersionTag(string Name, SemanticVersion Version, string Commit);

	/// <summary>
	/// The nearest commit, walking back from <paramref name="tip"/> by date, with a <c>version/X.Y.0</c> tag; the highest
	/// version if it has several. A version tag with a patch other than 0 is an error.
	/// </summary>
	static LastVersionTag? FindLastVersionTag(GitClient git, string tip)
	{
		const string prefix = "version/";
		var tagVersions = new Dictionary<string, LastVersionTag>();
		foreach (var tag in git.Tags())
		{
			if (!tag.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				continue;
			var value = tag.Name[prefix.Length..];
			if (!SemanticVersion.TryParse(value, out var semver))
				continue;
			if (semver.Patch != 0)
				throw new VersioningException(VersioningErrorCodes.InvalidTag, $"Git versioning failed: tag '{tag.Name}' patch must be 0.");
			if (!tagVersions.TryGetValue(tag.Commit, out var current) || semver.CompareTo(current.Version) > 0)
				tagVersions[tag.Commit] = new(tag.Name, semver, tag.Commit);
		}

		if (tagVersions.Count == 0)
			return null;

		foreach (var commit in git.CommitsByDate(tip))
			if (tagVersions.TryGetValue(commit, out var found))
				return found;

		return null;
	}
}
