using System;
using System.Collections.Generic;
using System.IO;

namespace Fuxion.Tools.Core.Layout;

/// <summary>
/// What fx fixes on disk, in one place (plan P, decision 1): <c>_fx/</c>, the anchor of a root of fx (a repository or
/// a workspace), with a folder per module (<c>_fx/&lt;module&gt;/&lt;module&gt;.yaml</c>), the general configuration
/// (<c>_fx/fx.yaml</c>), what a repository receives from its workspace (<c>_fx/.inherited/</c>) and the local files
/// (<c>~$</c>). Paths relative to a root, with '/'.
/// </summary>
public static class FxLayout
{
	public const string Folder = "_fx";
	public const string ConfigFile = "_fx/fx.yaml";
	public const string Inherited = "_fx/.inherited";
	public const string InheritedFile = "_fx/.inherited/inherited.yaml";
	public const string LocalPrefix = "~$";
	public const string Parking = "_fx/~$parking";
	public const string Worktrees = "_fx/~$worktrees";
	public const string WorktreeFile = "~$worktree.yaml";

	/// <summary>Where every root of fx has the skill of fx for coding agents (plan O, O5.1): Claude Code, and the agents that read <c>.agents/</c>.</summary>
	public static readonly IReadOnlyList<string> SkillFiles = [".claude/skills/fx/SKILL.md", ".agents/skills/fx/SKILL.md"];

	/// <summary>The folder of a module: <c>_fx/dotnet</c>.</summary>
	public static string ModuleFolder(string module) => $"{Folder}/{module}";

	/// <summary>The configuration of a module: <c>_fx/dotnet/dotnet.yaml</c>.</summary>
	public static string ModuleFile(string module) => $"{Folder}/{module}/{module}.yaml";

	/// <summary>What a repository receives of a module: <c>_fx/.inherited/dotnet</c>.</summary>
	public static string InheritedModuleFolder(string module) => $"{Inherited}/{module}";

	/// <summary>A local file or folder of fx (<c>~$…</c>): never versioned.</summary>
	public static bool IsLocal(string name) => name.StartsWith(LocalPrefix, StringComparison.Ordinal);

	/// <summary>
	/// The root of fx that contains <paramref name="directory"/>: the first folder above (itself included) with an
	/// <c>_fx/</c> folder or a git repository (a repository without <c>_fx/</c> can still be one, if a workspace declares it).
	/// </summary>
	public static string? FindRoot(string directory)
	{
		for (var dir = new DirectoryInfo(Path.GetFullPath(directory)); dir is not null; dir = dir.Parent)
			if (FxFiles.DirectoryExists(Path.Combine(dir.FullName, Folder)) || IsGitRoot(dir.FullName))
				return Normalize(dir.FullName);
		return null;
	}

	/// <summary>Whether <paramref name="directory"/> is the top of a git working tree (a <c>.git</c> folder, or file in a worktree).</summary>
	public static bool IsGitRoot(string directory)
		=> FxFiles.DirectoryExists(Path.Combine(directory, ".git")) || FxFiles.Exists(Path.Combine(directory, ".git"));

	/// <summary>A full path with a trailing separator.</summary>
	public static string Normalize(string directory)
		=> Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;

	/// <summary>The full path of <paramref name="relative"/> (with '/') under <paramref name="root"/>.</summary>
	public static string Full(string root, string relative) => Path.GetFullPath(Path.Combine(root, relative));

	/// <summary><paramref name="path"/> relative to <paramref name="root"/>, with '/'.</summary>
	public static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

	/// <summary>Whether two folders are the same one (case-insensitive, with or without the trailing separator).</summary>
	public static bool SameFolder(string a, string b)
		=> string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
}
