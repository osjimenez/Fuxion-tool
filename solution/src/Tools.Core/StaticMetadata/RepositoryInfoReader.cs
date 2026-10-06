using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Fuxion.Tools.Core.Configuration;
using Fuxion.Tools.Core.Git;

namespace Fuxion.Tools.Core.StaticMetadata;

/// <param name="CommitDateUtc">Committer date of HEAD: the build date of a reproducible build (plan K, K3.2).</param>
public sealed record RepositoryInfo(string? Branch, string? Commit, string? OriginUrlSHA256, string? FirstCommit, DateTimeOffset? CommitDateUtc = null)
{
	public static RepositoryInfo None { get; } = new(null, null, null, null);
}

// Moved from the metadata command (plan K, K2.1) so it can be tested; on the git executable since K2.2.
public static class RepositoryInfoReader
{
	public static RepositoryInfo TryRead(FuxionToolsConfig config, string? configPath)
	{
		try
		{
			var repositoryPath = ResolveRepositoryPath((config.Versioning as GitVersioningConfig)?.RepositoryPath, configPath);
			var git = GitClient.Discover(repositoryPath);
			if (git is null)
				return RepositoryInfo.None;

			var normalizedOrigin = NormalizeOriginUrl(git.RemoteUrl("origin"));
			var originUrlSha256 = string.IsNullOrWhiteSpace(normalizedOrigin) ? null : ComputeSha256(normalizedOrigin);
			var head = git.Head();
			var firstCommit = head is null ? null : git.FirstCommit(head);
			// a detached HEAD is named as LibGit2Sharp named it, so the generated metadata does not change
			return new(git.CurrentBranch() ?? "(no branch)", head, originUrlSha256, firstCommit, head is null ? null : git.CommitDate(head)?.ToUniversalTime());
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

		// "..\" in a fuxion-tools.json written on Windows must also work on Linux
		repositoryPath = repositoryPath.Replace('\\', Path.DirectorySeparatorChar);

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
