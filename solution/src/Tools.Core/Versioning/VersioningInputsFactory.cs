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
	{
		config ??= new();
		var versioning = config.Versioning;

		if (versioning is null)
			return VersioningInputs.Default;

		return versioning switch
		{
			FixedVersioningConfig fixedCfg => CreateFixed(fixedCfg),
			GitVersioningConfig gitCfg => CreateGit(gitCfg, configPath),
			_ => VersioningInputs.Default
		};
	}

	static VersioningInputs CreateFixed(FixedVersioningConfig cfg)
	{
		var version = string.IsNullOrWhiteSpace(cfg.Version) ? VersioningInputs.Default.BaseVersion : cfg.Version.Trim();
		var suffix = ResolveInformationalSuffix(cfg.InformationalSuffix, forceCi: false);
		return new(version, suffix);
	}

	static VersioningInputs CreateGit(GitVersioningConfig cfg, string? configPath)
	{
		var suffix = ResolveInformationalSuffix(cfg.InformationalSuffix, cfg.ForceCIEnvironment);
		try
		{
			var git = GitClient.Discover(ResolveRepositoryPath(cfg.RepositoryPath, configPath));
			if (git is null)
				return FailOrDefault(cfg, suffix, "Git repository not found (Repository.Discover returned null).", null);

			var head = git.Head();
			if (head is null)
				return FailOrDefault(cfg, suffix, "Git HEAD not available (repository has no commits).", null);

			var branchName = git.CurrentBranch() ?? DetachedBranchName;
			var stableTip = git.BranchTip("master") ?? git.BranchTip("main") ?? head;

			if (IsPreviewBranch(branchName))
			{
				if (!TryGetLastVersionTag(git, head, out var tagVersion, out var tagCommit))
					return FailOrDefault(cfg, suffix, $"No version tag found for preview branch '{branchName}' in repo '{git.Root}'.", null);
				var previewId = GetBranchSuffix(branchName, "preview/");
				var commitCount = CountCommits(git, head, tagCommit);
				var baseVersion = $"{tagVersion.Major}.{tagVersion.Minor}.{tagVersion.Patch}";
				var prerelease = $"preview.{previewId}.{commitCount}";
				return new($"{baseVersion}-{prerelease}", suffix);
			}

			if (IsReleaseBranch(branchName))
			{
				if (!TryGetLastVersionTag(git, head, out var tagVersion, out var tagCommit))
					return FailOrDefault(cfg, suffix, $"No version tag found for release branch '{branchName}' in repo '{git.Root}'.", null);
				var releaseId = GetBranchSuffix(branchName, "release/");
				var commitCount = CountCommits(git, head, tagCommit);
				var baseVersion = $"{tagVersion.Major}.{tagVersion.Minor}.{tagVersion.Patch}";
				var prerelease = $"rc.{releaseId}.{commitCount}";
				return new($"{baseVersion}-{prerelease}", suffix);
			}

			if (IsFeatureBranch(branchName))
			{
				var mergeBase = git.MergeBase(head, stableTip) ?? stableTip;
				if (!TryGetLastVersionTag(git, mergeBase, out var tagVersion, out var tagCommit))
					return FailOrDefault(cfg, suffix, $"No version tag found on feature branch history for feature versioning (branch '{branchName}', repo '{git.Root}').", null);
				var stablePatch = CountCommits(git, mergeBase, tagCommit);
				var baseVersion = $"{tagVersion.Major}.{tagVersion.Minor}.{stablePatch}";
				var featureName = GetBranchSuffix(branchName, "feature/");
				var delta = CountCommitsSinceLastStableMerge(git, head, stableTip, mergeBase);
				var prerelease = $"feature.{featureName}.{delta}";
				return new($"{baseVersion}-{prerelease}", suffix);
			}

			if (IsDevelopBranch(branchName))
			{
				if (!TryGetLastVersionTag(git, head, out var tagVersion, out var tagCommit))
					return FailOrDefault(cfg, suffix, $"No version tag found for develop branch in repo '{git.Root}'.", null);
				var patch = CountCommits(git, head, tagCommit);
				var baseVersion = $"{tagVersion.Major}.{tagVersion.Minor}.{patch}";
				return new($"{baseVersion}-alpha", suffix);
			}

			if (IsStableBranch(branchName))
			{
				if (!TryGetLastVersionTag(git, head, out var tagVersion, out var tagCommit))
					return FailOrDefault(cfg, suffix, $"No version tag found for stable branch in repo '{git.Root}'.", null);
				var patch = CountCommits(git, head, tagCommit);
				var baseVersion = $"{tagVersion.Major}.{tagVersion.Minor}.{patch}";
				return new(baseVersion, suffix);
			}

			if (!TryGetLastVersionTag(git, head, out var fallbackVersion, out var fallbackCommit))
				return FailOrDefault(cfg, suffix, $"No version tag found for branch '{branchName}' in repo '{git.Root}'.", null);

			var fallbackPatch = CountCommits(git, head, fallbackCommit);
			var fallbackBaseVersion = $"{fallbackVersion.Major}.{fallbackVersion.Minor}.{fallbackPatch}";
			var fallbackBranch = SanitizeIdentifier(branchName);
			return new($"{fallbackBaseVersion}-{fallbackBranch}", suffix);
		}
		catch (Exception ex)
		{
			if (ex is InvalidOperationException && ex.Message.StartsWith("Git versioning failed:", StringComparison.Ordinal))
				throw;
			return FailOrDefault(cfg, suffix, ex.ToString(), null);
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

	static VersioningInputs FailOrDefault(GitVersioningConfig cfg, string suffix, string reason, Exception? exception)
	{
		if (!cfg.FailOnError)
		{
			var details = exception is null ? reason : $"{reason} ({exception.GetType().Name}: {exception.Message})";
			Console.Error.WriteLine($"[Warn] Git versioning fallback: {details}");
			return new(VersioningInputs.Default.BaseVersion, suffix);
		}

		if (exception is null)
			throw new InvalidOperationException($"Git versioning failed: {reason}");

		throw new InvalidOperationException($"Git versioning failed: {reason} ({exception.GetType().Name}: {exception.Message})", exception);
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

	/// <summary>
	/// The nearest commit, walking back from <paramref name="tip"/> by date, with a <c>version/X.Y.0</c> tag; the highest
	/// version if it has several. A version tag with a patch other than 0 is an error.
	/// </summary>
	static bool TryGetLastVersionTag(GitClient git, string tip, out SemanticVersion version, out string tagCommit)
	{
		version = null!;
		tagCommit = null!;
		const string prefix = "version/";
		var tagVersions = new Dictionary<string, SemanticVersion>();
		foreach (var tag in git.Tags())
		{
			if (!tag.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				continue;
			var value = tag.Name[prefix.Length..];
			if (!SemanticVersion.TryParse(value, out var semver))
				continue;
			if (semver.Patch != 0)
				throw new InvalidOperationException($"Git versioning failed: tag '{tag.Name}' patch must be 0.");
			if (!tagVersions.TryGetValue(tag.Commit, out var current) || semver.CompareTo(current) > 0)
				tagVersions[tag.Commit] = semver;
		}

		if (tagVersions.Count == 0)
			return false;

		foreach (var commit in git.CommitsByDate(tip))
		{
			if (!tagVersions.TryGetValue(commit, out var semver))
				continue;
			version = semver;
			tagCommit = commit;
			return true;
		}

		return false;
	}
}
