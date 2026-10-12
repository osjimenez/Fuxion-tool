using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Yaml;

namespace Fuxion.Tools.Core.Workspace;

public enum MountPolicy
{
	/// <summary>Mounted when the developer says so (<c>fx repo mount</c>), or adopted if its clone is already there.</summary>
	Manual,
	/// <summary>Always mounted.</summary>
	Mandatory
}

/// <summary>
/// A repository the workspace mounts (plan P, decisions 1, 10-12 and 18): a final repository or another workspace.
/// The workspace gives it its name and its tags.
/// </summary>
/// <param name="Path">Where, relative to the workspace, with '/': any folder, at any depth.</param>
/// <param name="Inherits">False for a guest: the workspace uses it, but it inherits from its own parent.</param>
/// <param name="Aliases">Its old names (<c>fx repo rename</c>), so that old references keep working.</param>
public sealed record WorkspaceRepository(
	string Name,
	string Path,
	IReadOnlyList<string> Tags,
	string? Url = null,
	MountPolicy Mount = MountPolicy.Manual,
	IReadOnlyList<string>? DependsOn = null,
	bool Inherits = true,
	IReadOnlyList<string>? Aliases = null,
	int Line = 0);

/// <summary>
/// What the git of the workspace versions (plan P, decision 8): a file, or a folder (ending in '/'). With
/// <see cref="Exclude"/>, an exception inside something included; with <see cref="Worktree"/> false, a folder the
/// worktrees of the workspace leave out unless asked (decision 26).
/// </summary>
public sealed record WorkspacePath(string Path, bool Exclude = false, bool Worktree = true, int Line = 0)
{
	public bool IsFolder => Path.EndsWith('/');
}

/// <summary>A file (or folder) the workspace gives the repositories below it (plan P, decision 23).</summary>
public sealed record WorkspaceShare(string Path, string Destination, FxAppliesTo AppliesTo, int Line = 0);

/// <summary>An invalid workspace manifest: every problem found, each with its line.</summary>
public sealed class WorkspaceManifestException(IReadOnlyList<FxDiagnostic> diagnostics)
	: Exception(string.Join(Environment.NewLine, diagnostics.Select(d => d.WhereIn(CultureInfo.InvariantCulture))))
{
	public IReadOnlyList<FxDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>
/// <c>_fx/workspace/workspace.yaml</c>, the module that makes a root of fx a workspace (plan P, decision 3): the
/// repositories it mounts, what its git versions (<c>paths</c>) and what it shares. Nothing about solutions or .NET.
/// </summary>
public sealed record WorkspaceManifest(
	string Root,
	IReadOnlyList<WorkspaceRepository> Repositories,
	IReadOnlyList<WorkspacePath>? Paths = null,
	IReadOnlyList<WorkspaceShare>? Shares = null)
{
	public const string InvalidManifest = "workspace.invalid-manifest";
	public const string ModuleName = "workspace";
	public static readonly string RelativePath = FxLayout.ModuleFile(ModuleName);

	/// <summary>The first segments a path of a repository cannot take: fx's and git's.</summary>
	public static readonly IReadOnlyList<string> ReservedSegments = [FxLayout.Folder, ".git", ".gitignore"];

	/// <summary>Whether the root at <paramref name="root"/> is a workspace: it has the module, and it is not off.</summary>
	public static bool IsWorkspace(string root)
	{
		var file = FxLayout.Full(root, RelativePath);
		if (!Directory.Exists(FxLayout.Full(root, FxLayout.ModuleFolder(ModuleName))))
			return false;
		if (!File.Exists(file))
			return true;
		try
		{
			return YamlDocument.Load(File.ReadAllText(file)) is not YamlMapping map || map["enabled"] is not YamlScalar { Value: "false" };
		}
		catch (YamlSyntaxException)
		{
			return true;
		}
	}

	/// <summary>The nearest workspace that contains <paramref name="directory"/> (itself included), if any.</summary>
	public static string? FindRoot(string directory)
	{
		for (var dir = new DirectoryInfo(System.IO.Path.GetFullPath(directory)); dir is not null; dir = dir.Parent)
			if (IsWorkspace(dir.FullName))
				return FxLayout.Normalize(dir.FullName);
		return null;
	}

	/// <summary>Reads and validates the manifest of the workspace at <paramref name="root"/>.</summary>
	public static WorkspaceManifest Load(string root)
	{
		var file = FxLayout.Full(root, RelativePath);
		return Read(root, File.Exists(file) ? File.ReadAllText(file) : "", strict: true);
	}

	/// <summary>
	/// Reads a manifest. Lenient by default (what other modules need: repositories, paths and tags); with
	/// <paramref name="strict"/>, every problem is an error with its line.
	/// </summary>
	public static WorkspaceManifest Read(string root, string yaml, bool strict = false)
	{
		var reader = new FxYamlReader(InvalidManifest, RelativePath);
		var map = reader.Load(yaml);
		if (map is null)
		{
			if (strict)
				throw new WorkspaceManifestException(reader.Diagnostics);
			return new(root, []);
		}
		reader.Keys(map, "schema", "enabled", "repositories", "paths", "shares");

		var repositories = new List<WorkspaceRepository>();
		if (map["repositories"] is YamlSequence list)
			foreach (var item in list.Items)
			{
				if (item is not YamlMapping repo)
				{
					reader.Error(item, "repository-mapping");
					continue;
				}
				reader.Keys(repo, "name", "url", "path", "mount", "tags", "depends-on", "inherits", "aliases");
				var name = FxYamlReader.Scalar(repo["name"]);
				var path = FxYamlReader.Scalar(repo["path"])?.Replace('\\', '/').Trim('/');
				if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
				{
					reader.Error(item, "name-and-path");
					continue;
				}
				var mountText = FxYamlReader.Scalar(repo["mount"]);
				var mount = mountText is null or "manual" ? MountPolicy.Manual : mountText == "mandatory" ? MountPolicy.Mandatory : (MountPolicy?)null;
				if (mount is null)
					reader.Error(item, "mount", name, mountText);
				repositories.Add(new(name, path, FxYamlReader.List(repo["tags"]), FxYamlReader.Scalar(repo["url"]), mount ?? MountPolicy.Manual,
					FxYamlReader.List(repo["depends-on"]), reader.Bool(repo, "inherits", true), FxYamlReader.List(repo["aliases"]), item.Line));
			}
		else if (map["repositories"] is { } notAList && notAList is not YamlScalar { Value: "" })
			reader.Error(notAList, "list", "repositories");

		var paths = new List<WorkspacePath>();
		if (map["paths"] is YamlSequence pathList)
			foreach (var item in pathList.Items)
			{
				if (item is not YamlMapping entry || FxYamlReader.Scalar(entry["path"]) is not { Length: > 0 } path)
				{
					reader.Error(item, "path-entry");
					continue;
				}
				reader.Keys(entry, "path", "exclude", "worktree");
				paths.Add(new(path.Replace('\\', '/').TrimStart('/'), reader.Bool(entry, "exclude", false), reader.Bool(entry, "worktree", true), item.Line));
			}
		else if (map["paths"] is { } notAList && notAList is not YamlScalar { Value: "" })
			reader.Error(notAList, "list", "paths");

		var shares = new List<WorkspaceShare>();
		if (map["shares"] is YamlSequence shareList)
			foreach (var item in shareList.Items)
			{
				if (item is not YamlMapping entry || FxYamlReader.Scalar(entry["path"]) is not { Length: > 0 } path)
				{
					reader.Error(item, "share-entry");
					continue;
				}
				reader.Keys(entry, "path", "destination", "applies-to");
				path = path.Replace('\\', '/').TrimStart('/');
				var destination = FxYamlReader.Scalar(entry["destination"])?.Replace('\\', '/').TrimStart('/') ?? path;
				shares.Add(new(path, destination, FxAppliesTo.Read(entry, reader) ?? FxAppliesTo.All, item.Line));
			}

		var manifest = new WorkspaceManifest(root, repositories, paths, shares);
		if (strict)
		{
			var errors = reader.Diagnostics.Concat(manifest.Validate()).ToList();
			if (errors.Any(d => d.Severity == FxSeverity.Error))
				throw new WorkspaceManifestException(errors);
		}
		return manifest;
	}

	IEnumerable<FxDiagnostic> Validate()
	{
		FxDiagnostic Error(int line, string key, params object?[] args) => FxDiagnostic.Error(InvalidManifest, new($"{InvalidManifest}.{key}", args), RelativePath, line);

		static bool BadPath(string path)
			=> System.IO.Path.IsPathRooted(path) || path.TrimEnd('/').Split('/').Any(s => s is "" or "." or "..");

		foreach (var repo in Repositories)
		{
			if (!FxConfig.NamePattern().IsMatch(repo.Name))
				yield return Error(repo.Line, "name", repo.Name);
			if (BadPath(repo.Path))
				yield return Error(repo.Line, "path-relative", repo.Name, repo.Path);
			var segments = repo.Path.Split('/');
			if (ReservedSegments.Contains(segments[0], StringComparer.OrdinalIgnoreCase) || segments.Any(FxLayout.IsLocal))
				yield return Error(repo.Line, "path-reserved", repo.Name, repo.Path);
			if (string.IsNullOrWhiteSpace(repo.Url))
				yield return Error(repo.Line, "url", repo.Name);
			foreach (var dependency in repo.DependsOn ?? [])
				// an old name still works (its doctor warns)
				if (!Repositories.Any(r => r.Name == dependency || (r.Aliases ?? []).Contains(dependency)))
					yield return Error(repo.Line, "depends-on", repo.Name, dependency);
			foreach (var alias in repo.Aliases ?? [])
				if (!FxConfig.NamePattern().IsMatch(alias))
					yield return Error(repo.Line, "name", alias);
		}
		foreach (var duplicate in Repositories.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			yield return Error(duplicate.Last().Line, "duplicate-name", duplicate.Key);
		// an old name is still a name: it cannot be another repository's
		foreach (var repo in Repositories)
			foreach (var alias in (repo.Aliases ?? []).Where(a => Repositories.Any(o => o != repo && (o.Name == a || (o.Aliases ?? []).Contains(a)))))
				yield return Error(repo.Line, "duplicate-name", alias);
		// two paths that only differ in case are the same folder on Windows and macOS
		foreach (var duplicate in Repositories.GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			yield return Error(duplicate.Last().Line, "duplicate-path", duplicate.Key);
		// a repository inside another: the one that contains it has to declare it (if it is a workspace)
		foreach (var repo in Repositories)
			foreach (var other in Repositories.Where(o => o != repo && o.Path.StartsWith(repo.Path + "/", StringComparison.OrdinalIgnoreCase)))
				yield return Error(other.Line, "nested", other.Name, repo.Name);
		foreach (var path in Paths ?? [])
		{
			if (BadPath(path.Path))
				yield return Error(path.Line, "path-relative", path.Path, path.Path);
			else if (FxLayout.IsLocal(path.Path.Split('/')[0]) || path.Path.TrimEnd('/') is FxLayout.Folder or ".git" or ".gitignore")
				yield return Error(path.Line, "path-fixed", path.Path);
			// inside a repository it is that repository's, not the workspace's
			if (Repositories.FirstOrDefault(r => path.Path.StartsWith(r.Path + "/", StringComparison.OrdinalIgnoreCase)) is { } inside)
				yield return Error(path.Line, "path-in-repository", path.Path, inside.Name);
		}
		foreach (var duplicate in (Paths ?? []).GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			yield return Error(duplicate.Last().Line, "duplicate-path-entry", duplicate.Key);
	}

	/// <summary>Full path of a repository's mount point, with a trailing separator.</summary>
	public string FullPath(WorkspaceRepository repository) => FxLayout.Normalize(System.IO.Path.Combine(Root, repository.Path));

	/// <summary>The repository mounted at <paramref name="repositoryRoot"/>, if the manifest lists it.</summary>
	public WorkspaceRepository? RepositoryAt(string repositoryRoot)
		=> Repositories.FirstOrDefault(r => FxLayout.SameFolder(FullPath(r), repositoryRoot));

	/// <summary>
	/// Whether <paramref name="relative"/> (a path of the workspace, with '/') is versioned by its <c>paths</c>: the
	/// longest entry that contains it decides (an exclusion inside an inclusion wins).
	/// </summary>
	public bool Versions(string relative)
	{
		var match = (Paths ?? [])
			.Where(p => p.IsFolder
				? relative.StartsWith(p.Path, StringComparison.OrdinalIgnoreCase) || string.Equals(relative.TrimEnd('/') + "/", p.Path, StringComparison.OrdinalIgnoreCase)
				: string.Equals(relative.TrimEnd('/'), p.Path, StringComparison.OrdinalIgnoreCase))
			.MaxBy(p => p.Path.Length);
		return match is { Exclude: false };
	}
}
