using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fuxion.Tool.Core.Layout;

namespace Fuxion.Tool.Core.Docs;

/// <summary>
/// The files of a document as found: its Markdown and its sidecar, with the same stem in the same folder; one of them
/// may be missing.
/// </summary>
/// <param name="Folder">Its folder, relative to the catalog, with '/' (empty at its root).</param>
/// <param name="Markdown">The full path of its Markdown, if it has one.</param>
/// <param name="Sidecar">The full path of its sidecar, if it has one.</param>
public sealed record DocFile(string Folder, string Stem, string? Markdown, string? Sidecar)
{
	/// <summary>Its path in the catalog, the one of its Markdown (the links of reading point to it).</summary>
	public string Path => Folder.Length == 0 ? $"{Stem}.md" : $"{Folder}/{Stem}.md";

	/// <summary>Its slug: the stem after the type (<c>ADR-slug</c>); a <c>README</c>, its own name.</summary>
	public string Slug => Stem.IndexOf('-') is var dash and > 0 ? Stem[(dash + 1)..] : Stem;
}

/// <summary>
/// The catalog of a space (the SPEC of the module, 13.1): the folder of <c>docs.yaml</c> with every document in it. Its
/// folders that start with '.' or are local (<c>~$</c>) are not part of it, nor the files that are not Markdown or sidecars.
/// </summary>
public sealed class DocCatalog
{
	DocCatalog(string root, DocsConfig config, IReadOnlyList<DocFile> files)
	{
		Root = root;
		Config = config;
		Folder = FxLayout.Full(root, config.Catalog);
		Files = files;
	}

	/// <summary>The root of the space.</summary>
	public string Root { get; }

	public DocsConfig Config { get; }

	/// <summary>The full path of the catalog.</summary>
	public string Folder { get; }

	public IReadOnlyList<DocFile> Files { get; }

	public static DocCatalog Scan(string root, DocsConfig config)
	{
		var folder = FxLayout.Full(root, config.Catalog);
		var files = new List<DocFile>();
		Walk(folder, "");
		return new(root, config, files);

		void Walk(string full, string relative)
		{
			if (!FxFiles.DirectoryExists(full))
				return;
			var markdowns = new Dictionary<string, string>(StringComparer.Ordinal);
			var sidecars = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var file in FxFiles.EnumerateFiles(full))
			{
				var name = System.IO.Path.GetFileName(file);
				if (name.EndsWith(DocSidecar.Extension, StringComparison.Ordinal))
					sidecars[name[..^DocSidecar.Extension.Length]] = file;
				else if (name.EndsWith(".md", StringComparison.Ordinal))
					markdowns[name[..^".md".Length]] = file;
			}
			foreach (var stem in markdowns.Keys.Union(sidecars.Keys).Order(StringComparer.Ordinal))
				files.Add(new(relative, stem, markdowns.GetValueOrDefault(stem), sidecars.GetValueOrDefault(stem)));
			foreach (var sub in FxFiles.EnumerateDirectories(full).Order(StringComparer.Ordinal))
			{
				var name = System.IO.Path.GetFileName(sub);
				if (!name.StartsWith('.') && !FxLayout.IsLocal(name))
					Walk(sub, relative.Length == 0 ? name : $"{relative}/{name}");
			}
		}
	}

	/// <summary>The full path of <paramref name="path"/>, relative to the catalog.</summary>
	public string Full(string path) => FxLayout.Full(Folder, path);

	/// <summary>Whether <paramref name="full"/> is inside the catalog.</summary>
	public bool Contains(string full)
	{
		var relative = System.IO.Path.GetRelativePath(Folder, full);
		return relative != ".." && !relative.StartsWith($"..{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(relative);
	}

	/// <summary>Whether <paramref name="file"/> is in the bootstrap list of <c>docs.yaml</c>.</summary>
	public bool IsBootstrap(DocFile file) => Config.Bootstrap.Contains(file.Path, StringComparer.Ordinal);

	/// <summary>
	/// The documents <paramref name="selector"/> names (the SPEC of the module, 11.2): a path (relative to
	/// <paramref name="currentDirectory"/>, or to the catalog), with or without its extension, or a folder (everything
	/// below it); or an identity, among <paramref name="ids"/>. None, if it names nothing.
	/// </summary>
	public IReadOnlyList<DocFile> Select(string selector, string currentDirectory, IReadOnlyDictionary<string, DocFile>? ids = null)
	{
		if (ids is not null && ids.TryGetValue(selector, out var byId))
			return [byId];
		foreach (var full in new[] { FxLayout.Full(currentDirectory, selector), Full(selector) }.Distinct())
		{
			if (!Contains(full))
				continue;
			var relative = FxLayout.Relative(Folder, full);
			if (relative == ".")
				relative = "";
			if (FxFiles.DirectoryExists(full))
				return Files.Where(f => relative.Length == 0 || f.Folder == relative || f.Folder.StartsWith($"{relative}/", StringComparison.Ordinal)).ToList();
			var stem = relative.EndsWith(DocSidecar.Extension, StringComparison.Ordinal) ? relative[..^DocSidecar.Extension.Length]
				: relative.EndsWith(".md", StringComparison.Ordinal) ? relative[..^".md".Length]
				: relative;
			if (Files.FirstOrDefault(f => (f.Folder.Length == 0 ? f.Stem : $"{f.Folder}/{f.Stem}") == stem) is { } file)
				return [file];
		}
		return [];
	}
}
