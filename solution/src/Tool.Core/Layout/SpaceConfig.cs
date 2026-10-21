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

/// <summary>Whether a space is public or private (plan spaces, decision 23): what may live in it. Undeclared, private.</summary>
public enum SpaceVisibility
{
	Private,
	Public
}

/// <summary>
/// What the git of the space versions (plan P, decision 8; plan spaces, decision 11): a file, or a folder (ending in
/// '/'). With <see cref="Exclude"/>, an exception inside something included; with <see cref="Worktree"/> false, a folder
/// the worktrees of the space leave out unless asked (decision 26).
/// </summary>
public sealed record SpacePath(string Path, bool Exclude = false, bool Worktree = true, int Line = 0)
{
	public bool IsFolder => Path.EndsWith('/');
}

/// <summary>A file (or folder) the space gives what it mounts (plan P, decision 23; plan spaces, decision 19).</summary>
public sealed record SpaceShare(string Path, string Destination, FxAppliesTo AppliesTo, int Line = 0);

/// <summary>
/// <c>_fx/space.yaml</c>, the descriptor of a space (plan spaces, decisions 3, 11, 19 and 23). <c>name</c>: the name of a
/// root space (nobody mounts it); in a sub-space, only a proposal. <c>visibility</c>: public or private. <c>requires</c>:
/// the minimum version of fx. <c>tags</c>: the catalog of tags. <c>paths</c>: what its git versions. <c>shares</c>: what
/// it gives what it mounts. Only <c>requires</c> and the tags are inherited: a space below adds its own tags, but does
/// not redefine those from above.
/// </summary>
public sealed partial record SpaceConfig(
	string? Name,
	string? Requires,
	IReadOnlyList<FxTag> Tags,
	SpaceVisibility Visibility = SpaceVisibility.Private,
	IReadOnlyList<SpacePath>? Paths = null,
	IReadOnlyList<SpaceShare>? Shares = null)
{
	public const string Invalid = "space.invalid-config";
	public const string TagRedefined = "space.tag-redefined";

	public static SpaceConfig Empty { get; } = new(null, null, []);

	[GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
	public static partial Regex NamePattern();

	/// <summary>Its own <c>_fx/space.yaml</c> under <paramref name="root"/>, if it exists; its problems go to <paramref name="diagnostics"/>.</summary>
	public static SpaceConfig Load(string root, ICollection<FxDiagnostic>? diagnostics = null) => Load(root, FxLayout.ConfigFile, diagnostics ?? []);

	/// <summary><paramref name="relative"/> under <paramref name="root"/>, if it exists; its problems go to <paramref name="diagnostics"/>.</summary>
	public static SpaceConfig Load(string root, string relative, ICollection<FxDiagnostic> diagnostics)
	{
		var path = FxLayout.Full(root, relative);
		return FxFiles.Exists(path) ? Read(FxFiles.ReadAllText(path), relative, diagnostics) : Empty;
	}

	public static SpaceConfig Read(string text, string file, ICollection<FxDiagnostic> diagnostics)
	{
		var reader = new FxYamlReader(Invalid, file);
		var config = Empty;
		if (reader.Load(text) is { } map)
		{
			reader.Keys(map, "schema", "name", "visibility", "requires", "tags", "paths", "shares");
			var name = FxYamlReader.Scalar(map["name"]);
			if (name is not null && !NamePattern().IsMatch(name))
				reader.Error(map["name"], "name", name);
			var visibilityText = FxYamlReader.Scalar(map["visibility"]);
			var visibility = visibilityText is null or "private" ? SpaceVisibility.Private : visibilityText == "public" ? SpaceVisibility.Public : (SpaceVisibility?)null;
			if (visibility is null)
				reader.Error(map["visibility"], "visibility", visibilityText);
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
			config = new(name, requires, tags, visibility ?? SpaceVisibility.Private, ReadPaths(map, reader), ReadShares(map, reader));
		}
		foreach (var diagnostic in reader.Diagnostics.Concat(config.Validate(file)))
			diagnostics.Add(diagnostic);
		return config;
	}

	static List<SpacePath> ReadPaths(YamlMapping map, FxYamlReader reader)
	{
		var paths = new List<SpacePath>();
		if (map["paths"] is YamlSequence pathList)
			foreach (var item in pathList.Items)
			{
				if (item is not YamlMapping entry || FxYamlReader.Scalar(entry["path"]) is not { Length: > 0 } path)
				{
					reader.Error(item, "path-entry");
					continue;
				}
				reader.Keys(entry, "path", "exclude", "worktree");
				paths.Add(new(path.Replace('\\', '/').TrimStart('/'), reader.Bool(entry, "exclude", false), reader.Bool(entry, "worktree", true), item.Line));
			}
		else if (map["paths"] is { } notAList && notAList is not YamlScalar { Value: "" })
			reader.Error(notAList, "list", "paths");
		return paths;
	}

	static List<SpaceShare> ReadShares(YamlMapping map, FxYamlReader reader)
	{
		var shares = new List<SpaceShare>();
		if (map["shares"] is YamlSequence shareList)
			foreach (var item in shareList.Items)
			{
				if (item is not YamlMapping entry || FxYamlReader.Scalar(entry["path"]) is not { Length: > 0 } path)
				{
					reader.Error(item, "share-entry");
					continue;
				}
				reader.Keys(entry, "path", "destination", "applies-to");
				path = path.Replace('\\', '/').TrimStart('/');
				var destination = FxYamlReader.Scalar(entry["destination"])?.Replace('\\', '/').TrimStart('/') ?? path;
				// a folder goes to a folder; a file to a folder ('/' at the end) keeps its name
				if (path.EndsWith('/') && !destination.EndsWith('/'))
					destination += "/";
				else if (!path.EndsWith('/') && destination.EndsWith('/'))
					destination += System.IO.Path.GetFileName(path);
				shares.Add(new(path, destination, FxAppliesTo.Read(entry, reader) ?? FxAppliesTo.All, item.Line));
			}
		else if (map["shares"] is { } notAList && notAList is not YamlScalar { Value: "" })
			reader.Error(notAList, "list", "shares");
		return shares;
	}

	IEnumerable<FxDiagnostic> Validate(string file)
	{
		FxDiagnostic Error(int line, string key, params object?[] args) => FxDiagnostic.Error(Invalid, new($"{Invalid}.{key}", args), file, line);

		static bool BadPath(string path)
			=> System.IO.Path.IsPathRooted(path) || path.TrimEnd('/').Split('/').Any(s => s is "" or "." or "..");

		foreach (var path in Paths ?? [])
			if (BadPath(path.Path))
				yield return Error(path.Line, "path-relative", path.Path);
			else if (FxLayout.IsLocal(path.Path.Split('/')[0]) || path.Path.TrimEnd('/') is FxLayout.Folder or ".git" or ".gitignore")
				yield return Error(path.Line, "path-fixed", path.Path);
		foreach (var duplicate in (Paths ?? []).GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			yield return Error(duplicate.Last().Line, "duplicate-path-entry", duplicate.Key);
		foreach (var share in Shares ?? [])
		{
			if (!Workspace.WorkspaceShares.SafeSource(share.Path))
				yield return Error(share.Line, "share-path", share.Path);
			if (!Workspace.WorkspaceShares.SafeDestination(share.Destination))
				yield return Error(share.Line, "share-destination", share.Path, share.Destination);
		}
		foreach (var duplicate in (Shares ?? []).GroupBy(s => s.Destination, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			yield return Error(duplicate.Last().Line, "duplicate-share", duplicate.Key);
	}

	/// <summary>
	/// What comes from above with this one on top: the name, the visibility, the paths and the shares are this one's (they
	/// are not inherited); <c>requires</c>, the highest; the tags, those from above and the new ones of this one (one
	/// already defined above stays as it was, with a warning in <paramref name="diagnostics"/>).
	/// </summary>
	public static SpaceConfig Merge(SpaceConfig inherited, SpaceConfig own, string file, ICollection<FxDiagnostic>? diagnostics = null)
	{
		var tags = inherited.Tags.ToList();
		foreach (var tag in own.Tags)
			if (tags.Any(t => string.Equals(t.Name, tag.Name, StringComparison.OrdinalIgnoreCase)))
				diagnostics?.Add(FxDiagnostic.Warning(TagRedefined, new(TagRedefined, tag.Name), file, tag.Line));
			else
				tags.Add(tag);
		return own with { Requires = Highest(inherited.Requires, own.Requires), Tags = tags };
	}

	static string? Highest(string? a, string? b)
		=> a is null ? b : b is null ? a : FxYamlReader.Parse(a) is { } va && FxYamlReader.Parse(b) is { } vb && vb.CompareTo(va) > 0 ? b : a;

	/// <summary>Whether the catalog has <paramref name="tag"/>.</summary>
	public bool Knows(string tag) => Tags.Any(t => string.Equals(t.Name, tag, StringComparison.OrdinalIgnoreCase));

	/// <summary>Whether <paramref name="version"/> (fx's) is older than <see cref="Requires"/>.</summary>
	public bool RequiresNewer(string version)
		=> Requires is not null && FxYamlReader.Parse(Requires) is { } required && FxYamlReader.Parse(version) is { } current && current.CompareTo(required) < 0;

	/// <summary>
	/// Whether <paramref name="relative"/> (a path of the space, with '/') is versioned by its <c>paths</c>: the longest
	/// entry that contains it decides (an exclusion inside an inclusion wins).
	/// </summary>
	public bool Versions(string relative)
	{
		var match = (Paths ?? [])
			.Where(p => p.IsFolder
				? relative.StartsWith(p.Path, StringComparison.OrdinalIgnoreCase) || string.Equals(relative.TrimEnd('/') + "/", p.Path, StringComparison.OrdinalIgnoreCase)
				: string.Equals(relative.TrimEnd('/'), p.Path, StringComparison.OrdinalIgnoreCase))
			.MaxBy(p => p.Path.Length);
		return match is { Exclude: false };
	}

	/// <summary>What a sub-space receives in <c>_fx/.inherited/space.yaml</c>: <c>requires</c> and the tags, with the header.</summary>
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
