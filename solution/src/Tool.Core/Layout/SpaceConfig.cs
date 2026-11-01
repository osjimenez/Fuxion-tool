using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Yaml;

namespace Fuxion.Tool.Core.Layout;

/// <summary>A tag of the catalog: its name and what it means.</summary>
public sealed record FxTag(string Name, string? Description, int Line = 0);

/// <summary>
/// An audience of the catalog, who may read something: its name and what it means. The
/// catalog goes from the widest audience to the narrowest.
/// </summary>
public sealed record FxAudience(string Name, string? Description, int Line = 0);

/// <summary>
/// What the git of the space versions: a file, or a folder (ending in
/// '/'). With <see cref="Exclude"/>, an exception inside something included; with <see cref="Worktree"/> false, a folder
/// the worktrees of the space leave out unless asked.
/// </summary>
public sealed record SpacePath(string Path, bool Exclude = false, bool Worktree = true, int Line = 0)
{
	public bool IsFolder => Path.EndsWith('/');
}

/// <summary>A file (or folder) the space gives what it mounts.</summary>
public sealed record SpaceShare(string Path, string Destination, FxAppliesTo AppliesTo, int Line = 0);

/// <summary>
/// <c>_fx/space.yaml</c>, the descriptor of a space. <c>name</c>: the name of a
/// root space (nobody mounts it); in a sub-space, only a proposal. <c>audience</c>: who may read what is its own (one
/// of the catalog; undeclared, the widest). <c>audiences</c>: the catalog of audiences, from the widest to the narrowest
/// (undeclared, public and private). <c>requires</c>: the minimum version of fx. <c>tags</c>: the catalog of tags.
/// <c>paths</c>: what its git versions. <c>shares</c>: what it gives what it mounts. Only <c>requires</c> and the two
/// catalogs are inherited: a space below adds its own tags, but does not redefine those from above, nor the audiences.
/// fx only reads, validates and shows the audience; what may be written for each one is for whoever reviews.
/// </summary>
public sealed partial record SpaceConfig(
	string? Name,
	string? Requires,
	IReadOnlyList<FxTag> Tags,
	string? Audience = null,
	IReadOnlyList<SpacePath>? Paths = null,
	IReadOnlyList<SpaceShare>? Shares = null,
	IReadOnlyList<FxAudience>? Audiences = null,
	int AudienceLine = 0)
{
	public const string Invalid = "space.invalid-config";
	public const string TagRedefined = "space.tag-redefined";
	public const string AudiencesRedefined = "space.audiences-redefined";

	/// <summary>The catalog of audiences of a space that declares none: public and private.</summary>
	public static IReadOnlyList<FxAudience> DefaultAudiences { get; } = [new("public", "Anyone."), new("private", "Only its owner, and whom they share it with.")];

	/// <summary>The catalog of audiences in force: the declared one, or public and private.</summary>
	public IReadOnlyList<FxAudience> AudienceCatalog => Audiences is { Count: > 0 } ? Audiences : DefaultAudiences;

	/// <summary>The widest audience of the catalog: that of what does not declare one.</summary>
	public string WidestAudience => AudienceCatalog[0].Name;

	/// <summary>The audience of what is its own: the declared one, or the widest.</summary>
	public string OwnAudience => Audience ?? WidestAudience;

	/// <summary>Whether the catalog of audiences has <paramref name="audience"/>.</summary>
	public bool KnowsAudience(string audience) => AudienceCatalog.Any(a => string.Equals(a.Name, audience, StringComparison.Ordinal));

	public static SpaceConfig Empty { get; } = new(null, null, []);

	[GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
	public static partial Regex NamePattern();

	/// <summary>
	/// What can be read of <paramref name="relative"/> under <paramref name="root"/> (its own <c>_fx/space.yaml</c> by
	/// default), whatever its problems (<see cref="ReadFile"/> finds them); empty if it does not exist.
	/// </summary>
	public static SpaceConfig Load(string root, string relative = FxLayout.ConfigFile)
	{
		var path = FxLayout.Full(root, relative);
		return FxFiles.Exists(path) ? Parse(FxFiles.ReadAllText(path), relative) : Empty;
	}

	/// <summary><paramref name="relative"/> under <paramref name="root"/>, read with its problems: an error if it has one; empty if it does not exist.</summary>
	public static Response<SpaceConfig> ReadFile(string root, string relative = FxLayout.ConfigFile)
	{
		var path = FxLayout.Full(root, relative);
		return FxFiles.Exists(path) ? Read(FxFiles.ReadAllText(path), relative) : Empty;
	}

	/// <summary>A <c>space.yaml</c> read with its problems: an error (<see cref="HttpStatusCode.Conflict"/>) if it has one; its warnings, with it.</summary>
	public static Response<SpaceConfig> Read(string text, string file)
	{
		var found = new List<FxDiagnostic>();
		return FxResponse.Outcome(Parse(text, file, found), found, HttpStatusCode.Conflict);
	}

	/// <summary>What can be read of a <c>space.yaml</c>, whatever its problems.</summary>
	public static SpaceConfig Parse(string text, string file) => Parse(text, file, null);

	static SpaceConfig Parse(string text, string file, List<FxDiagnostic>? found)
	{

		var reader = new FxYamlReader(Invalid, file);
		var config = Empty;
		if (reader.Load(text) is YamlMapping map)
		{
			reader.Keys(map, "schema", "name", "audience", "audiences", "requires", "tags", "paths", "shares");
			var name = FxYamlReader.Scalar(map["name"]);
			if (name is not null && !NamePattern().IsMatch(name))
				reader.Error(map["name"], "name", name);
			var audience = FxYamlReader.Scalar(map["audience"]);
			var audiences = new List<FxAudience>();
			if (map["audiences"] is YamlSequence audienceList)
				foreach (var item in audienceList.Items)
					if (item is YamlMapping entry && FxYamlReader.Scalar(entry["name"]) is { Length: > 0 } audienceName)
					{
						reader.Keys(entry, "name", "description");
						if (!NamePattern().IsMatch(audienceName))
							reader.Error(entry["name"], "audience-name", audienceName);
						else if (audiences.Any(a => a.Name == audienceName))
							reader.Error(entry["name"], "duplicate-audience", audienceName);
						else
							audiences.Add(new(audienceName, FxYamlReader.Scalar(entry["description"]), item.Line));
					}
					else
						reader.Error(item, "audience-entry");
			else if (map["audiences"] is { } notAudiences)
				reader.Error(notAudiences, "audiences");
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
			config = new(name, requires, tags, audience, ReadPaths(map, reader), ReadShares(map, reader), audiences, map["audience"]?.Line ?? 0);
		}
		found?.AddRange(reader.Diagnostics.Concat(config.Validate(file)));
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
		FxDiagnostic Error(int line, string key, params FxArg[] args) => FxDiagnostic.Error(Invalid, new($"{Invalid}.{key}", args), file, line);

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
			if (!Sync.SpaceShares.SafeSource(share.Path))
				yield return Error(share.Line, "share-path", share.Path);
			if (!Sync.SpaceShares.SafeDestination(share.Destination))
				yield return Error(share.Line, "share-destination", share.Path, share.Destination);
		}
		foreach (var duplicate in (Shares ?? []).GroupBy(s => s.Destination, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			yield return Error(duplicate.Last().Line, "duplicate-share", duplicate.Key);
	}

	/// <summary>
	/// What comes from above with this one on top: the name, the audience, the paths and the shares are this one's (they
	/// are not inherited); <c>requires</c>, the highest; the tags, those from above and the new ones of this one (one
	/// already defined above stays as it was, with a warning); the audiences, those from above if they declare any (this
	/// one's, then, are ignored with a warning). Its audience must be one of them.
	/// </summary>
	public static Response<SpaceConfig> Merge(SpaceConfig inherited, SpaceConfig own, string file)
	{
		var found = new List<FxDiagnostic>();
		return FxResponse.Outcome(Combine(inherited, own, file, found), found, HttpStatusCode.Conflict);
	}

	/// <summary>What comes from above with this one on top (as <see cref="Merge"/>), whatever its problems.</summary>
	public static SpaceConfig Combine(SpaceConfig inherited, SpaceConfig own) => Combine(inherited, own, FxLayout.ConfigFile, null);

	static SpaceConfig Combine(SpaceConfig inherited, SpaceConfig own, string file, List<FxDiagnostic>? found)
	{
		var tags = inherited.Tags.ToList();
		foreach (var tag in own.Tags)
			if (tags.Any(t => string.Equals(t.Name, tag.Name, StringComparison.OrdinalIgnoreCase)))
				found?.Add(FxDiagnostic.Warning(TagRedefined, new(TagRedefined, tag.Name), file, tag.Line));
			else
				tags.Add(tag);
		var audiences = own.Audiences;
		if (inherited.Audiences is { Count: > 0 })
		{
			if (own.Audiences is { Count: > 0 } redefined)
				found?.Add(FxDiagnostic.Warning(AudiencesRedefined, new(AudiencesRedefined), file, redefined[0].Line));
			audiences = inherited.Audiences;
		}
		var merged = own with { Requires = Highest(inherited.Requires, own.Requires), Tags = tags, Audiences = audiences };
		if (own.Audience is { } audience && !merged.KnowsAudience(audience))
			found?.Add(FxDiagnostic.Error(Invalid, new($"{Invalid}.audience", audience, string.Join(", ", merged.AudienceCatalog.Select(a => a.Name))), file, own.AudienceLine));
		return merged;
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
	public bool Versions(string relative) => Entry(relative) is { Exclude: false };

	/// <summary>The entry of <c>paths</c> that decides about <paramref name="relative"/> (a path of the space, with '/'): the longest that contains it, if any.</summary>
	public SpacePath? Entry(string relative) => (Paths ?? []).Where(p => Contains(p, relative)).MaxBy(p => p.Path.Length);

	/// <summary>
	/// Whether the worktrees of the space leave <paramref name="relative"/> (a path of the space, with '/') out: it is, or
	/// is inside, an entry with <c>worktree: false</c> that is not an exclusion, as <c>fx worktree add</c> does.
	/// </summary>
	public bool LeavesOut(string relative) => (Paths ?? []).Any(p => !p.Worktree && !p.Exclude && Contains(p, relative));

	static bool Contains(SpacePath entry, string relative)
		=> entry.IsFolder
			? relative.StartsWith(entry.Path, StringComparison.OrdinalIgnoreCase) || string.Equals(relative.TrimEnd('/') + "/", entry.Path, StringComparison.OrdinalIgnoreCase)
			: string.Equals(relative.TrimEnd('/'), entry.Path, StringComparison.OrdinalIgnoreCase);

	/// <summary>What a sub-space receives in <c>_fx/.inherited/space.yaml</c>: <c>requires</c> and the two catalogs, with the header.</summary>
	public string ToInheritedYaml(string from)
	{
		static YamlValue Named(string name, string? description) => description is null
			? new YamlValue.Map(("name", name))
			: new YamlValue.Map(("name", name), ("description", description));
		var entries = new List<(string, YamlValue)> { ("schema", FxYamlReader.Schema) };
		if (Requires is not null)
			entries.Add(("requires", Requires));
		if (Audiences is { Count: > 0 })
			entries.Add(("audiences", new YamlValue.Block(Audiences.Select(a => Named(a.Name, a.Description)).ToArray())));
		if (Tags.Count > 0)
			entries.Add(("tags", new YamlValue.Block(Tags.Select(t => Named(t.Name, t.Description)).ToArray())));
		return FxHeader.Hash(from) + YamlWriter.Document(new(entries.ToArray()));
	}
}
