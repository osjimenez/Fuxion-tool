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

/// <summary>
/// A repository of the manifest and its state here: whether it is mounted (the intent), at its place or parked, and
/// the copy used instead of it, if a higher one supersedes it (plan P, decisions 19 and 20).
/// </summary>
public sealed record RepositoryState(WorkspaceRepository Repository, string FullPath, bool Mounted, bool Present, GitStatus? Status, bool Parked = false,
	RepositoryCopy? SupersededBy = null);

/// <summary>
/// The workspace module of fx (plan P, decision 3): what a workspace mounts and what its git versions. In
/// <c>fx sync</c> it clones the mounted repositories that are missing (or brings them back from the parking) and
/// fetches the others, parks what it does not use, keeps the local state and generates the <c>.gitignore</c>;
/// <c>fx doctor</c> checks the same without touching anything, plus the state of each repository and what is on disk
/// that nobody declared. <c>fx repo</c> lists, mounts, unmounts and pulls them. It never commits, never pushes, never
/// pulls implicitly, never deletes: unmounting is parking, a move. What it does on each repository goes to the
/// progress channel as a step (<c>clone</c>, <c>fetch</c>, <c>status</c>, <c>remote</c>, <c>pull</c>).
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
	public const string AliasReference = "workspace.alias-reference";
	public const string PullFailed = "workspace.pull-failed";
	public const string NotParked = "workspace.not-parked";
	public const string MoveFailed = "workspace.move-failed";

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

	/// <summary>
	/// The repositories of the manifest and their state; with <paramref name="withStatus"/>, their git status (in
	/// parallel); with <paramref name="copies"/>, the copy that supersedes each one.
	/// </summary>
	public static IReadOnlyList<RepositoryState> Repositories(WorkspaceManifest manifest, bool withStatus, IFxProgress? progress = null, CancellationToken cancellationToken = default,
		WorkspaceCopies? copies = null)
	{
		var state = WorkspaceState.Load(manifest.Root);
		var result = new RepositoryState[manifest.Repositories.Count];
		Parallel.For(0, result.Length, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, i =>
		{
			var r = manifest.Repositories[i];
			var full = manifest.FullPath(r);
			var present = FxLayout.IsGitRoot(full);
			var status = present && withStatus ? progress.Step(Name, r.Name, "status", () => GitClient.Discover(full)?.Status(), s => s is not null) : null;
			var mounted = state.IsMounted(r, present);
			result[i] = new RepositoryState(r, full, mounted, present, status, !present && FxLayout.IsGitRoot(ParkingPath(manifest, r)),
				mounted ? copies?.SupersededBy(full) : null);
		});
		return result;
	}

	/// <summary>Where a repository of the workspace is parked (plan P, decision 20): its path, under <c>_fx/~$parking/</c>.</summary>
	public static string ParkingPath(WorkspaceManifest manifest, WorkspaceRepository repository)
		=> FxLayout.Normalize(FxLayout.Full(manifest.Root, $"{FxLayout.Parking}/{repository.Path}"));

	/// <summary>
	/// The mounted repositories of the workspace at <paramref name="node"/>: a clone already there is adopted; what is
	/// mounted and missing comes back from the parking, or is cloned (not in a dry run, <c>--offline</c> or the doctor);
	/// what is there is checked and fetched. What is there and not used goes to the parking (plan P, decisions 19 and
	/// 20): an unmounted repository, or a copy a higher one supersedes (the intent stays: if the higher copy goes, this
	/// one comes back). The local state is saved. Returns the repositories used, on disk, in the order of the manifest.
	/// </summary>
	public static IReadOnlyList<WorkspaceRepository> Mount(WorkspaceManifest manifest, SyncContext context, FxDiagnostics diagnostics, List<RepositoryAction> actions)
	{
		var root = manifest.Root;
		var cancellationToken = context.CancellationToken;
		var progress = context.Progress;
		var state = WorkspaceState.Load(root);
		// which copy of each repository is used: the highest in the tree (decision 19), now that what is above is mounted
		var copies = WorkspaceCopies.Survey(root);
		var manifestFile = Display(context, root, WorkspaceManifest.RelativePath);
		diagnostics.AddRange(copies.Ties(root, manifestFile));
		var repositories = Repositories(manifest, withStatus: false, cancellationToken: cancellationToken, copies: copies);
		foreach (var repo in repositories.Where(r => r.Present && !state.Mount.ContainsKey(r.Repository.Name)))
			state = state.With(repo.Repository.Name, true);

		var mounted = new List<WorkspaceRepository>();
		var toFetch = new List<(RepositoryState Info, GitClient Git)>();
		foreach (var repo in repositories)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var name = repo.Repository.Name;
			var parking = ParkingPath(manifest, repo.Repository);
			if (repo.Present && (!repo.Mounted || repo.SupersededBy is not null))
			{
				if (!context.Write)
					diagnostics.Add(FxDiagnostic.Warning(NotParked, repo.SupersededBy is { } higher
						? new($"{NotParked}.superseded", name, repo.Repository.Path, higher.FullName)
						: new(NotParked, name, repo.Repository.Path), manifestFile, repo.Repository.Line, name, FxFix.Run(FxHeader.Sync)));
				else if (Move(name, repo.FullPath, parking, root, diagnostics.Add))
					actions.Add(new(name, "parked", repo.SupersededBy is { } used
						? new("workspace.action.superseded", used.FullName)
						: new("workspace.action.parked-in", FxLayout.Relative(root, parking).TrimEnd('/'))));
				continue;
			}
			if (!repo.Mounted)
				continue;
			if (repo.SupersededBy is { } superseding)
			{
				actions.Add(new(name, "superseded", new("workspace.action.superseded", superseding.FullName)));
				continue;
			}
			var present = repo.Present;
			if (!present && repo.Parked)
			{
				if (!context.Write)
				{
					diagnostics.Add(FxDiagnostic.Error(Missing, new($"{Missing}.parked", name, repo.Repository.Path, FxLayout.Relative(root, parking).TrimEnd('/')),
						manifestFile, repo.Repository.Line, name, FxFix.Run(FxHeader.Sync)));
					actions.Add(new(name, "missing"));
					continue;
				}
				if (!Move(name, parking, repo.FullPath, root, diagnostics.Add))
					continue;
				actions.Add(new(name, "unparked"));
				present = true;
			}
			if (!present)
			{
				if (!context.Write || context.Offline)
				{
					diagnostics.Add(FxDiagnostic.Error(Missing, new(Missing, name, repo.Repository.Path), manifestFile, repo.Repository.Line, name, FxFix.Run(FxHeader.Sync)));
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
	/// generate and version, and <paramref name="shared"/>, the shares it receives) and, in the doctor, what only it
	/// checks. Its own shares, in both.
	/// </summary>
	public static void Outputs(FxNode node, FxIdentity identity, WorkspaceManifest manifest, FxConfig config, IEnumerable<string> versioned, IEnumerable<string> shared,
		SyncContext context, FxOutputs outputs, FxDiagnostics diagnostics)
	{
		outputs.Generated(node.Full(GitignoreGenerator.FileName), GitignoreGenerator.Generate(manifest, versioned, shared));
		var manifestFile = Display(context, node.Root, WorkspaceManifest.RelativePath);
		WorkspaceShares.Check(node, manifest, manifestFile, context.Doctor, diagnostics);
		if (!context.Doctor)
			return;
		foreach (var repo in manifest.Repositories)
			foreach (var tag in repo.Tags.Where(t => !config.Knows(t)))
				diagnostics.Add(FxDiagnostic.Warning(UnknownTag, new(UnknownTag, tag, repo.Name), manifestFile, repo.Line, repo.Name, FxFix.Do($"{UnknownTag}.fix")));
		AliasReferences(node, identity, manifest, context, diagnostics);
		Walk(node.Root, manifest, context, diagnostics);
		Check(manifest, context, diagnostics);
	}

	/// <summary>
	/// The references of this workspace that use an old name of one of its repositories (<c>aliases</c>, plan P,
	/// decision 12): they still work, but they should change. In <c>depends-on</c> and in the <c>applies-to</c> of the
	/// shares and of the .NET module.
	/// </summary>
	static void AliasReferences(FxNode node, FxIdentity identity, WorkspaceManifest manifest, SyncContext context, FxDiagnostics diagnostics)
	{
		var aliases = manifest.Repositories.SelectMany(r => (r.Aliases ?? []).Select(a => (Alias: a, r.Name))).ToList();
		if (aliases.Count == 0)
			return;
		void Check(string reference, string file, int line)
		{
			var relative = reference.StartsWith(identity.FullName + "/", StringComparison.Ordinal) ? reference[(identity.FullName.Length + 1)..]
				: reference.StartsWith('/') ? null
				: reference;
			var first = relative?.Split('/')[0];
			foreach (var (alias, name) in aliases.Where(a => a.Alias == first))
				diagnostics.Add(FxDiagnostic.Warning(AliasReference, new(AliasReference, reference, alias, name), file, line, name, FxFix.Do($"{AliasReference}.fix")));
		}
		var manifestFile = Display(context, node.Root, WorkspaceManifest.RelativePath);
		foreach (var repo in manifest.Repositories)
			foreach (var dependency in repo.DependsOn ?? [])
				Check(dependency, manifestFile, repo.Line);
		foreach (var share in manifest.Shares ?? [])
			foreach (var reference in share.AppliesTo.Repositories)
				Check(reference, manifestFile, share.Line);
		var dotnetFile = node.Full(FxLayout.ModuleFile(Dotnet.DotnetModule.Name));
		if (!File.Exists(dotnetFile))
			return;
		Dotnet.DotnetConfig dotnet;
		try
		{
			dotnet = Dotnet.DotnetYamlReader.ReadFile(dotnetFile);
		}
		catch (Dotnet.DotnetConfigException)
		{
			return;
		}
		var display = Display(context, node.Root, FxLayout.ModuleFile(Dotnet.DotnetModule.Name));
		foreach (var reference in dotnet.AppliesTo?.Repositories ?? [])
			Check(reference, display, 1);
		foreach (var (appliesTo, line) in dotnet.Variables.Select(v => (v.AppliesTo, v.Line))
			         .Concat(dotnet.Imports.Select(i => (i.AppliesTo, i.Line)))
			         .Concat(dotnet.Packages.Select(p => (p.AppliesTo, p.Line))))
			foreach (var reference in appliesTo.Repositories)
				Check(reference, display, line);
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
		// a copy a higher one supersedes is not used: sync parks it
		var repositories = Repositories(manifest, withStatus: false, cancellationToken: cancellationToken, copies: WorkspaceCopies.Survey(manifest.Root))
			.Where(r => r.SupersededBy is null)
			.ToList();
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

	internal static string NormalizeUrl(string? url)
	{
		var u = (url ?? "").Trim().Replace('\\', '/').TrimEnd('/');
		if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
			u = u[..^4];
		return u.ToLowerInvariant();
	}

	// fx repo

	/// <summary>
	/// Moves a clone between its place and the parking of its workspace (plan P, decision 20): a rename on the same
	/// disk, instant whatever it holds. If something has it open, nothing moves and a diagnostic says so. Then git's
	/// links with its worktrees are repaired, and the empty folders it leaves behind go.
	/// </summary>
	static bool Move(string name, string from, string to, string root, Action<FxDiagnostic> report)
	{
		var source = Path.TrimEndingDirectorySeparator(from);
		var target = Path.TrimEndingDirectorySeparator(to);
		var (shownSource, shownTarget) = (FxLayout.Relative(root, source), FxLayout.Relative(root, target));
		if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
		{
			report(FxDiagnostic.Error(MoveFailed, new($"{MoveFailed}.occupied", name, shownSource, shownTarget), repository: name));
			return false;
		}
		try
		{
			if (Directory.Exists(target))
				Directory.Delete(target);
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			Directory.Move(source, target);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			RemoveEmpty(Path.GetDirectoryName(target)!, root);
			report(FxDiagnostic.Error(MoveFailed, new(MoveFailed, name, shownSource, shownTarget, ex.Message), repository: name, fix: FxFix.Do($"{MoveFailed}.fix")));
			return false;
		}
		// git keeps absolute paths between a repository and its worktrees: a main clone with worktrees, or a worktree
		if (Directory.Exists(Path.Combine(target, ".git", "worktrees")) || File.Exists(Path.Combine(target, ".git")))
			GitClient.Discover(target)?.RepairWorktrees();
		RemoveEmpty(Path.GetDirectoryName(source)!, root);
		return true;
	}

	/// <summary>Removes <paramref name="folder"/>, and the folders above it, while they are empty; never the root nor its <c>_fx/</c>.</summary>
	static void RemoveEmpty(string folder, string root)
	{
		var fx = FxLayout.Full(root, FxLayout.Folder);
		for (var dir = folder; dir is not null && FxLayout.Normalize(dir).StartsWith(FxLayout.Normalize(root), StringComparison.OrdinalIgnoreCase)
		                       && !FxLayout.SameFolder(dir, root) && !FxLayout.SameFolder(dir, fx); dir = Path.GetDirectoryName(dir))
		{
			if (!Directory.Exists(dir))
				continue;
			if (Directory.EnumerateFileSystemEntries(dir).Any())
				return;
			try
			{
				Directory.Delete(dir);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return;
			}
		}
	}

	/// <summary>
	/// <c>fx repo mount</c>/<c>unmount</c> (plan P, decision 20): the intent in the local state, and the clone moved now.
	/// Unmounting parks it; mounting brings it back from the parking, unless a higher copy supersedes it (then it stays
	/// there). It never clones (<c>fx sync</c> does) and never deletes. With an unknown name, or a mandatory repository
	/// to unmount, nothing changes; a clone that cannot move keeps its state.
	/// </summary>
	public static FxModuleResult SetMount(WorkspaceManifest manifest, IEnumerable<string> names, bool mounted)
	{
		var actions = new List<RepositoryAction>();
		var diagnostics = new List<FxDiagnostic>();
		var repositories = new List<WorkspaceRepository>();
		foreach (var name in names)
		{
			var repo = manifest.Repositories.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
			if (repo is null)
				diagnostics.Add(FxDiagnostic.Error(UnknownRepository, new(UnknownRepository, name), repository: name));
			else if (!mounted && repo.Mount == MountPolicy.Mandatory)
				diagnostics.Add(FxDiagnostic.Error(UnknownRepository, new($"{UnknownRepository}.mandatory", repo.Name), repository: repo.Name));
			else
				repositories.Add(repo);
		}
		if (diagnostics.Count > 0)
			return new(Name, manifest.Root, null, [], actions, diagnostics);

		var root = manifest.Root;
		var state = WorkspaceState.Load(root);
		var copies = mounted ? WorkspaceCopies.Survey(root) : WorkspaceCopies.None;
		foreach (var repo in repositories)
		{
			var full = manifest.FullPath(repo);
			var parking = ParkingPath(manifest, repo);
			if (mounted)
			{
				if (FxLayout.IsGitRoot(full))
					actions.Add(new(repo.Name, "mounted"));
				else if (copies.SupersededBy(full) is { } higher)
					actions.Add(new(repo.Name, "superseded", new("workspace.action.superseded", higher.FullName)));
				else if (FxLayout.IsGitRoot(parking))
				{
					if (!Move(repo.Name, parking, full, root, diagnostics.Add))
						continue;
					actions.Add(new(repo.Name, "unparked"));
				}
				else
					actions.Add(new(repo.Name, "mounted", new("workspace.action.to-clone")));
			}
			else if (FxLayout.IsGitRoot(full))
			{
				if (!Move(repo.Name, full, parking, root, diagnostics.Add))
					continue;
				actions.Add(new(repo.Name, "parked", new("workspace.action.parked-in", FxLayout.Relative(root, parking).TrimEnd('/'))));
			}
			else
				actions.Add(new(repo.Name, "unmounted"));
			state = state.With(repo.Name, mounted);
		}
		Save(FxLayout.Full(root, WorkspaceState.RelativePath), state.ToYaml());
		return new(Name, root, null, [], actions, diagnostics);
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
