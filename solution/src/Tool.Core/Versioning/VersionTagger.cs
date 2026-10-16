using System;
using System.Linq;
using Fuxion.Tool.Core.Git;
using Fuxion.Tool.Core.Results;

namespace Fuxion.Tool.Core.Versioning;

public sealed record CreatedVersionTag(string Name, string Commit);

/// <summary>
/// Creates the tag of a new minor or major (design §9, D-18): <c>version/X.Y.0</c> on HEAD, the only source of the
/// numbers. Local only (D-20): pushing it is up to the user.
/// </summary>
public static class VersionTagger
{
	public const string InvalidArgument = "version.invalid-argument";
	public const string TagExists = "version.tag-exists";
	public const string TagNotNewer = "version.tag-not-newer";

	/// <summary>Parses <c>X.Y</c> (two non-negative integers).</summary>
	public static bool TryParseMajorMinor(string value, out int major, out int minor)
	{
		major = minor = 0;
		var parts = value.Trim().Split('.');
		return parts.Length == 2
		       && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit))
		       && int.TryParse(parts[0], out major)
		       && int.TryParse(parts[1], out minor);
	}

	public static CreatedVersionTag Create(string directory, int major, int minor)
	{
		var git = GitClient.Discover(directory)
		          ?? throw new VersioningException(VersioningErrorCodes.RepositoryNotFound, new(VersioningErrorCodes.RepositoryNotFound, directory));
		var head = git.Head()
		           ?? throw new VersioningException(VersioningErrorCodes.NoCommits, new(VersioningErrorCodes.NoCommits));

		var name = $"version/{major}.{minor}.0";
		var requested = new SemanticVersion($"{major}.{minor}.0");
		if (git.Tags().Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
			throw new VersioningException(TagExists, new(TagExists, name));

		var highest = HighestTag(git);
		if (highest.Version is not null && requested.CompareTo(highest.Version) <= 0)
			throw new VersioningException(TagNotNewer, new(TagNotNewer, name, highest.Tag!.Name));

		git.CreateTag(name, head);
		return new(name, head);
	}

	/// <summary>
	/// The major and minor of the highest <c>version/</c> tag, the one a new tag must go beyond; <see langword="null"/>
	/// if there is none. fx proposes the next minor and the next major from it.
	/// </summary>
	public static (int Major, int Minor)? Highest(string directory)
	{
		var git = GitClient.Discover(directory)
		          ?? throw new VersioningException(VersioningErrorCodes.RepositoryNotFound, new(VersioningErrorCodes.RepositoryNotFound, directory));
		return HighestTag(git).Version is { } version ? ((int)version.Major, (int)version.Minor) : null;
	}

	static (GitTag? Tag, SemanticVersion? Version) HighestTag(GitClient git)
		=> git.Tags()
			.Where(t => t.Name.StartsWith("version/", StringComparison.OrdinalIgnoreCase))
			.Select(t => SemanticVersion.TryParse(t.Name["version/".Length..], out var v) ? (Tag: (GitTag?)t, Version: (SemanticVersion?)v) : default)
			.Where(x => x.Version is not null)
			.OrderByDescending(x => x.Version)
			.FirstOrDefault();
}
