using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Yaml;

namespace Fuxion.Tools.Core.Dotnet;

/// <summary>An invalid <c>dotnet.yaml</c>: every problem found, each with its line.</summary>
public sealed class DotnetConfigException(IReadOnlyList<FxDiagnostic> diagnostics)
	: Exception(string.Join(Environment.NewLine, diagnostics.Select(d => d.WhereIn(CultureInfo.InvariantCulture))))
{
	public IReadOnlyList<FxDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>
/// Reads <c>dotnet.yaml</c> with fx's YAML reader (SharpYaml's parser, no reflection: AOT) and validates it.
/// </summary>
public static partial class DotnetYamlReader
{
	public const string InvalidYaml = "dotnet.invalid-yaml";

	/// <summary>Reports a problem at a node: the text <c>dotnet.invalid-yaml.&lt;key&gt;</c> with its arguments.</summary>
	delegate void ErrorSink(YamlNode? node, string key, params object?[] args);

	[GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_\-]*$")]
	private static partial Regex PropertyName();

	[GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
	private static partial Regex ConstantName();

	public static DotnetConfig ReadFile(string path) => Read(File.ReadAllText(path), path);

	public static DotnetConfig Read(string yaml, string source)
	{
		var errors = new List<FxDiagnostic>();
		void Error(YamlNode? node, string key, params object?[] args) => errors.Add(FxDiagnostic.Error(InvalidYaml, new($"{InvalidYaml}.{key}", args), source, node?.Line));

		YamlNode? document;
		try
		{
			document = YamlDocument.Load(yaml);
		}
		catch (YamlSyntaxException ex)
		{
			throw new DotnetConfigException([FxDiagnostic.Error(InvalidYaml, FxText.Plain(ex.Message), source, ex.Line)]);
		}

		if (document is null or YamlScalar { Value: "" })
			return DotnetConfig.Empty;
		if (document is not YamlMapping root)
			throw new DotnetConfigException([FxDiagnostic.Error(InvalidYaml, new($"{InvalidYaml}.not-a-mapping"), source, 1)]);

		var sdks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var variables = new List<DotnetVariable>();
		var packages = new List<DotnetPackage>();
		var imports = new List<DotnetImport>();

		foreach (var (keyNode, valueNode) in root.Entries)
		{
			switch (Scalar(keyNode))
			{
				case "sdks":
					if (valueNode is not YamlMapping sdkMap)
					{
						Error(valueNode, "sdks-map");
						break;
					}
					foreach (var (name, version) in sdkMap.Entries)
						if (Scalar(name) is { Length: > 0 } sdk && Scalar(version) is { Length: > 0 } v)
							sdks[sdk] = v;
						else
							Error(name, "sdk");
					break;

				case "variables":
					foreach (var item in Sequence(valueNode, "variables", Error))
						if (ReadVariable(item, source, Error) is { } variable)
							variables.Add(variable);
					break;

				case "imports":
					foreach (var item in Sequence(valueNode, "imports", Error))
						if (ReadImport(item, source, Error) is { } import)
							imports.Add(import);
					break;

				case "packages":
					foreach (var item in Sequence(valueNode, "packages", Error))
						if (ReadPackage(item, source, Error) is { } package)
							packages.Add(package);
					break;

				default:
					Error(keyNode, "unknown-section", Scalar(keyNode));
					break;
			}
		}

		foreach (var duplicate in variables.GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			errors.Add(FxDiagnostic.Error(InvalidYaml, new($"{InvalidYaml}.duplicate-variable", duplicate.Key), source, duplicate.Last().Line));
		foreach (var duplicate in packages.SelectMany(p => p.Ids.Select(id => (Id: id, p.Line))).GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			errors.Add(FxDiagnostic.Error(InvalidYaml, new($"{InvalidYaml}.duplicate-package", duplicate.Key), source, duplicate.Last().Line));

		if (errors.Count > 0)
			throw new DotnetConfigException(errors);
		return new(sdks, variables, packages, imports);
	}

	static DotnetVariable? ReadVariable(YamlNode node, string source, ErrorSink error)
	{
		if (node is not YamlMapping map)
		{
			error(node, "variable-mapping");
			return null;
		}
		var name = Get(map, "name");
		var value = Get(map, "value");
		var when = Get(map, "when");
		if (string.IsNullOrWhiteSpace(name) || !PropertyName().IsMatch(name))
		{
			error(node, "variable-name", name);
			return null;
		}
		if ((value is null) == (when is null))
		{
			error(node, "value-or-when", name);
			return null;
		}
		foreach (var key in map.Entries.Select(e => e.Key).Select(Scalar).Where(k => k is not ("name" or "value" or "when" or "define" or "tags")))
			error(node, "variable-key", name, key);
		var define = Get(map, "define");
		if (define is not null && when is null)
			error(node, "define-needs-when", name);
		else if (define is not null && define.Split(';').Any(c => !ConstantName().IsMatch(c.Trim())))
			error(node, "define-constants", name, define);
		return new(name, value, when, Tags(map, error), source, node.Line, define?.Trim());
	}

	static DotnetImport? ReadImport(YamlNode node, string source, ErrorSink error)
	{
		if (node is not YamlMapping map)
		{
			error(node, "import-mapping");
			return null;
		}
		foreach (var key in map.Entries.Select(e => e.Key).Select(Scalar).Where(k => k is not ("props" or "targets" or "when" or "tags")))
			error(node, "import-key", key);
		var props = Paths(map, "props", error);
		var targets = Paths(map, "targets", error);
		if (props.Count + targets.Count == 0)
		{
			error(node, "import-empty");
			return null;
		}
		// Relative to the folder of the yaml and inside it: fx copies the workspace's to each repo (_fx/.workspace/)
		foreach (var path in props.Concat(targets).Where(p => Path.IsPathRooted(p) || p.Replace('\\', '/').Split('/').Contains("..")))
			error(node, "import-path", path);
		return new(props, targets, Get(map, "when"), Tags(map, error), source, node.Line);
	}

	static IReadOnlyList<string> Paths(YamlMapping map, string key, ErrorSink error)
	{
		if (map[key] is not { } node)
			return [];
		if (node is YamlSequence list)
			return list.Items.Select(Scalar).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Replace('\\', '/')).ToList();
		if (Scalar(node) is { Length: > 0 } single)
			return [single.Replace('\\', '/')];
		error(node, "paths", key);
		return [];
	}

	static DotnetPackage? ReadPackage(YamlNode node, string source, ErrorSink error)
	{
		if (node is not YamlMapping map)
		{
			error(node, "package-mapping");
			return null;
		}
		var ids = map["ids"] is { } idsNode
			? idsNode is YamlSequence idList ? idList.Items.Select(Scalar).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList()
				: Scalar(idsNode) is { Length: > 0 } single ? [single] : []
			: [];
		if (ids.Count == 0)
		{
			error(node, "package-ids");
			return null;
		}
		var versions = new List<DotnetPackageVersion>();
		if (map["versions"] is not YamlSequence versionList || versionList.Items.Count == 0)
			error(node, "package-versions", string.Join(", ", ids));
		else
			foreach (var v in versionList.Items)
			{
				if (v is not YamlMapping versionMap || Get(versionMap, "version") is not { Length: > 0 } version)
				{
					error(v, "package-version", string.Join(", ", ids));
					continue;
				}
				var when = Get(versionMap, "when") is { Length: > 0 } w ? w : null;
				if (when is null && versionList.Items.Count > 1)
				{
					// D-32: no "the rest": a new framework would silently get a wrong version. A single version is for all.
					error(v, "package-when", string.Join(", ", ids), version);
					continue;
				}
				versions.Add(new(when, version, v.Line));
			}
		return new(ids, versions, Tags(map, error), source, node.Line);
	}

	static IReadOnlyList<string> Tags(YamlMapping map, ErrorSink error)
	{
		if (map["tags"] is not { } tags)
			return [];
		if (tags is YamlSequence list)
			return list.Items.Select(Scalar).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).ToList();
		error(tags, "tags");
		return [];
	}

	static IEnumerable<YamlNode> Sequence(YamlNode node, string name, ErrorSink error)
	{
		if (node is YamlSequence sequence)
			return sequence.Items;
		if (node is not YamlScalar { Value: "" })
			error(node, "list", name);
		return [];
	}

	static string? Get(YamlMapping map, string key)
		=> map[key] is { } node ? Scalar(node) : null;

	static string? Scalar(YamlNode node) => (node as YamlScalar)?.Value.Trim();
}
