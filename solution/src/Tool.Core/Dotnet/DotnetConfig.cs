using System;
using System.Collections.Generic;
using System.Linq;
using Fuxion.Tool.Core.Layout;

namespace Fuxion.Tool.Core.Dotnet;

/// <summary>
/// A variable of <c>dotnet.yaml</c> (design §7.3): the tool defines it and the repo uses it. A <c>value</c>, or a
/// <c>when</c> condition (true or false); a <c>when</c> one can also <c>define</c> compilation constants
/// (<c>OLD_FRAMEWORKS</c>, for <c>#if</c>) where its condition holds.
/// </summary>
public sealed record DotnetVariable(string Name, string? Value, string? When, FxAppliesTo AppliesTo, string Source, int Line, string? Define = null)
{
	public bool IsWhen => When is not null;
}

/// <summary>One version of a governed package, with the condition that selects it (D-32; none: the only version, for all).</summary>
public sealed record DotnetPackageVersion(string? When, string Version, int Line);

/// <summary>Packages that share their versions.</summary>
public sealed record DotnetPackage(IReadOnlyList<string> Ids, IReadOnlyList<DotnetPackageVersion> Versions, FxAppliesTo AppliesTo, string Source, int Line);

/// <summary>
/// An import of <c>dotnet.yaml</c>: plain MSBuild files, imported in the projects where <c>when</c> holds (all, without
/// it). <c>Props</c> go before the project file; <c>Targets</c> right after it, before the .NET SDK computes anything.
/// The paths are relative to the folder of the yaml that declares them; <c>Base</c> is that folder, relative to
/// <c>_fx/dotnet/</c>, where the generated files are (empty for the repository's own, <c>../.inherited/dotnet</c> for
/// what it inherits).
/// </summary>
public sealed record DotnetImport(IReadOnlyList<string> Props, IReadOnlyList<string> Targets, string? When, FxAppliesTo AppliesTo, string Source, int Line, string Base = "")
{
	/// <summary>Every file, relative to the folder of its yaml.</summary>
	public IEnumerable<string> Files => Props.Concat(Targets);

	/// <summary>
	/// The files that travel with it and are not imported (<c>files:</c>; the <c>icon.png</c> of <c>packable.targets</c>):
	/// the module gives a repository only the files of what applies to it (plan R, decision 2).
	/// </summary>
	public IReadOnlyList<string> Assets { get; init; } = [];

	/// <summary>What it imports and what travels with it.</summary>
	public IEnumerable<string> AllFiles => Files.Concat(Assets);

	/// <summary>A path of this import, relative to <c>_fx/dotnet/</c>.</summary>
	public string FromModule(string path) => Base.Length == 0 ? path : $"{Base}/{path}";
}

/// <summary>A folder of a generated solution, like <c>&lt;Folder Name&gt;</c>: other solutions whose content goes in it, and files.</summary>
public sealed record DotnetSolutionFolder(string Name, IReadOnlyList<string> Solutions, IReadOnlyList<string> Files, int Line);

/// <summary>A solution the root generates (plan P, decision 13), relative to the root, and its folders.</summary>
public sealed record DotnetSolution(string Path, IReadOnlyList<DotnetSolutionFolder> Folders, int Line);

/// <summary>
/// <c>_fx/dotnet/dotnet.yaml</c> (design §7.3, plan P): MSBuild SDKs, variables, imports and common packages, which go
/// down to the repositories of <see cref="AppliesTo"/>; and what does not go down: the solutions this root generates and
/// whether it builds on its own (<see cref="Standalone"/>).
/// </summary>
public sealed record DotnetConfig(
	IReadOnlyDictionary<string, string> Sdks,
	IReadOnlyList<DotnetVariable> Variables,
	IReadOnlyList<DotnetPackage> Packages,
	IReadOnlyList<DotnetImport> Imports)
{
	public static DotnetConfig Empty { get; } = new(new Dictionary<string, string>(), [], [], []);

	/// <summary>Who the configuration is for; null: it does not say (everyone, or what it inherits says).</summary>
	public FxAppliesTo? AppliesTo { get; init; }

	public IReadOnlyList<DotnetSolution> Solutions { get; init; } = [];

	public bool Standalone { get; init; }

	public bool Enabled { get; init; } = true;

	public bool IsEmpty => Sdks.Count == 0 && Variables.Count == 0 && Packages.Count == 0 && Imports.Count == 0;

	/// <summary>
	/// What comes from above with <paramref name="own"/> on top (plan P, decision 16: the nearest wins by name): by SDK
	/// name, variable name, package id and imported file; its <c>applies-to</c>, if it says one. The solutions and
	/// <c>standalone</c> are <paramref name="own"/>'s: they do not go down.
	/// </summary>
	public static DotnetConfig Combine(DotnetConfig inherited, DotnetConfig own)
	{
		var sdks = new Dictionary<string, string>(inherited.Sdks, StringComparer.OrdinalIgnoreCase);
		foreach (var (name, version) in own.Sdks)
			sdks[name] = version;

		var ownVariables = own.Variables.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var variables = inherited.Variables.Where(v => !ownVariables.Contains(v.Name)).Concat(own.Variables).ToList();

		var ownIds = own.Packages.SelectMany(p => p.Ids).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var packages = inherited.Packages
			.Select(p => p with { Ids = p.Ids.Where(id => !ownIds.Contains(id)).ToList() })
			.Where(p => p.Ids.Count > 0)
			.Concat(own.Packages)
			.ToList();

		var ownFiles = own.Imports.SelectMany(i => i.Files).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var imports = inherited.Imports
			.Select(i => i with { Props = i.Props.Where(p => !ownFiles.Contains(p)).ToList(), Targets = i.Targets.Where(t => !ownFiles.Contains(t)).ToList() })
			.Where(i => i.Props.Count + i.Targets.Count > 0)
			.Concat(own.Imports)
			.ToList();

		return new(sdks, variables, packages, imports)
		{
			AppliesTo = own.AppliesTo ?? inherited.AppliesTo,
			Solutions = own.Solutions,
			Standalone = own.Standalone
		};
	}

	/// <summary>What may be for the space <paramref name="who"/> or for something below it, to pass it on (plan R, decision 2).</summary>
	public DotnetConfig ForBelow(FxIdentity who) => this with
	{
		Variables = Variables.Where(v => v.AppliesTo.MayApplyBelow(who)).ToList(),
		Packages = Packages.Where(p => p.AppliesTo.MayApplyBelow(who)).ToList(),
		Imports = Imports.Where(i => i.AppliesTo.MayApplyBelow(who)).ToList()
	};

	/// <summary>Only what is for <paramref name="who"/>: what has no <c>applies-to</c>, or one that matches.</summary>
	public DotnetConfig ForTarget(FxIdentity who) => this with
	{
		Variables = Variables.Where(v => v.AppliesTo.Matches(who)).ToList(),
		Packages = Packages.Where(p => p.AppliesTo.Matches(who)).ToList(),
		Imports = Imports.Where(i => i.AppliesTo.Matches(who)).ToList()
	};

	/// <summary>Every <c>applies-to</c> with its repositories absolute, as written by <paramref name="writer"/> (decision 16).</summary>
	public DotnetConfig Absolute(string writer) => this with
	{
		AppliesTo = AppliesTo?.Absolute(writer),
		Variables = Variables.Select(v => v with { AppliesTo = v.AppliesTo.Absolute(writer) }).ToList(),
		Packages = Packages.Select(p => p with { AppliesTo = p.AppliesTo.Absolute(writer) }).ToList(),
		Imports = Imports.Select(i => i with { AppliesTo = i.AppliesTo.Absolute(writer) }).ToList()
	};

	/// <summary>The imports, every one from the folder <paramref name="base"/> (relative to <c>_fx/dotnet/</c>).</summary>
	public DotnetConfig WithBase(string @base) => this with { Imports = Imports.Select(i => i with { Base = @base }).ToList() };
}
