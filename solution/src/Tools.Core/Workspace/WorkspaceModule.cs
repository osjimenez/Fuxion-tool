using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Fuxion.Tools.Core.Git;
using Fuxion.Tools.Core.Results;

namespace Fuxion.Tools.Core.Workspace;

public sealed record WorkspaceSyncOptions(bool DryRun = false, bool Offline = false, bool Adopt = false);

/// <summary>A repository of the manifest and its state here.</summary>
public sealed record RepositoryState(WorkspaceRepository Repository, string FullPath, bool Mounted, bool Present, GitStatus? Status);

/// <summary>
/// The workspace module of fx (design §4, §5, §10.2): <c>fx sync workspace</c> clones and fetches the mounted
/// repositories and regenerates the metarepo's <c>.gitignore</c> and <c>Fuxion.slnx</c>; <c>fx doctor workspace</c>
/// checks the same without touching anything, plus the state of each repository; <c>fx repo</c> lists, mounts, unmounts
/// and pulls them. It never commits, never pushes, never pulls implicitly, never deletes (§10.4). What it does on each
/// repository goes to the progress channel as a step (<c>clone</c>, <c>fetch</c>, <c>status</c>, <c>remote</c>,
/// <c>pull</c>).
/// </summary>
public static class WorkspaceModule
{
	public const string Name = "workspace";
	public const string NotAWorkspace = "workspace.not-a-workspace";
	public const string NotGenerated = "workspace.not-generated";
	public const string Outdated = "workspace.outdated";
	public const string Missing = "workspace.missing";
	public const string CloneFailed = "workspace.clone-failed";
	public const string FetchFailed = "workspace.fetch-failed";
	public const string NotARepository = "workspace.not-a-repository";
	public const string OriginMismatch = "workspace.origin-mismatch";
	public const string Orphan = "workspace.orphan";
	public const string NotIgnored = "workspace.not-ignored";
	public const string Dirty = "workspace.dirty";
	public const string Detached = "workspace.detached";
	public const string NoUpstream = "workspace.no-upstream";
	public const string Unpushed = "workspace.unpushed";
	public const string Unreachable = "workspace.unreachable";
	public const string UnknownRepository = "workspace.unknown-repository";
	public const string StandaloneReference = "workspace.standalone-reference";
	public const string PullFailed = "workspace.pull-failed";

	const string SyncCommand = "fx sync workspace";

	static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(10);
	static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(60);
	static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(30);

	/// <summary>Whether <paramref name="directory"/> is in the metarepo itself (not inside a mounted repository).</summary>
	public static bool Applies(string directory)
		=> WorkspaceManifest.FindRoot(directory) is { } root
		   && GitClient.Discover(directory) is { } git
		   && string.Equals(Path.TrimEndingDirectorySeparator(git.Root), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);

	/// <summary>Loads the manifest of the workspace around <paramref name="directory"/>, or a diagnostic.</summary>
	public static (WorkspaceManifest? Manifest, FxDiagnostic? Error) Find(string directory)
	{
		var root = WorkspaceManifest.FindRoot(directory);
		if (root is null)
			return (null, FxDiagnostic.Error(NotAWorkspace, new(NotAWorkspace, directory)));
		try
		{
			return (WorkspaceManifest.Load(root), null);
		}
		catch (WorkspaceManifestException ex)
		{
			return (null, ex.Diagnostics[0]);
		}
	}

	/// <summary>The repositories of the manifest and their state; with <paramref name="withStatus"/>, their git status (in parallel).</summary>
	public static IReadOnlyList<RepositoryState> Repositories(WorkspaceManifest manifest, bool withStatus, IFxProgress? progress = null, CancellationToken cancellationToken = default)
	{
		var state = WorkspaceState.Load(manifest.Root);
		var result = new RepositoryState[manifest.Repositories.Count];
		Parallel.For(0, result.Length, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, i =>
		{
			var r = manifest.Repositories[i];
			var full = manifest.FullPath(r);
			var present = Directory.Exists(Path.Combine(full, ".git")) || File.Exists(Path.Combine(full, ".git"));
			var status = present && withStatus ? progress.Step(Name, r.Name, "status", () => GitClient.Discover(full)?.Status(), s => s is not null) : null;
			result[i] = new RepositoryState(r, full, state.IsMounted(r, present), present, status);
		});
		return result;
	}

	public static FxModuleResult Sync(string directory, WorkspaceSyncOptions options, IFxProgress? progress = null, CancellationToken cancellationToken = default)
		=> Run(directory, options, doctor: false, progress, cancellationToken);

	public static FxModuleResult Doctor(string directory, bool offline, IFxProgress? progress = null, CancellationToken cancellationToken = default)
		=> Run(directory, new(DryRun: true, Offline: offline), doctor: true, progress, cancellationToken);

	static FxModuleResult Run(string directory, WorkspaceSyncOptions options, bool doctor, IFxProgress? progress, CancellationToken cancellationToken)
	{
		var diagnostics = new FxDiagnostics(progress);
		var files = new List<SyncFile>();
		var actions = new List<RepositoryAction>();
		var (manifest, error) = Find(directory);
		if (manifest is null)
		{
			diagnostics.Add(error!);
			return FxModuleResult.Stopped(Name, directory, null, diagnostics.ToList());
		}
		cancellationToken.ThrowIfCancellationRequested();
		var root = manifest.Root;
		var write = !options.DryRun;

		// 1. Which repositories are mounted; a clone already there is adopted (EnergIA's rule)
		var state = WorkspaceState.Load(root);
		var repositories = Repositories(manifest, withStatus: false, cancellationToken: cancellationToken);
		foreach (var repo in repositories.Where(r => r.Present && !state.Mount.ContainsKey(r.Repository.Name)))
			state = state.With(repo.Repository.Name, true);

		// 2. Clone what is mounted and missing; check and fetch what is there (in parallel, always with a timeout)
		var toFetch = new List<(RepositoryState Info, GitClient Git)>();
		foreach (var repo in repositories.Where(r => r.Mounted))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var name = repo.Repository.Name;
			if (!repo.Present)
			{
				if (doctor || options.DryRun || options.Offline)
				{
					diagnostics.Add(FxDiagnostic.Error(Missing, new(Missing, name, repo.Repository.Path), WorkspaceManifest.RelativePath, repo.Repository.Line,
						name, FxFix.Run(SyncCommand)));
					actions.Add(new(name, "missing"));
					continue;
				}
				var clone = progress.Step(Name, name, "clone", () => GitClient.Clone(repo.Repository.Url!, repo.FullPath, CloneTimeout), c => c.ExitCode == 0);
				if (clone.ExitCode != 0)
				{
					diagnostics.Add(FxDiagnostic.Error(CloneFailed, new(CloneFailed, name, repo.Repository.Url, clone.StandardError.Trim()), repository: name));
					actions.Add(new(name, "clone-failed"));
					continue;
				}
				actions.Add(new(name, "cloned"));
				state = state.With(name, true);
				continue;
			}
			var git = GitClient.Discover(repo.FullPath);
			if (git is null || !string.Equals(Path.TrimEndingDirectorySeparator(git.Root), Path.TrimEndingDirectorySeparator(repo.FullPath), StringComparison.OrdinalIgnoreCase))
			{
				diagnostics.Add(FxDiagnostic.Error(NotARepository, new(NotARepository, name, repo.Repository.Path), repository: name));
				continue;
			}
			var origin = git.RemoteUrl("origin");
			if (!SameUrl(origin, repo.Repository.Url))
			{
				diagnostics.Add(FxDiagnostic.Warning(OriginMismatch, new(OriginMismatch, name, origin, repo.Repository.Url), repository: name,
					fix: FxFix.Run($"git -C {repo.Repository.Path} remote set-url origin {repo.Repository.Url}")));
				continue;
			}
			toFetch.Add((repo, git));
		}
		if (!options.Offline && !doctor)
		{
			var results = new RepositoryAction?[toFetch.Count];
			Parallel.For(0, toFetch.Count, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, i =>
			{
				var name = toFetch[i].Info.Repository.Name;
				var fetch = progress.Step(Name, name, "fetch", () => toFetch[i].Git.Fetch(FetchTimeout), f => f.ExitCode == 0);
				results[i] = fetch.ExitCode == 0 ? new(name, "fetched") : new(name, "fetch-failed", FxText.Plain(fetch.StandardError.Trim()));
			});
			foreach (var result in results.OfType<RepositoryAction>())
			{
				actions.Add(result);
				if (result.Action == "fetch-failed")
					diagnostics.Add(FxDiagnostic.Warning(FetchFailed, new(FetchFailed, result.Repository, result.Detail), repository: result.Repository));
			}
		}

		// 3. Clones nobody listed (<name>/repo with a .git)
		foreach (var dir in Directory.EnumerateDirectories(root))
		{
			var candidate = Path.Combine(dir, "repo");
			var relative = Path.GetRelativePath(root, candidate).Replace('\\', '/');
			if (Directory.Exists(Path.Combine(candidate, ".git")) && !manifest.Repositories.Any(r => string.Equals(r.Path, relative, StringComparison.OrdinalIgnoreCase)))
				diagnostics.Add(FxDiagnostic.Warning(Orphan, new(Orphan, relative), fix: FxFix.Do($"{Orphan}.fix")));
		}

		// 4. The generated files of the metarepo
		var mounted = repositories.Where(r => r.Mounted && (r.Present || actions.Any(a => a.Repository == r.Repository.Name && a.Action == "cloned"))).Select(r => r.Repository).ToList();
		Generate(root, ".gitignore", Gitignore(manifest), "#", write, options.Adopt, files, diagnostics);
		Generate(root, SolutionGenerator.FileName, SolutionGenerator.Generate(manifest, mounted, diagnostics), "<!--", write, options.Adopt, files, diagnostics);
		Generate(root, WorkspacePropsGenerator.FileName, WorkspacePropsGenerator.Generate(manifest, mounted, diagnostics), "<!--", write, options.Adopt, files, diagnostics);
		// 5. What the modules of the workspace give each repository (_fx/.workspace/); their generated files
		ModulePropagation.Run(manifest, mounted, write, doctor, files, diagnostics, progress);
		if (write)
			Save(Path.Combine(root, "_fx", "~$workspace.yaml"), state.ToYaml());

		if (doctor)
		{
			Check(manifest, repositories, files, diagnostics, options.Offline, progress, cancellationToken);
			CheckStandalone(manifest, mounted, diagnostics);
		}
		return new(Name, root, null, files, actions, diagnostics.ToList());
	}

	/// <summary>
	/// A standalone repository (it builds without the workspace, design D-2) cannot reference a project of a repository
	/// that is not: outside the workspace that reference would have to be a package, and that repository publishes
	/// none (design §6). <c>WorkspaceOnly="true"</c> references only exist in the workspace by definition.
	/// </summary>
	static void CheckStandalone(WorkspaceManifest manifest, IReadOnlyList<WorkspaceRepository> mounted, FxDiagnostics diagnostics)
	{
		var projects = WorkspacePropsGenerator.Projects(manifest, mounted, []);
		var standalone = manifest.Repositories.ToDictionary(r => r.Name, r => r.Standalone, StringComparer.OrdinalIgnoreCase);
		foreach (var repo in mounted.Where(r => r.Standalone))
		{
			if (GitClient.Discover(manifest.FullPath(repo)) is not { } git)
				continue;
			foreach (var file in git.ListFiles(":(glob)**/*.csproj", ":(glob)**/*.props", ":(glob)**/*.targets"))
				foreach (var (reference, line) in FxReferences(Path.Combine(git.Root, file)))
					if (projects.TryGetValue(reference, out var target) && standalone.TryGetValue(target.Repository, out var isStandalone) && !isStandalone)
						diagnostics.Add(FxDiagnostic.Error(StandaloneReference, new(StandaloneReference, repo.Name, reference, target.Repository),
							$"{repo.Path}/{file}", line, repo.Name, FxFix.Do($"{StandaloneReference}.fix")));
		}
	}

	/// <summary>The <c>FxReference</c> items of a build file that can be packages (not <c>WorkspaceOnly="true"</c>).</summary>
	static IEnumerable<(string Name, int Line)> FxReferences(string file)
	{
		XDocument document;
		try
		{
			document = XDocument.Load(file, LoadOptions.SetLineInfo);
		}
		catch (Exception ex) when (ex is XmlException or IOException)
		{
			yield break;
		}
		foreach (var item in document.Descendants().Where(e => e.Name.LocalName == "FxReference"))
		{
			if (string.Equals((string?)item.Attribute("WorkspaceOnly") ?? (string?)item.Elements().FirstOrDefault(e => e.Name.LocalName == "WorkspaceOnly"), "true", StringComparison.OrdinalIgnoreCase))
				continue;
			foreach (var name in ((string?)item.Attribute("Include") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
				yield return (name, ((IXmlLineInfo)item).LineNumber);
		}
	}

	// What only the doctor looks at
	static void Check(WorkspaceManifest manifest, IReadOnlyList<RepositoryState> repositories, List<SyncFile> files, FxDiagnostics diagnostics, bool offline,
		IFxProgress? progress, CancellationToken cancellationToken)
	{
		var repositoryPaths = repositories.Select(r => r.Repository.Path.TrimEnd('/') + "/").ToList();
		foreach (var file in files.Where(f => f.Status == SyncFileStatus.WouldWrite))
		{
			// the generated files of a repository: its own fx doctor dotnet already reported them
			var inRepository = repositoryPaths.Any(p => file.Path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
			if (file.Path.Contains("/_fx/.workspace/", StringComparison.OrdinalIgnoreCase))
				diagnostics.Add(FxDiagnostic.Error(Outdated, new($"{Outdated}.workspace-layer"), file.Path, fix: FxFix.Run(SyncCommand)));
			else if (!inRepository)
				diagnostics.Add(file.Path is SolutionGenerator.FileName or WorkspacePropsGenerator.FileName
					? FxDiagnostic.Warning(Outdated, new($"{Outdated}.solutions"), file.Path, fix: FxFix.Run(SyncCommand))
					: FxDiagnostic.Error(Outdated, new($"{Outdated}.manifest"), file.Path, fix: FxFix.Run(SyncCommand)));
		}

		var metarepo = GitClient.Discover(manifest.Root);
		var mounted = repositories.Where(r => r.Mounted && r.Present).ToList();
		Parallel.ForEach(mounted, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, repo =>
		{
			var name = repo.Repository.Name;
			// a mounted repository the metarepo does not ignore would be added to it by the next git add (design D-12)
			if (metarepo is not null && !metarepo.Ignores(repo.Repository.Path + "/"))
				diagnostics.Add(FxDiagnostic.Error(NotIgnored, new(NotIgnored, name, repo.Repository.Path), ".gitignore",
					repository: name, fix: FxFix.Run(SyncCommand)));
			if (progress.Step(Name, name, "status", () => GitClient.Discover(repo.FullPath)?.Status(), s => s is not null) is not { } status)
				return;
			if (status.Changes > 0)
				diagnostics.Add(FxDiagnostic.Warning(Dirty, new(Dirty, name, status.Changes), repository: name));
			if (status.Detached)
				diagnostics.Add(FxDiagnostic.Warning(Detached, new(Detached, name), repository: name));
			else if (status.Upstream is null)
				diagnostics.Add(FxDiagnostic.Warning(NoUpstream, new(NoUpstream, name, status.Branch), repository: name,
					fix: FxFix.Run($"git -C {repo.Repository.Path} push -u origin {status.Branch}")));
			else if (status.Ahead > 0)
				diagnostics.Add(FxDiagnostic.Warning(Unpushed, new(Unpushed, name, status.Ahead, status.Branch), repository: name,
					fix: FxFix.Run($"git -C {repo.Repository.Path} push")));
		});
		if (offline)
			return;
		Parallel.ForEach(repositories.Where(r => r.Mounted && r.Repository.Url is not null), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, repo =>
		{
			if (progress.Step(Name, repo.Repository.Name, "remote", () => GitClient.LsRemote(repo.Repository.Url!, RemoteTimeout), r => r.ExitCode == 0).ExitCode != 0)
				diagnostics.Add(FxDiagnostic.Warning(Unreachable, new(Unreachable, repo.Repository.Name, repo.Repository.Url), repository: repo.Repository.Name));
		});
	}

	/// <summary>
	/// The whitelist <c>.gitignore</c> of the metarepo (design §4.3, D-12): everything at the root is ignored (the
	/// mounted repositories above all) except what the metarepo versions.
	/// </summary>
	public static string Gitignore(WorkspaceManifest manifest)
	{
		var b = new StringBuilder();
		b.Append("# GENERATED by fx from _fx/workspace.yaml. DO NOT EDIT. Regenerate with: fx sync workspace\n");
		b.Append("# Whitelist: everything at the root is ignored (the mounted repositories above all) except what the metarepo\n");
		b.Append("# versions. Fuxion.slnx, nuget.config and ~$publish are local on purpose.\n");
		b.Append("/*\n");
		foreach (var folder in new[] { ".claude", ".github", "_fx" }.Concat(manifest.Folders ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
			b.Append($"!/{folder}/\n");
		foreach (var file in new[] { ".gitattributes", ".gitignore", ".mcp.json", "AGENTS.md", "CLAUDE.md", "README.md", "global.json" })
			b.Append($"!/{file}\n");
		b.Append('\n');
		b.Append("# Local files, at any level\n");
		b.Append("~$*\n");
		b.Append(".claude/settings.local.json\n");
		return b.ToString();
	}

	internal static void Generate(string root, string relative, string content, string commentStart, bool write, bool adopt, List<SyncFile> files, FxDiagnostics diagnostics)
	{
		var path = Path.Combine(root, relative);
		if (File.Exists(path))
		{
			var current = File.ReadAllText(path).Replace("\r\n", "\n");
			if (current == content)
			{
				files.Add(new(relative, SyncFileStatus.Unchanged));
				return;
			}
			if (!current.Contains("GENERATED by fx", StringComparison.Ordinal) && !adopt)
			{
				files.Add(new(relative, SyncFileStatus.Refused));
				diagnostics.Add(FxDiagnostic.Error(NotGenerated, new("fx.not-generated"), relative,
					fix: new FxFix($"{SyncCommand} --adopt", new($"{NotGenerated}.fix"))));
				return;
			}
		}
		if (write)
			Save(path, content);
		files.Add(new(relative, write ? SyncFileStatus.Written : SyncFileStatus.WouldWrite));
	}

	static void Save(string path, string content)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content.Replace("\n", Environment.NewLine), new UTF8Encoding(false));
	}

	static bool SameUrl(string? a, string? b) => NormalizeUrl(a) == NormalizeUrl(b);

	static string NormalizeUrl(string? url)
	{
		var u = (url ?? "").Trim().Replace('\\', '/').TrimEnd('/');
		if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
			u = u[..^4];
		return u.ToLowerInvariant();
	}

	// fx repo

	/// <summary><c>fx repo mount</c>/<c>unmount</c>: changes the intent in the local state; never clones or deletes.</summary>
	public static IReadOnlyList<FxDiagnostic> SetMount(WorkspaceManifest manifest, IEnumerable<string> names, bool mounted)
	{
		var diagnostics = new List<FxDiagnostic>();
		var state = WorkspaceState.Load(manifest.Root);
		foreach (var name in names)
		{
			var repo = manifest.Repositories.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
			if (repo is null)
				diagnostics.Add(FxDiagnostic.Error(UnknownRepository, new(UnknownRepository, name), repository: name));
			else if (!mounted && repo.Mount == MountPolicy.Mandatory)
				diagnostics.Add(FxDiagnostic.Error(UnknownRepository, new($"{UnknownRepository}.mandatory", repo.Name), repository: repo.Name));
			else
				state = state.With(repo.Name, mounted);
		}
		if (!diagnostics.Any(d => d.Severity == FxSeverity.Error))
			Save(Path.Combine(manifest.Root, "_fx", "~$workspace.yaml"), state.ToYaml());
		return diagnostics;
	}

	/// <summary><c>fx repo pull</c>: fast-forward only, and only with a clean working tree.</summary>
	public static FxModuleResult Pull(WorkspaceManifest manifest, IReadOnlyCollection<string> only, IFxProgress? progress = null, CancellationToken cancellationToken = default)
	{
		var actions = new List<RepositoryAction>();
		var diagnostics = new FxDiagnostics(progress);
		foreach (var repo in Repositories(manifest, withStatus: true, progress, cancellationToken)
			         .Where(r => r.Mounted && r.Present && (only.Count == 0 || only.Contains(r.Repository.Name, StringComparer.OrdinalIgnoreCase))))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var name = repo.Repository.Name;
			if (repo.Status is { Changes: > 0 })
			{
				actions.Add(new(name, "skipped", new("workspace.action.uncommitted-changes")));
				continue;
			}
			if (repo.Status is { Detached: true } or { Upstream: null })
			{
				actions.Add(new(name, "skipped", new("workspace.action.no-upstream")));
				continue;
			}
			var pull = progress.Step(Name, name, "pull", () => GitClient.Discover(repo.FullPath)!.PullFastForward(TimeSpan.FromMinutes(2)), p => p.ExitCode == 0);
			if (pull.ExitCode == 0)
				actions.Add(new(name, "pulled"));
			else
			{
				actions.Add(new(name, "failed", FxText.Plain(pull.StandardError.Trim())));
				diagnostics.Add(FxDiagnostic.Error(PullFailed, new(PullFailed, name, pull.StandardError.Trim()), repository: name));
			}
		}
		return new(Name, manifest.Root, null, [], actions, diagnostics.ToList());
	}
}
