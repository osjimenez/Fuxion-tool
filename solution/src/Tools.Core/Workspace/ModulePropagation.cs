using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Dotnet;
using Fuxion.Tools.Core.Yaml;

namespace Fuxion.Tools.Core.Workspace;

/// <summary>
/// What the modules of the workspace (<c>modules:</c> of the manifest) give each repository, in its <c>_fx/.workspace/</c>
/// (plan N, decision 17): for the .NET module, the workspace's <c>_fx/dotnet.yaml</c> and its folder <c>_fx/dotnet/</c>,
/// whole (binaries too), to the repositories with one of the module's tags; then each repository's generated files are
/// brought up to date (<c>fx sync dotnet</c>). <c>_fx/.workspace/</c> is the tool's (design D-11): what the workspace no
/// longer gives is removed, and <c>_fx/.workspace/repository.yaml</c> (generated) says the repository's name, its tags
/// (for a clone outside the workspace) and what was copied.
/// </summary>
public static class ModulePropagation
{
	public const string MissingConfig = "workspace.missing-module-config";
	public const string InvalidConfig = "workspace.invalid-dotnet";
	public const string Folder = ".workspace";
	public const string RepositoryFile = "repository.yaml";

	public static void Run(WorkspaceManifest manifest, IReadOnlyList<WorkspaceRepository> mounted, bool write, bool doctor,
		List<SyncFile> files, FxDiagnostics diagnostics, IFxProgress? progress = null)
	{
		var dotnet = manifest.Module("dotnet");
		var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // in .workspace -> in the workspace
		if (dotnet is not null)
		{
			var yaml = Path.Combine(manifest.Root, "_fx", "dotnet.yaml");
			if (!File.Exists(yaml))
				diagnostics.Add(FxDiagnostic.Error(MissingConfig, "The manifest uses the module 'dotnet', but there is no _fx/dotnet.yaml.", WorkspaceManifest.RelativePath, dotnet.Line));
			else
			{
				try
				{
					DotnetYamlReader.ReadFile(yaml);
				}
				catch (DotnetConfigException ex)
				{
					diagnostics.AddRange(ex.Diagnostics.Select(d => d with { Code = InvalidConfig, File = "_fx/dotnet.yaml" }));
					return;
				}
				sources["dotnet.yaml"] = yaml;
				var folder = Path.Combine(manifest.Root, "_fx", "dotnet");
				if (Directory.Exists(folder))
					foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
						sources["dotnet/" + Path.GetRelativePath(folder, file).Replace('\\', '/')] = file;
			}
		}

		foreach (var repo in mounted)
		{
			var repoRoot = manifest.FullPath(repo);
			var target = Path.Combine(repoRoot, "_fx", Folder);
			var prefix = repo.Path.TrimEnd('/') + "/_fx/" + Folder + "/";
			var uses = dotnet is not null && dotnet.AppliesTo(repo) && sources.Count > 0;
			if (!uses && !Directory.Exists(target))
				continue;

			// The copies, then the list of what is the tool's
			var expected = uses ? sources : new Dictionary<string, string>();
			foreach (var (relative, source) in expected.OrderBy(e => e.Key, StringComparer.Ordinal))
				Copy(File.ReadAllBytes(source), Path.Combine(target, relative), prefix + relative, write, files);
			if (expected.Count > 0)
				Copy(Encoding.UTF8.GetBytes(RepositoryYaml(repo, expected.Keys).Replace("\n", Environment.NewLine)),
					Path.Combine(target, RepositoryFile), prefix + RepositoryFile, write, files);

			// What the workspace no longer gives
			if (Directory.Exists(target))
				foreach (var path in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).ToList())
				{
					var relative = Path.GetRelativePath(target, path).Replace('\\', '/');
					if (expected.ContainsKey(relative) || (expected.Count > 0 && relative == RepositoryFile))
						continue;
					if (write)
						File.Delete(path);
					files.Add(new(prefix + relative, write ? SyncFileStatus.Written : SyncFileStatus.WouldWrite));
				}

			// The repo's generated files, from the copy (in a dry run, from what is there)
			if (!DotnetModule.Applies(repoRoot))
				continue;
			var result = doctor ? DotnetModule.Doctor(repoRoot, progress.StepsOnly()) : DotnetModule.Sync(repoRoot, dryRun: !write, progress.StepsOnly());
			files.AddRange(result.Files.Select(f => f with { Path = repo.Path.TrimEnd('/') + "/" + f.Path }));
			diagnostics.AddRange(result.Diagnostics.Select(d => d with { File = d.File is null ? repo.Path : repo.Path.TrimEnd('/') + "/" + d.File, Repository = repo.Name }));
		}
	}

	/// <summary>The tags of the repository kept in its <c>_fx/.workspace/repository.yaml</c>, if there is one.</summary>
	public static IReadOnlyList<string>? RepositoryTags(string repositoryRoot)
	{
		var path = Path.Combine(repositoryRoot, "_fx", Folder, RepositoryFile);
		if (!File.Exists(path))
			return null;
		YamlNode? root;
		try
		{
			root = YamlDocument.Load(File.ReadAllText(path));
		}
		catch (YamlSyntaxException)
		{
			return null;
		}
		return root is YamlMapping map && map["tags"] is YamlSequence list
			? list.Items.OfType<YamlScalar>().Select(t => t.Value).Where(t => t.Length > 0).ToList()
			: [];
	}

	static string RepositoryYaml(WorkspaceRepository repo, IEnumerable<string> copied)
	{
		var b = new StringBuilder();
		b.Append("# GENERATED by fx from the workspace (_fx/workspace.yaml). DO NOT EDIT. Regenerate with: fx sync workspace\n");
		b.Append("# What the workspace gives this repository: everything in _fx/.workspace/ is fx's (design D-11).\n");
		b.Append($"name: {repo.Name}\n");
		b.Append($"tags: [{string.Join(", ", repo.Tags)}]\n");
		b.Append("files:\n");
		foreach (var file in copied.Order(StringComparer.Ordinal))
			b.Append($"  - {file}\n");
		return b.ToString();
	}

	static void Copy(byte[] content, string path, string relative, bool write, List<SyncFile> files)
	{
		if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
		{
			files.Add(new(relative, SyncFileStatus.Unchanged));
			return;
		}
		if (write)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllBytes(path, content);
		}
		files.Add(new(relative, write ? SyncFileStatus.Written : SyncFileStatus.WouldWrite));
	}
}
