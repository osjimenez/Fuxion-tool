using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Fuxion.Tool.Core.Git;
using Fuxion.Tool.Core.Results;

namespace Fuxion.Tool.Core.Versioning;

public sealed record CreatedVersionTag(string Name, string Commit);

/// <summary>
/// Creates the tag of a new minor or major: <c>version/X.Y.0</c> on HEAD, the only source of the
/// numbers. Local only: pushing it is up to the user.
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

	/// <summary>
	/// The tag <c>version/X.Y.0</c> on HEAD; an error if there is no repository or no commit
	/// (<see cref="HttpStatusCode.NotFound"/>), if it exists or is not beyond the highest one
	/// (<see cref="HttpStatusCode.Conflict"/>), or if git fails.
	/// </summary>
	public static Response<CreatedVersionTag> Create(string directory, int major, int minor)
	{
		if (GitClient.Discover(directory) is not { } git)
			return VersioningErrors.Of(VersioningErrorCodes.RepositoryNotFound, new(VersioningErrorCodes.RepositoryNotFound, directory));
		if (git.Head() is not { } head)
			return VersioningErrors.Of(VersioningErrorCodes.NoCommits, new(VersioningErrorCodes.NoCommits));

		var name = $"version/{major}.{minor}.0";
		var requested = new SemanticVersion($"{major}.{minor}.0");
		if (git.Tags() is not IReadOnlyList<GitTag> tags)
			return (Error)git.Tags();
		if (tags.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
			return VersioningErrors.Of(TagExists, new(TagExists, name));

		var highest = HighestTag(tags);
		if (highest.Version is not null && requested.CompareTo(highest.Version) <= 0)
			return VersioningErrors.Of(TagNotNewer, new(TagNotNewer, name, highest.Tag!.Name));

		return git.CreateTag(name, head) is Error error ? error : new CreatedVersionTag(name, head);
	}

	/// <summary>
	/// The major and minor of the highest <c>version/</c> tag, the one a new tag must go beyond; none if there is none.
	/// fx proposes the next minor and the next major from it. An error if there is no repository, or git fails.
	/// </summary>
	public static ResponseMaybe<(int Major, int Minor)> Highest(string directory)
	{
		if (GitClient.Discover(directory) is not { } git)
			return VersioningErrors.Of(VersioningErrorCodes.RepositoryNotFound, new(VersioningErrorCodes.RepositoryNotFound, directory));
		if (git.Tags() is not IReadOnlyList<GitTag> tags)
			return (Error)git.Tags();
		return HighestTag(tags).Version is { } version ? ((int)version.Major, (int)version.Minor) : None.Value;
	}

	static (GitTag? Tag, SemanticVersion? Version) HighestTag(IReadOnlyList<GitTag> tags)
		=> tags

			.Where(t => t.Name.StartsWith("version/", StringComparison.OrdinalIgnoreCase))
			.Select(t => SemanticVersion.TryParse(t.Name["version/".Length..], out var v) ? (Tag: (GitTag?)t, Version: (SemanticVersion?)v) : default)
			.Where(x => x.Version is not null)
			.OrderByDescending(x => x.Version)
			.FirstOrDefault();
}
