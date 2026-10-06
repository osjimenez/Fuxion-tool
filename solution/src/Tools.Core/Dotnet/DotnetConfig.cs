using System;
using System.Collections.Generic;
using System.Linq;

namespace Fuxion.Tools.Core.Dotnet;

/// <summary>A variable of <c>dotnet.yaml</c> (design §7.3): a <c>value</c>, or a <c>when</c> condition.</summary>
public sealed record DotnetVariable(string Name, string? Value, string? When, IReadOnlyList<string> Tags, string Source, int Line)
{
	public bool IsWhen => When is not null;
}

/// <summary>One version of a governed package, with the condition (always explicit, D-32) that selects it.</summary>
public sealed record DotnetPackageVersion(string When, string Version, int Line);

/// <summary>Packages that share their versions.</summary>
public sealed record DotnetPackage(IReadOnlyList<string> Ids, IReadOnlyList<DotnetPackageVersion> Versions, IReadOnlyList<string> Tags, string Source, int Line);

/// <summary><c>_fx/dotnet.yaml</c>: MSBuild SDKs, variables and common packages (design §7.3).</summary>
public sealed record DotnetConfig(
	IReadOnlyDictionary<string, string> Sdks,
	IReadOnlyList<DotnetVariable> Variables,
	IReadOnlyList<DotnetPackage> Packages)
{
	public static DotnetConfig Empty { get; } = new(new Dictionary<string, string>(), [], []);

	/// <summary>
	/// The workspace layer (<c>_fx/.workspace/dotnet.yaml</c>) with the repo on top: the repo wins by SDK name, variable
	/// name and package id (design §7.3).
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

		return new(sdks, variables, packages);
	}

	/// <summary>Only what applies to a repo with these tags: what has no tags, or shares one (design §7.3).</summary>
	public DotnetConfig ForTags(IReadOnlyCollection<string> repoTags)
	{
		bool Applies(IReadOnlyList<string> tags) => tags.Count == 0 || tags.Any(t => repoTags.Contains(t, StringComparer.OrdinalIgnoreCase));
		return this with
		{
			Variables = Variables.Where(v => Applies(v.Tags)).ToList(),
			Packages = Packages.Where(p => Applies(p.Tags)).ToList()
		};
	}
}
