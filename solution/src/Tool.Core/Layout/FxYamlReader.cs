using System;
using System.Collections.Generic;
using System.Linq;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Yaml;

namespace Fuxion.Tool.Core.Layout;

/// <summary>
/// The reading of one of fx's YAML files: the problems found, each with its line (<c>&lt;code&gt;.&lt;key&gt;</c>
/// texts), and its <c>schema</c> (plan P, decision 5 and proposal G). Each file has its own version, semver: with the
/// major fx knows it is read; with a higher major it is not (an error: a newer fx is needed); with a higher minor it is
/// read and its unknown keys are warnings; otherwise an unknown key is an error. Without <c>schema</c>, a warning.
/// </summary>
public sealed class FxYamlReader(string code, string file, string schema = FxYamlReader.Schema)
{
	/// <summary>The version of every file fx reads today.</summary>
	public const string Schema = "1.0.0";

	public const string SchemaNewer = "fx.schema-newer";
	public const string SchemaMissing = "fx.schema-missing";
	public const string SchemaInvalid = "fx.schema-invalid";
	public const string UnknownKey = "fx.unknown-key";

	public List<FxDiagnostic> Diagnostics { get; } = [];

	public bool HasErrors => Diagnostics.Any(d => d.Severity == FxSeverity.Error);

	/// <summary>A newer minor of the schema: what fx does not know is only a warning.</summary>
	public bool Lenient { get; private set; }

	public string File => file;

	public void Error(YamlNode? node, string key, params object?[] args)
		=> Diagnostics.Add(FxDiagnostic.Error(code, new($"{code}.{key}", args), file, node?.Line));

	/// <summary>
	/// The document as a mapping, its schema checked; null (with the error) if it is not valid YAML, not a mapping or its
	/// schema is newer than fx. An empty document is an empty mapping.
	/// </summary>
	public YamlMapping? Load(string text)
	{
		YamlNode? document;
		try
		{
			document = YamlDocument.Load(text);
		}
		catch (YamlSyntaxException ex)
		{
			Diagnostics.Add(FxDiagnostic.Error(code, FxText.Plain(ex.Message), file, ex.Line));
			return null;
		}
		if (document is null or YamlScalar { Value: "" })
			return new YamlMapping();
		if (document is not YamlMapping map)
		{
			Diagnostics.Add(FxDiagnostic.Error(code, new("fx.not-a-mapping"), file, 1));
			return null;
		}
		if (map["schema"] is not YamlScalar { Value.Length: > 0 } version)
		{
			Diagnostics.Add(FxDiagnostic.Warning(SchemaMissing, new(SchemaMissing, schema), file, 1, fix: FxFix.Do(new FxText($"{SchemaMissing}.fix", schema))));
			return map;
		}
		if (Parse(version.Value.Trim()) is not { } read)
		{
			Diagnostics.Add(FxDiagnostic.Error(SchemaInvalid, new(SchemaInvalid, version.Value.Trim()), file, version.Line));
			return null;
		}
		var known = Parse(schema)!.Value;
		if (read.Major > known.Major)
		{
			Diagnostics.Add(FxDiagnostic.Error(SchemaNewer, new(SchemaNewer, version.Value.Trim(), schema), file, version.Line, fix: FxFix.Do($"{SchemaNewer}.fix")));
			return null;
		}
		Lenient = read.Major == known.Major && read.Minor > known.Minor;
		return map;
	}

	/// <summary>The keys of <paramref name="map"/> that are not <paramref name="known"/>: errors, or warnings with a newer minor.</summary>
	public void Keys(YamlMapping map, params string[] known)
	{
		foreach (var key in map.Entries.Select(e => e.Key).OfType<YamlScalar>().Where(k => !known.Contains(k.Value)))
			Diagnostics.Add(Lenient
				? FxDiagnostic.Warning(UnknownKey, new(UnknownKey, key.Value, string.Join(", ", known)), file, key.Line)
				: FxDiagnostic.Error(code, new(UnknownKey, key.Value, string.Join(", ", known)), file, key.Line));
	}

	/// <summary><c>enabled</c> of a module: true unless it says <c>false</c>.</summary>
	public bool Enabled(YamlMapping map)
	{
		if (map["enabled"] is not { } node)
			return true;
		if (Scalar(node) is "true" or "false")
			return Scalar(node) == "true";
		Error(node, "enabled");
		return true;
	}

	public static string? Scalar(YamlNode? node) => (node as YamlScalar)?.Value.Trim();

	/// <summary>A list of scalars (a single scalar is a list of one).</summary>
	public static IReadOnlyList<string> List(YamlNode? node) => node switch
	{
		YamlSequence list => list.Items.Select(Scalar).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList(),
		YamlScalar { Value.Length: > 0 } single => [single.Value.Trim()],
		_ => []
	};

	/// <summary>A boolean key (<c>true</c>/<c>false</c>), or <paramref name="default"/>.</summary>
	public bool Bool(YamlMapping map, string key, bool @default)
	{
		if (map[key] is not { } node)
			return @default;
		switch (Scalar(node))
		{
			case "true":
				return true;
			case "false":
				return false;
			default:
				Error(node, "bool", key);
				return @default;
		}
	}

	/// <summary><c>major.minor.patch</c>, ignoring a pre-release or build suffix.</summary>
	public static (int Major, int Minor, int Patch)? Parse(string version)
	{
		var core = version.Split('-', '+')[0].Split('.');
		return core.Length == 3 && int.TryParse(core[0], out var major) && int.TryParse(core[1], out var minor) && int.TryParse(core[2], out var patch)
			? (major, minor, patch)
			: null;
	}
}
