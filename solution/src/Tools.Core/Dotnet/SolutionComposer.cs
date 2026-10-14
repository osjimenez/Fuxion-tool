using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Results;

namespace Fuxion.Tools.Core.Dotnet;

/// <summary>
/// The solutions a root generates (plan P, decision 13), composed by hand in its <c>dotnet.yaml</c> with the vocabulary
/// of the <c>.slnx</c>: each folder takes files and the content of other solutions (their projects, files and folders,
/// as subfolders). They are versioned with everything declared: a solution of a repository that is not on disk keeps
/// what the generated solution already had of it (Visual Studio shows it as unavailable; plan P, P1, detail 2). A project
/// that arrives twice appears once; two different projects with the same name in the same folder are an error (Visual
/// Studio does not take them).
/// </summary>
public static class SolutionComposer
{
	public const string MissingSolution = "dotnet.missing-solution";
	public const string DuplicateName = "dotnet.duplicate-project-name";

	public static void Run(FxNode node, IReadOnlyList<DotnetSolution> solutions, FxOutputs outputs, FxDiagnostics diagnostics)
	{
		foreach (var solution in solutions)
		{
			var path = node.Full(solution.Path);
			var previous = FxFiles.Exists(path) ? FxFiles.ReadAllText(path) : null;
			outputs.Generated(path, Compose(node.Root, solution, previous, diagnostics, outputs.Display));
		}
	}

	public static string Compose(string root, DotnetSolution solution, string? previous, FxDiagnostics diagnostics, Func<string, string> display)
	{
		var outDir = Path.GetDirectoryName(FxLayout.Full(root, solution.Path))!;
		// the folder of the solution, relative to the root: the paths in it are made as text (Relative)
		var outFolder = Path.GetDirectoryName(solution.Path.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
		var platforms = new List<string>();
		var buildTypes = new List<string>();
		var folders = new SortedDictionary<string, List<XElement>>(StringComparer.Ordinal);
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var previousDocument = Parse(previous);
		var sources = new List<string> { $"{DotnetModule.ConfigFile} ({solution.Path})" };

		List<XElement> Folder(string name)
		{
			if (!folders.TryGetValue(name, out var items))
				folders[name] = items = [];
			return items;
		}

		// "/" is the root of the solution; any other folder, "/a/b/"
		static string Join(string folder, string sub)
		{
			var name = (folder.Trim('/') + "/" + sub.Trim('/')).Trim('/');
			return name.Length == 0 ? "/" : $"/{name}/";
		}

		// an item, by its path relative to the folder of the solution, as it is written
		void Add(XElement item, string folder, string relative)
		{
			if (!seen.Add(Path.GetFullPath(Path.Combine(outDir, relative))))
				return;
			var copy = new XElement(item);
			copy.SetAttributeValue("Path", relative);
			Folder(folder).Add(copy);
		}

		void Configurations(XElement document)
		{
			foreach (var platform in document.Elements("Configurations").Elements("Platform").Select(p => (string?)p.Attribute("Name")).OfType<string>())
				if (!platforms.Contains(platform, StringComparer.OrdinalIgnoreCase))
					platforms.Add(platform);
			foreach (var buildType in document.Elements("Configurations").Elements("BuildType").Select(p => (string?)p.Attribute("Name")).OfType<string>())
				if (!buildTypes.Contains(buildType, StringComparer.OrdinalIgnoreCase))
					buildTypes.Add(buildType);
		}

		foreach (var declared in solution.Folders)
		{
			var folder = Join(declared.Name, "");
			Folder(folder);
			foreach (var file in declared.Files)
				Add(new XElement("File"), folder, Relative(outFolder, file));
			foreach (var sub in declared.Solutions)
			{
				var subFile = FxLayout.Full(root, sub);
				var subFolder = Path.GetDirectoryName(sub.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
				string Item(string itemPath) => Path.IsPathRooted(itemPath)
					? Path.GetRelativePath(outDir, itemPath).Replace('\\', '/')
					: Relative(outFolder, $"{subFolder}/{itemPath}");
				// what it is made of, on disk or not: the solution does not change with what is mounted (decision 13)
				sources.Add(sub);
				if (FxFiles.Exists(subFile) && Parse(FxFiles.ReadAllText(subFile)) is { } document)
				{
					Configurations(document);
					foreach (var item in Items(document))
						if ((string?)item.Attribute("Path") is { } itemPath)
							Add(item, folder, Item(itemPath));
					foreach (var group in document.Elements("Folder"))
					{
						var name = Join(folder, (string?)group.Attribute("Name") ?? "/");
						Folder(name);
						foreach (var item in Items(group))
							if ((string?)item.Attribute("Path") is { } itemPath)
								Add(item, name, Item(itemPath));
					}
					continue;
				}
				// not on disk (not mounted): what the generated solution already had of it, under this folder: its items, its
				// folders (the empty ones too) and its configurations
				var prefix = Relative(outFolder, subFolder).TrimEnd('/') + "/";
				var kept = previousDocument is null ? [] : PreviousItems(previousDocument, folder, prefix).ToList();
				if (kept.Count == 0)
					diagnostics.Add(FxDiagnostic.Warning(MissingSolution, new(MissingSolution, sub, solution.Path), display(FxLayout.Full(root, DotnetModule.ConfigFile)), declared.Line));
				foreach (var (name, item) in kept)
				{
					Folder(name);
					Add(item, name, Normalize((string)item.Attribute("Path")!));
				}
				if (previousDocument is not null && folder != "/")
				{
					foreach (var name in previousDocument.Elements("Folder").Select(f => (string?)f.Attribute("Name")).OfType<string>()
						         .Where(n => n.StartsWith(folder, StringComparison.Ordinal)))
						Folder(name);
					Configurations(previousDocument);
				}
			}
		}

		// two different projects with the same name in one folder: Visual Studio does not take them
		foreach (var (name, items) in folders)
			foreach (var duplicate in items.Where(i => i.Name.LocalName == "Project")
				         .GroupBy(i => Path.GetFileNameWithoutExtension((string?)i.Attribute("Path") ?? ""), StringComparer.OrdinalIgnoreCase)
				         .Where(g => g.Count() > 1))
				diagnostics.Add(FxDiagnostic.Error(DuplicateName, new(DuplicateName, duplicate.Key, name, solution.Path,
					string.Join(", ", duplicate.Select(i => (string?)i.Attribute("Path")))), display(FxLayout.Full(root, DotnetModule.ConfigFile)), solution.Line));

		// every parent folder is declared too (/a/ and /a/b/ for /a/b/c/)
		foreach (var name in folders.Keys.Where(n => n != "/").ToList())
		{
			var parts = name.Trim('/').Split('/');
			for (var i = 1; i < parts.Length; i++)
				Folder("/" + string.Join('/', parts.Take(i)) + "/");
		}

		var b = new StringBuilder();
		b.Append("<Solution>\n");
		b.Append(FxHeader.Xml(string.Join(", ", sources), indent: "  "));
		if (platforms.Count > 0 || buildTypes.Count > 0)
		{
			b.Append("  <Configurations>\n");
			foreach (var buildType in buildTypes)
				b.Append($"    <BuildType Name=\"{buildType}\" />\n");
			foreach (var platform in platforms)
				b.Append($"    <Platform Name=\"{platform}\" />\n");
			b.Append("  </Configurations>\n");
		}
		if (folders.TryGetValue("/", out var rootItems))
			foreach (var item in Sorted(rootItems))
				b.Append("  ").Append(item.ToString(SaveOptions.DisableFormatting)).Append('\n');
		foreach (var (name, items) in folders.Where(f => f.Key != "/"))
		{
			if (items.Count == 0)
			{
				b.Append($"  <Folder Name=\"{name}\" />\n");
				continue;
			}
			b.Append($"  <Folder Name=\"{name}\">\n");
			foreach (var item in Sorted(items))
				b.Append("    ").Append(item.ToString(SaveOptions.DisableFormatting)).Append('\n');
			b.Append("  </Folder>\n");
		}
		b.Append("</Solution>\n");
		return b.ToString();
	}

	static IEnumerable<XElement> Sorted(IEnumerable<XElement> items)
		=> items.OrderBy(i => i.Name.LocalName == "File" ? 0 : 1).ThenBy(i => (string?)i.Attribute("Path"), StringComparer.OrdinalIgnoreCase);

	static IEnumerable<XElement> Items(XElement parent) => parent.Elements().Where(e => e.Name.LocalName is "Project" or "File");

	/// <summary>The items of a generated solution under <paramref name="folder"/> whose path starts with <paramref name="prefix"/>, with their folder.</summary>
	static IEnumerable<(string Folder, XElement Item)> PreviousItems(XElement document, string folder, string prefix)
	{
		bool Under(string name) => folder == "/" || name.StartsWith(folder, StringComparison.Ordinal);
		bool Matches(XElement item) => ((string?)item.Attribute("Path") ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
		if (folder == "/")
			foreach (var item in Items(document).Where(Matches))
				yield return ("/", item);
		foreach (var f in document.Elements("Folder"))
			if ((string?)f.Attribute("Name") is { } name && Under(name))
				foreach (var item in Items(f).Where(Matches))
					yield return (name, item);
	}

	/// <summary>A path with '/' and its '.' and '..' resolved, as text: the case stays as it is written.</summary>
	static string Normalize(string path)
	{
		var parts = new List<string>();
		foreach (var part in path.Replace('\\', '/').Split('/'))
			if (part is "" or ".")
				continue;
			else if (part == ".." && parts.Count > 0 && parts[^1] != "..")
				parts.RemoveAt(parts.Count - 1);
			else
				parts.Add(part);
		return string.Join('/', parts);
	}

	/// <summary>
	/// <paramref name="path"/> from <paramref name="folder"/>, both relative to the root, as text. Not with the disk: with a
	/// '~' in a path (a worktree, in <c>_fx/~$worktrees/</c>) .NET takes it for an 8.3 short name and expands it, giving
	/// each folder the case it has on disk, and the solution would change with where it is generated.
	/// </summary>
	static string Relative(string folder, string path)
	{
		var from = Normalize(folder).Split('/', StringSplitOptions.RemoveEmptyEntries);
		var to = Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
		var common = 0;
		while (common < from.Length && common < to.Length && string.Equals(from[common], to[common], StringComparison.OrdinalIgnoreCase))
			common++;
		return string.Join('/', Enumerable.Repeat("..", from.Length - common).Concat(to.Skip(common)));
	}

	static XElement? Parse(string? text)
	{
		if (text is null)
			return null;
		try
		{
			return XDocument.Parse(text).Root;
		}
		catch (XmlException)
		{
			return null;
		}
	}
}
