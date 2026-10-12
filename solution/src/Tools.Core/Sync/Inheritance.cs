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
/// <item><c>workspace/</c>, in a workspace: the shares, to pass them on (<see cref="WorkspaceShares"/>).</item>
/// <item><c>inherited.yaml</c>: who the repository is for its parent, whose it is and where each file comes from,
/// also the shares it receives, written outside <c>_fx/</c>.</item>
/// </list>
/// Only where something applies (a module, a share) or to a workspace, which passes it on: a repository nothing applies
/// to gets nothing (plan P, P1, detail 1).
/// </summary>
public static class Inheritance
{
	public const string InvalidParent = "workspace.invalid-parent";

	/// <summary>
	/// What <paramref name="child"/> (declared by <paramref name="parent"/> as <paramref name="identity"/>) receives,
	/// written or checked. Returns the destinations of the shares that are fx's in it now.
	/// </summary>
	/// <param name="parentRef">The branch read of a parent read from its remote (on disk, the one it has).</param>
	public static IReadOnlyList<string> Run(FxNode parent, FxIdentity parentIdentity, FxNode child, FxIdentity identity, FxOutputs outputs, FxDiagnostics diagnostics,
		string? parentRef = null)
	{
		var expected = new SortedDictionary<string, (byte[] Content, IReadOnlyList<string> From)>(StringComparer.Ordinal);
		var parentInherited = parent.Inherited();
		IReadOnlyList<string> FromOf(string relative)
			=> parentInherited?.Files.FirstOrDefault(f => string.Equals(f.Path, relative, StringComparison.OrdinalIgnoreCase))?.From ?? [parentInherited?.Parent.FullName ?? parentIdentity.FullName];
		var existingInherited = child.Inherited();

		var dotnet = Dotnet(parent, parentIdentity, identity, child.IsWorkspace, expected, FromOf, diagnostics);
		var flows = WorkspaceShares.Of(parent, parentIdentity, FromOf);
		var received = WorkspaceShares.For(flows, identity);
		if (child.IsWorkspace && flows.Count > 0)
			Conduit(child, flows, existingInherited, expected);
		var applies = dotnet || child.IsWorkspace || received.Any;
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

		// the shares in the repository itself, outside _fx/ (also removing what it no longer gets)
		var shares = WorkspaceShares.Apply(child, received, existingInherited, outputs, diagnostics);

		var folder = child.Full(FxLayout.Inherited);
		if (applies)
		{
			var git = GitClient.Discover(parent.Root);
			var inherited = new FxInherited(identity,
				new(parentIdentity.FullName, git?.RemoteUrl("origin"), parentRef ?? git?.CurrentBranch(), git?.Head() is { Length: >= 7 } head ? head[..7] : null),
				expected.Select(e => new FxInheritedFile(e.Key, e.Value.From)).ToList(), shares);
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
		return shares.Select(s => s.Destination).ToList();
	}

	/// <summary>
	/// A workspace passes the shares on (decision 23): in its <c>.inherited/workspace/</c>, the merged entries and their
	/// files, under their destination. Of a share whose source is not there, it keeps what it had.
	/// </summary>
	static void Conduit(FxNode child, IReadOnlyList<ShareFlow> flows, FxInherited? existing,
		SortedDictionary<string, (byte[] Content, IReadOnlyList<string> From)> expected)
	{
		var folder = WorkspaceShares.Folder;
		var froms = flows.SelectMany(f => f.Files?.Select(file => file.From) ?? []).Distinct().ToList();
		expected[$"{folder}/{folder}.yaml"] = (Encoding.UTF8.GetBytes(WorkspaceShares.ToYaml(flows, Workspaces(froms, $"_fx/{folder}/{folder}.yaml"))), froms);
		foreach (var flow in flows)
			if (flow.Files is not null)
				foreach (var file in flow.Files)
					expected[$"{folder}/{file.Destination}"] = (file.Content, [file.From]);
			else
				foreach (var kept in (existing?.Files ?? []).Where(f => f.Path.StartsWith(folder + "/", StringComparison.Ordinal)
				                                                     && WorkspaceShares.Under(f.Path[(folder.Length + 1)..], flow.Share.Destination)))
					if (File.Exists(child.Full($"{FxLayout.Inherited}/{kept.Path}")))
						expected[kept.Path] = (File.ReadAllBytes(child.Full($"{FxLayout.Inherited}/{kept.Path}")), kept.From);
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
