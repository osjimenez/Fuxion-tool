using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fuxion;
using Fuxion.Tools.Core.Configuration;
using LibGit2Sharp;

namespace Fuxion.Tools.Core.Versioning;

public static class VersioningInputsFactory
{
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
			var repoPath = Repository.Discover(ResolveRepositoryPath(cfg.RepositoryPath, configPath));
			if (string.IsNullOrWhiteSpace(repoPath))
				return FailOrDefault(cfg, suffix, "Git repository not found (Repository.Discover returned null).", null);

			using var repo = new Repository(repoPath);
			if (repo.Head?.Tip is null)
				return FailOrDefault(cfg, suffix, "Git HEAD not available (repository has no commits).", null);

			var branchName = repo.Head.FriendlyName ?? "detached";
			var stableBranch = GetStableBranch(repo) ?? repo.Head;

			if (IsPreviewBranch(branchName))
			{
				if (!TryGetLastVersionTag(repo, repo.Head.Tip, out var tagVersion, out var tagCommit))
					return FailOrDefault(cfg, suffix, $"No version tag found for preview branch '{branchName}' in repo '{repo.Info.WorkingDirectory}'.", null);
				var previewId = GetBranchSuffix(branchName, "preview/");
				var commitCount = CountCommits(repo, repo.Head.Tip, tagCommit);
				var baseVersion = $"{tagVersion.Major}.{tagVersion.Minor}.{tagVersion.Patch}";
				var prerelease = $"preview.{previewId}.{commitCount}";
				return new($"{baseVersion}-{prerelease}", suffix);
			}

			if (IsReleaseBranch(branchName))
			{
				if (!TryGetLastVersionTag(repo, repo.Head.Tip, out var tagVersion, out var tagCommit))
					return FailOrDefault(cfg, suffix, $"No version tag found for release branch '{branchName}' in repo '{repo.Info.WorkingDirectory}'.", null);
				var releaseId = GetBranchSuffix(branchName, "release/");
				var commitCount = CountCommits(repo, repo.Head.Tip, tagCommit);
				var baseVersion = $"{tagVersion.Major}.{tagVersion.Minor}.{tagVersion.Patch}";
				var prerelease = $"rc.{releaseId}.{commitCount}";
				return new($"{baseVersion}-{prerelease}", suffix);
			}

			if (IsFeatureBranch(branchName))
			{
				var mergeBase = repo.ObjectDatabase.FindMergeBase(repo.Head.Tip, stableBranch.Tip) ?? stableBranch.Tip;
				if (!TryGetLastVersionTag(repo, mergeBase, out var tagVersion, out var tagCommit))
					return FailOrDefault(cfg, suffix, $"No version tag found on feature branch history for feature versioning (branch '{branchName}', repo '{repo.Info.WorkingDirectory}').", null);
				var stablePatch = CountCommits(repo, mergeBase, tagCommit);
				var baseVersion = $"{tagVersion.Major}.{tagVersion.Minor}.{stablePatch}";
				var featureName = GetBranchSuffix(branchName, "feature/");
				var delta = CountCommitsSinceLastStableMerge(repo, repo.Head.Tip, stableBranch.Tip, mergeBase);
				var prerelease = $"feature.{featureName}.{delta}";
				return new($"{baseVersion}-{prerelease}", suffix);
			}

			if (IsDevelopBranch(branchName))
			{
				if (!TryGetLastVersionTag(repo, repo.Head.Tip, out var tagVersion, out var tagCommit))
					return FailOrDefault(cfg, suffix, $"No version tag found for develop branch in repo '{repo.Info.WorkingDirectory}'.", null);
				var patch = CountCommits(repo, repo.Head.Tip, tagCommit);
				var baseVersion = $"{tagVersion.Major}.{tagVersion.Minor}.{patch}";
				return new($"{baseVersion}-alpha", suffix);
			}

			if (IsStableBranch(branchName))
			{
				if (!TryGetLastVersionTag(repo, repo.Head.Tip, out var tagVersion, out var tagCommit))
					return FailOrDefault(cfg, suffix, $"No version tag found for stable branch in repo '{repo.Info.WorkingDirectory}'.", null);
				var patch = CountCommits(repo, repo.Head.Tip, tagCommit);
				var baseVersion = $"{tagVersion.Major}.{tagVersion.Minor}.{patch}";
				return new(baseVersion, suffix);
			}

			if (!TryGetLastVersionTag(repo, repo.Head.Tip, out var fallbackVersion, out var fallbackCommit))
				return FailOrDefault(cfg, suffix, $"No version tag found for branch '{branchName}' in repo '{repo.Info.WorkingDirectory}'.", null);

			var fallbackPatch = CountCommits(repo, repo.Head.Tip, fallbackCommit);
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

	static Branch? GetStableBranch(Repository repo)
		=> repo.Branches["master"] ?? repo.Branches["main"];

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

	static int CountBranchDelta(Repository repo, Commit branchTip, Commit stableTip)
	{
		var mergeBase = repo.ObjectDatabase.FindMergeBase(branchTip, stableTip) ?? stableTip;
		var filter = new CommitFilter
		{
			IncludeReachableFrom = branchTip,
			ExcludeReachableFrom = mergeBase
		};
		return repo.Commits.QueryBy(filter).Count();
	}

	static int CountCommitsSinceLastStableMerge(Repository repo, Commit branchTip, Commit stableTip, Commit mergeBase)
	{
		if (branchTip.Id == stableTip.Id)
			return 0;
		var lastMerge = FindLastStableMergeCommit(repo, branchTip, stableTip);
		var exclude = new List<Commit> { stableTip };
		if (lastMerge is null)
		{
			exclude.Add(mergeBase);
		}
		else
		{
			exclude.AddRange(lastMerge.Parents);
		}
		var filter = new CommitFilter
		{
			IncludeReachableFrom = branchTip,
			ExcludeReachableFrom = exclude
		};
		return repo.Commits.QueryBy(filter).Count();
	}

	static Commit? FindLastStableMergeCommit(Repository repo, Commit branchTip, Commit stableTip)
	{
		var filter = new CommitFilter
		{
			IncludeReachableFrom = branchTip,
			SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time
		};
		foreach (var commit in repo.Commits.QueryBy(filter))
		{
			if (commit.Parents.Count() < 2)
				continue;
			foreach (var parent in commit.Parents)
			{
				var common = repo.ObjectDatabase.FindMergeBase(parent, stableTip);
				if (common is not null && common.Id == parent.Id)
					return commit;
			}
		}
		return null;
	}

	static int CountCommits(Repository repo, Commit tip, Commit baseCommit)
	{
		if (tip == baseCommit)
			return 0;
		var filter = new CommitFilter
		{
			IncludeReachableFrom = tip,
			ExcludeReachableFrom = baseCommit
		};
		return repo.Commits.QueryBy(filter).Count();
	}

	static bool TryGetLastVersionTag(Repository repo, Commit tip, out SemanticVersion version, out Commit tagCommit)
	{
		version = null!;
		tagCommit = null!;
		const string prefix = "version/";
		var tagVersions = new Dictionary<ObjectId, SemanticVersion>();
		foreach (var tag in repo.Tags)
		{
			var name = tag.FriendlyName;
			if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				continue;
			var value = name[prefix.Length..];
			if (!SemanticVersion.TryParse(value, out var semver))
				continue;
			if (semver.Patch != 0)
				throw new InvalidOperationException($"Git versioning failed: tag '{name}' patch must be 0.");
			var commit = GetTagCommit(tag.Target);
			if (commit is null)
				continue;
			if (!tagVersions.TryGetValue(commit.Id, out var current) || semver.CompareTo(current) > 0)
				tagVersions[commit.Id] = semver;
		}

		if (tagVersions.Count == 0)
			return false;

		var filter = new CommitFilter
		{
			IncludeReachableFrom = tip,
			SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time
		};

		foreach (var commit in repo.Commits.QueryBy(filter))
		{
			if (!tagVersions.TryGetValue(commit.Id, out var semver))
				continue;
			version = semver;
			tagCommit = commit;
			return true;
		}

		return false;
	}

	static Commit? GetTagCommit(GitObject target)
		=> target switch
		{
			Commit commit => commit,
			TagAnnotation annotation when annotation.Target is Commit commit => commit,
			_ => null
		};
}
