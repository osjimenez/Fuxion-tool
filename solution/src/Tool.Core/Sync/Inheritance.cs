using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Fuxion.Tool.Core.Dotnet;
using Fuxion.Tool.Core.Git;
using Fuxion.Tool.Core.Harness;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Workspace;

namespace Fuxion.Tool.Core.Sync;

/// <summary>
/// What a repository receives from its workspace, in its <c>_fx/.inherited/</c> (plan P, decisions 14-16): calculated
/// from its parent only, one level up and read-only (the parent's <c>_fx/.inherited/</c> and its own module folders),
/// already merged (the nearest wins by name), with the repositories of every <c>applies-to</c> made absolute. Nobody
/// writes in someone else's repository: each one gets this when its own sync runs.
/// <list type="bullet">
/// <item><c>fx.yaml</c>: <c>requires</c> and the catalog of tags.</item>
/// <item><c>dotnet/</c>: the merged <c>dotnet.yaml</c> of what is for it (without the solutions) and the files of those imports.</item>
/// <item><c>harness/</c>: the merged <c>harness.yaml</c> of what is for it, and the files of those rules and skills (plan Q).</item>
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
		var harness = Harness(parent, parentIdentity, identity, child.IsWorkspace, expected, FromOf, diagnostics);
		var flows = WorkspaceShares.Of(parent, parentIdentity, FromOf);
		var received = WorkspaceShares.For(flows, identity);
		if (child.IsWorkspace && flows.Count > 0)
			Conduit(child, flows, existingInherited, expected);
		var applies = dotnet || harness || child.IsWorkspace || received.Any;
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
		if (FxFiles.DirectoryExists(folder))
		{
			foreach (var path in FxFiles.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList())
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
					if (FxFiles.Exists(child.Full($"{FxLayout.Inherited}/{kept.Path}")))
						expected[kept.Path] = (FxFiles.ReadAllBytes(child.Full($"{FxLayout.Inherited}/{kept.Path}")), kept.From);
	}

	/// <summary>
	/// The .NET module: the parent's inherited <c>dotnet.yaml</c> with its own on top, what of it is for
	/// <paramref name="identity"/> (a workspace: what may be for it or below it), and the files of those imports from both
	/// folders (its own win by name; plan R, decision 2). Whether it applies.
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
			inherited = FxFiles.Exists(inheritedYaml) ? DotnetYamlReader.ReadFile(inheritedYaml) : DotnetConfig.Empty;
			own = FxFiles.Exists(ownYaml) ? DotnetYamlReader.ReadFile(ownYaml).Absolute(parentIdentity.FullName) : DotnetConfig.Empty;
		}
		catch (DotnetConfigException ex)
		{
			diagnostics.AddRange(ex.Diagnostics.Select(d => d with { Code = InvalidParent, File = d.File is null ? null : FxLayout.Relative(parent.Root, d.File) }));
			return false;
		}
		var ownOn = FxFiles.DirectoryExists(ownFolder) && own.Enabled;
		if (!FxFiles.Exists(inheritedYaml) && !ownOn)
			return false;
		var merged = DotnetConfig.Combine(inherited, ownOn ? own : DotnetConfig.Empty) with { Solutions = [], Standalone = false };
		if (!conduit && !(merged.AppliesTo ?? FxAppliesTo.All).Matches(identity))
			return false;

		// only what is for it (a workspace: what may be for it or below it), and only the files of those imports, what they
		// import and what travels with them (plan R, decision 2); its own win by name
		var going = conduit ? merged.ForBelow(identity) : merged.ForTarget(identity);
		var files = new SortedDictionary<string, (byte[] Content, IReadOnlyList<string> From)>(StringComparer.OrdinalIgnoreCase);
		foreach (var relative in going.Imports.SelectMany(i => i.AllFiles).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			var ownFile = Path.Combine(ownFolder, relative);
			var inheritedFile = Path.Combine(inheritedFolder, relative);
			if (ownOn && FxFiles.Exists(ownFile))
				files[relative] = (FxFiles.ReadAllBytes(ownFile), [parentIdentity.FullName]);
			else if (FxFiles.Exists(inheritedFile))
				files[relative] = (FxHeader.Strip(FxFiles.ReadAllBytes(inheritedFile)), fromOf($"{module}/{relative}"));
			// a file that is in neither: the doctor of the parent says so (dotnet.missing-import)
		}

		var yamlFrom = new List<string>();
		if (FxFiles.Exists(inheritedYaml))
			yamlFrom.AddRange(fromOf($"{module}/{module}.yaml"));
		if (ownOn && FxFiles.Exists(ownYaml))
			yamlFrom.Add(parentIdentity.FullName);
		yamlFrom = yamlFrom.Distinct().ToList();
		expected[$"{module}/{module}.yaml"] = (Encoding.UTF8.GetBytes(DotnetYamlWriter.Inherited(going, Workspaces(yamlFrom, $"_fx/{module}/{module}.yaml"))), yamlFrom);
		foreach (var (relative, (content, from)) in files)
			expected[$"{module}/{relative}"] = (FxHeader.Add(relative, content, Workspaces(from, $"_fx/{module}/{relative}")), from);
		return true;
	}

	/// <summary>
	/// The harness (plan Q): the parent's inherited <c>harness.yaml</c> with its own on top, and only what is for
	/// <paramref name="identity"/> (proposal 1: what is only for others, a rule of the workspace itself, does not reach a
	/// public repository): the yaml of that, and the files of its rules and skills. A workspace gets what may be for it or
	/// for something below it, to pass it on (<see cref="HarnessConfig.ForBelow"/>). Whether it applies.
	/// </summary>
	static bool Harness(FxNode parent, FxIdentity parentIdentity, FxIdentity identity, bool conduit,
		SortedDictionary<string, (byte[] Content, IReadOnlyList<string> From)> expected, Func<string, IReadOnlyList<string>> fromOf, FxDiagnostics diagnostics)
	{
		const string module = HarnessModule.Name;
		var inheritedYaml = parent.Full(HarnessModule.InheritedConfigFile);
		var ownYaml = parent.Full(HarnessModule.ConfigFile);
		HarnessConfig inherited, own;
		try
		{
			inherited = FxFiles.Exists(inheritedYaml) ? HarnessYamlReader.ReadFile(inheritedYaml) : HarnessConfig.Empty;
			own = FxFiles.Exists(ownYaml) ? HarnessYamlReader.ReadFile(ownYaml).Absolute(parentIdentity.FullName) : HarnessConfig.Empty;
		}
		catch (HarnessConfigException ex)
		{
			diagnostics.AddRange(ex.Diagnostics.Select(d => d with { Code = InvalidParent, File = d.File is null ? null : FxLayout.Relative(parent.Root, d.File) }));
			return false;
		}
		if (!own.Enabled)
			own = HarnessConfig.Empty;
		var merged = HarnessConfig.Combine(inherited.WithBase(HarnessModule.InheritedFolder), own.WithBase(HarnessModule.Folder));
		if (!conduit && !(merged.AppliesTo ?? FxAppliesTo.All).Matches(identity))
			return false;
		var going = conduit ? merged.ForBelow(identity) : merged.ForTarget(identity);
		if (going.IsEmpty)
			return false;

		// the files of what goes down, from where each one is in the parent (what is not there, its doctor says)
		var inheritedBase = HarnessModule.InheritedFolder;
		IReadOnlyList<string> From(string @base, string relative) => @base == inheritedBase ? fromOf($"{module}/{relative}") : [parentIdentity.FullName];
		foreach (var rule in going.Rules)
		{
			var file = parent.Full(rule.File);
			var relative = $"{HarnessModule.RulesFolder}/{rule.Name}.md";
			if (FxFiles.Exists(file))
				expected[$"{module}/{relative}"] = (FxFiles.ReadAllBytes(file), From(rule.Base, relative));
		}
		foreach (var skill in going.Skills)
		{
			var folder = parent.Full(skill.Folder);
			if (!FxFiles.DirectoryExists(folder))
				continue;
			foreach (var file in FxFiles.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
			{
				var inside = FxLayout.Relative(folder, file);
				if (inside.Split('/').Any(FxLayout.IsLocal))
					continue;
				var relative = $"{HarnessModule.SkillsFolder}/{skill.Name}/{inside}";
				expected[$"{module}/{relative}"] = (FxFiles.ReadAllBytes(file), From(skill.Base, relative));
			}
		}

		var yamlFrom = new List<string>();
		if (FxFiles.Exists(inheritedYaml))
			yamlFrom.AddRange(fromOf($"{module}/{module}.yaml"));
		if (FxFiles.Exists(ownYaml) && own.Enabled && !own.IsEmpty)
			yamlFrom.Add(parentIdentity.FullName);
		yamlFrom = yamlFrom.Distinct().ToList();
		expected[$"{module}/{module}.yaml"] = (Encoding.UTF8.GetBytes(HarnessYamlWriter.Inherited(going, Workspaces(yamlFrom, $"_fx/{module}/{module}.yaml"))), yamlFrom);
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
