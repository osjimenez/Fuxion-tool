using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Yaml;

namespace Fuxion.Tools.Core.Harness;

/// <summary>An invalid <c>harness.yaml</c>: every problem found, each with its line.</summary>
public sealed class HarnessConfigException(IReadOnlyList<FxDiagnostic> diagnostics)
	: Exception(string.Join(Environment.NewLine, diagnostics.Select(d => d.WhereIn(CultureInfo.InvariantCulture))))
{
	public IReadOnlyList<FxDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>
/// Reads <c>harness.yaml</c> with fx's YAML reader and validates it. Its warnings (an unknown key of a newer schema, no
/// schema) go to the list of <see cref="Read"/>; its errors, in a <see cref="HarnessConfigException"/>.
/// </summary>
public static partial class HarnessYamlReader
{
	public const string InvalidYaml = "harness.invalid-yaml";

	/// <summary>A name that is a file or folder name everywhere: letters, digits, '.', '-' and '_'.</summary>
	[GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]*$")]
	private static partial Regex ItemName();

	public static HarnessConfig ReadFile(string path, ICollection<FxDiagnostic>? warnings = null) => Read(FxFiles.ReadAllText(path), path, warnings);

	public static HarnessConfig Read(string yaml, string source, ICollection<FxDiagnostic>? warnings = null)
	{
		var reader = new FxYamlReader(InvalidYaml, source);
		var map = reader.Load(yaml);
		if (map is null)
			throw new HarnessConfigException(reader.Diagnostics);
		reader.Keys(map, "schema", "enabled", "applies-to", "rules", "skills");

		var rules = new List<HarnessRule>();
		var skills = new List<HarnessSkill>();
		foreach (var item in Sequence(map["rules"], "rules", reader))
			if (ReadRule(item, source, reader) is { } rule)
				rules.Add(rule);
		foreach (var item in Sequence(map["skills"], "skills", reader))
			if (ReadSkill(item, source, reader) is { } skill)
				skills.Add(skill);
		foreach (var duplicate in rules.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			reader.Diagnostics.Add(FxDiagnostic.Error(InvalidYaml, new($"{InvalidYaml}.duplicate-rule", duplicate.Key), source, duplicate.Last().Line));
		foreach (var duplicate in skills.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			reader.Diagnostics.Add(FxDiagnostic.Error(InvalidYaml, new($"{InvalidYaml}.duplicate-skill", duplicate.Key), source, duplicate.Last().Line));

		var config = new HarnessConfig(rules, skills)
		{
			AppliesTo = FxAppliesTo.Read(map, reader),
			Enabled = reader.Enabled(map)
		};
		if (reader.HasErrors)
			throw new HarnessConfigException(reader.Diagnostics);
		foreach (var warning in reader.Diagnostics)
			warnings?.Add(warning);
		return config;
	}

	static IEnumerable<YamlNode> Sequence(YamlNode? node, string key, FxYamlReader reader)
	{
		switch (node)
		{
			case null:
			case YamlScalar { Value: "" }:
				return [];
			case YamlSequence sequence:
				return sequence.Items;
			default:
				reader.Error(node, "list", key);
				return [];
		}
	}

	/// <summary>The name of a rule or a skill: a valid file name, and not <c>fx</c>, which is fx's own (proposal 7).</summary>
	static string? Name(YamlMapping map, YamlNode node, string kind, FxYamlReader reader)
	{
		var name = FxYamlReader.Scalar(map["name"]);
		if (string.IsNullOrWhiteSpace(name) || !ItemName().IsMatch(name))
		{
			reader.Error(node, $"{kind}-name", name);
			return null;
		}
		if (string.Equals(name, HarnessModule.Reserved, StringComparison.OrdinalIgnoreCase))
		{
			reader.Error(node, "reserved-name", name);
			return null;
		}
		return name;
	}

	static HarnessRule? ReadRule(YamlNode node, string source, FxYamlReader reader)
	{
		if (node is not YamlMapping map)
		{
			reader.Error(node, "rule-mapping");
			return null;
		}
		reader.Keys(map, "name", "paths", "applies-to");
		var name = Name(map, node, "rule", reader);
		var paths = new List<string>();
		if (map["paths"] is { } pathsNode)
		{
			if (pathsNode is YamlSequence or YamlScalar { Value.Length: > 0 } && FxYamlReader.List(pathsNode) is { Count: > 0 } list && list.All(p => p.Length > 0))
				paths.AddRange(list.Select(p => p.Replace('\\', '/')));
			else
				reader.Error(pathsNode, "paths", name);
		}
		var appliesTo = FxAppliesTo.Read(map, reader) ?? FxAppliesTo.All;
		return name is null ? null : new(name, paths, appliesTo, source, node.Line);
	}

	static HarnessSkill? ReadSkill(YamlNode node, string source, FxYamlReader reader)
	{
		if (node is not YamlMapping map)
		{
			reader.Error(node, "skill-mapping");
			return null;
		}
		reader.Keys(map, "name", "applies-to");
		var name = Name(map, node, "skill", reader);
		var appliesTo = FxAppliesTo.Read(map, reader) ?? FxAppliesTo.All;
		return name is null ? null : new(name, appliesTo, source, node.Line);
	}
}
