using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fuxion.Tools.Core.Git;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Processes;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Sync;

namespace Fuxion.Tools.Core.Workspace;

/// <summary>A repository of the manifest and its state here.</summary>
public sealed record RepositoryState(WorkspaceRepository Repository, string FullPath, bool Mounted, bool Present, GitStatus? Status);

/// <summary>
/// The workspace module of fx (plan P, decision 3): what a workspace mounts and what its git versions. In
/// <c>fx sync</c> it clones the mounted repositories that are missing and fetches the others, keeps the local state and
/// generates the <c>.gitignore</c>; <c>fx doctor</c> checks the same without touching anything, plus the state of each
/// repository and what is on disk that nobody declared. <c>fx repo</c> lists, mounts, unmounts and pulls them. It never
/// commits, never pushes, never pulls implicitly, never deletes. What it does on each repository goes to the progress
/// channel as a step (<c>clone</c>, <c>fetch</c>, <c>status</c>, <c>remote</c>, <c>pull</c>).
/// </summary>
public static class WorkspaceModule
{
	public const string Name = WorkspaceManifest.ModuleName;
	public const string NotAWorkspace = "workspace.not-a-workspace";
	public const string Missing = "workspace.missing";
	public const string CloneFailed = "workspace.clone-failed";
	public const string FetchFailed = "workspace.fetch-failed";
	public const string NotARepository = "workspace.not-a-repository";
	public const string OriginMismatch = "workspace.origin-mismatch";
	public const string Orphan = "workspace.orphan";
	public const string NotVersioned = "workspace.not-versioned";
	public const string NotIgnored = "workspace.not-ignored";
	public const string LocalNotIgnored = "workspace.local-not-ignored";
	public const string Outdated = "workspace.outdated";
	public const string Dirty = "workspace.dirty";
	public const string Detached = "workspace.detached";
	public const string NoUpstream = "workspace.no-upstream";
	public const string Unpushed = "workspace.unpushed";
	public const string Unreachable = "workspace.unreachable";
	public const string UnknownRepository = "workspace.unknown-repository";
	public const string UnknownTag = "workspace.unknown-tag";
	public const string PullFailed = "workspace.pull-failed";

	static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(10);
	static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(60);
	static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(30);

	/// <summary>Loads the manifest of the nearest workspace around <paramref name="directory"/>, or a diagnostic.</summary>
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
			return (null, ex.Diagnostics.First(d => d.Severity == FxSeverity.Error));
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
			var present = FxLayout.IsGitRoot(full);
			var status = present && withStatus ? progress.Step(Name, r.Name, "status", () => GitClient.Discover(full)?.Status(), s => s is not null) : null;
			result[i] = new RepositoryState(r, full, state.IsMounted(r, present), present, status);
		});
		return result;
	}

	/// <summary>
	/// The mounted repositories of the workspace at <paramref name="node"/>: a clone already there is adopted; what is
	/// mounted and missing is cloned (not in a dry run, <c>--offline</c> or the doctor); what is there is checked and
	/// fetched. The local state is saved. Returns the repositories mounted and on disk, in the order of the manifest.
	/// </summary>
	public static IReadOnlyList<WorkspaceRepository> Mount(WorkspaceManifest manifest, SyncContext context, FxDiagnostics diagnostics, List<RepositoryAction> actions)
	{
		var root = manifest.Root;
		var cancellationToken = context.CancellationToken;
		var progress = context.Progress;
		var state = WorkspaceState.Load(root);
		var repositories = Repositories(manifest, withStatus: false, cancellationToken: cancellationToken);
		foreach (var repo in repositories.Where(r => r.Present && !state.Mount.ContainsKey(r.Repository.Name)))
			state = state.With(repo.Repository.Name, true);

		var mounted = new List<WorkspaceRepository>();
		var toFetch = new List<(RepositoryState Info, GitClient Git)>();
		foreach (var repo in repositories.Where(r => r.Mounted))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var name = repo.Repository.Name;
			if (!repo.Present)
			{
				if (!context.Write || context.Offline)
				{
					diagnostics.Add(FxDiagnostic.Error(Missing, new(Missing, name, repo.Repository.Path), Display(context, root, WorkspaceManifest.RelativePath),
						repo.Repository.Line, name, FxFix.Run(FxHeader.Sync)));
					actions.Add(new(name, "missing"));
					continue;
				}
				var existed = Directory.Exists(repo.FullPath);
				ProcessResult clone;
				try
				{
					clone = progress.Step(Name, name, "clone", () => GitClient.Clone(repo.Repository.Url!, repo.FullPath, CloneTimeout, cancellationToken), c => c.ExitCode == 0);
				}
				catch (OperationCanceledException) when (!existed)
				{
					// a clone killed half way leaves a folder that is not a repository yet
					TryDelete(repo.FullPath);
					throw;
				}
				if (clone.ExitCode != 0)
				{
					diagnostics.Add(FxDiagnostic.Error(CloneFailed, new(CloneFailed, name, repo.Repository.Url, clone.StandardError.Trim()), repository: name));
					actions.Add(new(name, "clone-failed"));
					continue;
				}
				actions.Add(new(name, "cloned"));
				state = state.With(name, true);
				mounted.Add(repo.Repository);
				continue;
			}
			var git = GitClient.Discover(repo.FullPath);
			if (git is null || !FxLayout.SameFolder(git.Root, repo.FullPath))
			{
				diagnostics.Add(FxDiagnostic.Error(NotARepository, new(NotARepository, name, repo.Repository.Path), repository: name));
				continue;
			}
			mounted.Add(repo.Repository);
			var origin = git.RemoteUrl("origin");
			if (!SameUrl(origin, repo.Repository.Url))
			{
				diagnostics.Add(FxDiagnostic.Warning(OriginMismatch, new(OriginMismatch, name, origin, repo.Repository.Url), repository: name,
					fix: FxFix.Run($"git -C {repo.Repository.Path} remote set-url origin {repo.Repository.Url}")));
				continue;
			}
			toFetch.Add((repo, git));
		}
		if (context.Write && !context.Offline)
		{
			var results = new RepositoryAction?[toFetch.Count];
			Parallel.For(0, toFetch.Count, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, i =>
			{
				var name = toFetch[i].Info.Repository.Name;
				var fetch = progress.Step(Name, name, "fetch", () => toFetch[i].Git.Fetch(FetchTimeout, cancellationToken), f => f.ExitCode == 0);
				results[i] = fetch.ExitCode == 0 ? new(name, "fetched") : new(name, "fetch-failed", FxText.Plain(fetch.StandardError.Trim()));
			});
			foreach (var result in results.OfType<RepositoryAction>())
			{
				actions.Add(result);
				if (result.Action == "fetch-failed")
					diagnostics.Add(FxDiagnostic.Warning(FetchFailed, new(FetchFailed, result.Repository, result.Detail), repository: result.Repository));
			}
		}
		if (context.Write)
			Save(FxLayout.Full(root, WorkspaceState.RelativePath), state.ToYaml());
		return manifest.Repositories.Where(mounted.Contains).ToList();
	}

	/// <summary>
	/// What the workspace generates (its <c>.gitignore</c>, with <paramref name="versioned"/>, what its modules
	/// generate and version) and, in the doctor, what only it checks.
	/// </summary>
	public static void Outputs(FxNode node, WorkspaceManifest manifest, FxConfig config, IEnumerable<string> versioned, SyncContext context, FxOutputs outputs, FxDiagnostics diagnostics)
	{
		outputs.Generated(node.Full(GitignoreGenerator.FileName), GitignoreGenerator.Generate(manifest, versioned));
		if (!context.Doctor)
			return;
		var manifestFile = Display(context, node.Root, WorkspaceManifest.RelativePath);
		foreach (var repo in manifest.Repositories)
			foreach (var tag in repo.Tags.Where(t => !config.Knows(t)))
				diagnostics.Add(FxDiagnostic.Warning(UnknownTag, new(UnknownTag, tag, repo.Name), manifestFile, repo.Line, repo.Name, FxFix.Do($"{UnknownTag}.fix")));
		Walk(node.Root, manifest, context, diagnostics);
		Check(manifest, context, diagnostics);
	}

	/// <summary>
	/// What is on disk that nobody declared (plan P, P1, detail 6): repositories (a warning) and folders the workspace does
	/// not version (an info). It does not go into the repositories, <c>_fx/</c>, the local files nor what is versioned; a
	/// folder with only folders is walked, one with files is reported and left.
	/// </summary>
	static void Walk(string root, WorkspaceManifest manifest, SyncContext context, FxDiagnostics diagnostics)
	{
		var repositories = manifest.Repositories.Select(r => manifest.FullPath(r)).ToList();
		void Visit(string directory)
		{
			foreach (var sub in Directory.EnumerateDirectories(directory))
			{
				var name = Path.GetFileName(sub);
				var relative = FxLayout.Relative(root, sub) + "/";
				if (name is ".git" || FxLayout.IsLocal(name) || relative == FxLayout.Folder + "/" || manifest.Versions(relative))
					continue;
				if (repositories.Any(r => FxLayout.SameFolder(r, sub)))
					continue;
				if (FxLayout.IsGitRoot(sub))
				{
					diagnostics.Add(FxDiagnostic.Warning(Orphan, new(Orphan, relative.TrimEnd('/')), fix: FxFix.Do($"{Orphan}.fix")));
					continue;
				}
				// a folder that leads to a repository (or to versioned paths) is walked; one with files of its own is reported
				var hasFiles = Directory.EnumerateFiles(sub).Any(f => !FxLayout.IsLocal(Path.GetFileName(f)));
				var leads = repositories.Any(r => r.StartsWith(FxLayout.Normalize(sub), StringComparison.OrdinalIgnoreCase))
				            || (manifest.Paths ?? []).Any(p => !p.Exclude && p.Path.StartsWith(relative, StringComparison.OrdinalIgnoreCase));
				if (hasFiles && !leads)
					diagnostics.Add(FxDiagnostic.Info(NotVersioned, new(NotVersioned, relative), Display(context, root, relative.TrimEnd('/')), fix: FxFix.Do($"{NotVersioned}.fix")));
				else if (!hasFiles || leads)
					Visit(sub);
			}
		}
		Visit(root);
	}

	// The state of each mounted repository
	static void Check(WorkspaceManifest manifest, SyncContext context, FxDiagnostics diagnostics)
	{
		var progress = context.Progress;
		var cancellationToken = context.CancellationToken;
		var repositories = Repositories(manifest, withStatus: false, cancellationToken: cancellationToken);
		var metarepo = GitClient.Discover(manifest.Root);
		var mounted = repositories.Where(r => r.Mounted && r.Present).ToList();
		Parallel.ForEach(mounted, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, repo =>
		{
			var name = repo.Repository.Name;
			// a mounted repository the workspace does not ignore would be added to it by the next git add
			if (metarepo is not null && !metarepo.Ignores(repo.Repository.Path + "/"))
				diagnostics.Add(FxDiagnostic.Error(NotIgnored, new(NotIgnored, name, repo.Repository.Path), Display(context, manifest.Root, GitignoreGenerator.FileName),
					repository: name, fix: FxFix.Run(FxHeader.Sync)));
			if (GitClient.Discover(repo.FullPath) is not { } git)
				return;
			// fx writes ~$ files inside the repositories: they have to be ignored (plan P, decision 9)
			if (!git.Ignores(FxLayout.LocalPrefix + "fx-probe"))
				diagnostics.Add(FxDiagnostic.Error(LocalNotIgnored, new(LocalNotIgnored, name), $"{repo.Repository.Path}/.gitignore", repository: name,
					fix: FxFix.Do($"{LocalNotIgnored}.fix")));
			if (progress.Step(Name, name, "status", git.Status, _ => true) is not { } status)
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
		if (context.Offline)
			return;
		Parallel.ForEach(repositories.Where(r => r.Mounted && r.Repository.Url is not null), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, repo =>
		{
			if (progress.Step(Name, repo.Repository.Name, "remote", () => GitClient.LsRemote(repo.Repository.Url!, RemoteTimeout, cancellationToken), r => r.ExitCode == 0).ExitCode != 0)
				diagnostics.Add(FxDiagnostic.Warning(Unreachable, new(Unreachable, repo.Repository.Name, repo.Repository.Url), repository: repo.Repository.Name));
		});
	}

	static string Display(SyncContext context, string root, string relative) => FxLayout.Relative(context.DisplayRoot, FxLayout.Full(root, relative));

	static void Save(string path, string content)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content.Replace("\n", Environment.NewLine), new UTF8Encoding(false));
	}

	/// <summary>Deletes a folder fx has just created (git leaves read-only files in it); if it cannot, the next run reports it.</summary>
	static void TryDelete(string path)
	{
		try
		{
			foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
				File.SetAttributes(file, FileAttributes.Normal);
			Directory.Delete(path, recursive: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// left for the user: fx sync reports a folder that is not a repository
		}
	}

	public static bool SameUrl(string? a, string? b) => NormalizeUrl(a) == NormalizeUrl(b);

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
			Save(FxLayout.Full(manifest.Root, WorkspaceState.RelativePath), state.ToYaml());
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
			var pull = progress.Step(Name, name, "pull", () => GitClient.Discover(repo.FullPath)!.PullFastForward(TimeSpan.FromMinutes(2), cancellationToken), p => p.ExitCode == 0);
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
