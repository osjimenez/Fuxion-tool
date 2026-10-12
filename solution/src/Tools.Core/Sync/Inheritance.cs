using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Fuxion.Tools.Core.Dotnet;
using Fuxion.Tools.Core.Git;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Workspace;

namespace Fuxion.Tools.Core.Sync;

/// <summary>
/// What a repository receives from its workspace, in its <c>_fx/.inherited/</c> (plan P, decisions 14-16): calculated
/// from its parent only, one level up and read-only (the parent's <c>_fx/.inherited/</c> and its own module folders),
/// already merged (the nearest wins by name), with the repositories of every <c>applies-to</c> made absolute. Nobody
/// writes in someone else's repository: each one gets this when its own sync runs.
/// <list type="bullet">
/// <item><c>fx.yaml</c>: <c>requires</c> and the catalog of tags.</item>
/// <item><c>dotnet/</c>: the merged <c>dotnet.yaml</c> (without the solutions) and the files of the module folders.</item>
/// <item><c>inherited.yaml</c>: who the repository is for its parent, whose it is and where each file comes from.</item>
/// </list>
/// Only where something applies (a module, a share) or to a workspace, which passes it on: a repository nothing applies
/// to gets nothing (plan P, P1, detail 1).
/// </summary>
public static class Inheritance
{
	public const string InvalidParent = "workspace.invalid-parent";

	/// <summary>What <paramref name="child"/> (declared by <paramref name="parent"/> as <paramref name="identity"/>) receives, written or checked.</summary>
	/// <param name="parentRef">The branch read of a parent read from its remote (on disk, the one it has).</param>
	public static void Run(FxNode parent, FxIdentity parentIdentity, FxNode child, FxIdentity identity, FxOutputs outputs, FxDiagnostics diagnostics,
		string? parentRef = null)
	{
		var expected = new SortedDictionary<string, (byte[] Content, IReadOnlyList<string> From)>(StringComparer.Ordinal);
		var parentInherited = parent.Inherited();
		IReadOnlyList<string> FromOf(string relative)
			=> parentInherited?.Files.FirstOrDefault(f => string.Equals(f.Path, relative, StringComparison.OrdinalIgnoreCase))?.From ?? [parentInherited?.Parent.FullName ?? parentIdentity.FullName];

		var dotnet = Dotnet(parent, parentIdentity, identity, child.IsWorkspace, expected, FromOf, diagnostics);
		var applies = dotnet || child.IsWorkspace;
		if (applies)
		{
			// fx.yaml: requires and the catalog, from above and from the parent
			var inheritedConfig = FxConfig.Load(parent.Root, $"{FxLayout.Inherited}/fx.yaml", []);
			var ownConfig = parent.OwnConfig();
			var from = new List<string>();
			if (inheritedConfig.Requires is not null || inheritedConfig.Tags.Count > 0)
				from.AddRange(FromOf("fx.yaml"));
			if (ownConfig.Requires is not null || ownConfig.Tags.Count > 0)
				from.Add(parentIdentity.FullName);
			var merged = FxConfig.Merge(inheritedConfig, ownConfig, FxLayout.ConfigFile);
			expected["fx.yaml"] = (Encoding.UTF8.GetBytes(merged.ToInheritedYaml(Workspaces(from, "_fx/fx.yaml"))), from.Distinct().ToList());
		}

		var folder = child.Full(FxLayout.Inherited);
		var existingInherited = child.Inherited();
		if (applies)
		{
			var git = GitClient.Discover(parent.Root);
			var inherited = new FxInherited(identity,
				new(parentIdentity.FullName, git?.RemoteUrl("origin"), parentRef ?? git?.CurrentBranch(), git?.Head() is { Length: >= 7 } head ? head[..7] : null),
				expected.Select(e => new FxInheritedFile(e.Key, e.Value.From)).ToList(), []);
			var before = outputs.Files.Count;
			foreach (var (relative, (content, _)) in expected)
				outputs.Owned(Path.Combine(folder, relative), content);
			// the branch and the commit of the parent only change if what is inherited changes
			if (inherited.SameContent(existingInherited) && outputs.Files.Skip(before).All(f => f.Status == SyncFileStatus.Unchanged))
				inherited = inherited with { Parent = existingInherited!.Parent };
			outputs.Owned(Path.Combine(folder, "inherited.yaml"), Encoding.UTF8.GetBytes(inherited.ToYaml()));
		}

		// What the parent no longer gives: everything in .inherited/ is fx's
		if (Directory.Exists(folder))
		{
			foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList())
			{
				var relative = FxLayout.Relative(folder, path);
				if (!applies || (!expected.ContainsKey(relative) && relative != "inherited.yaml"))
					outputs.Delete(path);
			}
			outputs.Prune(folder);
		}
	}

	/// <summary>
	/// The .NET module: the parent's inherited <c>dotnet.yaml</c> with its own on top, and the files of both folders
	/// (its own win by name), if they are for <paramref name="identity"/> or it is a workspace. Whether it applies.
	/// </summary>
	static bool Dotnet(FxNode parent, FxIdentity parentIdentity, FxIdentity identity, bool conduit,
		SortedDictionary<string, (byte[] Content, IReadOnlyList<string> From)> expected, Func<string, IReadOnlyList<string>> fromOf, FxDiagnostics diagnostics)
	{
		const string module = DotnetModule.Name;
		var inheritedFolder = parent.Full(FxLayout.InheritedModuleFolder(module));
		var ownFolder = parent.Full(FxLayout.ModuleFolder(module));
		var inheritedYaml = Path.Combine(inheritedFolder, $"{module}.yaml");
		var ownYaml = Path.Combine(ownFolder, $"{module}.yaml");
		DotnetConfig inherited, own;
		try
		{
			inherited = File.Exists(inheritedYaml) ? DotnetYamlReader.ReadFile(inheritedYaml) : DotnetConfig.Empty;
			own = File.Exists(ownYaml) ? DotnetYamlReader.ReadFile(ownYaml).Absolute(parentIdentity.FullName) : DotnetConfig.Empty;
		}
		catch (DotnetConfigException ex)
		{
			diagnostics.AddRange(ex.Diagnostics.Select(d => d with { Code = InvalidParent, File = d.File is null ? null : FxLayout.Relative(parent.Root, d.File) }));
			return false;
		}
		var ownOn = Directory.Exists(ownFolder) && own.Enabled;
		if (!File.Exists(inheritedYaml) && !ownOn)
			return false;
		var merged = DotnetConfig.Combine(inherited, ownOn ? own : DotnetConfig.Empty) with { Solutions = [], Standalone = false };
		if (!conduit && !(merged.AppliesTo ?? FxAppliesTo.All).Matches(identity))
			return false;

		var files = new SortedDictionary<string, (byte[] Content, IReadOnlyList<string> From)>(StringComparer.OrdinalIgnoreCase);
		if (Directory.Exists(inheritedFolder))
			foreach (var file in Directory.EnumerateFiles(inheritedFolder, "*", SearchOption.AllDirectories))
			{
				var relative = FxLayout.Relative(inheritedFolder, file);
				if (relative != $"{module}.yaml")
					files[relative] = (FxHeader.Strip(File.ReadAllBytes(file)), fromOf($"{module}/{relative}"));
			}
		if (ownOn)
			foreach (var file in Directory.EnumerateFiles(ownFolder, "*", SearchOption.AllDirectories))
			{
				var relative = FxLayout.Relative(ownFolder, file);
				// not its yaml, not what fx generates there for the parent itself, not its local files
				if (relative == $"{module}.yaml" || relative.Split('/').Any(FxLayout.IsLocal) || Path.GetFileName(relative).Contains(".g.", StringComparison.OrdinalIgnoreCase))
					continue;
				files[relative] = (File.ReadAllBytes(file), [parentIdentity.FullName]);
			}

		var yamlFrom = new List<string>();
		if (File.Exists(inheritedYaml))
			yamlFrom.AddRange(fromOf($"{module}/{module}.yaml"));
		if (ownOn && File.Exists(ownYaml))
			yamlFrom.Add(parentIdentity.FullName);
		yamlFrom = yamlFrom.Distinct().ToList();
		expected[$"{module}/{module}.yaml"] = (Encoding.UTF8.GetBytes(DotnetYamlWriter.Inherited(merged, Workspaces(yamlFrom, $"_fx/{module}/{module}.yaml"))), yamlFrom);
		foreach (var (relative, (content, from)) in files)
			expected[$"{module}/{relative}"] = (FxHeader.Add(relative, content, Workspaces(from, $"_fx/{module}/{relative}")), from);
		return true;
	}

	/// <summary>The <c>Generated from</c> of a file: the workspace (or workspaces) it comes from and the file there.</summary>
	static string Workspaces(IReadOnlyList<string> from, string file)
		=> from.Count switch
		{
			0 => file,
			1 => $"workspace {from[0]} ({file})",
			_ => $"workspaces {string.Join(" and ", from)} ({file} of each)"
		};
}
