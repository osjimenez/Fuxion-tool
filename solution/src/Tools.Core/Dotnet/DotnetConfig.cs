using System;
using System.Collections.Generic;
using System.Linq;

namespace Fuxion.Tools.Core.Dotnet;

/// <summary>
/// A variable of <c>dotnet.yaml</c> (design §7.3): the tool defines it and the repo uses it. A <c>value</c>, or a
/// <c>when</c> condition (true or false); a <c>when</c> one can also <c>define</c> compilation constants
/// (<c>OLD_FRAMEWORKS</c>, for <c>#if</c>) where its condition holds.
/// </summary>
public sealed record DotnetVariable(string Name, string? Value, string? When, IReadOnlyList<string> Tags, string Source, int Line, string? Define = null)
{
	public bool IsWhen => When is not null;
}

/// <summary>One version of a governed package, with the condition (always explicit, D-32) that selects it.</summary>
public sealed record DotnetPackageVersion(string When, string Version, int Line);

/// <summary>Packages that share their versions.</summary>
public sealed record DotnetPackage(IReadOnlyList<string> Ids, IReadOnlyList<DotnetPackageVersion> Versions, IReadOnlyList<string> Tags, string Source, int Line);

/// <summary>
/// An import of <c>dotnet.yaml</c> (plan N, decision 15): plain MSBuild files of the repo or the workspace, imported in
/// the projects where <c>when</c> holds (all, without it). <c>Props</c> go before the project file; <c>Targets</c> right
/// after it, before the .NET SDK computes anything. The paths are relative to the folder of the yaml that declares them,
/// which is <c>Base</c> relative to <c>_fx/</c> (empty for the repo's <c>_fx/dotnet.yaml</c>, <c>.workspace</c> for the
/// copy of the workspace's).
/// </summary>
public sealed record DotnetImport(IReadOnlyList<string> Props, IReadOnlyList<string> Targets, string? When, IReadOnlyList<string> Tags, string Source, int Line, string Base = "")
{
	/// <summary>Every file, relative to <c>_fx/</c>.</summary>
	public IEnumerable<string> Files => Props.Concat(Targets).Select(FromFx);

	/// <summary>A path of this import, relative to <c>_fx/</c>.</summary>
	public string FromFx(string path) => Base.Length == 0 ? path : $"{Base}/{path}";
}

/// <summary><c>_fx/dotnet.yaml</c>: MSBuild SDKs, variables, imports and common packages (design §7.3).</summary>
public sealed record DotnetConfig(
	IReadOnlyDictionary<string, string> Sdks,
	IReadOnlyList<DotnetVariable> Variables,
	IReadOnlyList<DotnetPackage> Packages,
	IReadOnlyList<DotnetImport> Imports)
{
	public static DotnetConfig Empty { get; } = new(new Dictionary<string, string>(), [], [], []);

	/// <summary>
	/// The workspace layer (<c>_fx/.workspace/dotnet.yaml</c>) with the repo on top: the repo wins by SDK name, variable
	/// name and package id; the imports of both apply, the workspace's first (design §7.3).
	/// </summary>
	public static DotnetConfig Combine(DotnetConfig workspace, DotnetConfig repo)
	{
		var sdks = new Dictionary<string, string>(workspace.Sdks, StringComparer.OrdinalIgnoreCase);
		foreach (var (name, version) in repo.Sdks)
			sdks[name] = version;

		var repoVariables = repo.Variables.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var variables = workspace.Variables.Where(v => !repoVariables.Contains(v.Name)).Concat(repo.Variables).ToList();

		var repoIds = repo.Packages.SelectMany(p => p.Ids).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var packages = workspace.Packages
			.Select(p => p with { Ids = p.Ids.Where(id => !repoIds.Contains(id)).ToList() })
			.Where(p => p.Ids.Count > 0)
			.Concat(repo.Packages)
			.ToList();

		return new(sdks, variables, packages, workspace.Imports.Concat(repo.Imports).ToList());
	}

	/// <summary>Only what applies to a repo with these tags: what has no tags, or shares one (design §7.3).</summary>
	public DotnetConfig ForTags(IReadOnlyCollection<string> repoTags)
	{
		bool Applies(IReadOnlyList<string> tags) => tags.Count == 0 || tags.Any(t => repoTags.Contains(t, StringComparer.OrdinalIgnoreCase));
		return this with
		{
			Variables = Variables.Where(v => Applies(v.Tags)).ToList(),
			Packages = Packages.Where(p => Applies(p.Tags)).ToList(),
			Imports = Imports.Where(i => Applies(i.Tags)).ToList()
		};
	}

	/// <summary>The imports, every one from the folder <paramref name="base"/> (relative to <c>_fx/</c>).</summary>
	public DotnetConfig WithBase(string @base) => this with { Imports = Imports.Select(i => i with { Base = @base }).ToList() };
}
