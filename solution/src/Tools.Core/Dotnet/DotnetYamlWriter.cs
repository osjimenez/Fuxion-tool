using System.Collections.Generic;
using System.Linq;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Yaml;

namespace Fuxion.Tools.Core.Dotnet;

/// <summary>
/// Writes the <c>dotnet.yaml</c> a repository receives in <c>_fx/.inherited/dotnet/</c> (plan P, decision 16): what
/// goes down, already merged, with the repositories of every <c>applies-to</c> absolute. Without what does not go
/// down (the solutions, <c>standalone</c>, <c>enabled</c>) and without comments.
/// </summary>
public static class DotnetYamlWriter
{
	public static string Inherited(DotnetConfig config, string from)
	{
		var entries = new List<(string, YamlValue)> { ("schema", FxYamlReader.Schema) };
		if (config.AppliesTo?.ToYaml() is { } appliesTo)
			entries.Add(("applies-to", appliesTo));
		if (config.Sdks.Count > 0)
			entries.Add(("sdks", new YamlValue.Map(config.Sdks.OrderBy(s => s.Key, System.StringComparer.OrdinalIgnoreCase)
				.Select(s => (s.Key, (YamlValue)s.Value)).ToArray())));
		if (config.Variables.Count > 0)
			entries.Add(("variables", new YamlValue.Block(config.Variables.Select(Variable).ToArray())));
		if (config.Imports.Count > 0)
			entries.Add(("imports", new YamlValue.Block(config.Imports.Select(Import).ToArray())));
		if (config.Packages.Count > 0)
			entries.Add(("packages", new YamlValue.Block(config.Packages.Select(Package).ToArray())));
		return FxHeader.Hash(from) + YamlWriter.Document(new(entries.ToArray()));
	}

	static YamlValue Variable(DotnetVariable variable)
	{
		var entries = new List<(string, YamlValue)> { ("name", variable.Name) };
		if (variable.Value is not null)
			entries.Add(("value", variable.Value));
		if (variable.When is not null)
			entries.Add(("when", variable.When));
		if (variable.Define is not null)
			entries.Add(("define", variable.Define));
		AppliesTo(entries, variable.AppliesTo);
		return new YamlValue.Map(entries.ToArray());
	}

	static YamlValue Import(DotnetImport import)
	{
		var entries = new List<(string, YamlValue)>();
		if (import.Props.Count > 0)
			entries.Add(("props", Paths(import.Props)));
		if (import.Targets.Count > 0)
			entries.Add(("targets", Paths(import.Targets)));
		if (import.Assets.Count > 0)
			entries.Add(("files", Paths(import.Assets)));
		if (import.When is not null)
			entries.Add(("when", import.When));
		AppliesTo(entries, import.AppliesTo);
		return new YamlValue.Map(entries.ToArray());
	}

	static YamlValue Package(DotnetPackage package)
	{
		var entries = new List<(string, YamlValue)>
		{
			("ids", new YamlValue.Flow(package.Ids.Select(i => (YamlValue)i).ToArray())),
			("versions", new YamlValue.Block(package.Versions.Select(v => (YamlValue)(v.When is null
				? new YamlValue.Map(("version", v.Version))
				: new YamlValue.Map(("version", v.Version), ("when", v.When)))).ToArray()))
		};
		AppliesTo(entries, package.AppliesTo);
		return new YamlValue.Map(entries.ToArray());
	}

	static YamlValue Paths(IReadOnlyList<string> paths) => paths.Count == 1 ? paths[0] : new YamlValue.Flow(paths.Select(p => (YamlValue)p).ToArray());

	static void AppliesTo(List<(string, YamlValue)> entries, FxAppliesTo appliesTo)
	{
		if (appliesTo.ToYaml() is { } value)
			entries.Add(("applies-to", value));
	}
}
