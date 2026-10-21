using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Yaml;

namespace Fuxion.Tool.Core.Mounts;

/// <summary>What may be done in a mount (plan spaces, decision 13); what makes each one hold comes with them.</summary>
public enum MountMode
{
	/// <summary>Anything: the default.</summary>
	Write,
	/// <summary>Read it, never write in it.</summary>
	ReadOnly,
	/// <summary>Changes go on a branch and by pull request, never to its main branch.</summary>
	ProposeOnly
}

/// <summary>
/// A space this space mounts (plan P, decisions 1, 10-12 and 18; plan spaces, decisions 6 and 20): a repository of code
/// or another space that mounts others. The space that mounts it gives it its name and its tags.
/// </summary>
/// <param name="Path">Where, relative to the space, with '/': any folder, at any depth.</param>
/// <param name="Required">Always mounted: it cannot be parked. Without it, each copy parks it or brings it back.</param>
/// <param name="Inherits">False for a guest: the space uses it, but it inherits from its own parent.</param>
/// <param name="Aliases">Its old names (<c>fx repo rename</c>), so that old references keep working.</param>
public sealed record Mount(
	string Name,
	string Path,
	IReadOnlyList<string> Tags,
	string? Url = null,
	bool Required = false,
	MountMode Mode = MountMode.Write,
	IReadOnlyList<string>? DependsOn = null,
	bool Inherits = true,
	IReadOnlyList<string>? Aliases = null,
	int Line = 0);

/// <summary>An invalid <c>mounts.yaml</c>: every problem found, each with its line.</summary>
public sealed class MountsManifestException(IReadOnlyList<FxDiagnostic> diagnostics)
	: Exception(string.Join(Environment.NewLine, diagnostics.Select(d => d.WhereIn(CultureInfo.InvariantCulture))))
{
	public IReadOnlyList<FxDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>
/// <c>_fx/mounts/mounts.yaml</c>, the module that makes a space mount others (plan P, decision 3; plan spaces, decision
/// 6): what it mounts. What its git versions and what it shares are the space's (<c>_fx/space.yaml</c>, plan spaces,
/// decisions 11 and 19). Nothing about solutions or .NET.
/// </summary>
public sealed record MountsManifest(string Root, IReadOnlyList<Mount> Mounts)
{
	public const string InvalidManifest = "mounts.invalid-manifest";
	public const string ModuleName = "mounts";
	public static readonly string RelativePath = FxLayout.ModuleFile(ModuleName);

	/// <summary>The first segments the path of a mount cannot take: fx's and git's.</summary>
	public static readonly IReadOnlyList<string> ReservedSegments = [FxLayout.Folder, ".git", ".gitignore"];

	/// <summary>The modes as <c>mounts.yaml</c> writes them.</summary>
	public static readonly IReadOnlyList<string> Modes = ["write", "read-only", "propose-only"];

	/// <summary>The mode written as <paramref name="text"/>; null if it is none.</summary>
	public static MountMode? ParseMode(string? text)
		=> text switch
		{
			"write" => MountMode.Write,
			"read-only" => MountMode.ReadOnly,
			"propose-only" => MountMode.ProposeOnly,
			_ => null
		};

	/// <summary>How <c>mounts.yaml</c> writes <paramref name="mode"/>.</summary>
	public static string ModeText(MountMode mode) => Modes[(int)mode];

	/// <summary>Whether the space at <paramref name="root"/> mounts others: it has the module, and it is not off.</summary>
	public static bool HasMounts(string root)
	{
		var file = FxLayout.Full(root, RelativePath);
		if (!FxFiles.DirectoryExists(FxLayout.Full(root, FxLayout.ModuleFolder(ModuleName))))
			return false;
		if (!FxFiles.Exists(file))
			return true;
		try
		{
			return YamlDocument.Load(FxFiles.ReadAllText(file)) is not YamlMapping map || map["enabled"] is not YamlScalar { Value: "false" };
		}
		catch (YamlSyntaxException)
		{
			return true;
		}
	}

	/// <summary>The nearest space that mounts others and contains <paramref name="directory"/> (itself included), if any.</summary>
	public static string? FindRoot(string directory)
	{
		for (var dir = new DirectoryInfo(System.IO.Path.GetFullPath(directory)); dir is not null; dir = dir.Parent)
			if (HasMounts(dir.FullName))
				return FxLayout.Normalize(dir.FullName);
		return null;
	}

	/// <summary>Reads and validates the <c>mounts.yaml</c> of the space at <paramref name="root"/>.</summary>
	public static MountsManifest Load(string root)
	{
		var file = FxLayout.Full(root, RelativePath);
		return Read(root, FxFiles.Exists(file) ? FxFiles.ReadAllText(file) : "", strict: true);
	}

	/// <summary>
	/// Reads a <c>mounts.yaml</c>. Lenient by default (what other modules need: the mounts and their tags); with
	/// <paramref name="strict"/>, every problem is an error with its line.
	/// </summary>
	public static MountsManifest Read(string root, string yaml, bool strict = false)
	{
		var reader = new FxYamlReader(InvalidManifest, RelativePath);
		var map = reader.Load(yaml);
		if (map is null)
		{
			if (strict)
				throw new MountsManifestException(reader.Diagnostics);
			return new(root, []);
		}
		reader.Keys(map, "schema", "enabled", "mounts");

		var mounts = new List<Mount>();
		if (map["mounts"] is YamlSequence list)
			foreach (var item in list.Items)
			{
				if (item is not YamlMapping mount)
				{
					reader.Error(item, "mount-mapping");
					continue;
				}
				reader.Keys(mount, "name", "url", "path", "required", "mode", "tags", "depends-on", "inherits", "aliases");
				var name = FxYamlReader.Scalar(mount["name"]);
				var path = FxYamlReader.Scalar(mount["path"])?.Replace('\\', '/').Trim('/');
				if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
				{
					reader.Error(item, "name-and-path");
					continue;
				}
				var modeText = FxYamlReader.Scalar(mount["mode"]);
				var mode = modeText is null ? MountMode.Write : ParseMode(modeText);
				if (mode is null)
					reader.Error(mount["mode"], "mode", name, modeText);
				mounts.Add(new(name, path, FxYamlReader.List(mount["tags"]), FxYamlReader.Scalar(mount["url"]), reader.Bool(mount, "required", false), mode ?? MountMode.Write,
					FxYamlReader.List(mount["depends-on"]), reader.Bool(mount, "inherits", true), FxYamlReader.List(mount["aliases"]), item.Line));
			}
		else if (map["mounts"] is { } notAList && notAList is not YamlScalar { Value: "" })
			reader.Error(notAList, "list", "mounts");

		var manifest = new MountsManifest(root, mounts);
		if (strict)
		{
			var errors = reader.Diagnostics.Concat(manifest.Validate()).ToList();
			if (errors.Any(d => d.Severity == FxSeverity.Error))
				throw new MountsManifestException(errors);
		}
		return manifest;
	}

	IEnumerable<FxDiagnostic> Validate()
	{
		FxDiagnostic Error(int line, string key, params object?[] args) => FxDiagnostic.Error(InvalidManifest, new($"{InvalidManifest}.{key}", args), RelativePath, line);

		static bool BadPath(string path)
			=> System.IO.Path.IsPathRooted(path) || path.TrimEnd('/').Split('/').Any(s => s is "" or "." or "..");

		foreach (var mount in Mounts)
		{
			if (!SpaceConfig.NamePattern().IsMatch(mount.Name))
				yield return Error(mount.Line, "name", mount.Name);
			if (BadPath(mount.Path))
				yield return Error(mount.Line, "path-relative", mount.Name, mount.Path);
			var segments = mount.Path.Split('/');
			if (ReservedSegments.Contains(segments[0], StringComparer.OrdinalIgnoreCase) || segments.Any(FxLayout.IsLocal))
				yield return Error(mount.Line, "path-reserved", mount.Name, mount.Path);
			if (string.IsNullOrWhiteSpace(mount.Url))
				yield return Error(mount.Line, "url", mount.Name);
			foreach (var dependency in mount.DependsOn ?? [])
				// an old name still works (its doctor warns)
				if (!Mounts.Any(m => m.Name == dependency || (m.Aliases ?? []).Contains(dependency)))
					yield return Error(mount.Line, "depends-on", mount.Name, dependency);
			foreach (var alias in mount.Aliases ?? [])
				if (!SpaceConfig.NamePattern().IsMatch(alias))
					yield return Error(mount.Line, "name", alias);
		}
		foreach (var duplicate in Mounts.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			yield return Error(duplicate.Last().Line, "duplicate-name", duplicate.Key);
		// an old name is still a name: it cannot be another mount's
		foreach (var mount in Mounts)
			foreach (var alias in (mount.Aliases ?? []).Where(a => Mounts.Any(o => o != mount && (o.Name == a || (o.Aliases ?? []).Contains(a)))))
				yield return Error(mount.Line, "duplicate-name", alias);
		// two paths that only differ in case are the same folder on Windows and macOS
		foreach (var duplicate in Mounts.GroupBy(m => m.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			yield return Error(duplicate.Last().Line, "duplicate-path", duplicate.Key);
		// a mount inside another: the one that contains it has to declare it (if it mounts others)
		foreach (var mount in Mounts)
			foreach (var other in Mounts.Where(o => o != mount && o.Path.StartsWith(mount.Path + "/", StringComparison.OrdinalIgnoreCase)))
				yield return Error(other.Line, "nested", other.Name, mount.Name);
	}

	/// <summary>Full path of a mount point, with a trailing separator.</summary>
	public string FullPath(Mount mount) => FxLayout.Normalize(System.IO.Path.Combine(Root, mount.Path));

	/// <summary>The mount at <paramref name="mountRoot"/>, if <c>mounts.yaml</c> lists it.</summary>
	public Mount? MountAt(string mountRoot)
		=> Mounts.FirstOrDefault(m => FxLayout.SameFolder(FullPath(m), mountRoot));
}
