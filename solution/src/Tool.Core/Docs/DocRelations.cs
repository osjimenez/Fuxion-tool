using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Fuxion.Tool.Core.Docs;

/// <summary>What a kind of relation asks of the types it joins: the types its source and its target may be; null, any.</summary>
public sealed record DocRelationRule(string Kind, IReadOnlyList<string>? Sources, IReadOnlyList<string>? Targets);

/// <summary>
/// The kinds of relation of the standard and the types each one joins, from the annotation <c>x-docs-relation-rules</c>
/// of their definition in the schemas (the metadata model, section 8; plan U, ADR "esquemas cerrados, con costuras").
/// </summary>
public static class DocRelations
{
	static readonly Lazy<(IReadOnlyList<string> Kinds, IReadOnlyDictionary<string, DocRelationRule> Rules)> Loaded = new(Load);

	public static IReadOnlyList<string> Kinds => Loaded.Value.Kinds;

	/// <summary>The rule of <paramref name="kind"/>: what its source and its target may be.</summary>
	public static DocRelationRule Rule(string kind) => Loaded.Value.Rules.GetValueOrDefault(kind) ?? new(kind, null, null);

	/// <summary>Whether a <paramref name="source"/> may have a relation <paramref name="kind"/> with a <paramref name="target"/>.</summary>
	public static bool Allows(string kind, string source, string target)
		=> Rule(kind) is var rule && (rule.Sources is null || rule.Sources.Contains(source)) && (rule.Targets is null || rule.Targets.Contains(target));

	static (IReadOnlyList<string>, IReadOnlyDictionary<string, DocRelationRule>) Load()
	{
		using var common = JsonDocument.Parse(DocsSchemas.Texts["common.schema.json"]);
		var kind = common.RootElement.GetProperty("$defs").GetProperty("relationKind");
		var kinds = kind.GetProperty("enum").EnumerateArray().Select(k => k.GetString()!).ToList();
		static IReadOnlyList<string>? Types(JsonElement rule, string name)
			=> rule.TryGetProperty(name, out var types) ? types.EnumerateArray().Select(t => t.GetString()!).ToList() : null;
		var rules = kind.TryGetProperty("x-docs-relation-rules", out var annotation)
			? annotation.EnumerateObject().ToDictionary(r => r.Name, r => new DocRelationRule(r.Name, Types(r.Value, "sources"), Types(r.Value, "targets")), StringComparer.Ordinal)
			: new Dictionary<string, DocRelationRule>(StringComparer.Ordinal);
		return (kinds, rules);
	}
}
