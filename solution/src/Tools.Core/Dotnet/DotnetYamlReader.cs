using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Fuxion.Tools.Core.Diagnostics;
using Fuxion.Tools.Core.Yaml;

namespace Fuxion.Tools.Core.Dotnet;

/// <summary>An invalid <c>dotnet.yaml</c>: every problem found, each with its line.</summary>
public sealed class DotnetConfigException(IReadOnlyList<FxDiagnostic> diagnostics)
	: Exception(string.Join(Environment.NewLine, diagnostics.Select(d => d.Where)))
{
	public IReadOnlyList<FxDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>
/// Reads <c>dotnet.yaml</c> with fx's YAML reader (SharpYaml's parser, no reflection: AOT) and validates it.
/// </summary>
public static partial class DotnetYamlReader
{
	public const string InvalidYaml = "dotnet.invalid-yaml";

	[GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_\-]*$")]
	private static partial Regex PropertyName();

	[GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
	private static partial Regex ConstantName();

	public static DotnetConfig ReadFile(string path) => Read(File.ReadAllText(path), path);

	public static DotnetConfig Read(string yaml, string source)
	{
		var errors = new List<FxDiagnostic>();
		void Error(YamlNode? node, string message) => errors.Add(FxDiagnostic.Error(InvalidYaml, message, source, node?.Line));

		YamlNode? document;
		try
		{
			document = YamlDocument.Load(yaml);
		}
		catch (YamlSyntaxException ex)
		{
			throw new DotnetConfigException([FxDiagnostic.Error(InvalidYaml, ex.Message, source, ex.Line)]);
		}

		if (document is null or YamlScalar { Value: "" })
			return DotnetConfig.Empty;
		if (document is not YamlMapping root)
			throw new DotnetConfigException([FxDiagnostic.Error(InvalidYaml, "The document must be a mapping (sdks, variables, imports, packages).", source, 1)]);

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
						Error(valueNode, "'sdks' must map SDK names to versions.");
						break;
					}
					foreach (var (name, version) in sdkMap.Entries)
						if (Scalar(name) is { Length: > 0 } sdk && Scalar(version) is { Length: > 0 } v)
							sdks[sdk] = v;
						else
							Error(name, "Every SDK needs a name and a version.");
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
					Error(keyNode, $"Unknown section '{Scalar(keyNode)}' (sdks, variables, imports, packages).");
					break;
			}
		}

		foreach (var duplicate in variables.GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			errors.Add(FxDiagnostic.Error(InvalidYaml, $"Variable '{duplicate.Key}' is defined more than once.", source, duplicate.Last().Line));
		foreach (var duplicate in packages.SelectMany(p => p.Ids.Select(id => (Id: id, p.Line))).GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			errors.Add(FxDiagnostic.Error(InvalidYaml, $"Package '{duplicate.Key}' is listed more than once.", source, duplicate.Last().Line));

		if (errors.Count > 0)
			throw new DotnetConfigException(errors);
		return new(sdks, variables, packages, imports);
	}

	static DotnetVariable? ReadVariable(YamlNode node, string source, Action<YamlNode?, string> error)
	{
		if (node is not YamlMapping map)
		{
			error(node, "A variable is a mapping: name, and value or when.");
			return null;
		}
		var name = Get(map, "name");
		var value = Get(map, "value");
		var when = Get(map, "when");
		if (string.IsNullOrWhiteSpace(name) || !PropertyName().IsMatch(name))
		{
			error(node, $"Variable without a valid MSBuild property name ('{name}').");
			return null;
		}
		if ((value is null) == (when is null))
		{
			error(node, $"Variable '{name}' needs exactly one of 'value' or 'when'.");
			return null;
		}
		foreach (var key in map.Entries.Select(e => e.Key).Select(Scalar).Where(k => k is not ("name" or "value" or "when" or "define" or "tags")))
			error(node, $"Variable '{name}': unknown key '{key}'.");
		var define = Get(map, "define");
		if (define is not null && when is null)
			error(node, $"Variable '{name}': 'define' needs 'when' (the constants are defined where the condition holds).");
		else if (define is not null && define.Split(';').Any(c => !ConstantName().IsMatch(c.Trim())))
			error(node, $"Variable '{name}': 'define' must be compilation constants separated by ';' ('{define}').");
		return new(name, value, when, Tags(map, error), source, node.Line, define?.Trim());
	}

	static DotnetImport? ReadImport(YamlNode node, string source, Action<YamlNode?, string> error)
	{
		if (node is not YamlMapping map)
		{
			error(node, "An import is a mapping: props and/or targets, and when.");
			return null;
		}
		foreach (var key in map.Entries.Select(e => e.Key).Select(Scalar).Where(k => k is not ("props" or "targets" or "when" or "tags")))
			error(node, $"Import: unknown key '{key}' (props, targets, when, tags).");
		var props = Paths(map, "props", error);
		var targets = Paths(map, "targets", error);
		if (props.Count + targets.Count == 0)
		{
			error(node, "An import needs 'props' or 'targets'.");
			return null;
		}
		// Relative to the folder of the yaml and inside it: fx copies the workspace's to each repo (_fx/.workspace/)
		foreach (var path in props.Concat(targets).Where(p => Path.IsPathRooted(p) || p.Replace('\\', '/').Split('/').Contains("..")))
			error(node, $"Import '{path}': the path must be relative to the folder of dotnet.yaml, without '..'.");
		return new(props, targets, Get(map, "when"), Tags(map, error), source, node.Line);
	}

	static IReadOnlyList<string> Paths(YamlMapping map, string key, Action<YamlNode?, string> error)
	{
		if (map[key] is not { } node)
			return [];
		if (node is YamlSequence list)
			return list.Items.Select(Scalar).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Replace('\\', '/')).ToList();
		if (Scalar(node) is { Length: > 0 } single)
			return [single.Replace('\\', '/')];
		error(node, $"'{key}' must be a path or a list of paths.");
		return [];
	}

	static DotnetPackage? ReadPackage(YamlNode node, string source, Action<YamlNode?, string> error)
	{
		if (node is not YamlMapping map)
		{
			error(node, "A package entry is a mapping: ids and versions.");
			return null;
		}
		var ids = map["ids"] is { } idsNode
			? idsNode is YamlSequence idList ? idList.Items.Select(Scalar).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList()
				: Scalar(idsNode) is { Length: > 0 } single ? [single] : []
			: [];
		if (ids.Count == 0)
		{
			error(node, "A package entry needs 'ids'.");
			return null;
		}
		var versions = new List<DotnetPackageVersion>();
		if (map["versions"] is not YamlSequence versionList || versionList.Items.Count == 0)
			error(node, $"Packages {string.Join(", ", ids)}: 'versions' must list at least one version.");
		else
			foreach (var v in versionList.Items)
			{
				if (v is not YamlMapping versionMap || Get(versionMap, "version") is not { Length: > 0 } version)
				{
					error(v, $"Packages {string.Join(", ", ids)}: every version needs 'version' (and 'when', if there are several).");
					continue;
				}
				var when = Get(versionMap, "when") is { Length: > 0 } w ? w : null;
				if (when is null && versionList.Items.Count > 1)
				{
					// D-32: no "the rest": a new framework would silently get a wrong version. A single version is for all.
					error(v, $"Packages {string.Join(", ", ids)}, version {version}: with several versions, each needs 'when' (design D-32).");
					continue;
				}
				versions.Add(new(when, version, v.Line));
			}
		return new(ids, versions, Tags(map, error), source, node.Line);
	}

	static IReadOnlyList<string> Tags(YamlMapping map, Action<YamlNode?, string> error)
	{
		if (map["tags"] is not { } tags)
			return [];
		if (tags is YamlSequence list)
			return list.Items.Select(Scalar).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).ToList();
		error(tags, "'tags' must be a list, like [windows].");
		return [];
	}

	static IEnumerable<YamlNode> Sequence(YamlNode node, string name, Action<YamlNode?, string> error)
	{
		if (node is YamlSequence sequence)
			return sequence.Items;
		if (node is not YamlScalar { Value: "" })
			error(node, $"'{name}' must be a list.");
		return [];
	}

	static string? Get(YamlMapping map, string key)
		=> map[key] is { } node ? Scalar(node) : null;

	static string? Scalar(YamlNode node) => (node as YamlScalar)?.Value.Trim();
}
