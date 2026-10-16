using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Text;
using System.Threading;

namespace Fuxion.Tool.Core.Layout;

/// <summary>
/// How the core reads its files and walks its folders (plan O, O4.3a): from the disk, unless an overlay is in place
/// for the current flow of work (<see cref="Overlay"/>, carried by tasks and parallel loops). Then the files of the
/// overlay, by full path, are read with their content (or are gone, if it removes them), and their folders exist and
/// list them. Without one, each method is the one of <see cref="File"/> or <see cref="Directory"/>.
/// <para>
/// Two uses: what <c>fx ui</c> and the commands that edit put in place of the files they would change, to tell what
/// <c>fx sync</c> would do without writing them; and what a run that does not write (<c>--dry-run</c>, the doctor)
/// would write (<see cref="Stage"/>), so that each level reads what the one above it would leave, as it would read it
/// once written.
/// </para>
/// </summary>
public static class FxFiles
{
	// full path → content, or null for a file the overlay removes
	static readonly AsyncLocal<ConcurrentDictionary<string, byte[]?>?> Current = new();

	static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	static string Key(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

	/// <summary>
	/// Puts <paramref name="files"/> (full path → text) in place of the disk for this flow of work until it is
	/// disposed; over an overlay already in place, the new one wins, and the one below is left as it was.
	/// </summary>
	public static IDisposable Overlay(IEnumerable<KeyValuePair<string, string>> files)
	{
		var previous = Current.Value;
		var merged = new ConcurrentDictionary<string, byte[]?>(previous ?? [], Paths);
		foreach (var (path, text) in files)
			merged[Key(path)] = Encoding.UTF8.GetBytes(text);
		Current.Value = merged;
		return new Restore(previous);
	}

	sealed class Restore(ConcurrentDictionary<string, byte[]?>? previous) : IDisposable
	{
		public void Dispose() => Current.Value = previous;
	}

	/// <summary>Whether an overlay is in place for this flow of work.</summary>
	public static bool Overlaid => Current.Value is not null;

	/// <summary>
	/// What a run that does not write would leave at <paramref name="path"/> (null: it would remove it), in the overlay
	/// in place: what is read from there next. Without an overlay, nothing.
	/// </summary>
	public static void Stage(string path, byte[]? content)
	{
		if (Current.Value is { } overlay)
			overlay[Key(path)] = content;
	}

	/// <summary>Whether the overlay has <paramref name="path"/>, and what: its content, or null if it removes it.</summary>
	static bool Pending(string path, out byte[]? content)
	{
		content = null;
		return Current.Value is { } overlay && overlay.TryGetValue(Key(path), out content);
	}

	/// <summary>The files of the overlay inside <paramref name="folder"/> (anywhere below it), and what it has of each.</summary>
	static IEnumerable<KeyValuePair<string, byte[]?>> Below(string folder)
	{
		if (Current.Value is not { } overlay)
			return [];
		var prefix = Key(folder) + Path.DirectorySeparatorChar;
		return overlay.Where(k => k.Key.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
	}

	public static bool Exists(string path) => Pending(path, out var content) ? content is not null : File.Exists(path);

	/// <summary>The text of a file, as <see cref="File.ReadAllText(string)"/> reads it (its byte order mark, out).</summary>
	public static string ReadAllText(string path)
	{
		if (!Pending(path, out var content))
			return File.ReadAllText(path);
		if (content is null)
			throw new FileNotFoundException(null, path);
		using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		return reader.ReadToEnd();
	}

	public static byte[] ReadAllBytes(string path)
		=> !Pending(path, out var content) ? File.ReadAllBytes(path) : content ?? throw new FileNotFoundException(null, path);

	public static bool DirectoryExists(string path) => Directory.Exists(path) || Below(path).Any(f => f.Value is not null);

	public static IEnumerable<string> EnumerateFiles(string folder) => EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly);

	public static IEnumerable<string> EnumerateFiles(string folder, string pattern) => EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly);

	/// <summary>The files of the disk the overlay does not remove, and then those of the overlay that are not on it.</summary>
	public static IEnumerable<string> EnumerateFiles(string folder, string pattern, SearchOption option)
	{
		var disk = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, pattern, option).ToList() : [];
		if (Current.Value is null)
			return disk;
		var top = Key(folder);
		var below = Below(folder).ToList();
		var removed = below.Where(f => f.Value is null).Select(f => f.Key).ToHashSet(Paths);
		var extra = below.Where(f => f.Value is not null).Select(f => f.Key)
			.Where(f => option == SearchOption.AllDirectories || Paths.Equals(Path.GetDirectoryName(f), top))
			.Where(f => FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(f), OperatingSystem.IsWindows()))
			.Where(f => !disk.Any(x => Paths.Equals(Key(x), f)))
			.Select(f => Path.Combine(folder, Path.GetRelativePath(top, f)));
		return disk.Where(f => !removed.Contains(Key(f))).Concat(extra).ToList();
	}

	public static IEnumerable<string> EnumerateDirectories(string folder) => EnumerateDirectories(folder, "*", SearchOption.TopDirectoryOnly);

	/// <summary>The folders of the disk, and then those the overlay makes (the folders of its files) that are not on it.</summary>
	public static IEnumerable<string> EnumerateDirectories(string folder, string pattern, SearchOption option)
	{
		var disk = Directory.Exists(folder) ? Directory.EnumerateDirectories(folder, pattern, option).ToList() : [];
		if (Current.Value is null)
			return disk;
		var top = Key(folder);
		var made = new List<string>();
		foreach (var (file, _) in Below(folder).Where(f => f.Value is not null))
			for (var dir = Path.GetDirectoryName(file); dir is not null && !Paths.Equals(dir, top); dir = Path.GetDirectoryName(dir))
				if ((option == SearchOption.AllDirectories || Paths.Equals(Path.GetDirectoryName(dir), top))
				    && FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(dir), OperatingSystem.IsWindows())
				    && !made.Contains(dir, Paths))
					made.Add(dir);
		var extra = made.Where(d => !disk.Any(x => Paths.Equals(Key(x), d))).Select(d => Path.Combine(folder, Path.GetRelativePath(top, d)));
		return disk.Concat(extra).ToList();
	}
}
