using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using Fuxion.Tools.Core.Git;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Results;

namespace Fuxion.Tools.Core.Workspace;

/// <summary>
/// <c>_fx/dotnet/~$workspace.props</c> of the highest workspace on disk (plan P, decision 19 and proposal C): the map
/// Fuxion.Tools.Sdk uses to resolve <c>FxReference</c>. For each project versioned in the mounted repositories (named
/// by its file, like the package): a <c>FxWorkspaceProject</c> item, and a <c>ProjectReference</c> built from the
/// <c>FxReference</c> with that name, so it keeps its metadata. Absolute paths: local (<c>~$</c>), generated.
/// </summary>
public static class WorkspacePropsGenerator
{
	public const string FileName = "_fx/dotnet/~$workspace.props";
	public const string DuplicateProject = "workspace.duplicate-project";

	public static string Generate(IReadOnlyDictionary<string, (string Path, string Repository)> projects)
	{
		var b = new StringBuilder(FxHeader.Xml("the projects of the mounted repositories"));
		b.Append("<Project>\n");
		b.Append("\t<ItemGroup>\n");
		foreach (var (name, (path, repository)) in projects.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
		{
			var n = SecurityElement.Escape(name);
			var p = SecurityElement.Escape(path);
			b.Append($"\t\t<FxWorkspaceProject Include=\"{n}\" Repository=\"{SecurityElement.Escape(repository)}\" Path=\"{p}\" />\n");
			b.Append($"\t\t<ProjectReference Include=\"@(FxReference->WithMetadataValue('Identity', '{n}')->'{p}')\" />\n");
		}
		b.Append("\t</ItemGroup>\n");
		b.Append("</Project>\n");
		return b.ToString();
	}

	/// <summary>
	/// The projects versioned in each root (by name: the file name, like the package id; without case): full path and
	/// repository. The first root that brings a name wins, with a warning.
	/// </summary>
	public static Dictionary<string, (string Path, string Repository)> Projects(IEnumerable<(FxNode Node, FxIdentity Identity)> roots, FxDiagnostics diagnostics)
	{
		var projects = new Dictionary<string, (string Path, string Repository)>(StringComparer.OrdinalIgnoreCase);
		foreach (var (node, identity) in roots)
		{
			if (GitClient.Discover(node.Root) is not { } git || !FxLayout.SameFolder(git.Root, node.Root))
				continue;
			foreach (var project in git.ListFiles(":(glob)**/*.csproj", ":(glob)**/*.fsproj", ":(glob)**/*.vbproj").Order(StringComparer.Ordinal))
			{
				var full = Path.GetFullPath(Path.Combine(node.Root, project));
				var name = Path.GetFileNameWithoutExtension(full);
				if (projects.TryGetValue(name, out var existing))
				{
					if (!string.Equals(existing.Path, full, StringComparison.OrdinalIgnoreCase))
						diagnostics.Add(FxDiagnostic.Warning(DuplicateProject, new(DuplicateProject, name, existing.Repository, identity.Name), repository: identity.Name));
					continue;
				}
				projects[name] = (full, identity.Name);
			}
		}
		return projects;
	}
}
