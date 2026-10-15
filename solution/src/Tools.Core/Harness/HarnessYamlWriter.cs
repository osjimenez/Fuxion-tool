using System.Collections.Generic;
using System.Linq;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Yaml;

namespace Fuxion.Tools.Core.Harness;

/// <summary>
/// Writes the <c>harness.yaml</c> a repository receives in <c>_fx/.inherited/harness/</c>: what goes down to it (proposal
/// 1), already merged, with the repositories of every <c>applies-to</c> absolute. Without <c>enabled</c> and without
/// comments.
/// </summary>
public static class HarnessYamlWriter
{
	public static string Inherited(HarnessConfig config, string from)
	{
		var entries = new List<(string, YamlValue)> { ("schema", FxYamlReader.Schema) };
		if (config.AppliesTo?.ToYaml() is { } appliesTo)
			entries.Add(("applies-to", appliesTo));
		if (config.Rules.Count > 0)
			entries.Add(("rules", new YamlValue.Block(config.Rules.Select(Rule).ToArray())));
		if (config.Skills.Count > 0)
			entries.Add(("skills", new YamlValue.Block(config.Skills.Select(Skill).ToArray())));
		return FxHeader.Hash(from) + YamlWriter.Document(new(entries.ToArray()));
	}

	static YamlValue Rule(HarnessRule rule)
	{
		var entries = new List<(string, YamlValue)> { ("name", rule.Name) };
		if (rule.ByPath)
			entries.Add(("paths", new YamlValue.Flow(rule.Paths.Select(p => (YamlValue)p).ToArray())));
		AppliesTo(entries, rule.AppliesTo);
		return new YamlValue.Map(entries.ToArray());
	}

	static YamlValue Skill(HarnessSkill skill)
	{
		var entries = new List<(string, YamlValue)> { ("name", skill.Name) };
		AppliesTo(entries, skill.AppliesTo);
		return new YamlValue.Map(entries.ToArray());
	}

	static void AppliesTo(List<(string, YamlValue)> entries, FxAppliesTo appliesTo)
	{
		if (appliesTo.ToYaml() is { } value)
			entries.Add(("applies-to", value));
	}
}
