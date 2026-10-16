using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fuxion.Tools.Core.Git;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Sync;

namespace Fuxion.Tools.Core.Dotnet;

/// <summary>
/// The .NET module of fx in one root (design §7.2, §7.3; plan P): from what it inherits
/// (<c>_fx/.inherited/dotnet/dotnet.yaml</c>, if it is for this root) and its own <c>_fx/dotnet/dotnet.yaml</c> on
/// top, it generates <c>_fx/dotnet/dotnet.g.props</c>, <c>dotnet.g.targets</c> and <c>packages.g.props</c>, the
/// <c>msbuild-sdks</c> of <c>global.json</c> and the solutions the root declares. <c>fx doctor</c> checks the same with
/// the same code, plus the single owner of each package (D-16) and the coverage of the conditions (D-32). A workspace
/// generates its <c>.g.*</c> only if it has projects of its own (plan P, P1, detail 3).
/// </summary>
public static partial class DotnetModule
{
	public const string Name = "dotnet";
	public const string Outdated = "dotnet.outdated";
	public const string DuplicateOwner = "dotnet.duplicate-owner";
	public const string CoverageGap = "dotnet.coverage-gap";
	public const string CoverageOverlap = "dotnet.coverage-overlap";
	public const string Unverifiable = "dotnet.unverifiable";
	public const string IgnoredVersion = "dotnet.ignored-version";
	public const string UnusedPackage = "dotnet.unused-package";
	public const string MissingImport = "dotnet.missing-import";
	public const string UnlistedFile = "dotnet.unlisted-file";

	/// <summary>Where the module writes, relative to a root.</summary>
	public const string Folder = "_fx/dotnet";

	/// <summary>The projects of the repository by name (plan R, decision 1), versioned so a clone on its own has it.</summary>
	public const string ProjectsFile = "_fx/dotnet/projects.g.props";

	public static readonly string ConfigFile = FxLayout.ModuleFile(Name);
	public static readonly string InheritedConfigFile = $"{FxLayout.InheritedModuleFolder(Name)}/{Name}.yaml";

	[GeneratedRegex("""<PackageReference\s[^>]*?(?:Include|Update)\s*=\s*"(?<id>[^"]+)""", RegexOptions.IgnoreCase)]
	private static partial Regex PackageReference();

	[GeneratedRegex("""<FxReference\s[^>]*?Include\s*=\s*"(?<id>[^"]+)""", RegexOptions.IgnoreCase)]
	private static partial Regex FxReference();

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

	/// <summary>The import fx adds to a <c>Directory.Packages.props</c> (and its comment), wherever it pointed.</summary>
	[GeneratedRegex("""(?:[ \t]*<!--[^\n]*-->\r?\n)?[ \t]*<Import Project="\$\(MSBuildThisFileDirectory\)[^"]*packages\.g\.props"[^>]*/>""")]
	private static partial Regex PackagesImport();

	/// <summary>
	/// The configuration of <paramref name="node"/> (who is <paramref name="identity"/>): what it inherits, if it is for it,
	/// with its own on top; null if the module has nothing here.
	/// </summary>
	public static (DotnetConfig Config, string Sources, DotnetConfig Own)? Configuration(FxNode node, FxIdentity identity, ICollection<FxDiagnostic> warnings)
	{
		var inheritedFile = node.Full(InheritedConfigFile);
		var ownFile = node.Full(ConfigFile);
		var inherited = FxFiles.Exists(inheritedFile) ? DotnetYamlReader.ReadFile(inheritedFile, warnings) : null;
		var own = FxFiles.Exists(ownFile) ? DotnetYamlReader.ReadFile(ownFile, warnings).Absolute(identity.FullName) : HasOwnFiles(node) ? DotnetConfig.Empty : null;
		if (own is { Enabled: false })
			own = null;
		// a workspace receives what comes from above to pass it on; it only uses it if it is for it
		if (inherited is not null && !(inherited.AppliesTo ?? FxAppliesTo.All).Matches(identity))
			inherited = null;
		if (inherited is null && own is null)
			return null;
		var sources = string.Join(", ", new[] { inherited is null ? null : InheritedConfigFile, own is null || !FxFiles.Exists(ownFile) ? null : ConfigFile }.OfType<string>());
		var config = DotnetConfig.Combine(inherited?.WithBase($"../.inherited/{Name}") ?? DotnetConfig.Empty, own ?? DotnetConfig.Empty).ForTarget(identity);
		return (config, sources.Length == 0 ? Folder : sources, own ?? DotnetConfig.Empty);
	}

	/// <summary>
	/// Whether the root has a .NET module of its own without a <c>dotnet.yaml</c>: files in <c>_fx/dotnet/</c> that
	/// are not what fx generates there nor local. Only generated files means the module came from above.
	/// </summary>
	static bool HasOwnFiles(FxNode node)
	{
		var folder = node.Full(Folder);
		return FxFiles.DirectoryExists(folder) && FxFiles.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
			.Select(Path.GetFileName)
			.Any(f => !FxLayout.IsLocal(f!) && !f!.Contains(".g.", StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>The module in <paramref name="node"/>, written or checked; null if it has nothing here.</summary>
	public static FxModuleResult? Run(FxNode node, FxIdentity identity, string? repository, SyncContext context)
	{
		var diagnostics = new FxDiagnostics(context.Progress, repository);
		var outputs = new FxOutputs(context.DisplayRoot, context.Write, context.Adopt, diagnostics);
		(DotnetConfig Config, string Sources, DotnetConfig Own)? configuration;
		var warnings = new List<FxDiagnostic>();
		try
		{
			configuration = Configuration(node, identity, warnings);
		}
		catch (DotnetConfigException ex)
		{
			diagnostics.AddRange(ex.Diagnostics.Select(d => d with { File = d.File is null ? null : outputs.Display(d.File) }));
			return new(Name, context.DisplayRoot, repository, [], [], diagnostics.ToList()) { Node = node.Root };
		}
		if (configuration is not { } found)
		{
			// the module no longer applies: what fx generated for it goes
			var folder = node.Full(Folder);
			if (!FxFiles.DirectoryExists(folder))
				return null;
			foreach (var file in FxFiles.EnumerateFiles(folder, "*.g.*").Where(f => FxHeader.IsGenerated(FxFiles.ReadAllText(f))))
				outputs.Delete(file);
			outputs.Prune(folder);
			if (context.Doctor)
				foreach (var file in outputs.Files)
					diagnostics.Add(FxDiagnostic.Error(Outdated, new(Outdated), file.Path, fix: FxFix.Run(FxHeader.Sync)));
			return outputs.Files.Count == 0 ? null : new(Name, context.DisplayRoot, repository, outputs.Files, [], diagnostics.ToList()) { Node = node.Root };
		}
		diagnostics.AddRange(warnings.Select(d => d with { File = d.File is null ? null : outputs.Display(d.File) }));
		var (config, sources, own) = found;

		context.Progress.Step(Name, repository, context.Doctor ? "check" : "generate", () =>
		{
			var git = GitClient.Discover(node.Root);
			if (git is not null && FxLayout.SameFolder(git.Root, node.Root))
				Generate(node, git, identity, config, sources, context, outputs, diagnostics);
			SolutionComposer.Run(node, own.Solutions, outputs, diagnostics);
			return true;
		});
		if (context.Doctor)
		{
			foreach (var file in outputs.Files.Where(f => f.Status == SyncFileStatus.WouldWrite))
				diagnostics.Add(FxDiagnostic.Error(Outdated, new(Outdated), file.Path, fix: FxFix.Run(FxHeader.Sync)));
			CheckUnlisted(node, own, outputs, diagnostics);
		}
		return new(Name, context.DisplayRoot, repository, outputs.Files, [], diagnostics.ToList()) { Node = node.Root };
	}

	/// <summary>
	/// A file of the root's own <c>_fx/dotnet/</c> that no import names (as a file to import or one that travels with it,
	/// <c>files:</c>): it reaches no repository (plan R, decision 2).
	/// </summary>
	static void CheckUnlisted(FxNode node, DotnetConfig own, FxOutputs outputs, FxDiagnostics diagnostics)
	{
		var folder = node.Full(Folder);
		if (!FxFiles.DirectoryExists(folder))
			return;
		var named = own.Imports.SelectMany(i => i.AllFiles).ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var file in FxFiles.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
		{
			var relative = FxLayout.Relative(folder, file);
			if (relative == $"{Name}.yaml" || named.Contains(relative) || relative.Split('/').Any(FxLayout.IsLocal)
			    || Path.GetFileName(relative).Contains(".g.", StringComparison.OrdinalIgnoreCase))
				continue;
			diagnostics.Add(FxDiagnostic.Warning(UnlistedFile, new(UnlistedFile, relative), outputs.Display(file), fix: FxFix.Do($"{UnlistedFile}.fix")));
		}
	}

	static void Generate(FxNode node, GitClient git, FxIdentity identity, DotnetConfig config, string sources, SyncContext context, FxOutputs outputs, FxDiagnostics diagnostics)
	{
		var root = node.Root;
		var buildFiles = git.ListFiles(":(glob)**/*.csproj", ":(glob)**/*.fsproj", ":(glob)**/*.vbproj", ":(glob)**/*.props", ":(glob)**/*.targets")
			.Where(f => !(f.StartsWith("_fx/", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(f).Contains(".g.", StringComparison.OrdinalIgnoreCase)))
			.ToList();
		// a workspace without projects of its own generates nothing for itself (only for its repositories, through them),
		// but the solutions it composes build their projects, and MSBuild reads msbuild-sdks from the folder of the
		// solution: its global.json pins the same SDKs
		if (node.IsWorkspace && !buildFiles.Any(f => Path.GetExtension(f) is ".csproj" or ".fsproj" or ".vbproj"))
		{
			if (config.Sdks.Count > 0 && config.Solutions.Count > 0)
				GlobalJson(root, config.Sdks, outputs, diagnostics);
			return;
		}

		// 1. The when variables and the conditions of packages and imports, expanded
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
			propsImports = config.Imports.SelectMany(i => i.Props.Select(p => (Path: i.FromModule(p), Condition: Condition(i)))).ToList();
			targetsImports = config.Imports.SelectMany(i => i.Targets.Select(t => (Path: i.FromModule(t), Condition: Condition(i)))).ToList();
		}
		catch (InvalidOperationException ex)
		{
			diagnostics.Add(FxDiagnostic.Error(DotnetYamlReader.InvalidYaml, FxText.Plain(ex.Message), outputs.Display(node.Full(ConfigFile))));
			return;
		}

		// 2. Only the packages the repo uses (design §7.3): in its projects and in what dotnet.yaml imports, directly or
		// through FxReference (a package outside the workspace)
		var used = buildFiles
			.SelectMany(f => { var text = Read(root, f); return PackageReference().Matches(text).Concat(FxReference().Matches(text)); })
			.SelectMany(m => m.Groups["id"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var import in propsImports.Concat(targetsImports).Select(i => Path.Combine(root, Folder, i.Path)).Where(File.Exists))
			foreach (Match m in PackageReference().Matches(FxFiles.ReadAllText(import)))
				used.Add(m.Groups["id"].Value);
		var packages = governed.Where(p => used.Contains(p.Id)).OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();

		// 3. The generated files. With central package management (CPM), the versions are PackageVersion items in
		// packages.g.props; without it, PackageReference Update items in dotnet.g.targets, right after the project file
		var centralFiles = git.ListFiles(":(glob)**/Directory.Packages.props");
		var centralManagement = centralFiles.Any(f => CentralManagement().IsMatch(Read(root, f)));
		// a repository of a workspace that does not build on its own needs it (plan R, proposal 5)
		var requiresWorkspace = !config.Standalone && !node.IsWorkspace && node.Inherited() is not null;
		outputs.Generated(Path.Combine(root, Folder, "dotnet.g.props"), DotnetGenerator.Props(config, identity, propsImports, sources, requiresWorkspace));
		outputs.Generated(Path.Combine(root, Folder, "dotnet.g.targets"),
			DotnetGenerator.Targets(config, whenExpanded, targetsImports, centralManagement ? [] : packages, sources));
		if (centralManagement)
			outputs.Generated(Path.Combine(root, Folder, "packages.g.props"), DotnetGenerator.Packages(packages, sources));
		else
			outputs.Delete(Path.Combine(root, Folder, "packages.g.props"));
		// its projects by name, for an FxReference inside the repository (plan R, decision 1)
		var projects = Workspace.WorkspacePropsGenerator.Projects([(node, identity)], diagnostics)
			.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
			.Select(p => (p.Key, Path.GetRelativePath(node.Full(Folder), p.Value.Path).Replace('\\', '/')))
			.ToList();
		if (projects.Count > 0)
			outputs.Generated(node.Full(ProjectsFile), DotnetGenerator.Projects(projects, "the projects of this repository"));
		else
			outputs.Delete(node.Full(ProjectsFile));

		// 4. The keys fx owns in files that are not fx's: msbuild-sdks and the Import of packages.g.props
		if (config.Sdks.Count > 0)
			GlobalJson(root, config.Sdks, outputs, diagnostics);
		if (centralManagement)
			foreach (var central in centralFiles)
				ImportPackages(root, central, outputs);

		if (context.Doctor)
		{
			foreach (var path in propsImports.Concat(targetsImports).Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase))
				if (!FxFiles.Exists(Path.Combine(root, Folder, path)))
					diagnostics.Add(FxDiagnostic.Error(MissingImport, new(MissingImport, path), outputs.Display(Path.Combine(root, Folder, "dotnet.g.props")), fix: FxFix.Run(FxHeader.Sync)));
			if (centralManagement)
			{
				CheckOwners(root, centralFiles, packages, outputs, diagnostics);
				CheckUnusedPackages(root, git, centralFiles, buildFiles, outputs, diagnostics);
			}
			else
				CheckVersionsInProjects(root, buildFiles, packages, outputs, diagnostics);
			CheckCoverage(root, git, config, governed, outputs, diagnostics);
		}
	}

	static void GlobalJson(string root, IReadOnlyDictionary<string, string> sdks, FxOutputs outputs, FxDiagnostics diagnostics)
	{
		var path = Path.Combine(root, "global.json");
		JsonObject json;
		try
		{
			json = FxFiles.Exists(path)
				? JsonNode.Parse(FxFiles.ReadAllText(path), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? new JsonObject()
				: new JsonObject();
		}
		catch (JsonException ex)
		{
			diagnostics.Add(FxDiagnostic.Error(DotnetYamlReader.InvalidYaml, new("dotnet.invalid-json", ex.Message), outputs.Display(path)));
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
			outputs.Files.Add(new(outputs.Display(path), SyncFileStatus.Unchanged));
			return;
		}
		json["msbuild-sdks"] = msbuildSdks;
		using var buffer = new MemoryStream();
		using (var writer = new Utf8JsonWriter(buffer, new() { Indented = true, IndentSize = 2 }))
			json.WriteTo(writer);
		outputs.Owned(path, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n") + "\n"));
	}

	/// <summary>The Import of <c>_fx/dotnet/packages.g.props</c> in a <c>Directory.Packages.props</c>: added, or moved where it points.</summary>
	static void ImportPackages(string root, string relative, FxOutputs outputs)
	{
		var path = Path.Combine(root, relative);
		var text = FxFiles.ReadAllText(path);
		var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
		// with '/': MSBuild takes it on Windows and on Linux
		var toRoot = Path.GetRelativePath(Path.GetDirectoryName(path)!, root).Replace('\\', '/');
		var target = toRoot == "." ? $"{Folder}/packages.g.props" : $"{toRoot}/{Folder}/packages.g.props";
		var import = $"\t<!-- The package versions fx governs (fx sync) -->{newline}" +
		             $"\t<Import Project=\"$(MSBuildThisFileDirectory){target}\" Condition=\"Exists('$(MSBuildThisFileDirectory){target}')\" />";
		string updated;
		if (PackagesImport().Match(text) is { Success: true } existing)
			updated = text[..existing.Index] + import + text[(existing.Index + existing.Length)..];
		else if (ProjectElement().Match(text) is { Success: true } project)
			updated = text.Insert(project.Index + project.Length, newline + newline + import);
		else
			return;
		if (updated == text)
		{
			outputs.Files.Add(new(outputs.Display(path), SyncFileStatus.Unchanged));
			return;
		}
		if (outputs.Write)
			File.WriteAllText(path, updated);
		outputs.Files.Add(new(outputs.Display(path), outputs.Write ? SyncFileStatus.Written : SyncFileStatus.WouldWrite));
	}

	// D-16: each id has a single owner, the yaml or the repo's Directory.Packages.props
	static void CheckOwners(string root, IReadOnlyList<string> centralFiles, List<(string Id, string Version, string? Condition)> packages, FxOutputs outputs, FxDiagnostics diagnostics)
	{
		var governedIds = packages.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var central in centralFiles)
			foreach (var id in PackageVersion().Matches(Read(root, central)).Select(m => m.Groups["id"].Value).Distinct(StringComparer.OrdinalIgnoreCase))
				if (governedIds.Contains(id))
					diagnostics.Add(FxDiagnostic.Error(DuplicateOwner, new(DuplicateOwner, id), outputs.Display(Path.Combine(root, central)), fix: FxFix.Do($"{DuplicateOwner}.fix")));
	}

	// A version in the repo's Directory.Packages.props that no project uses, directly (a PackageReference or an
	// FxReference in its build files, or a GlobalPackageReference) or transitively (in the packages a project resolved:
	// obj/project.assets.json, of its last restore; transitive ones are often pinned on purpose). Only a warning: removing
	// it is the repo's call. Without any restore (a fresh clone), what is transitive cannot be told, and nothing is reported.
	static void CheckUnusedPackages(string root, GitClient git, IReadOnlyList<string> centralFiles, IReadOnlyList<string> buildFiles, FxOutputs outputs, FxDiagnostics diagnostics)
	{
		var used = buildFiles
			.SelectMany(f => { var text = Read(root, f); return PackageReference().Matches(text).Concat(GlobalPackageReference().Matches(text)).Concat(FxReference().Matches(text)); })
			.SelectMany(m => m.Groups["id"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var restored = 0;
		foreach (var project in git.ListFiles(":(glob)**/*.csproj"))
		{
			var assets = Path.Combine(root, Path.GetDirectoryName(project) ?? "", "obj", "project.assets.json");
			if (!FxFiles.Exists(assets))
				continue;
			try
			{
				using var json = JsonDocument.Parse(FxFiles.ReadAllText(assets));
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
						outputs.Display(Path.Combine(root, central)), text.AsSpan(0, m.Index).Count('\n') + 1, fix: FxFix.Do($"{UnusedPackage}.fix")));
		}
	}

	// Without CPM, the version of a governed package is fx's: a Version= in a project would be overridden
	static void CheckVersionsInProjects(string root, IReadOnlyList<string> buildFiles, List<(string Id, string Version, string? Condition)> packages, FxOutputs outputs, FxDiagnostics diagnostics)
	{
		var governedIds = packages.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var file in buildFiles.Where(f => !f.StartsWith("_fx/", StringComparison.OrdinalIgnoreCase)))
			foreach (var id in PackageReferenceWithVersion().Matches(Read(root, file)).Select(m => m.Groups["id"].Value).Distinct(StringComparer.OrdinalIgnoreCase))
				if (governedIds.Contains(id))
					diagnostics.Add(FxDiagnostic.Warning(IgnoredVersion, new(IgnoredVersion, id), outputs.Display(Path.Combine(root, file)), fix: FxFix.Do($"{IgnoredVersion}.fix")));
	}

	// D-32: for every real target framework of a project that references a governed package, exactly one version matches
	static void CheckCoverage(string root, GitClient git, DotnetConfig config, List<(string Id, string Version, string? Condition)> governed, FxOutputs outputs, FxDiagnostics diagnostics)
	{
		var values = config.Variables.Where(v => !v.IsWhen).ToDictionary(v => v.Name, v => v.Value!, StringComparer.OrdinalIgnoreCase);
		var byId = governed.GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
		foreach (var project in git.ListFiles(":(glob)**/*.csproj"))
		{
			var text = Read(root, project);
			var display = outputs.Display(Path.Combine(root, project));
			var ids = PackageReference().Matches(text).Select(m => m.Groups["id"].Value).Where(byId.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			if (ids.Count == 0)
				continue;
			var tfmMatch = TargetFrameworks().Match(text);
			var frameworks = tfmMatch.Success ? MSBuildConditions.Frameworks(tfmMatch.Groups["value"].Value, values) : null;
			if (frameworks is null)
			{
				diagnostics.Add(FxDiagnostic.Warning(Unverifiable, new($"{Unverifiable}.frameworks"), display));
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
						diagnostics.Add(FxDiagnostic.Warning(Unverifiable, new($"{Unverifiable}.condition", id, tfm), display));
					else if (matches.Count == 0)
						diagnostics.Add(FxDiagnostic.Error(CoverageGap, new(CoverageGap, id, tfm), display));
					else if (matches.Count > 1)
						diagnostics.Add(FxDiagnostic.Error(CoverageOverlap, new(CoverageOverlap, id, tfm, string.Join(", ", matches)), display));
				}
		}
	}

	static string Read(string root, string relative) => FxFiles.ReadAllText(Path.Combine(root, relative));
}
