using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Yaml;

namespace Fuxion.Tool.Core.Layout;

/// <summary>Whose a repository is: its workspace (full name), its remote, the branch read and the commit of what was read.</summary>
public sealed record FxInheritedParent(string FullName, string? Url, string? Ref, string? Commit);

/// <summary>A file of <c>_fx/.inherited/</c> (relative to it) and the roots it comes from (several: merged).</summary>
public sealed record FxInheritedFile(string Path, IReadOnlyList<string> From);

/// <summary>A file of the repository, outside <c>_fx/</c>, that is fx's: a share (plan P, decision 23).</summary>
public sealed record FxInheritedShare(string Destination, string From);

/// <summary>
/// <c>_fx/.inherited/inherited.yaml</c> (plan P, decision 14): the repository as its workspace declares it, whose it
/// is, and what fx put in <c>_fx/.inherited/</c> (and outside, the shares), each with where it comes from. Generated.
/// </summary>
public sealed record FxInherited(FxIdentity Repository, FxInheritedParent Parent, IReadOnlyList<FxInheritedFile> Files, IReadOnlyList<FxInheritedShare> Shares)
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
		if (reader.Load(text) is { } map && map["repository"] is YamlMapping repository && map["parent"] is YamlMapping parent)
		{
			var name = FxYamlReader.Scalar(repository["name"]) ?? "";
			var fullName = FxYamlReader.Scalar(repository["full-name"]) ?? "";
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
					new(name, fullName, FxYamlReader.List(repository["tags"]), FxYamlReader.List(repository["aliases"])),
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
		var repository = new List<(string, YamlValue)> { ("name", Repository.Name), ("full-name", Repository.FullName) };
		if (Repository.Tags.Count > 0)
			repository.Add(("tags", Flow(Repository.Tags)));
		if (Repository.Aliases is { Count: > 0 } aliases)
			repository.Add(("aliases", Flow(aliases)));
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
			("repository", new YamlValue.Map(repository.ToArray())),
			("parent", new YamlValue.Map(parent.ToArray()))
		};
		if (Files.Count > 0)
			entries.Add(("files", new YamlValue.Block(Files.Select(f => (YamlValue)new YamlValue.Map(
				("path", f.Path), ("from", f.From.Count == 1 ? f.From[0] : Flow(f.From)))).ToArray())));
		if (Shares.Count > 0)
			entries.Add(("shares", new YamlValue.Block(Shares.Select(s => (YamlValue)new YamlValue.Map(
				("destination", s.Destination), ("from", s.From))).ToArray())));
		return FxHeader.Hash($"workspace {Parent.FullName}") + YamlWriter.Document(new(entries.ToArray()));
	}

	/// <summary>Whether this and <paramref name="other"/> say the same, but for the branch and commit of the parent.</summary>
	public bool SameContent(FxInherited? other)
		=> other is not null
		   && other.Repository.Name == Repository.Name && other.Repository.FullName == Repository.FullName
		   && other.Repository.Tags.SequenceEqual(Repository.Tags) && (other.Repository.Aliases ?? []).SequenceEqual(Repository.Aliases ?? [])
		   && other.Parent.FullName == Parent.FullName && other.Parent.Url == Parent.Url
		   && other.Files.Count == Files.Count && other.Files.Zip(Files).All(p => p.First.Path == p.Second.Path && p.First.From.SequenceEqual(p.Second.From))
		   && other.Shares.SequenceEqual(Shares);

	static YamlValue.Flow Flow(IEnumerable<string> items) => new(items.Select(i => (YamlValue)i).ToArray());
}
