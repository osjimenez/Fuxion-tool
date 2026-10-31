using System;
using System.IO;
using System.Text.Json;
using Fuxion.Tool.Core.Git;

namespace Fuxion.Tool.Core.Docs;

/// <summary>
/// What git has committed of a catalog (plan U, U6): what <c>HEAD</c> has is history; what it does not, still the work of
/// whoever is writing it. A document is frozen once <c>HEAD</c> has it frozen, and only one that <c>HEAD</c> does not have
/// can be discarded. Without git nothing is known, and everything counts as history.
/// </summary>
public sealed class DocHistory
{
	readonly GitClient? _git;
	readonly bool _commits;

	DocHistory(GitClient? git)
	{
		_git = git;
		_commits = git?.Head() is not null;
	}

	/// <summary>The history of <paramref name="catalog"/>: that of the git repository it is in, if any.</summary>
	public static DocHistory Of(DocCatalog catalog) => new(GitClient.Discover(catalog.Root));

	/// <summary>Whether it knows what is committed: the catalog is in a git repository.</summary>
	public bool Known => _git is not null;

	/// <summary>The path of <paramref name="full"/> in the repository, with '/'; null if it is not in it.</summary>
	string? InRepository(string full)
	{
		var path = Path.GetRelativePath(_git!.Root, full).Replace('\\', '/');
		return path.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(path) ? null : path;
	}

	/// <summary>Whether <c>HEAD</c> has <paramref name="full"/>; without git, true (everything counts as history).</summary>
	public bool Has(string full)
		=> _git is null || _commits && InRepository(full) is { } path && _git.Execute("cat-file", "-e", $"HEAD:{path}").ExitCode == 0;

	/// <summary>The sidecar at <paramref name="full"/> as <c>HEAD</c> has it; null if it does not have it, or it cannot be read.</summary>
	public DocSidecar? Sidecar(string full)
	{
		if (_git is null || !_commits || InRepository(full) is not { } path || _git.Execute("show", $"HEAD:{path}") is not { ExitCode: 0 } show)
			return null;
		try
		{
			using var json = DocSidecar.Parse(show.StandardOutput);
			return DocsSchemas.Validate(json.RootElement).Count == 0 ? DocSidecar.From(json.RootElement) : null;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// <summary>
	/// Whether <paramref name="file"/> is frozen in history: <c>HEAD</c> has it in a status its type freezes. Without git,
	/// true; a sidecar of <c>HEAD</c> that cannot be read, too.
	/// </summary>
	public bool Frozen(DocFile file)
	{
		if (_git is null)
			return true;
		if (!Has(file.Sidecar!))
			return false;
		return Sidecar(file.Sidecar!) is not { } committed || DocTypes.Find(committed.Type) is not { } type || type.IsFrozen(committed.Status);
	}
}
