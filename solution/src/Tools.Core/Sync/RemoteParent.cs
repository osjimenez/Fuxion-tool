using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Fuxion.Tools.Core.Git;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Workspace;

namespace Fuxion.Tools.Core.Sync;

/// <summary>
/// The parent of a repository that is not on disk above it, read from its remote (plan P, decision 15 and proposal E):
/// a clone of one branch, with only its last commit and only <c>_fx/</c> (and the sources of its shares), in a cache of the user
/// (<c>%LOCALAPPDATA%\fx\cache\</c>, or <c>FX_CACHE</c>), brought up to date on each read. Without network
/// (<c>--offline</c>) the cache is used as it is. Read-only: nobody writes there but fx.
/// </summary>
public static class RemoteParent
{
	public const string Unavailable = "workspace.parent-unavailable";
	public const string NotDeclared = "workspace.parent-not-declared";

	static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

	/// <summary>Where the parents read from their remotes are kept.</summary>
	public static string CacheRoot
		=> System.Environment.GetEnvironmentVariable("FX_CACHE") is { Length: > 0 } cache
			? cache
			: Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "fx", "cache");

	/// <summary>The folder of the cache for <paramref name="url"/>: its last segment and a hash of the whole URL.</summary>
	public static string Folder(string url)
	{
		var normalized = url.Trim().Replace('\\', '/').TrimEnd('/');
		var name = normalized.Split('/', ':').Last();
		if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
			name = name[..^4];
		var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToLowerInvariant())))[..10];
		return Path.Combine(CacheRoot, $"{name}-{hash}");
	}

	/// <summary>
	/// The parent at <paramref name="url"/>, on <paramref name="branch"/> (its default branch, without it), up to date
	/// unless <paramref name="offline"/>; null, with the reason in <paramref name="diagnostics"/>, if it cannot be read.
	/// Also the branch read and its commit.
	/// </summary>
	public static (FxNode Node, string? Branch)? Read(string url, string? branch, bool offline, IFxProgress? progress, FxDiagnostics diagnostics, CancellationToken cancellationToken)
	{
		var folder = Folder(url);
		var present = Directory.Exists(Path.Combine(folder, ".git"));
		if (offline)
		{
			if (present)
				return (new FxNode(folder), branch);
			diagnostics.Add(FxDiagnostic.Warning(Unavailable, new($"{Unavailable}.offline", url), fix: FxFix.Run(FxHeader.Sync)));
			return null;
		}
		return progress.Step(WorkspaceModuleName, null, "parent", () =>
		{
			branch ??= GitClient.DefaultBranch(url, Timeout, cancellationToken);
			if (branch is null)
			{
				diagnostics.Add(FxDiagnostic.Warning(Unavailable, new(Unavailable, url)));
				return ((FxNode, string?)?)null;
			}
			var result = present
				? GitClient.Discover(folder)!.FetchCheckout(branch, Timeout, cancellationToken)
				: GitClient.CloneSparse(url, folder, branch, [FxLayout.Folder], Timeout, cancellationToken);
			// the sources of its shares are outside _fx/ (decision 23): the folders that hold them too
			if (result.ExitCode == 0 && Shares(folder) is { Count: > 1 } folders)
				result = GitClient.Discover(folder)!.SparseCheckout(folders, Timeout, cancellationToken);
			if (result.ExitCode != 0)
			{
				diagnostics.Add(FxDiagnostic.Warning(Unavailable, new($"{Unavailable}.git", url, branch, result.StandardError.Trim())));
				return null;
			}
			return (new FxNode(folder), branch);
		}, r => r is not null);
	}

	/// <summary>What the cache of a parent has in its working tree: <c>_fx/</c> and the folders of the sources of its shares.</summary>
	static IReadOnlyList<string> Shares(string folder)
	{
		var manifest = Path.Combine(folder, WorkspaceManifest.RelativePath);
		var shares = File.Exists(manifest) ? WorkspaceManifest.Read(folder, File.ReadAllText(manifest)).Shares ?? [] : [];
		return new[] { FxLayout.Folder }
			.Concat(shares.Where(s => WorkspaceShares.SafeSource(s.Path))
				.Select(s => s.Path.EndsWith('/') ? s.Path.TrimEnd('/') : Path.GetDirectoryName(s.Path)?.Replace('\\', '/') ?? ""))
			.Where(f => f.Length > 0)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	const string WorkspaceModuleName = "workspace";
}
