using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Fuxion.Tools.Core.Diagnostics;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Fuxion.Tools.Core.Workspace;

public enum MountPolicy
{
	/// <summary>Mounted when the developer says so (<c>fx repo mount</c>), or adopted if its clone is already there.</summary>
	Manual,
	/// <summary>Always mounted.</summary>
	Mandatory
}

/// <summary>A repository of the workspace manifest (design §5).</summary>
/// <param name="Path">Mount point, relative to the workspace root, with '/' (like <c>plus/repo</c>).</param>
/// <param name="Solution">Its solution, relative to the repo (<c>FxPlus.slnx</c>).</param>
/// <param name="SolutionExclude">Paths (relative to the repo) whose projects and files do not go into the workspace solution.</param>
public sealed record WorkspaceRepository(
	string Name,
	string Path,
	IReadOnlyList<string> Tags,
	string? Url = null,
	MountPolicy Mount = MountPolicy.Manual,
	bool Standalone = false,
	IReadOnlyList<string>? DependsOn = null,
	string? Solution = null,
	IReadOnlyList<string>? SolutionExclude = null,
	int Line = 0);

/// <summary>
/// A module of fx used by the workspace (<c>modules:</c> of the manifest, plan N decision 17): the repositories with one of
/// its tags (all of them, without tags) receive its configuration and its folder in <c>_fx/.workspace/</c>.
/// </summary>
public sealed record WorkspaceModuleSettings(string Name, IReadOnlyList<string> Tags, int Line = 0)
{
	public bool AppliesTo(WorkspaceRepository repository)
		=> Tags.Count == 0 || Tags.Any(t => repository.Tags.Contains(t, StringComparer.OrdinalIgnoreCase));
}

/// <summary>An invalid workspace manifest: every problem found, each with its line.</summary>
public sealed class WorkspaceManifestException(IReadOnlyList<FxDiagnostic> diagnostics)
	: Exception(string.Join(Environment.NewLine, diagnostics.Select(d => d.Where)))
{
	public IReadOnlyList<FxDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary><c>_fx/workspace.yaml</c>, the workspace manifest (design §5).</summary>
public sealed partial record WorkspaceManifest(
	string Root,
	IReadOnlyList<WorkspaceRepository> Repositories,
	string? Name = null,
	int? Major = null,
	string? MinimumFx = null,
	IReadOnlyList<string>? Folders = null,
	IReadOnlyList<WorkspaceModuleSettings>? Modules = null)
{
	public const string InvalidManifest = "workspace.invalid-manifest";
	public const string RelativePath = "_fx/workspace.yaml";

	/// <summary>The modules a workspace can use (<c>modules:</c>).</summary>
	public static readonly IReadOnlyList<string> KnownModules = ["dotnet"];

	/// <summary>
	/// First segments a mount point cannot take: the metarepo's own folders and files (design §4.3), and its
	/// <c>~$</c> local files.
	/// </summary>
	public static readonly IReadOnlyList<string> ReservedSegments =
	[
		"_fx", "_docs", ".git", ".github", ".claude", ".vs", ".vscode", ".gitignore", ".gitattributes", ".mcp.json",
		"agents.md", "claude.md", "readme.md", "global.json", "nuget.config", "fuxion.slnx"
	];

	[GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
	private static partial Regex RepositoryName();

	/// <summary>The workspace that contains <paramref name="directory"/> (a folder with <c>_fx/workspace.yaml</c> above it), if any.</summary>
	public static WorkspaceManifest? FindAbove(string directory)
		=> FindRoot(directory) is { } root ? Read(root, File.ReadAllText(System.IO.Path.Combine(root, "_fx", "workspace.yaml"))) : null;

	/// <summary>The root of the workspace that contains <paramref name="directory"/>, if any.</summary>
	public static string? FindRoot(string directory)
	{
		for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
			if (File.Exists(System.IO.Path.Combine(dir.FullName, "_fx", "workspace.yaml")))
				return System.IO.Path.TrimEndingDirectorySeparator(dir.FullName) + System.IO.Path.DirectorySeparatorChar;
		return null;
	}

	/// <summary>Reads and validates the manifest of the workspace at <paramref name="root"/>.</summary>
	public static WorkspaceManifest Load(string root)
	{
		var file = System.IO.Path.Combine(root, "_fx", "workspace.yaml");
		var manifest = Read(root, File.ReadAllText(file), strict: true);
		return manifest;
	}

	/// <summary>
	/// Reads a manifest. Lenient by default (what other modules need: repositories, paths and tags); with
	/// <paramref name="strict"/>, every problem is an error with its line.
	/// </summary>
	public static WorkspaceManifest Read(string root, string yaml, bool strict = false)
	{
		var errors = new List<FxDiagnostic>();
		void Error(YamlNode? node, string message) => errors.Add(FxDiagnostic.Error(InvalidManifest, message, RelativePath, node is null ? null : (int)node.Start.Line));

		var stream = new YamlStream();
		try
		{
			stream.Load(new StringReader(yaml));
		}
		catch (YamlException ex)
		{
			if (strict)
				throw new WorkspaceManifestException([FxDiagnostic.Error(InvalidManifest, ex.Message, RelativePath, (int)ex.Start.Line)]);
			return new(root, []);
		}

		if (stream.Documents.FirstOrDefault()?.RootNode is not YamlMappingNode map)
		{
			if (strict)
				throw new WorkspaceManifestException([FxDiagnostic.Error(InvalidManifest, "The manifest must be a mapping (version, workspace, folders, repositories).", RelativePath, 1)]);
			return new(root, []);
		}

		string? name = null;
		int? major = null;
		string? fx = null;
		if (Child(map, "workspace") is YamlMappingNode workspace)
		{
			name = Scalar(Child(workspace, "name"));
			major = int.TryParse(Scalar(Child(workspace, "major")), out var m) ? m : null;
			fx = Scalar(Child(workspace, "fx"));
		}
		var folders = Child(map, "folders") is YamlSequenceNode folderList
			? folderList.Children.Select(Scalar).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!).ToList()
			: [];
		foreach (var folder in folders.Where(f => f.Contains('/') || f.Contains('\\')))
			Error(Child(map, "folders"), $"Folder '{folder}' must be a single segment.");

		var modules = new List<WorkspaceModuleSettings>();
		if (Child(map, "modules") is YamlMappingNode moduleMap)
			foreach (var (key, value) in moduleMap.Children)
			{
				var moduleName = Scalar(key) ?? "";
				if (!KnownModules.Contains(moduleName))
					Error(key, $"Unknown module '{moduleName}' ({string.Join(", ", KnownModules)}).");
				modules.Add(new(moduleName, value is YamlMappingNode settings ? List(Child(settings, "tags")) : [], (int)key.Start.Line));
			}
		else if (Child(map, "modules") is { } notAMap && notAMap is not YamlScalarNode { Value: null or "" })
			Error(notAMap, "'modules' maps module names to their settings (dotnet: { tags: [dotnet] }).");

		var repositories = new List<WorkspaceRepository>();
		if (Child(map, "repositories") is YamlSequenceNode list)
			foreach (var item in list.Children)
			{
				if (item is not YamlMappingNode repo)
				{
					Error(item, "A repository is a mapping: name, path, url…");
					continue;
				}
				var repoName = Scalar(Child(repo, "name"));
				var path = Scalar(Child(repo, "path"))?.Replace('\\', '/').Trim('/');
				if (string.IsNullOrWhiteSpace(repoName) || string.IsNullOrWhiteSpace(path))
				{
					Error(item, "Every repository needs 'name' and 'path'.");
					continue;
				}
				var mountText = Scalar(Child(repo, "mount"));
				var mount = mountText is null or "manual" ? MountPolicy.Manual : mountText == "mandatory" ? MountPolicy.Mandatory : (MountPolicy?)null;
				if (mount is null)
					Error(item, $"Repository '{repoName}': mount is 'mandatory' or 'manual', not '{mountText}'.");
				repositories.Add(new(
					repoName,
					path,
					List(Child(repo, "tags")),
					Scalar(Child(repo, "url")),
					mount ?? MountPolicy.Manual,
					string.Equals(Scalar(Child(repo, "standalone")), "true", StringComparison.OrdinalIgnoreCase),
					List(Child(repo, "dependsOn")),
					Scalar(Child(repo, "solution"))?.Replace('\\', '/'),
					List(Child(repo, "solutionExclude")).Select(p => p.Replace('\\', '/').TrimStart('/')).ToList(),
					(int)item.Start.Line));
			}

		var manifest = new WorkspaceManifest(root, repositories, name, major, fx, folders, modules);
		if (strict)
		{
			errors.AddRange(manifest.Validate());
			if (errors.Count > 0)
				throw new WorkspaceManifestException(errors);
		}
		return manifest;
	}

	IEnumerable<FxDiagnostic> Validate()
	{
		FxDiagnostic Error(WorkspaceRepository repo, string message) => FxDiagnostic.Error(InvalidManifest, message, RelativePath, repo.Line);

		foreach (var repo in Repositories)
		{
			if (!RepositoryName().IsMatch(repo.Name))
				yield return Error(repo, $"Repository name '{repo.Name}': lowercase letters, digits and '-' (like 'plus' or 'my-repo').");
			if (repo.Path != repo.Path.ToLowerInvariant())
				yield return Error(repo, $"Repository '{repo.Name}': the path '{repo.Path}' must be lowercase.");
			var segments = repo.Path.Split('/');
			if (segments.Any(s => s is "" or "." or "..") || System.IO.Path.IsPathRooted(repo.Path))
				yield return Error(repo, $"Repository '{repo.Name}': the path '{repo.Path}' must be relative, inside the workspace, without '.' or '..'.");
			if (ReservedSegments.Contains(segments[0], StringComparer.OrdinalIgnoreCase) || segments[0].StartsWith("~$", StringComparison.Ordinal)
			    || (Folders ?? []).Contains(segments[0], StringComparer.OrdinalIgnoreCase))
				yield return Error(repo, $"Repository '{repo.Name}': '{segments[0]}' is reserved for the metarepo.");
			if (string.IsNullOrWhiteSpace(repo.Url))
				yield return Error(repo, $"Repository '{repo.Name}' needs 'url'.");
			foreach (var dependency in repo.DependsOn ?? [])
				if (!Repositories.Any(r => r.Name == dependency))
					yield return Error(repo, $"Repository '{repo.Name}' depends on '{dependency}', which the manifest does not list.");
		}
		foreach (var duplicate in Repositories.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			yield return Error(duplicate.Last(), $"Repository name '{duplicate.Key}' is used more than once.");
		foreach (var duplicate in Repositories.GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			yield return Error(duplicate.Last(), $"Path '{duplicate.Key}' is used more than once.");
		foreach (var repo in Repositories)
			foreach (var other in Repositories.Where(o => o != repo && o.Path.StartsWith(repo.Path + "/", StringComparison.OrdinalIgnoreCase)))
				yield return Error(other, $"Repository '{other.Name}' is mounted inside '{repo.Name}'.");
	}

	/// <summary>The settings of a module, if the workspace uses it.</summary>
	public WorkspaceModuleSettings? Module(string name) => (Modules ?? []).FirstOrDefault(m => m.Name == name);

	/// <summary>Full path of a repository's mount point, with a trailing separator.</summary>
	public string FullPath(WorkspaceRepository repository)
		=> System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, repository.Path))) + System.IO.Path.DirectorySeparatorChar;

	/// <summary>The repository mounted at <paramref name="repositoryRoot"/>, if the manifest lists it.</summary>
	public WorkspaceRepository? RepositoryAt(string repositoryRoot)
	{
		var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(repositoryRoot));
		return Repositories.FirstOrDefault(r => string.Equals(System.IO.Path.TrimEndingDirectorySeparator(FullPath(r)), full, StringComparison.OrdinalIgnoreCase));
	}

	static YamlNode? Child(YamlMappingNode map, string key) => map.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;

	static string? Scalar(YamlNode? node) => (node as YamlScalarNode)?.Value?.Trim();

	static IReadOnlyList<string> List(YamlNode? node)
		=> node is YamlSequenceNode list ? list.Children.Select(Scalar).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList() : [];
}
