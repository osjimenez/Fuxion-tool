using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Fuxion.Tool.Core.Results;

namespace Fuxion.Tool.Core.Docs;

/// <summary>A problem of a sidecar against its schema: the JSON pointer of the value, the keyword that failed and its message.</summary>
public sealed record DocsSchemaError(string Pointer, string Keyword, FxText Message);

/// <summary>
/// The schemas of the documentation standard, inside fx (plan U, ADR "esquemas cerrados, con costuras"): the version 1.0
/// of the metadata of a document, one schema per type. fx validates the sidecars with them, with its own validator
/// (<see cref="DocsValidator"/>), and reads from their annotations what its logic needs of each type (<see cref="DocTypes"/>).
/// </summary>
public static class DocsSchemas
{
	public const string Version = "1.0";

	/// <summary>Where they are inside the assembly: the logical name of each one is this and its path in the version.</summary>
	const string Prefix = "docs/schemas/document/v1.0/";

	static readonly Lazy<(IReadOnlyDictionary<string, string> Texts, DocsValidator Validator)> Loaded = new(Load);

	/// <summary>The text of each schema, by its path inside the version (<c>base.schema.json</c>, <c>types/adr.schema.json</c>…).</summary>
	public static IReadOnlyDictionary<string, string> Texts => Loaded.Value.Texts;

	static (IReadOnlyDictionary<string, string>, DocsValidator) Load()
	{
		var assembly = typeof(DocsSchemas).Assembly;
		var texts = new SortedDictionary<string, string>(StringComparer.Ordinal);
		foreach (var name in assembly.GetManifestResourceNames())
		{
			// the folders of the logical name come with the separator of the machine that built it
			var normalized = name.Replace('\\', '/');
			if (!normalized.StartsWith(Prefix, StringComparison.Ordinal))
				continue;
			using var stream = assembly.GetManifestResourceStream(name)!;
			using var reader = new StreamReader(stream);
			texts[normalized[Prefix.Length..]] = reader.ReadToEnd();
		}
		// one validator for all of them, so their $ref between files resolve by $id
		return (texts, new DocsValidator(texts));
	}

	/// <summary>The schema of the type <paramref name="type"/>; null if the standard has no such type.</summary>
	public static DocsSchema? ForType(string type) => Loaded.Value.Validator.Schemas.GetValueOrDefault($"types/{type}.schema.json");

	/// <summary>
	/// The problems of <paramref name="sidecar"/> against the schema of its type, ordered by their place; none, an empty list. A type that
	/// does not exist is one problem at <c>/type</c>.
	/// </summary>
	public static IReadOnlyList<DocsSchemaError> Validate(JsonElement sidecar)
	{
		var type = sidecar.ValueKind == JsonValueKind.Object && sidecar.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : null;
		if (type is null || ForType(type) is not { } schema)
			return [new("/type", "enum", type is null ? new($"{DocChecker.InvalidType}.missing") : new(DocChecker.InvalidType, type, string.Join(", ", DocTypes.All.Select(x => x.Name))))];
		return Loaded.Value.Validator.Validate(schema, sidecar);
	}
}
