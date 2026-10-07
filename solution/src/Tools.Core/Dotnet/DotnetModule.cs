using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fuxion.Tools.Core.Diagnostics;
using Fuxion.Tools.Core.Git;
using Fuxion.Tools.Core.Workspace;

namespace Fuxion.Tools.Core.Dotnet;

public enum SyncFileStatus
{
	Unchanged,
	Written,
	/// <summary>Would be written (dry run, or doctor).</summary>
	WouldWrite,
	/// <summary>Not written: the file is not fx's (no GENERATED header).</summary>
	Refused
}

public sealed record SyncFile(string Path, SyncFileStatus Status);

public sealed record DotnetResult(string Repository, IReadOnlyList<SyncFile> Files, IReadOnlyList<FxDiagnostic> Diagnostics)
{
	public bool Failed => Diagnostics.Any(d => d.Severity == FxSeverity.Error);
}

/// <summary>
/// The .NET module of fx in one repository (design §7.2, §7.3): <c>fx sync dotnet</c> generates from
/// <c>_fx/dotnet.yaml</c> (combined with <c>_fx/.workspace/dotnet.yaml</c>) and <c>fx doctor dotnet</c> checks the same
/// with the same code, plus the single owner of each package (D-16) and the coverage of the conditions (D-32).
/// </summary>
public static partial class DotnetModule
{
	public const string NoConfig = "dotnet.no-config";
	public const string NotGenerated = "dotnet.not-generated";
	public const string Outdated = "dotnet.outdated";
	public const string DuplicateOwner = "dotnet.duplicate-owner";
	public const string CoverageGap = "dotnet.coverage-gap";
	public const string CoverageOverlap = "dotnet.coverage-overlap";
	public const string Unverifiable = "dotnet.unverifiable";
	public const string NoCentralPackages = "dotnet.no-central-packages";
	public const string NotARepository = "dotnet.not-a-repository";
	public const string MissingImport = "dotnet.missing-import";
	public const string WorkspaceLayer = "dotnet.workspace-layer";

	/// <summary>
	/// The line of the copy of the workspace's dotnet.yaml (<c>_fx/.workspace/dotnet.yaml</c>) that keeps the repo's tags, so a
	/// clone outside the workspace generates the same (plan N, decision 15).
	/// </summary>
	public const string TagsMark = "# fx-tags:";

	[GeneratedRegex("""<PackageReference\s[^>]*?(?:Include|Update)\s*=\s*"(?<id>[^"]+)""", RegexOptions.IgnoreCase)]
	private static partial Regex PackageReference();

	[GeneratedRegex("""<PackageVersion\s[^>]*?Include\s*=\s*"(?<id>[^"]+)""", RegexOptions.IgnoreCase)]
	private static partial Regex PackageVersion();

	[GeneratedRegex("<TargetFrameworks?>(?<value>[^<]*)</TargetFrameworks?>", RegexOptions.IgnoreCase)]
	private static partial Regex TargetFrameworks();

	[GeneratedRegex(@"<Project[^>]*>")]
	private static partial Regex ProjectElement();

	/// <summary>
	/// Whether the module applies to the repository at <paramref name="directory"/>: it has a dotnet.yaml, and it is not
	/// the metarepo (whose dotnet.yaml is the workspace layer that fx sync workspace copies to the repositories).
	/// </summary>
	public static bool Applies(string directory)
		=> GitClient.Discover(directory) is { } git && !IsWorkspace(git.Root) && (File.Exists(ConfigPath(git.Root)) || File.Exists(WorkspaceConfigPath(git.Root)));

	static bool IsWorkspace(string root) => File.Exists(Path.Combine(root, "_fx", "workspace.yaml"));

	public static DotnetResult Sync(string directory, bool dryRun) => Run(directory, dryRun ? Mode.DryRun : Mode.Write);

	public static DotnetResult Doctor(string directory) => Run(directory, Mode.Doctor);

	enum Mode { Write, DryRun, Doctor }

	static string ConfigPath(string root) => Path.Combine(root, "_fx", "dotnet.yaml");

	static string WorkspaceConfigPath(string root) => Path.Combine(root, "_fx", ".workspace", "dotnet.yaml");

	static DotnetResult Run(string directory, Mode mode)
	{
		var diagnostics = new List<FxDiagnostic>();
		var files = new List<SyncFile>();
		var git = GitClient.Discover(directory);
		if (git is null)
			return new(directory, [], [FxDiagnostic.Error(NotARepository, $"'{directory}' is not inside a git repository.")]);
		var root = git.Root;
		if (IsWorkspace(root))
			return new(root, [], [FxDiagnostic.Error(WorkspaceLayer, "This is the metarepo: its _fx/dotnet.yaml is the workspace layer, which fx sync workspace copies to the repositories.")]);

		// 1. The configuration: the workspace layer, the repo on top, only what applies to the repo's tags
		DotnetConfig config;
		string sources;
		try
		{
			var hasWorkspace = File.Exists(WorkspaceConfigPath(root));
			var hasRepo = File.Exists(ConfigPath(root));
			if (!hasWorkspace && !hasRepo)
				return new(root, [], [FxDiagnostic.Error(NoConfig, "There is no _fx/dotnet.yaml (nor _fx/.workspace/dotnet.yaml) in this repository.")]);
			config = DotnetConfig.Combine(
				hasWorkspace ? DotnetYamlReader.ReadFile(WorkspaceConfigPath(root)).WithBase(".workspace") : DotnetConfig.Empty,
				hasRepo ? DotnetYamlReader.ReadFile(ConfigPath(root)) : DotnetConfig.Empty);
			sources = hasWorkspace && hasRepo ? "_fx/.workspace/dotnet.yaml and _fx/dotnet.yaml" : hasRepo ? "_fx/dotnet.yaml" : "_fx/.workspace/dotnet.yaml";
		}
		catch (DotnetConfigException ex)
		{
			return new(root, [], ex.Diagnostics.Select(d => d with { File = Relative(root, d.File) }).ToList());
		}
		// The repo's tags: from the workspace manifest; outside the workspace, from the copy of its dotnet.yaml
		var tags = WorkspaceManifest.FindAbove(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root))!)?.RepositoryAt(root)?.Tags
		           ?? CopyTags(WorkspaceConfigPath(root));
		config = config.ForTags(tags);

		// 2. The when variables and the conditions of packages and imports, expanded
		var whenRaw = config.Variables.Where(v => v.IsWhen).ToDictionary(v => v.Name, v => v.When!, StringComparer.OrdinalIgnoreCase);
		Dictionary<string, string> whenExpanded;
		List<(string Id, string Version, string Condition)> governed;
		List<(string Path, string? Condition)> propsImports, targetsImports;
		try
		{
			whenExpanded = whenRaw.ToDictionary(kv => kv.Key, kv => MSBuildConditions.Expand(kv.Value, whenRaw), StringComparer.OrdinalIgnoreCase);
			governed = config.Packages
				.SelectMany(p => p.Ids.SelectMany(id => p.Versions.Select(v => (Id: id, v.Version, Condition: MSBuildConditions.Expand(v.When, whenRaw)))))
				.ToList();
			string? Condition(DotnetImport import) => import.When is null ? null : MSBuildConditions.Expand(import.When, whenRaw);
			propsImports = config.Imports.SelectMany(i => i.Props.Select(p => (Path: i.FromFx(p), Condition: Condition(i)))).ToList();
			targetsImports = config.Imports.SelectMany(i => i.Targets.Select(t => (Path: i.FromFx(t), Condition: Condition(i)))).ToList();
		}
		catch (InvalidOperationException ex)
		{
			return new(root, [], [FxDiagnostic.Error(DotnetYamlReader.InvalidYaml, ex.Message, "_fx/dotnet.yaml")]);
		}

		// 3. Only the packages the repo uses (design §7.3), in its projects and in what dotnet.yaml imports
		var buildFiles = git.ListFiles(":(glob)**/*.csproj", ":(glob)**/*.props", ":(glob)**/*.targets")
			.Where(f => !(f.StartsWith("_fx/", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(f).Contains(".g.", StringComparison.OrdinalIgnoreCase)))
			.ToList();
		var used = buildFiles
			.SelectMany(f => PackageReference().Matches(Read(root, f)).Select(m => m.Groups["id"].Value))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var packages = governed.Where(p => used.Contains(p.Id)).OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();

		// 4. The generated files
		var write = mode == Mode.Write;
		Generate(root, "_fx/dotnet.g.props", DotnetGenerator.Props(config, propsImports, sources), write, files, diagnostics);
		Generate(root, "_fx/dotnet.g.targets", DotnetGenerator.Targets(config, whenExpanded, targetsImports, sources), write, files, diagnostics);
		Generate(root, "_fx/packages.g.props", DotnetGenerator.Packages(packages, sources), write, files, diagnostics);

		// 5. The keys fx owns in files that are not fx's (design §4.2): msbuild-sdks and the Import of packages.g.props
		if (config.Sdks.Count > 0)
			GlobalJson(root, config.Sdks, write, files, diagnostics);
		var centralFiles = git.ListFiles(":(glob)**/Directory.Packages.props");
		if (centralFiles.Count == 0 && packages.Count > 0)
			diagnostics.Add(FxDiagnostic.Warning(NoCentralPackages, "The repository governs packages but has no Directory.Packages.props: packages.g.props is not imported anywhere."));
		foreach (var central in centralFiles)
			ImportPackages(root, central, write, files);

		if (mode == Mode.Doctor)
		{
			foreach (var file in files.Where(f => f.Status == SyncFileStatus.WouldWrite))
				diagnostics.Add(FxDiagnostic.Error(Outdated, "Out of date with _fx/dotnet.yaml: run fx sync dotnet.", file.Path));
			foreach (var path in propsImports.Concat(targetsImports).Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase))
				if (!File.Exists(Path.Combine(root, "_fx", path)))
					diagnostics.Add(FxDiagnostic.Error(MissingImport, $"dotnet.yaml imports _fx/{path}, which does not exist (in the workspace, fx sync workspace copies it).", "_fx/dotnet.g.props"));
			CheckOwners(root, centralFiles, packages, diagnostics);
			CheckCoverage(root, git, config, governed, diagnostics);
		}
		return new(root, files, diagnostics);
	}

	static void Generate(string root, string relative, string content, bool write, List<SyncFile> files, List<FxDiagnostic> diagnostics)
	{
		var path = Path.Combine(root, relative);
		if (File.Exists(path))
		{
			var current = File.ReadAllText(path);
			if (!current.Contains(DotnetGenerator.HeaderMark, StringComparison.Ordinal))
			{
				files.Add(new(relative, SyncFileStatus.Refused));
				diagnostics.Add(FxDiagnostic.Error(NotGenerated, "Not written: the file has no GENERATED header, so it is not fx's (design D-11).", relative));
				return;
			}
			if (Normalize(current) == content)
			{
				files.Add(new(relative, SyncFileStatus.Unchanged));
				return;
			}
		}
		Save(path, content, write);
		files.Add(new(relative, write ? SyncFileStatus.Written : SyncFileStatus.WouldWrite));
	}

	static void GlobalJson(string root, IReadOnlyDictionary<string, string> sdks, bool write, List<SyncFile> files, List<FxDiagnostic> diagnostics)
	{
		const string relative = "global.json";
		var path = Path.Combine(root, relative);
		JsonObject json;
		try
		{
			json = File.Exists(path)
				? JsonNode.Parse(File.ReadAllText(path), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? new JsonObject()
				: new JsonObject();
		}
		catch (JsonException ex)
		{
			diagnostics.Add(FxDiagnostic.Error(DotnetYamlReader.InvalidYaml, $"global.json is not valid JSON: {ex.Message}", relative));
			return;
		}
		var msbuildSdks = json["msbuild-sdks"] as JsonObject;
		var changed = msbuildSdks is null;
		msbuildSdks ??= new JsonObject();
		foreach (var (name, version) in sdks.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
			if (msbuildSdks[name]?.GetValue<string>() != version)
			{
				msbuildSdks[name] = version;
				changed = true;
			}
		if (!changed)
		{
			files.Add(new(relative, SyncFileStatus.Unchanged));
			return;
		}
		json["msbuild-sdks"] = msbuildSdks;
		using var buffer = new MemoryStream();
		using (var writer = new Utf8JsonWriter(buffer, new() { Indented = true, IndentSize = 2 }))
			json.WriteTo(writer);
		Save(path, Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n") + "\n", write);
		files.Add(new(relative, write ? SyncFileStatus.Written : SyncFileStatus.WouldWrite));
	}

	static void ImportPackages(string root, string relative, bool write, List<SyncFile> files)
	{
		var path = Path.Combine(root, relative);
		var text = File.ReadAllText(path);
		if (text.Contains("packages.g.props", StringComparison.OrdinalIgnoreCase))
		{
			files.Add(new(relative, SyncFileStatus.Unchanged));
			return;
		}
		var project = ProjectElement().Match(text);
		if (!project.Success)
			return;
		// with '/': MSBuild takes it on Windows and on Linux
		var toRoot = Path.GetRelativePath(Path.GetDirectoryName(path)!, root).Replace('\\', '/');
		var target = toRoot == "." ? "_fx/packages.g.props" : $"{toRoot}/_fx/packages.g.props";
		var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
		var import = $"{newline}{newline}\t<!-- The packages governed by _fx/dotnet.yaml (fx sync dotnet, design §7.3) -->{newline}" +
		             $"\t<Import Project=\"$(MSBuildThisFileDirectory){target}\" Condition=\"Exists('$(MSBuildThisFileDirectory){target}')\" />";
		if (write)
			File.WriteAllText(path, text.Insert(project.Index + project.Length, import));
		files.Add(new(relative, write ? SyncFileStatus.Written : SyncFileStatus.WouldWrite));
	}

	// D-16: each id has a single owner, the yaml or the repo's Directory.Packages.props
	static void CheckOwners(string root, IReadOnlyList<string> centralFiles, List<(string Id, string Version, string Condition)> packages, List<FxDiagnostic> diagnostics)
	{
		var governedIds = packages.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var central in centralFiles)
			foreach (var id in PackageVersion().Matches(Read(root, central)).Select(m => m.Groups["id"].Value).Distinct(StringComparer.OrdinalIgnoreCase))
				if (governedIds.Contains(id))
					diagnostics.Add(FxDiagnostic.Error(DuplicateOwner,
						$"'{id}' is governed by dotnet.yaml and also has a PackageVersion here: keep one owner (design D-16; NuGet rejects it, NU1506).", central));
	}

	// D-32: for every real target framework of a project that references a governed package, exactly one version matches
	static void CheckCoverage(string root, GitClient git, DotnetConfig config, List<(string Id, string Version, string Condition)> governed, List<FxDiagnostic> diagnostics)
	{
		var values = config.Variables.Where(v => !v.IsWhen).ToDictionary(v => v.Name, v => v.Value!, StringComparer.OrdinalIgnoreCase);
		var byId = governed.GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
		foreach (var project in git.ListFiles(":(glob)**/*.csproj"))
		{
			var text = Read(root, project);
			var ids = PackageReference().Matches(text).Select(m => m.Groups["id"].Value).Where(byId.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			if (ids.Count == 0)
				continue;
			var tfmMatch = TargetFrameworks().Match(text);
			var frameworks = tfmMatch.Success ? MSBuildConditions.Frameworks(tfmMatch.Groups["value"].Value, values) : null;
			if (frameworks is null)
			{
				diagnostics.Add(FxDiagnostic.Warning(Unverifiable, "Cannot tell the target frameworks of this project, so the coverage of its packages is not checked.", project));
				continue;
			}
			foreach (var id in ids)
				foreach (var tfm in frameworks)
				{
					var matches = new List<string>();
					var unknown = false;
					foreach (var (_, version, condition) in byId[id])
						switch (MSBuildConditions.Evaluate(condition, tfm))
						{
							case true: matches.Add(version); break;
							case null: unknown = true; break;
						}
					if (unknown)
						diagnostics.Add(FxDiagnostic.Warning(Unverifiable, $"'{id}' on {tfm}: a condition uses something the doctor cannot evaluate.", project));
					else if (matches.Count == 0)
						diagnostics.Add(FxDiagnostic.Error(CoverageGap, $"'{id}' on {tfm}: no version of dotnet.yaml applies (design D-32).", project));
					else if (matches.Count > 1)
						diagnostics.Add(FxDiagnostic.Error(CoverageOverlap, $"'{id}' on {tfm}: several versions apply ({string.Join(", ", matches)}) (design D-32).", project));
				}
		}
	}

	static void Save(string path, string content, bool write)
	{
		if (!write)
			return;
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content.Replace("\n", Environment.NewLine), new UTF8Encoding(false));
	}

	static string Read(string root, string relative) => File.ReadAllText(Path.Combine(root, relative));

	/// <summary>The tags kept in the copy of the workspace's dotnet.yaml (<see cref="TagsMark"/>), if any.</summary>
	static IReadOnlyList<string> CopyTags(string path)
		=> File.Exists(path) && File.ReadLines(path).Take(5).FirstOrDefault(l => l.StartsWith(TagsMark, StringComparison.Ordinal)) is { } line
			? line[TagsMark.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			: [];

	static string Normalize(string text) => text.Replace("\r\n", "\n");

	static string? Relative(string root, string? path)
		=> path is null ? null : Path.IsPathRooted(path) ? Path.GetRelativePath(root, path).Replace('\\', '/') : path;
}
