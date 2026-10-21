using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Yaml;

namespace Fuxion.Tool.Core.Layout;

/// <summary>Whose a space is: its parent (full name), its remote, the branch read and the commit of what was read.</summary>
public sealed record FxInheritedParent(string FullName, string? Url, string? Ref, string? Commit);

/// <summary>A file of <c>_fx/.inherited/</c> (relative to it) and the roots it comes from (several: merged).</summary>
public sealed record FxInheritedFile(string Path, IReadOnlyList<string> From);

/// <summary>A file of the space, outside <c>_fx/</c>, that is fx's: a share (plan P, decision 23).</summary>
public sealed record FxInheritedShare(string Destination, string From);

/// <summary>
/// <c>_fx/.inherited/inherited.yaml</c> (plan P, decision 14): the space as its parent mounts it, whose it
/// is, and what fx put in <c>_fx/.inherited/</c> (and outside, the shares), each with where it comes from. Generated.
/// </summary>
public sealed record FxInherited(FxIdentity Space, FxInheritedParent Parent, IReadOnlyList<FxInheritedFile> Files, IReadOnlyList<FxInheritedShare> Shares)
{
	public const string Invalid = "fx.invalid-inherited";

	/// <summary>The <c>inherited.yaml</c> of the root at <paramref name="root"/>, if it has a readable one.</summary>
	public static FxInherited? Load(string root, ICollection<FxDiagnostic>? diagnostics = null)
	{
		var path = FxLayout.Full(root, FxLayout.InheritedFile);
		return FxFiles.Exists(path) ? Read(FxFiles.ReadAllText(path), FxLayout.InheritedFile, diagnostics) : null;
	}

	public static FxInherited? Read(string text, string file, ICollection<FxDiagnostic>? diagnostics = null)
	{
		var reader = new FxYamlReader(Invalid, file);
		FxInherited? result = null;
		if (reader.Load(text) is { } map && map["space"] is YamlMapping space && map["parent"] is YamlMapping parent)
		{
			var name = FxYamlReader.Scalar(space["name"]) ?? "";
			var fullName = FxYamlReader.Scalar(space["full-name"]) ?? "";
			var files = (map["files"] as YamlSequence)?.Items.OfType<YamlMapping>()
				.Select(f => new FxInheritedFile(FxYamlReader.Scalar(f["path"]) ?? "", FxYamlReader.List(f["from"])))
				.Where(f => f.Path.Length > 0)
				.ToList() ?? [];
			var shares = (map["shares"] as YamlSequence)?.Items.OfType<YamlMapping>()
				.Select(s => new FxInheritedShare(FxYamlReader.Scalar(s["destination"]) ?? "", FxYamlReader.Scalar(s["from"]) ?? ""))
				.Where(s => s.Destination.Length > 0)
				.ToList() ?? [];
			if (name.Length > 0 && fullName.Length > 0)
				result = new(
					new(name, fullName, FxYamlReader.List(space["tags"]), FxYamlReader.List(space["aliases"])),
					new(FxYamlReader.Scalar(parent["full-name"]) ?? "", FxYamlReader.Scalar(parent["url"]), FxYamlReader.Scalar(parent["ref"]), FxYamlReader.Scalar(parent["commit"])),
					files, shares);
		}
		if (result is null && !reader.HasErrors)
			reader.Error(null, "shape");
		foreach (var diagnostic in reader.Diagnostics)
			diagnostics?.Add(diagnostic);
		return result;
	}

	/// <summary>The file, with its header.</summary>
	public string ToYaml()
	{
		var space = new List<(string, YamlValue)> { ("name", Space.Name), ("full-name", Space.FullName) };
		if (Space.Tags.Count > 0)
			space.Add(("tags", Flow(Space.Tags)));
		if (Space.Aliases is { Count: > 0 } aliases)
			space.Add(("aliases", Flow(aliases)));
		var parent = new List<(string, YamlValue)> { ("full-name", Parent.FullName) };
		if (Parent.Url is not null)
			parent.Add(("url", Parent.Url));
		if (Parent.Ref is not null)
			parent.Add(("ref", Parent.Ref));
		if (Parent.Commit is not null)
			parent.Add(("commit", Parent.Commit));
		var entries = new List<(string, YamlValue)>
		{
			("schema", FxYamlReader.Schema),
			("space", new YamlValue.Map(space.ToArray())),
			("parent", new YamlValue.Map(parent.ToArray()))
		};
		if (Files.Count > 0)
			entries.Add(("files", new YamlValue.Block(Files.Select(f => (YamlValue)new YamlValue.Map(
				("path", f.Path), ("from", f.From.Count == 1 ? f.From[0] : Flow(f.From)))).ToArray())));
		if (Shares.Count > 0)
			entries.Add(("shares", new YamlValue.Block(Shares.Select(s => (YamlValue)new YamlValue.Map(
				("destination", s.Destination), ("from", s.From))).ToArray())));
		return FxHeader.Hash($"space {Parent.FullName}") + YamlWriter.Document(new(entries.ToArray()));
	}

	/// <summary>Whether this and <paramref name="other"/> say the same, but for the branch and commit of the parent.</summary>
	public bool SameContent(FxInherited? other)
		=> other is not null
		   && other.Space.Name == Space.Name && other.Space.FullName == Space.FullName
		   && other.Space.Tags.SequenceEqual(Space.Tags) && (other.Space.Aliases ?? []).SequenceEqual(Space.Aliases ?? [])
		   && other.Parent.FullName == Parent.FullName && other.Parent.Url == Parent.Url
		   && other.Files.Count == Files.Count && other.Files.Zip(Files).All(p => p.First.Path == p.Second.Path && p.First.From.SequenceEqual(p.Second.From))
		   && other.Shares.SequenceEqual(Shares);

	static YamlValue.Flow Flow(IEnumerable<string> items) => new(items.Select(i => (YamlValue)i).ToArray());
}
