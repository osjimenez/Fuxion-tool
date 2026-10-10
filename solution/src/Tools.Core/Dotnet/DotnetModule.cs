using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Git;
using Fuxion.Tools.Core.Workspace;

namespace Fuxion.Tools.Core.Dotnet;

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
	public const string IgnoredVersion = "dotnet.ignored-version";
	public const string UnusedPackage = "dotnet.unused-package";
	public const string NotARepository = "dotnet.not-a-repository";
	public const string MissingImport = "dotnet.missing-import";
	public const string WorkspaceLayer = "dotnet.workspace-layer";


	[GeneratedRegex("""<PackageReference\s[^>]*?(?:Include|Update)\s*=\s*"(?<id>[^"]+)""", RegexOptions.IgnoreCase)]
	private static partial Regex PackageReference();

	[GeneratedRegex("""<PackageVersion\s[^>]*?Include\s*=\s*"(?<id>[^"]+)""", RegexOptions.IgnoreCase)]
	private static partial Regex PackageVersion();

	[GeneratedRegex("""<GlobalPackageReference\s[^>]*?Include\s*=\s*"(?<id>[^"]+)""", RegexOptions.IgnoreCase)]
	private static partial Regex GlobalPackageReference();

	[GeneratedRegex(@"<ManagePackageVersionsCentrally>\s*true\s*</ManagePackageVersionsCentrally>", RegexOptions.IgnoreCase)]
	private static partial Regex CentralManagement();

	[GeneratedRegex("""<PackageReference\s[^>]*?Include\s*=\s*"(?<id>[^"]+)"[^>]*?\sVersion\s*=""", RegexOptions.IgnoreCase)]
	private static partial Regex PackageReferenceWithVersion();

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

	public const string Name = "dotnet";

	public static FxModuleResult Sync(string directory, bool dryRun, IFxProgress? progress = null)
		=> Run(directory, dryRun ? Mode.DryRun : Mode.Write, progress);

	public static FxModuleResult Doctor(string directory, IFxProgress? progress = null) => Run(directory, Mode.Doctor, progress);

	enum Mode { Write, DryRun, Doctor }

	static string ConfigPath(string root) => Path.Combine(root, "_fx", "dotnet.yaml");

	static string WorkspaceConfigPath(string root) => Path.Combine(root, "_fx", ".workspace", "dotnet.yaml");

	static FxModuleResult Run(string directory, Mode mode, IFxProgress? progress)
	{
		var git = GitClient.Discover(directory);
		if (git is null)
			return FxModuleResult.Stopped(Name, directory, null, FxDiagnostic.Error(NotARepository, new(NotARepository, directory)));
		var root = git.Root;
		if (IsWorkspace(root))
			return FxModuleResult.Stopped(Name, root, null, FxDiagnostic.Error(WorkspaceLayer, new(WorkspaceLayer)));
		// The repository: its name in the workspace manifest, or its folder
		var manifest = WorkspaceManifest.FindAbove(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root))!);
		var repository = manifest?.RepositoryAt(root)?.Name ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
		return progress.Step(Name, repository, mode == Mode.Doctor ? "check" : "generate",
			() => Run(root, git, manifest, repository, mode, progress), r => !r.Failed);
	}

	static FxModuleResult Run(string root, GitClient git, WorkspaceManifest? manifest, string repository, Mode mode, IFxProgress? progress)
	{
		var diagnostics = new FxDiagnostics(progress, repository);
		var files = new List<SyncFile>();
		FxModuleResult Result() => new(Name, root, repository, files, [], diagnostics.ToList());
		FxModuleResult Stop(params IEnumerable<FxDiagnostic> found)
		{
			diagnostics.AddRange(found);
			return Result();
		}

		// 1. The configuration: the workspace layer, the repo on top, only what applies to the repo's tags
		DotnetConfig config;
		string sources;
		try
		{
			var hasWorkspace = File.Exists(WorkspaceConfigPath(root));
			var hasRepo = File.Exists(ConfigPath(root));
			if (!hasWorkspace && !hasRepo)
				return Stop(FxDiagnostic.Error(NoConfig, new(NoConfig)));
			config = DotnetConfig.Combine(
				hasWorkspace ? DotnetYamlReader.ReadFile(WorkspaceConfigPath(root)).WithBase(".workspace") : DotnetConfig.Empty,
				hasRepo ? DotnetYamlReader.ReadFile(ConfigPath(root)) : DotnetConfig.Empty);
			sources = hasWorkspace && hasRepo ? "_fx/.workspace/dotnet.yaml and _fx/dotnet.yaml" : hasRepo ? "_fx/dotnet.yaml" : "_fx/.workspace/dotnet.yaml";
		}
		catch (DotnetConfigException ex)
		{
			return Stop(ex.Diagnostics.Select(d => d with { File = Relative(root, d.File) }));
		}
		// The repo's tags: from the workspace manifest; outside the workspace, from _fx/.workspace/repository.yaml
		var tags = manifest?.RepositoryAt(root)?.Tags
		           ?? ModulePropagation.RepositoryTags(root) ?? [];
		config = config.ForTags(tags);

		// 2. The when variables and the conditions of packages and imports, expanded
		var whenRaw = config.Variables.Where(v => v.IsWhen).ToDictionary(v => v.Name, v => v.When!, StringComparer.OrdinalIgnoreCase);
		Dictionary<string, string> whenExpanded;
		List<(string Id, string Version, string? Condition)> governed;
		List<(string Path, string? Condition)> propsImports, targetsImports;
		try
		{
			whenExpanded = whenRaw.ToDictionary(kv => kv.Key, kv => MSBuildConditions.Expand(kv.Value, whenRaw), StringComparer.OrdinalIgnoreCase);
			governed = config.Packages
				.SelectMany(p => p.Ids.SelectMany(id => p.Versions.Select(v => (Id: id, v.Version, Condition: v.When is null ? null : MSBuildConditions.Expand(v.When, whenRaw)))))
				.ToList();
			string? Condition(DotnetImport import) => import.When is null ? null : MSBuildConditions.Expand(import.When, whenRaw);
			propsImports = config.Imports.SelectMany(i => i.Props.Select(p => (Path: i.FromFx(p), Condition: Condition(i)))).ToList();
			targetsImports = config.Imports.SelectMany(i => i.Targets.Select(t => (Path: i.FromFx(t), Condition: Condition(i)))).ToList();
		}
		catch (InvalidOperationException ex)
		{
			return Stop(FxDiagnostic.Error(DotnetYamlReader.InvalidYaml, FxText.Plain(ex.Message), "_fx/dotnet.yaml"));
		}

		// 3. Only the packages the repo uses (design §7.3), in its projects and in what dotnet.yaml imports
		var buildFiles = git.ListFiles(":(glob)**/*.csproj", ":(glob)**/*.props", ":(glob)**/*.targets")
			.Where(f => !(f.StartsWith("_fx/", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(f).Contains(".g.", StringComparison.OrdinalIgnoreCase)))
			.ToList();
		var used = buildFiles
			.SelectMany(f => PackageReference().Matches(Read(root, f)).Select(m => m.Groups["id"].Value))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var packages = governed.Where(p => used.Contains(p.Id)).OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();

		// 4. The generated files. With central package management (CPM), the versions are PackageVersion items in
		// packages.g.props; without it, PackageReference Update items in dotnet.g.targets, right after the project file,
		// which set the version of the references the project has (plan N, decision 17)
		var centralFiles = git.ListFiles(":(glob)**/Directory.Packages.props");
		var centralManagement = centralFiles.Any(f => CentralManagement().IsMatch(Read(root, f)));
		var write = mode == Mode.Write;
		Generate(root, "_fx/dotnet.g.props", DotnetGenerator.Props(config, propsImports, sources), write, files, diagnostics);
		Generate(root, "_fx/dotnet.g.targets",
			DotnetGenerator.Targets(config, whenExpanded, targetsImports, centralManagement ? [] : packages, sources), write, files, diagnostics);
		if (centralManagement)
			Generate(root, "_fx/packages.g.props", DotnetGenerator.Packages(packages, sources), write, files, diagnostics);

		// 5. The keys fx owns in files that are not fx's (design §4.2): msbuild-sdks and the Import of packages.g.props
		if (config.Sdks.Count > 0)
			GlobalJson(root, config.Sdks, write, files, diagnostics);
		if (centralManagement)
			foreach (var central in centralFiles)
				ImportPackages(root, central, write, files);

		if (mode == Mode.Doctor)
		{
			foreach (var file in files.Where(f => f.Status == SyncFileStatus.WouldWrite))
				diagnostics.Add(FxDiagnostic.Error(Outdated, new(Outdated), file.Path, fix: FxFix.Run("fx sync dotnet")));
			foreach (var path in propsImports.Concat(targetsImports).Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase))
				if (!File.Exists(Path.Combine(root, "_fx", path)))
					diagnostics.Add(FxDiagnostic.Error(MissingImport, new(MissingImport, path), "_fx/dotnet.g.props", fix: FxFix.Run("fx sync workspace")));
			if (centralManagement)
			{
				CheckOwners(root, centralFiles, packages, diagnostics);
				CheckUnusedPackages(root, git, centralFiles, buildFiles, diagnostics);
			}
			else
				CheckVersionsInProjects(root, buildFiles, packages, diagnostics);
			CheckCoverage(root, git, config, governed, diagnostics);
		}
		return Result();
	}

	static void Generate(string root, string relative, string content, bool write, List<SyncFile> files, FxDiagnostics diagnostics)
	{
		var path = Path.Combine(root, relative);
		if (File.Exists(path))
		{
			var current = File.ReadAllText(path);
			if (!current.Contains(DotnetGenerator.HeaderMark, StringComparison.Ordinal))
			{
				files.Add(new(relative, SyncFileStatus.Refused));
				diagnostics.Add(FxDiagnostic.Error(NotGenerated, new("fx.not-generated"), relative, fix: FxFix.Do($"{NotGenerated}.fix")));
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

	static void GlobalJson(string root, IReadOnlyDictionary<string, string> sdks, bool write, List<SyncFile> files, FxDiagnostics diagnostics)
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
			diagnostics.Add(FxDiagnostic.Error(DotnetYamlReader.InvalidYaml, new("dotnet.invalid-json", ex.Message), relative));
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
	static void CheckOwners(string root, IReadOnlyList<string> centralFiles, List<(string Id, string Version, string? Condition)> packages, FxDiagnostics diagnostics)
	{
		var governedIds = packages.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var central in centralFiles)
			foreach (var id in PackageVersion().Matches(Read(root, central)).Select(m => m.Groups["id"].Value).Distinct(StringComparer.OrdinalIgnoreCase))
				if (governedIds.Contains(id))
					diagnostics.Add(FxDiagnostic.Error(DuplicateOwner, new(DuplicateOwner, id), central, fix: FxFix.Do($"{DuplicateOwner}.fix")));
	}

	// A version in the repo's Directory.Packages.props that no project uses, directly (a PackageReference in its build
	// files, or a GlobalPackageReference) or transitively (in the packages a project resolved: obj/project.assets.json,
	// of its last restore; transitive ones are often pinned on purpose). Only a warning: removing it is the repo's call.
	// Without any restore (a fresh clone), what is transitive cannot be told, and nothing is reported.
	static void CheckUnusedPackages(string root, GitClient git, IReadOnlyList<string> centralFiles, IReadOnlyList<string> buildFiles, FxDiagnostics diagnostics)
	{
		var used = buildFiles
			.SelectMany(f => { var text = Read(root, f); return PackageReference().Matches(text).Concat(GlobalPackageReference().Matches(text)); })
			.Select(m => m.Groups["id"].Value)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var restored = 0;
		foreach (var project in git.ListFiles(":(glob)**/*.csproj"))
		{
			var assets = Path.Combine(root, Path.GetDirectoryName(project) ?? "", "obj", "project.assets.json");
			if (!File.Exists(assets))
				continue;
			try
			{
				using var json = JsonDocument.Parse(File.ReadAllText(assets));
				if (json.RootElement.TryGetProperty("libraries", out var libraries))
					foreach (var library in libraries.EnumerateObject())
						if (library.Value.TryGetProperty("type", out var type) && type.GetString() == "package")
							used.Add(library.Name.Split('/')[0]);
				restored++;
			}
			catch (JsonException)
			{
				// a broken assets file tells nothing; the next restore rewrites it
			}
		}
		if (restored == 0)
			return;
		foreach (var central in centralFiles)
		{
			var text = Read(root, central);
			foreach (Match m in PackageVersion().Matches(text))
				if (!used.Contains(m.Groups["id"].Value))
					diagnostics.Add(FxDiagnostic.Warning(UnusedPackage, new(UnusedPackage, m.Groups["id"].Value),
						central, text.AsSpan(0, m.Index).Count('\n') + 1, fix: FxFix.Do($"{UnusedPackage}.fix")));
		}
	}

	// Without CPM, the version of a governed package is fx's: a Version= in a project would be overridden
	static void CheckVersionsInProjects(string root, IReadOnlyList<string> buildFiles, List<(string Id, string Version, string? Condition)> packages, FxDiagnostics diagnostics)
	{
		var governedIds = packages.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var file in buildFiles.Where(f => !f.StartsWith("_fx/", StringComparison.OrdinalIgnoreCase)))
			foreach (var id in PackageReferenceWithVersion().Matches(Read(root, file)).Select(m => m.Groups["id"].Value).Distinct(StringComparer.OrdinalIgnoreCase))
				if (governedIds.Contains(id))
					diagnostics.Add(FxDiagnostic.Warning(IgnoredVersion, new(IgnoredVersion, id), file, fix: FxFix.Do($"{IgnoredVersion}.fix")));
	}

	// D-32: for every real target framework of a project that references a governed package, exactly one version matches
	static void CheckCoverage(string root, GitClient git, DotnetConfig config, List<(string Id, string Version, string? Condition)> governed, FxDiagnostics diagnostics)
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
				diagnostics.Add(FxDiagnostic.Warning(Unverifiable, new($"{Unverifiable}.frameworks"), project));
				continue;
			}
			foreach (var id in ids)
				foreach (var tfm in frameworks)
				{
					var matches = new List<string>();
					var unknown = false;
					foreach (var (_, version, condition) in byId[id])
						switch (condition is null ? true : MSBuildConditions.Evaluate(condition, tfm))
						{
							case true: matches.Add(version); break;
							case null: unknown = true; break;
						}
					if (unknown)
						diagnostics.Add(FxDiagnostic.Warning(Unverifiable, new($"{Unverifiable}.condition", id, tfm), project));
					else if (matches.Count == 0)
						diagnostics.Add(FxDiagnostic.Error(CoverageGap, new(CoverageGap, id, tfm), project));
					else if (matches.Count > 1)
						diagnostics.Add(FxDiagnostic.Error(CoverageOverlap, new(CoverageOverlap, id, tfm, string.Join(", ", matches)), project));
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


	static string Normalize(string text) => text.Replace("\r\n", "\n");

	static string? Relative(string root, string? path)
		=> path is null ? null : Path.IsPathRooted(path) ? Path.GetRelativePath(root, path).Replace('\\', '/') : path;
}
