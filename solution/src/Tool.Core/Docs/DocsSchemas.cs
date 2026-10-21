using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Json.Schema;

namespace Fuxion.Tool.Core.Docs;

/// <summary>A problem of a sidecar against its schema: the JSON pointer of the value, the keyword that failed and its message.</summary>
public sealed record DocsSchemaError(string Pointer, string Keyword, string Message);

/// <summary>
/// The schemas of the documentation standard, inside fx (plan U, ADR "esquemas cerrados, con costuras"): the version 1.0
/// of the metadata of a document, one schema per type. fx validates the sidecars with them, and reads from their
/// annotations what its logic needs of each type (<see cref="DocTypes"/>).
/// </summary>
public static class DocsSchemas
{
	public const string Version = "1.0";

	/// <summary>Where they are inside the assembly: the logical name of each one is this and its path in the version.</summary>
	const string Prefix = "docs/schemas/document/v1.0/";

	static readonly Lazy<(IReadOnlyDictionary<string, string> Texts, IReadOnlyDictionary<string, JsonSchema> Schemas)> Loaded = new(Load);

	/// <summary>The text of each schema, by its path inside the version (<c>base.schema.json</c>, <c>types/adr.schema.json</c>…).</summary>
	public static IReadOnlyDictionary<string, string> Texts => Loaded.Value.Texts;

	static (IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, JsonSchema>) Load()
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
		// one registry for all of them, so their $ref between files resolve by $id
		var registry = new SchemaRegistry();
		var schemas = texts.ToDictionary(t => t.Key, t => JsonSchema.FromText(t.Value, new BuildOptions { SchemaRegistry = registry }), StringComparer.Ordinal);
		return (texts, schemas);
	}

	/// <summary>The schema of the type <paramref name="type"/>; null if the standard has no such type.</summary>
	public static JsonSchema? ForType(string type) => Loaded.Value.Schemas.GetValueOrDefault($"types/{type}.schema.json");

	static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.List, RequireFormatValidation = true };

	/// <summary>
	/// The problems of <paramref name="sidecar"/> against the schema of its type; none, an empty list. A type that does not
	/// exist is one problem at <c>/type</c>. A failure inside the base drops its annotations (JSON Schema 2020-12), and then
	/// every property seems unexpected to <c>unevaluatedProperties</c>: that cascade is left out when there are other
	/// problems, and an unexpected property is reported alone only when it is the problem.
	/// </summary>
	public static IReadOnlyList<DocsSchemaError> Validate(JsonElement sidecar)
	{
		var type = sidecar.ValueKind == JsonValueKind.Object && sidecar.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : null;
		if (type is null || ForType(type) is not { } schema)
			return [new("/type", "enum", type is null ? "The type is missing" : $"The standard has no type '{type}'")];
		var result = schema.Evaluate(sidecar, Options);
		if (result.IsValid)
			return [];
		var leaves = new List<DocsSchemaError>();
		var unexpected = new List<DocsSchemaError>();
		foreach (var detail in result.Details ?? [])
			foreach (var (keyword, message) in detail.Errors ?? new Dictionary<string, string>())
			{
				var pointer = detail.InstanceLocation.ToString();
				if (keyword.Length == 0 && detail.EvaluationPath.ToString().EndsWith("/unevaluatedProperties", StringComparison.Ordinal))
					unexpected.Add(new(pointer, "unevaluatedProperties", message));
				else if (keyword is not ("allOf" or "anyOf" or "oneOf" or "properties" or "items" or "unevaluatedProperties" or "$ref"))
					leaves.Add(new(pointer, keyword, message));
			}
		return (leaves.Count > 0 ? leaves : unexpected).Distinct().OrderBy(e => e.Pointer, StringComparer.Ordinal).ThenBy(e => e.Keyword, StringComparer.Ordinal).ToList();
	}
}
