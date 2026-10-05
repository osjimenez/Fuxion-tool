using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Fuxion.Tools.Core.Configuration;
using LibGit2Sharp;

namespace Fuxion.Tools.Core.StaticMetadata;

public sealed record RepositoryInfo(string? Branch, string? Commit, string? OriginUrlSHA256, string? FirstCommit)
{
	public static RepositoryInfo None { get; } = new(null, null, null, null);
}

// Moved from the metadata command (plan K, K2.1) so it can be tested; logic unchanged.
public static class RepositoryInfoReader
{
	public static RepositoryInfo TryRead(FuxionToolsConfig config, string? configPath)
	{
		try
		{
			var repositoryPath = ResolveRepositoryPath((config.Versioning as GitVersioningConfig)?.RepositoryPath, configPath);
			var discovered = Repository.Discover(repositoryPath);
			if (string.IsNullOrWhiteSpace(discovered))
				return RepositoryInfo.None;

			using var repo = new Repository(discovered);
			var originUrl = repo.Network.Remotes["origin"]?.Url;
			var normalizedOrigin = NormalizeOriginUrl(originUrl);
			var originUrlSha256 = string.IsNullOrWhiteSpace(normalizedOrigin) ? null : ComputeSha256(normalizedOrigin);
			var firstCommit = repo.Head?.Tip is null
				? null
				: repo.Commits.QueryBy(new CommitFilter
				{
					IncludeReachableFrom = repo.Head.Tip,
					SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Reverse
				}).FirstOrDefault()?.Sha;

			return new(repo.Head?.FriendlyName, repo.Head?.Tip?.Sha, originUrlSha256, firstCommit);
		}
		catch
		{
			return RepositoryInfo.None;
		}
	}

	public static string? NormalizeOriginUrl(string? originUrl)
	{
		if (string.IsNullOrWhiteSpace(originUrl))
			return null;

		var normalized = originUrl.Trim();
		if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
			normalized = normalized[..^4];
		return normalized.TrimEnd('/').ToLowerInvariant();
	}

	static string ComputeSha256(string value)
		=> Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

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
}
