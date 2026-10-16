using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Yaml;

namespace Fuxion.Tool.Core.Layout;

/// <summary>A tag of the catalog: its name and what it means (plan P, decision 21).</summary>
public sealed record FxTag(string Name, string? Description, int Line = 0);

/// <summary>
/// <c>_fx/fx.yaml</c>, the general configuration of a root of fx (plan P, decision 4). <c>name</c>: the name of a root
/// (nobody declares it); in a repository a workspace declares, only a proposal. <c>requires</c>: the minimum version of
/// fx. <c>tags</c>: the catalog of tags. Everything but the name is inherited: a workspace below adds its own tags, but
/// does not redefine those from above.
/// </summary>
public sealed partial record FxConfig(string? Name, string? Requires, IReadOnlyList<FxTag> Tags)
{
	public const string Invalid = "fx.invalid-config";
	public const string TagRedefined = "fx.tag-redefined";

	public static FxConfig Empty { get; } = new(null, null, []);

	[GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
	public static partial Regex NamePattern();

	/// <summary><paramref name="relative"/> under <paramref name="root"/>, if it exists; its problems go to <paramref name="diagnostics"/>.</summary>
	public static FxConfig Load(string root, string relative, ICollection<FxDiagnostic> diagnostics)
	{
		var path = FxLayout.Full(root, relative);
		return FxFiles.Exists(path) ? Read(FxFiles.ReadAllText(path), relative, diagnostics) : Empty;
	}

	public static FxConfig Read(string text, string file, ICollection<FxDiagnostic> diagnostics)
	{
		var reader = new FxYamlReader(Invalid, file);
		var config = Empty;
		if (reader.Load(text) is { } map)
		{
			reader.Keys(map, "schema", "name", "requires", "tags");
			var name = FxYamlReader.Scalar(map["name"]);
			if (name is not null && !NamePattern().IsMatch(name))
				reader.Error(map["name"], "name", name);
			var requires = FxYamlReader.Scalar(map["requires"]);
			if (requires is not null && FxYamlReader.Parse(requires) is null)
				reader.Error(map["requires"], "requires", requires);
			var tags = new List<FxTag>();
			if (map["tags"] is YamlSequence list)
				foreach (var item in list.Items)
					if (item is YamlMapping tag && FxYamlReader.Scalar(tag["name"]) is { Length: > 0 } tagName)
					{
						reader.Keys(tag, "name", "description");
						tags.Add(new(tagName, FxYamlReader.Scalar(tag["description"]), item.Line));
					}
					else
						reader.Error(item, "tag");
			else if (map["tags"] is { } notAList)
				reader.Error(notAList, "tags");
			config = new(name, requires, tags);
		}
		foreach (var diagnostic in reader.Diagnostics)
			diagnostics.Add(diagnostic);
		return config;
	}

	/// <summary>
	/// What comes from above with this one on top: the name is this one's (it is not inherited); <c>requires</c>, the
	/// highest; the tags, those from above and the new ones of this one (one already defined above stays as it was, with
	/// a warning in <paramref name="diagnostics"/>).
	/// </summary>
	public static FxConfig Merge(FxConfig inherited, FxConfig own, string file, ICollection<FxDiagnostic>? diagnostics = null)
	{
		var tags = inherited.Tags.ToList();
		foreach (var tag in own.Tags)
			if (tags.Any(t => string.Equals(t.Name, tag.Name, StringComparison.OrdinalIgnoreCase)))
				diagnostics?.Add(FxDiagnostic.Warning(TagRedefined, new(TagRedefined, tag.Name), file, tag.Line));
			else
				tags.Add(tag);
		return new(own.Name, Highest(inherited.Requires, own.Requires), tags);
	}

	static string? Highest(string? a, string? b)
		=> a is null ? b : b is null ? a : FxYamlReader.Parse(a) is { } va && FxYamlReader.Parse(b) is { } vb && vb.CompareTo(va) > 0 ? b : a;

	/// <summary>Whether the catalog has <paramref name="tag"/>.</summary>
	public bool Knows(string tag) => Tags.Any(t => string.Equals(t.Name, tag, StringComparison.OrdinalIgnoreCase));

	/// <summary>Whether <paramref name="version"/> (fx's) is older than <see cref="Requires"/>.</summary>
	public bool RequiresNewer(string version)
		=> Requires is not null && FxYamlReader.Parse(Requires) is { } required && FxYamlReader.Parse(version) is { } current && current.CompareTo(required) < 0;

	/// <summary>What a repository receives in <c>_fx/.inherited/fx.yaml</c>: all but the name, with the header.</summary>
	public string ToInheritedYaml(string from)
	{
		var entries = new List<(string, YamlValue)> { ("schema", FxYamlReader.Schema) };
		if (Requires is not null)
			entries.Add(("requires", Requires));
		if (Tags.Count > 0)
			entries.Add(("tags", new YamlValue.Block(Tags.Select(t => (YamlValue)(t.Description is null
				? new YamlValue.Map(("name", t.Name))
				: new YamlValue.Map(("name", t.Name), ("description", t.Description)))).ToArray())));
		return FxHeader.Hash(from) + YamlWriter.Document(new(entries.ToArray()));
	}
}
