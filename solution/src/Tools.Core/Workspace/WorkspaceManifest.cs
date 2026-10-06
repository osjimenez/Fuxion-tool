using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.RepresentationModel;

namespace Fuxion.Tools.Core.Workspace;

/// <summary>A repository of the workspace manifest (design §5).</summary>
public sealed record WorkspaceRepository(string Name, string Path, IReadOnlyList<string> Tags);

/// <summary>
/// <c>_fx/workspace.yaml</c>, the part K4 needs: the repositories, with their mount paths and tags. The rest of the
/// manifest comes with the workspace module (phase 3).
/// </summary>
public sealed record WorkspaceManifest(string Root, IReadOnlyList<WorkspaceRepository> Repositories)
{
	/// <summary>The workspace that contains <paramref name="directory"/> (a folder with <c>_fx/workspace.yaml</c> above it), if any.</summary>
	public static WorkspaceManifest? FindAbove(string directory)
	{
		for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
		{
			var file = System.IO.Path.Combine(dir.FullName, "_fx", "workspace.yaml");
			if (File.Exists(file))
				return Read(dir.FullName, File.ReadAllText(file));
		}
		return null;
	}

	public static WorkspaceManifest Read(string root, string yaml)
	{
		var stream = new YamlStream();
		stream.Load(new StringReader(yaml));
		var repositories = new List<WorkspaceRepository>();
		if (stream.Documents.FirstOrDefault()?.RootNode is YamlMappingNode map
		    && map.Children.TryGetValue(new YamlScalarNode("repositories"), out var node) && node is YamlSequenceNode list)
			foreach (var item in list.Children.OfType<YamlMappingNode>())
			{
				string? Get(string key) => item.Children.TryGetValue(new YamlScalarNode(key), out var v) ? (v as YamlScalarNode)?.Value : null;
				var tags = item.Children.TryGetValue(new YamlScalarNode("tags"), out var t) && t is YamlSequenceNode tagList
					? tagList.Children.OfType<YamlScalarNode>().Select(s => s.Value ?? "").Where(s => s.Length > 0).ToList()
					: [];
				if (Get("name") is { Length: > 0 } name && Get("path") is { Length: > 0 } path)
					repositories.Add(new(name, path, tags));
			}
		return new(root, repositories);
	}

	/// <summary>The repository mounted at <paramref name="repositoryRoot"/>, if the manifest lists it.</summary>
	public WorkspaceRepository? RepositoryAt(string repositoryRoot)
	{
		var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(repositoryRoot));
		return Repositories.FirstOrDefault(r => string.Equals(
			System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, r.Path))), full, StringComparison.OrdinalIgnoreCase));
	}
}
