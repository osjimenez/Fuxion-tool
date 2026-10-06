using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Fuxion.Tools.Core.Diagnostics;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Fuxion.Tools.Core.Dotnet;

/// <summary>An invalid <c>dotnet.yaml</c>: every problem found, each with its line.</summary>
public sealed class DotnetConfigException(IReadOnlyList<FxDiagnostic> diagnostics)
	: Exception(string.Join(Environment.NewLine, diagnostics.Select(d => d.Where)))
{
	public IReadOnlyList<FxDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>
/// Reads <c>dotnet.yaml</c> with the representation model of YamlDotNet (no reflection: AOT) and validates it.
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
		void Error(YamlNode? node, string message) => errors.Add(FxDiagnostic.Error(InvalidYaml, message, source, node is null ? null : (int)node.Start.Line));

		var stream = new YamlStream();
		try
		{
			stream.Load(new StringReader(yaml));
		}
		catch (YamlException ex)
		{
			throw new DotnetConfigException([FxDiagnostic.Error(InvalidYaml, ex.Message, source, (int)ex.Start.Line)]);
		}

		if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is YamlScalarNode { Value: null or "" })
			return DotnetConfig.Empty;
		if (stream.Documents[0].RootNode is not YamlMappingNode root)
			throw new DotnetConfigException([FxDiagnostic.Error(InvalidYaml, "The document must be a mapping (sdks, variables, packages).", source, 1)]);

		var sdks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var variables = new List<DotnetVariable>();
		var packages = new List<DotnetPackage>();

		foreach (var (keyNode, valueNode) in root.Children)
		{
			switch (Scalar(keyNode))
			{
				case "sdks":
					if (valueNode is not YamlMappingNode sdkMap)
					{
						Error(valueNode, "'sdks' must map SDK names to versions.");
						break;
					}
					foreach (var (name, version) in sdkMap.Children)
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

				case "packages":
					foreach (var item in Sequence(valueNode, "packages", Error))
						if (ReadPackage(item, source, Error) is { } package)
							packages.Add(package);
					break;

				default:
					Error(keyNode, $"Unknown section '{Scalar(keyNode)}' (sdks, variables, packages).");
					break;
			}
		}

		foreach (var duplicate in variables.GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			errors.Add(FxDiagnostic.Error(InvalidYaml, $"Variable '{duplicate.Key}' is defined more than once.", source, duplicate.Last().Line));
		foreach (var duplicate in packages.SelectMany(p => p.Ids.Select(id => (Id: id, p.Line))).GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			errors.Add(FxDiagnostic.Error(InvalidYaml, $"Package '{duplicate.Key}' is listed more than once.", source, duplicate.Last().Line));

		if (errors.Count > 0)
			throw new DotnetConfigException(errors);
		return new(sdks, variables, packages);
	}

	static DotnetVariable? ReadVariable(YamlNode node, string source, Action<YamlNode?, string> error)
	{
		if (node is not YamlMappingNode map)
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
		foreach (var key in map.Children.Keys.Select(Scalar).Where(k => k is not ("name" or "value" or "when" or "define" or "tags")))
			error(node, $"Variable '{name}': unknown key '{key}'.");
		var define = Get(map, "define");
		if (define is not null && when is null)
			error(node, $"Variable '{name}': 'define' needs 'when' (the constants are defined where the condition holds).");
		else if (define is not null && define.Split(';').Any(c => !ConstantName().IsMatch(c.Trim())))
			error(node, $"Variable '{name}': 'define' must be compilation constants separated by ';' ('{define}').");
		return new(name, value, when, Tags(map, error), source, (int)node.Start.Line, define?.Trim());
	}

	static DotnetPackage? ReadPackage(YamlNode node, string source, Action<YamlNode?, string> error)
	{
		if (node is not YamlMappingNode map)
		{
			error(node, "A package entry is a mapping: ids and versions.");
			return null;
		}
		var ids = map.Children.TryGetValue(new YamlScalarNode("ids"), out var idsNode)
			? idsNode is YamlSequenceNode idList ? idList.Children.Select(Scalar).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList()
				: Scalar(idsNode) is { Length: > 0 } single ? [single] : []
			: [];
		if (ids.Count == 0)
		{
			error(node, "A package entry needs 'ids'.");
			return null;
		}
		var versions = new List<DotnetPackageVersion>();
		if (!map.Children.TryGetValue(new YamlScalarNode("versions"), out var versionsNode) || versionsNode is not YamlSequenceNode versionList || versionList.Children.Count == 0)
			error(node, $"Packages {string.Join(", ", ids)}: 'versions' must list at least one version.");
		else
			foreach (var v in versionList.Children)
			{
				if (v is not YamlMappingNode versionMap || Get(versionMap, "version") is not { Length: > 0 } version)
				{
					error(v, $"Packages {string.Join(", ", ids)}: every version needs 'version' and 'when'.");
					continue;
				}
				if (Get(versionMap, "when") is not { Length: > 0 } when)
				{
					// D-32: no "the rest": a new framework would silently get a wrong version
					error(v, $"Packages {string.Join(", ", ids)}, version {version}: 'when' is required (design D-32).");
					continue;
				}
				versions.Add(new(when, version, (int)v.Start.Line));
			}
		return new(ids, versions, Tags(map, error), source, (int)node.Start.Line);
	}

	static IReadOnlyList<string> Tags(YamlMappingNode map, Action<YamlNode?, string> error)
	{
		if (!map.Children.TryGetValue(new YamlScalarNode("tags"), out var tags))
			return [];
		if (tags is YamlSequenceNode list)
			return list.Children.Select(Scalar).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).ToList();
		error(tags, "'tags' must be a list, like [windows].");
		return [];
	}

	static IEnumerable<YamlNode> Sequence(YamlNode node, string name, Action<YamlNode?, string> error)
	{
		if (node is YamlSequenceNode sequence)
			return sequence.Children;
		if (node is not YamlScalarNode { Value: null or "" })
			error(node, $"'{name}' must be a list.");
		return [];
	}

	static string? Get(YamlMappingNode map, string key)
		=> map.Children.TryGetValue(new YamlScalarNode(key), out var node) ? Scalar(node) : null;

	static string? Scalar(YamlNode node) => (node as YamlScalarNode)?.Value?.Trim();
}
