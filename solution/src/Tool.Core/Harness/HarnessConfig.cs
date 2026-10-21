using System;
using System.Collections.Generic;
using System.Linq;
using Fuxion.Tool.Core.Layout;

namespace Fuxion.Tool.Core.Harness;

/// <summary>
/// A rule of <c>harness.yaml</c> (plan Q): Markdown for the coding agents, in <c>rules/&lt;name&gt;.md</c> of the folder of
/// the module that declares it. Without <c>paths</c>, it goes in <c>AGENTS.md</c> (always read); with them, in
/// <c>.claude/rules/&lt;name&gt;.md</c>, read when the agent works on those files, and in the index of <c>AGENTS.md</c>
/// (decision 5). <c>Base</c> is that folder, relative to the root (<c>_fx/harness</c>, or <c>_fx/.inherited/harness</c>
/// for what it inherits).
/// </summary>
public sealed record HarnessRule(string Name, IReadOnlyList<string> Paths, FxAppliesTo AppliesTo, string Source, int Line, string Base = "")
{
	public bool ByPath => Paths.Count > 0;

	/// <summary>Its file, relative to the root.</summary>
	public string File => $"{Base}/{HarnessModule.RulesFolder}/{Name}.md";
}

/// <summary>A skill of <c>harness.yaml</c> (plan Q): the folder <c>skills/&lt;name&gt;/</c>, with its <c>SKILL.md</c> and whatever else it needs.</summary>
public sealed record HarnessSkill(string Name, FxAppliesTo AppliesTo, string Source, int Line, string Base = "")
{
	/// <summary>Its folder, relative to the root.</summary>
	public string Folder => $"{Base}/{HarnessModule.SkillsFolder}/{Name}";
}

/// <summary>
/// <c>_fx/harness/harness.yaml</c> (plan Q): the rules and skills of the coding agents, each with its <c>applies-to</c>,
/// which go down to the repositories of <see cref="AppliesTo"/> (everyone, without it). As in <c>dotnet.yaml</c>, the
/// <c>applies-to</c> of an item also decides whether it is for the root that declares it (decision 3).
/// </summary>
public sealed record HarnessConfig(IReadOnlyList<HarnessRule> Rules, IReadOnlyList<HarnessSkill> Skills)
{
	public static HarnessConfig Empty { get; } = new([], []);

	/// <summary>Who the module is for; null: it does not say (everyone, or what it inherits says).</summary>
	public FxAppliesTo? AppliesTo { get; init; }

	public bool Enabled { get; init; } = true;

	public bool IsEmpty => Rules.Count == 0 && Skills.Count == 0;

	/// <summary>
	/// What comes from above with <paramref name="own"/> on top: the nearest wins by name (proposal 2). The order is the
	/// one of <c>AGENTS.md</c>: what is inherited first, then its own, each in the order of its list.
	/// </summary>
	public static HarnessConfig Combine(HarnessConfig inherited, HarnessConfig own)
	{
		var ownRules = own.Rules.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var ownSkills = own.Skills.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
		return new(inherited.Rules.Where(r => !ownRules.Contains(r.Name)).Concat(own.Rules).ToList(),
			inherited.Skills.Where(s => !ownSkills.Contains(s.Name)).Concat(own.Skills).ToList())
		{
			AppliesTo = own.AppliesTo ?? inherited.AppliesTo
		};
	}

	/// <summary>Only what is for <paramref name="who"/>: what has no <c>applies-to</c>, or one that matches.</summary>
	public HarnessConfig ForTarget(FxIdentity who) => this with
	{
		Rules = Rules.Where(r => r.AppliesTo.Matches(who)).ToList(),
		Skills = Skills.Where(s => s.AppliesTo.Matches(who)).ToList()
	};

	/// <summary>
	/// What may be for the space <paramref name="who"/> or for something below it (proposal 1): by tags (its
	/// repositories have tags its parent does not know), for everyone, or naming it or a repository below it.
	/// </summary>
	public HarnessConfig ForBelow(FxIdentity who) => this with
	{
		Rules = Rules.Where(r => r.AppliesTo.MayApplyBelow(who)).ToList(),
		Skills = Skills.Where(s => s.AppliesTo.MayApplyBelow(who)).ToList()
	};

	/// <summary>Every <c>applies-to</c> with its repositories absolute, as written by <paramref name="writer"/>.</summary>
	public HarnessConfig Absolute(string writer) => this with
	{
		AppliesTo = AppliesTo?.Absolute(writer),
		Rules = Rules.Select(r => r with { AppliesTo = r.AppliesTo.Absolute(writer) }).ToList(),
		Skills = Skills.Select(s => s with { AppliesTo = s.AppliesTo.Absolute(writer) }).ToList()
	};

	/// <summary>Every rule and skill from the folder <paramref name="base"/> (relative to the root).</summary>
	public HarnessConfig WithBase(string @base) => this with
	{
		Rules = Rules.Select(r => r with { Base = @base }).ToList(),
		Skills = Skills.Select(s => s with { Base = @base }).ToList()
	};
}
