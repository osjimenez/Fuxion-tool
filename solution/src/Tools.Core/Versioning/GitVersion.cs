using System;
using Fuxion.Tools.Core.Results;

namespace Fuxion.Tools.Core.Versioning;

/// <summary>Which rule of the versioning scheme (design §9) gave the version.</summary>
public enum GitVersionRule
{
	/// <summary><c>main</c>/<c>master</c>: <c>X.Y.{commits since the tag}</c>.</summary>
	Stable,
	/// <summary><c>develop</c>: <c>X.Y.{commits since the tag}-alpha</c>.</summary>
	Develop,
	/// <summary><c>feature/x</c>: <c>X.Y.{commits from the tag to the merge base}-feature.x.{commits since main was last merged}</c>.</summary>
	Feature,
	/// <summary><c>release/x</c>: <c>{tag}-rc.x.{commits since the tag}</c>.</summary>
	Release,
	/// <summary><c>preview/x</c>: <c>{tag}-preview.x.{commits since the tag}</c>.</summary>
	Preview,
	/// <summary>Any other branch, or a detached HEAD: <c>X.Y.{commits since the tag}-{branch}</c>.</summary>
	Other
}

/// <summary>How a git version was calculated: what <c>fx version --explain</c> shows.</summary>
/// <param name="Repository">Working tree root.</param>
/// <param name="Branch">Current branch, or <c>(no branch)</c> for a detached HEAD.</param>
/// <param name="Head">Commit of HEAD.</param>
/// <param name="StableTip">Commit of the local <c>master</c> or <c>main</c> (HEAD if there is neither).</param>
/// <param name="MergeBase">Merge base of HEAD and the stable branch (feature branches only).</param>
/// <param name="TagName">The version tag the version starts from.</param>
/// <param name="TagCommit">The commit of that tag.</param>
/// <param name="CommitsSinceTag">Commits from the tag to HEAD, or to the merge base for a feature branch.</param>
/// <param name="BranchCommits">Commits on a feature branch since main was last merged into it (feature only).</param>
public sealed record GitVersionDetails(
	string Repository,
	string Branch,
	GitVersionRule Rule,
	string Head,
	string StableTip,
	string? MergeBase,
	string TagName,
	string TagCommit,
	int CommitsSinceTag,
	int? BranchCommits);

/// <summary>A version and, when it comes from git, how it was calculated.</summary>
public sealed record VersioningResult(VersioningInputs Inputs, GitVersionDetails? Details);

/// <summary>Stable codes of the versioning errors (design §10.4: diagnostics with a code, not text to parse).</summary>
public static class VersioningErrorCodes
{
	public const string RepositoryNotFound = "version.repository-not-found";
	public const string NoCommits = "version.no-commits";
	public const string NoTag = "version.no-tag";
	public const string InvalidTag = "version.invalid-tag";
	public const string GitFailed = "version.git-failed";
}

/// <summary>
/// A versioning error, with its text for the user (<see cref="Text"/>); its message is that text in English (for
/// MSBuild and the logs; the versioning ones start with <c>Git versioning failed:</c>, as they always have).
/// </summary>
public sealed class VersioningException(string code, FxText text, Exception? inner = null)
	: InvalidOperationException(text.English, inner)
{
	public string Code { get; } = code;

	public FxText Text { get; } = text;

	/// <summary>The error as a diagnostic, with its fix when there is one.</summary>
	public FxDiagnostic ToDiagnostic() => FxDiagnostic.Error(Code, Text, fix: Code switch
	{
		VersioningErrorCodes.NoTag => new FxFix("fx version tag <X.Y>", new($"{VersioningErrorCodes.NoTag}.fix")),
		VersionTagger.TagNotNewer => FxFix.Do($"{VersionTagger.TagNotNewer}.fix"),
		_ => null
	});
}
