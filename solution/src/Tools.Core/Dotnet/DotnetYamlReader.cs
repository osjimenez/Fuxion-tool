using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Fuxion.Tools.Core.Layout;
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
/// Reads <c>dotnet.yaml</c> with fx's YAML reader (SharpYaml's parser, no reflection: AOT) and validates it. Its
/// warnings (an unknown key of a newer schema, no schema) go to <see cref="Read(string, string, ICollection{FxDiagnostic})"/>'s
/// list; its errors, in a <see cref="DotnetConfigException"/>.
/// </summary>
public static partial class DotnetYamlReader
{
	public const string InvalidYaml = "dotnet.invalid-yaml";

	[GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_\-]*$")]
	private static partial Regex PropertyName();

	[GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
	private static partial Regex ConstantName();

	public static DotnetConfig ReadFile(string path, ICollection<FxDiagnostic>? warnings = null) => Read(FxFiles.ReadAllText(path), path, warnings);

	public static DotnetConfig Read(string yaml, string source, ICollection<FxDiagnostic>? warnings = null)
	{
		var reader = new FxYamlReader(InvalidYaml, source);
		var map = reader.Load(yaml);
		if (map is null)
			throw new DotnetConfigException(reader.Diagnostics);

		var sdks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var variables = new List<DotnetVariable>();
		var packages = new List<DotnetPackage>();
		var imports = new List<DotnetImport>();
		var solutions = new List<DotnetSolution>();
		reader.Keys(map, "schema", "enabled", "applies-to", "sdks", "variables", "imports", "packages", "solutions", "standalone");

		if (map["sdks"] is { } sdkNode)
		{
			if (sdkNode is YamlMapping sdkMap)
				foreach (var (name, version) in sdkMap.Entries)
					if (FxYamlReader.Scalar(name) is { Length: > 0 } sdk && FxYamlReader.Scalar(version) is { Length: > 0 } v)
						sdks[sdk] = v;
					else
						reader.Error(name, "sdk");
			else if (sdkNode is not YamlScalar { Value: "" })
				reader.Error(sdkNode, "sdks-map");
		}
		foreach (var item in Sequence(map["variables"], "variables", reader))
			if (ReadVariable(item, source, reader) is { } variable)
				variables.Add(variable);
		foreach (var item in Sequence(map["imports"], "imports", reader))
			if (ReadImport(item, source, reader) is { } import)
				imports.Add(import);
		foreach (var item in Sequence(map["packages"], "packages", reader))
			if (ReadPackage(item, source, reader) is { } package)
				packages.Add(package);
		foreach (var item in Sequence(map["solutions"], "solutions", reader))
			if (ReadSolution(item, reader) is { } solution)
				solutions.Add(solution);

		foreach (var duplicate in variables.GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			reader.Diagnostics.Add(FxDiagnostic.Error(InvalidYaml, new($"{InvalidYaml}.duplicate-variable", duplicate.Key), source, duplicate.Last().Line));
		foreach (var duplicate in packages.SelectMany(p => p.Ids.Select(id => (Id: id, p.Line))).GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			reader.Diagnostics.Add(FxDiagnostic.Error(InvalidYaml, new($"{InvalidYaml}.duplicate-package", duplicate.Key), source, duplicate.Last().Line));
		foreach (var duplicate in solutions.GroupBy(s => s.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			reader.Diagnostics.Add(FxDiagnostic.Error(InvalidYaml, new($"{InvalidYaml}.duplicate-solution", duplicate.Key), source, duplicate.Last().Line));

		var config = new DotnetConfig(sdks, variables, packages, imports)
		{
			AppliesTo = FxAppliesTo.Read(map, reader),
			Solutions = solutions,
			Standalone = reader.Bool(map, "standalone", false),
			Enabled = reader.Enabled(map)
		};
		if (reader.HasErrors)
			throw new DotnetConfigException(reader.Diagnostics);
		foreach (var warning in reader.Diagnostics)
			warnings?.Add(warning);
		return config;
	}

	static DotnetVariable? ReadVariable(YamlNode node, string source, FxYamlReader reader)
	{
		if (node is not YamlMapping map)
		{
			reader.Error(node, "variable-mapping");
			return null;
		}
		var name = FxYamlReader.Scalar(map["name"]);
		var value = FxYamlReader.Scalar(map["value"]);
		var when = FxYamlReader.Scalar(map["when"]);
		if (string.IsNullOrWhiteSpace(name) || !PropertyName().IsMatch(name))
		{
			reader.Error(node, "variable-name", name);
			return null;
		}
		if ((value is null) == (when is null))
		{
			reader.Error(node, "value-or-when", name);
			return null;
		}
		reader.Keys(map, "name", "value", "when", "define", "applies-to");
		var define = FxYamlReader.Scalar(map["define"]);
		if (define is not null && when is null)
			reader.Error(node, "define-needs-when", name);
		else if (define is not null && define.Split(';').Any(c => !ConstantName().IsMatch(c.Trim())))
			reader.Error(node, "define-constants", name, define);
		return new(name, value, when, FxAppliesTo.Read(map, reader) ?? FxAppliesTo.All, source, node.Line, define?.Trim());
	}

	static DotnetImport? ReadImport(YamlNode node, string source, FxYamlReader reader)
	{
		if (node is not YamlMapping map)
		{
			reader.Error(node, "import-mapping");
			return null;
		}
		reader.Keys(map, "props", "targets", "files", "when", "applies-to");
		var props = Paths(map, "props", reader);
		var targets = Paths(map, "targets", reader);
		var assets = Paths(map, "files", reader);
		if (props.Count + targets.Count == 0)
		{
			reader.Error(node, "import-empty");
			return null;
		}
		// Relative to the folder of the yaml and inside it: fx copies it, with the folder, to the repositories below
		foreach (var path in props.Concat(targets).Concat(assets).Where(p => Path.IsPathRooted(p) || p.Split('/').Contains("..")))
			reader.Error(node, "import-path", path);
		return new(props, targets, FxYamlReader.Scalar(map["when"]), FxAppliesTo.Read(map, reader) ?? FxAppliesTo.All, source, node.Line) { Assets = assets };
	}

	static IReadOnlyList<string> Paths(YamlMapping map, string key, FxYamlReader reader)
	{
		if (map[key] is not { } node)
			return [];
		if (node is YamlSequence or YamlScalar { Value.Length: > 0 })
			return FxYamlReader.List(node).Select(p => p.Replace('\\', '/')).ToList();
		reader.Error(node, "paths", key);
		return [];
	}

	static DotnetPackage? ReadPackage(YamlNode node, string source, FxYamlReader reader)
	{
		if (node is not YamlMapping map)
		{
			reader.Error(node, "package-mapping");
			return null;
		}
		reader.Keys(map, "ids", "versions", "applies-to");
		var ids = FxYamlReader.List(map["ids"]);
		if (ids.Count == 0)
		{
			reader.Error(node, "package-ids");
			return null;
		}
		var versions = new List<DotnetPackageVersion>();
		if (map["versions"] is not YamlSequence versionList || versionList.Items.Count == 0)
			reader.Error(node, "package-versions", string.Join(", ", ids));
		else
			foreach (var v in versionList.Items)
			{
				if (v is not YamlMapping versionMap || FxYamlReader.Scalar(versionMap["version"]) is not { Length: > 0 } version)
				{
					reader.Error(v, "package-version", string.Join(", ", ids));
					continue;
				}
				reader.Keys(versionMap, "version", "when");
				var when = FxYamlReader.Scalar(versionMap["when"]) is { Length: > 0 } w ? w : null;
				if (when is null && versionList.Items.Count > 1)
				{
					// D-32: no "the rest": a new framework would silently get a wrong version. A single version is for all.
					reader.Error(v, "package-when", string.Join(", ", ids), version);
					continue;
				}
				versions.Add(new(when, version, v.Line));
			}
		return new(ids, versions, FxAppliesTo.Read(map, reader) ?? FxAppliesTo.All, source, node.Line);
	}

	static DotnetSolution? ReadSolution(YamlNode node, FxYamlReader reader)
	{
		if (node is not YamlMapping map || FxYamlReader.Scalar(map["path"]) is not { Length: > 0 } path)
		{
			reader.Error(node, "solution");
			return null;
		}
		reader.Keys(map, "path", "folders");
		path = path.Replace('\\', '/');
		if (!path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(path) || path.Split('/').Contains(".."))
			reader.Error(node, "solution-path", path);
		var folders = new List<DotnetSolutionFolder>();
		foreach (var item in Sequence(map["folders"], "folders", reader))
		{
			if (item is not YamlMapping folder || FxYamlReader.Scalar(folder["name"]) is not { Length: > 0 } name)
			{
				reader.Error(item, "solution-folder");
				continue;
			}
			reader.Keys(folder, "name", "solutions", "files");
			if (!name.StartsWith('/'))
				reader.Error(item, "solution-folder-name", name);
			var solutions = FxYamlReader.List(folder["solutions"]).Select(s => s.Replace('\\', '/')).ToList();
			var files = FxYamlReader.List(folder["files"]).Select(f => f.Replace('\\', '/')).ToList();
			// a .slnx shows no file outside a folder
			if (name.Trim('/').Length == 0 && files.Count > 0)
				reader.Error(item, "solution-root-file", path);
			foreach (var relative in solutions.Concat(files).Where(p => Path.IsPathRooted(p) || p.Split('/').Contains("..")))
				reader.Error(item, "solution-item-path", relative);
			folders.Add(new("/" + name.Trim('/'), solutions, files, item.Line));
		}
		return new(path, folders, node.Line);
	}

	static IEnumerable<YamlNode> Sequence(YamlNode? node, string name, FxYamlReader reader)
	{
		if (node is null)
			return [];
		if (node is YamlSequence sequence)
			return sequence.Items;
		if (node is not YamlScalar { Value: "" })
			reader.Error(node, "list", name);
		return [];
	}
}
