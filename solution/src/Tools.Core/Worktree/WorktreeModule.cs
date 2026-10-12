using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Fuxion.Tools.Core.Git;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Sync;
using Fuxion.Tools.Core.Workspace;

namespace Fuxion.Tools.Core.Worktree;

/// <summary>What a worktree command did: the worktree, what happened to each repository, its diagnostics and the sync that followed.</summary>
public sealed record WorktreeResult(string? Root, string Branch, IReadOnlyList<RepositoryAction> Actions, IReadOnlyList<FxDiagnostic> Diagnostics,
	IReadOnlyList<FxModuleResult> Sync)
{
	public IEnumerable<FxDiagnostic> AllDiagnostics => Diagnostics.Concat(Sync.SelectMany(s => s.Diagnostics));
}

/// <summary>A worktree found: its root, its card and, if asked, the uncommitted changes of each repository mounted in it.</summary>
public sealed record WorktreeInfo(string Root, WorktreeCard Card, IReadOnlyDictionary<string, int>? Changes = null);

/// <summary>
/// The worktrees of fx (plan P, decisions 25-29): a branch to work on apart, without touching the main tree. A worktree
/// is created in a workspace (the nearest, or <c>--in</c>), in its <c>_fx/~$worktrees/&lt;branch&gt;/</c>, and its root
/// is always that workspace mounted in worktree mode: a <c>git worktree</c> of its main clone, sparse (without the
/// <c>paths</c> marked <c>worktree: false</c>). Mounting in it is a <c>git worktree add</c> of each repository from its
/// main clone (cloned once, parked, if there is none), in the same branch and at the same path. Nothing is mandatory in
/// a worktree: it starts with its workspace and the node where it was created, and <c>--mandatory</c> brings the
/// mandatory ones down. For the code it is a closed tree (its own map of <c>FxReference</c>; what is inherited, read
/// inside, but its root, from the main tree). A repository on its own has them in a folder of the user. fx never
/// removes a worktree with uncommitted changes, and only deletes the branches it created that git deletes safely.
/// </summary>
public static class WorktreeModule
{
	public const string Name = "worktree";
	public const string InsideWorktree = "worktree.inside-worktree";
	public const string Exists = "worktree.exists";
	public const string UnknownWorkspace = "worktree.unknown-workspace";
	public const string NotFound = "worktree.not-found";
	public const string UnknownTarget = "worktree.unknown-target";
	public const string NotDeclared = "worktree.not-declared";
	public const string NotARepository = "worktree.not-a-repository";
	public const string AddFailed = "worktree.add-failed";
	public const string CloneFailed = "worktree.clone-failed";
	public const string Dirty = "worktree.dirty";
	public const string RemoveFailed = "worktree.remove-failed";
	public const string BranchKept = "worktree.branch-kept";
	public const string RootUnmount = "worktree.root";
	public const string Outside = "workspace.worktree-outside";
	public const string Prunable = "workspace.worktree-prunable";

	static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(10);

	/// <summary>Where the worktrees of a repository on its own go (decision 25): <c>FX_WORKTREES</c>, or <c>%LOCALAPPDATA%\fx\worktrees</c>.</summary>
	public static string UserFolder
		=> System.Environment.GetEnvironmentVariable("FX_WORKTREES") is { Length: > 0 } folder
			? folder
			: Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "fx", "worktrees");

	/// <summary>The folder of the worktrees of the repository on its own at <paramref name="main"/>: its name and a hash of its path.</summary>
	public static string UserFolderOf(string main, string name)
	{
		var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(FxLayout.Normalize(main).ToLowerInvariant())))[..10];
		return Path.Combine(UserFolder, $"{name}-{hash}");
	}

	/// <summary>Whether <paramref name="path"/> is where fx puts worktrees: under a <c>_fx/~$worktrees/</c>, or in the folder of the user.</summary>
	public static bool InItsPlace(string path)
	{
		var normalized = FxLayout.Normalize(path).Replace('\\', '/');
		return normalized.Contains($"/{FxLayout.Worktrees}/", StringComparison.OrdinalIgnoreCase)
		       || normalized.StartsWith(FxLayout.Normalize(UserFolder).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>The workspaces that contain <paramref name="directory"/>, the nearest first.</summary>
	static List<string> Workspaces(string directory)
	{
		var result = new List<string>();
		for (var dir = new DirectoryInfo(Path.GetFullPath(directory)); dir is not null; dir = dir.Parent)
			if (WorkspaceManifest.IsWorkspace(dir.FullName))
				result.Add(FxLayout.Normalize(dir.FullName));
		return result;
	}

	/// <summary>The paths of a workspace left out of its worktrees (<c>worktree: false</c>), relative to it.</summary>
	static List<string> LeftOut(string workspace)
	{
		var file = FxLayout.Full(workspace, WorkspaceManifest.RelativePath);
		return File.Exists(file)
			? (WorkspaceManifest.Read(workspace, File.ReadAllText(file)).Paths ?? []).Where(p => !p.Worktree && !p.Exclude).Select(p => p.Path).ToList()
			: [];
	}

	static WorkspaceManifest? Manifest(string root)
	{
		var file = FxLayout.Full(root, WorkspaceManifest.RelativePath);
		return WorkspaceManifest.IsWorkspace(root) && File.Exists(file) ? WorkspaceManifest.Read(root, File.ReadAllText(file)) : null;
	}

	static string Join(string a, string b) => a is "" or "." ? b : $"{a}/{b}";

	// fx worktree add

	/// <summary>
	/// <c>fx worktree add &lt;branch&gt; [--in &lt;workspace&gt;] [--mandatory]</c>: a worktree of <paramref name="branch"/>
	/// in the nearest workspace around <paramref name="directory"/> (or <paramref name="in"/>, one of those above),
	/// with the node of <paramref name="directory"/> mounted, and the workspaces on the way to it.
	/// </summary>
	public static WorktreeResult Add(string directory, string branch, string? @in, bool mandatory, IFxProgress? progress = null, CancellationToken cancellationToken = default)
	{
		var actions = new List<RepositoryAction>();
		var diagnostics = new List<FxDiagnostic>();
		WorktreeResult Fail(FxDiagnostic error) => new(null, branch, actions, [.. diagnostics, error], []);
		if (WorktreeCard.FindRoot(directory) is { } inside)
			return Fail(FxDiagnostic.Error(InsideWorktree, new(InsideWorktree, inside)));
		if (FxNode.Find(directory) is not { } node || GitClient.Discover(node.Root) is null)
			return Fail(FxDiagnostic.Error(NotARepository, new(NotARepository, directory)));

		// where: the nearest workspace, or the one asked for; a repository on its own, in the folder of the user
		var workspaces = Workspaces(node.Root);
		var workspace = @in is null ? workspaces.FirstOrDefault() : workspaces.FirstOrDefault(w => Is(w, @in));
		if (@in is not null && workspace is null)
			return Fail(FxDiagnostic.Error(UnknownWorkspace, new(UnknownWorkspace, @in, string.Join(", ", workspaces.Select(w => new FxNode(w).SelfIdentity().FullName)))));
		var main = workspace ?? node.Root;
		var mainIdentity = new FxNode(main).SelfIdentity();
		var root = FxLayout.Normalize(workspace is null
			? Path.Combine(UserFolderOf(main, mainIdentity.Name), WorktreeCard.Folder(branch))
			: FxLayout.Full(workspace, $"{FxLayout.Worktrees}/{WorktreeCard.Folder(branch)}"));
		if (Directory.Exists(root))
			return Fail(FxDiagnostic.Error(Exists, new(Exists, branch, root)));

		// the way down to where you are, before anything is created
		var target = workspace is null ? "" : FxLayout.Relative(workspace, node.Root).TrimEnd('/');
		if (target == ".")
			target = "";
		if (target.Length > 0 && Declarations(main, mainIdentity.FullName).All(d => d.Path != target))
			return Fail(FxDiagnostic.Error(NotDeclared, new(NotDeclared, target, mainIdentity.FullName)));

		// the root: the workspace (or the repository) in worktree mode
		var leftOut = workspace is null ? [] : LeftOut(main);
		var (added, created) = progress.Step(Name, mainIdentity.Name, "worktree", () => GitClient.Discover(main)!.AddWorktree(root, branch, checkout: leftOut.Count == 0, cancellationToken),
			r => r.Result.ExitCode == 0);
		if (added.ExitCode != 0)
			return Fail(FxDiagnostic.Error(AddFailed, new(AddFailed, mainIdentity.Name, added.StandardError.Trim()), repository: mainIdentity.Name));
		if (leftOut.Count > 0 && GitClient.Discover(root)!.SparseLeaveOut(leftOut, cancellationToken) is { ExitCode: not 0 } sparse)
			diagnostics.Add(FxDiagnostic.Error(AddFailed, new(AddFailed, mainIdentity.Name, sparse.StandardError.Trim()), repository: mainIdentity.Name));
		actions.Add(new(mainIdentity.Name, "mounted"));
		var session = new Session(root, new(branch, node.SelfIdentity().Name, leftOut, [new(".", mainIdentity.Name, created)]), actions, diagnostics, progress, cancellationToken);
		session.Save();

		if (target.Length > 0)
			session.MountPath(target);
		if (mandatory)
			session.Mandatory([""]);
		session.Save();
		return new(root, branch, actions, diagnostics, SyncEngine.Sync(root, new(), progress, cancellationToken));
	}

	/// <summary>Whether the workspace at <paramref name="workspace"/> is <paramref name="name"/>: its name, its full name or its path.</summary>
	static bool Is(string workspace, string name)
	{
		var identity = new FxNode(workspace).SelfIdentity();
		return identity.Name == name || identity.Is(name) || (Directory.Exists(name) && FxLayout.SameFolder(name, workspace));
	}

	/// <summary>A repository declared below a workspace: its path from there, its full name and its declaration.</summary>
	sealed record Declared(string Path, string FullName, WorkspaceRepository Repository);

	/// <summary>
	/// The repositories declared below the workspace at <paramref name="root"/> (named <paramref name="fullName"/>), at
	/// any depth: through the workspaces on disk, at their place or parked.
	/// </summary>
	static List<Declared> Declarations(string root, string fullName)
	{
		var result = new List<Declared>();
		void Walk(string workspace, string relative, string name, int depth)
		{
			if (depth > 32 || Manifest(workspace) is not { } manifest)
				return;
			foreach (var repository in manifest.Repositories)
			{
				var path = Join(relative, repository.Path);
				result.Add(new(path, $"{name}/{repository.Name}", repository));
				var at = manifest.FullPath(repository);
				var parked = WorkspaceModule.ParkingPath(manifest, repository);
				var clone = FxLayout.IsGitRoot(at) ? at : FxLayout.IsGitRoot(parked) ? parked : null;
				if (clone is not null && repository.Inherits)
					Walk(clone, path, $"{name}/{repository.Name}", depth + 1);
			}
		}
		Walk(root, "", fullName, 0);
		return result;
	}

	/// <summary>The mounting of repositories in a worktree, and its card.</summary>
	sealed class Session(string root, WorktreeCard card, List<RepositoryAction> actions, List<FxDiagnostic> diagnostics, IFxProgress? progress, CancellationToken cancellationToken)
	{
		public WorktreeCard Card { get; private set; } = card;

		public void Save() => Card.Save(root);

		string Full(string relative) => relative is "" or "." ? root : FxLayout.Normalize(FxLayout.Full(root, relative));

		/// <summary>How high a path of the worktree is mounted: the repositories mounted on the way to it, and it.</summary>
		int Height(string path) => Card.Worktrees.Count(w => w.Path != "." && (path == w.Path || path.StartsWith(w.Path + "/", StringComparison.OrdinalIgnoreCase))) + 1;

		/// <summary>Mounts the repository at <paramref name="path"/> (relative to the root) and the workspaces on the way to it.</summary>
		public bool MountPath(string path)
		{
			var workspace = "";
			var remaining = path;
			while (true)
			{
				var declaration = Manifest(Full(workspace))?.Repositories
					.FirstOrDefault(r => remaining == r.Path || remaining.StartsWith(r.Path + "/", StringComparison.OrdinalIgnoreCase));
				if (declaration is null)
				{
					diagnostics.Add(FxDiagnostic.Error(NotDeclared, new(NotDeclared, path, Card.Worktrees[0].Repository)));
					return false;
				}
				if (!MountOne(workspace, declaration))
					return false;
				if (remaining == declaration.Path)
					return true;
				workspace = Join(workspace, declaration.Path);
				remaining = remaining[(declaration.Path.Length + 1)..];
			}
		}

		/// <summary>The mandatory repositories of the workspaces at <paramref name="workspaces"/>, level by level down (the highest copies first).</summary>
		public void Mandatory(IReadOnlyList<string> workspaces)
		{
			var next = new List<string>();
			foreach (var workspace in workspaces)
				foreach (var repository in Manifest(Full(workspace))?.Repositories.Where(r => r.Mount == MountPolicy.Mandatory) ?? [])
				{
					var path = Join(workspace, repository.Path);
					if (MountOne(workspace, repository) && repository.Inherits && WorkspaceManifest.IsWorkspace(Full(path)))
						next.Add(path);
				}
			if (next.Count > 0)
				Mandatory(next);
		}

		/// <summary>
		/// <paramref name="repository"/>, declared by the workspace at <paramref name="workspace"/>, in worktree mode: a
		/// <c>git worktree add</c> from its main clone. Whether it is mounted (also if it already was).
		/// </summary>
		public bool MountOne(string workspace, WorkspaceRepository repository)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var path = Join(workspace, repository.Path);
			var destination = Full(path);
			if (FxLayout.IsGitRoot(destination))
				return true;
			var name = repository.Name;
			// the highest copy (decision 19): a copy of a repository mounted higher in the worktree is not mounted
			if (repository.Url is { } url)
			{
				var height = Height(path);
				var higher = Card.Worktrees.Where(w => w.Path != "." && Height(w.Path) <= height)
					.FirstOrDefault(w => GitClient.Discover(Full(w.Path))?.RemoteUrl("origin") is { } other && WorkspaceModule.SameUrl(other, url));
				if (higher is not null)
				{
					actions.Add(new(name, "superseded", new("workspace.action.superseded", higher.Path)));
					return false;
				}
			}
			// its main clone: in the main tree, at its place or parked; if there is none, cloned once, parked
			if (GitClient.Discover(Full(workspace))?.MainRoot() is not { } mainWorkspace || Manifest(mainWorkspace) is not { } manifest)
			{
				diagnostics.Add(FxDiagnostic.Error(NotDeclared, new(NotDeclared, path, Card.Worktrees[0].Repository)));
				return false;
			}
			var main = manifest.FullPath(repository);
			if (!FxLayout.IsGitRoot(main))
			{
				main = WorkspaceModule.ParkingPath(manifest, repository);
				if (!FxLayout.IsGitRoot(main))
				{
					if (repository.Url is null)
					{
						diagnostics.Add(FxDiagnostic.Error(CloneFailed, new(CloneFailed, name, "", "no url"), repository: name));
						return false;
					}
					var clone = progress.Step(Name, name, "clone", () => GitClient.Clone(repository.Url, main, CloneTimeout, cancellationToken), c => c.ExitCode == 0);
					if (clone.ExitCode != 0)
					{
						diagnostics.Add(FxDiagnostic.Error(CloneFailed, new(CloneFailed, name, repository.Url, clone.StandardError.Trim()), repository: name));
						return false;
					}
					actions.Add(new(name, "cloned", new("worktree.action.parked")));
				}
			}
			var leftOut = WorkspaceManifest.IsWorkspace(main) ? LeftOut(main) : [];
			var (added, created) = progress.Step(Name, name, "worktree",
				() => GitClient.Discover(main)!.AddWorktree(destination, Card.Branch, checkout: leftOut.Count == 0, cancellationToken), r => r.Result.ExitCode == 0);
			if (added.ExitCode != 0)
			{
				diagnostics.Add(FxDiagnostic.Error(AddFailed, new(AddFailed, name, added.StandardError.Trim()), repository: name));
				return false;
			}
			if (leftOut.Count > 0 && GitClient.Discover(destination)!.SparseLeaveOut(leftOut, cancellationToken) is { ExitCode: not 0 } sparse)
				diagnostics.Add(FxDiagnostic.Error(AddFailed, new(AddFailed, name, sparse.StandardError.Trim()), repository: name));
			Card = Card with
			{
				LeftOut = [.. Card.LeftOut, .. leftOut.Select(p => Join(path, p))],
				Worktrees = [.. Card.Worktrees, new(path, name, created)]
			};
			actions.Add(new(name, "mounted"));
			return true;
		}

		/// <summary>Brings back a path left out of the worktree (<c>worktree: false</c>), in the workspace mounted that has it.</summary>
		public void BringBack(string path)
		{
			var owner = Card.Worktrees.Where(w => w.Path == "." || path.StartsWith(w.Path + "/", StringComparison.OrdinalIgnoreCase)).MaxBy(w => w.Path == "." ? 0 : w.Path.Length)!;
			var prefix = owner.Path == "." ? "" : owner.Path + "/";
			var leftOut = Card.LeftOut.Where(p => p != path).ToList();
			var own = leftOut.Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !Card.Worktrees.Any(w => w.Path != owner.Path && w.Path != "."
				&& p.StartsWith(w.Path + "/", StringComparison.OrdinalIgnoreCase) && w.Path.Length > owner.Path.Length)).Select(p => p[prefix.Length..]).ToList();
			if (GitClient.Discover(Full(owner.Path))!.SparseLeaveOut(own, cancellationToken) is { ExitCode: not 0 } sparse)
			{
				diagnostics.Add(FxDiagnostic.Error(AddFailed, new(AddFailed, owner.Repository, sparse.StandardError.Trim()), repository: owner.Repository));
				return;
			}
			Card = Card with { LeftOut = leftOut };
			actions.Add(new(path, "mounted"));
		}

		/// <summary>
		/// Removes the worktrees of <paramref name="mounts"/>, the deepest first, if none has uncommitted changes; then the
		/// branches fx created, if git deletes them safely. Whether they were removed.
		/// </summary>
		public bool Remove(IReadOnlyList<WorktreeMount> mounts)
		{
			var dirty = mounts.Where(m => FxLayout.IsGitRoot(Full(m.Path)) && GitClient.Discover(Full(m.Path))?.Status() is { Changes: > 0 }).ToList();
			if (dirty.Count > 0)
			{
				diagnostics.AddRange(dirty.Select(m => FxDiagnostic.Error(Dirty, new(Dirty, m.Repository, m.Path), repository: m.Repository)));
				return false;
			}
			var removed = true;
			foreach (var mount in mounts.OrderByDescending(m => m.Path == "." ? 0 : m.Path.Count(c => c == '/') + 1))
			{
				cancellationToken.ThrowIfCancellationRequested();
				var path = Full(mount.Path);
				if (!FxLayout.IsGitRoot(path))
				{
					Card = Card with { Worktrees = Card.Worktrees.Where(w => w != mount).ToList() };
					continue;
				}
				var main = GitClient.Discover(path)?.MainRoot() is { } m ? GitClient.Discover(m) : null;
				var result = main is null ? null : progress.Step(Name, mount.Repository, "remove", () => main.RemoveWorktree(Path.TrimEndingDirectorySeparator(path)), r => r.ExitCode == 0);
				if (main is null || result!.ExitCode != 0)
				{
					diagnostics.Add(FxDiagnostic.Error(RemoveFailed, new(RemoveFailed, mount.Repository, mount.Path, result?.StandardError.Trim() ?? ""), repository: mount.Repository));
					removed = false;
					continue;
				}
				Card = Card with
				{
					Worktrees = Card.Worktrees.Where(w => w != mount).ToList(),
					LeftOut = Card.LeftOut.Where(p => mount.Path == "." || !p.StartsWith(mount.Path + "/", StringComparison.OrdinalIgnoreCase)).ToList()
				};
				actions.Add(new(mount.Repository, "removed"));
				if (mount.CreatedBranch && main.DeleteBranch(Card.Branch).ExitCode != 0)
					diagnostics.Add(FxDiagnostic.Info(BranchKept, new(BranchKept, Card.Branch, mount.Repository), repository: mount.Repository));
			}
			return removed;
		}
	}

	// fx worktree mount, unmount, remove, list

	/// <summary>The worktree of <paramref name="branch"/> for <paramref name="directory"/>: in the workspaces around it, or in the folder of the user of its repository.</summary>
	public static WorktreeInfo? Find(string directory, string branch)
		=> List(directory).FirstOrDefault(w => w.Card.Branch == branch);

	/// <summary>The worktrees of the workspaces around <paramref name="directory"/> (the nearest first) or of its repository on its own; with <paramref name="withStatus"/>, their changes.</summary>
	public static IReadOnlyList<WorktreeInfo> List(string directory, bool withStatus = false)
	{
		var folders = Workspaces(directory).Select(w => FxLayout.Full(w, FxLayout.Worktrees)).ToList();
		if (folders.Count == 0 && GitClient.Discover(directory) is { } git && (git.MainRoot() ?? git.Root) is { } main)
			folders.Add(UserFolderOf(main, new FxNode(main).SelfIdentity().Name));
		var result = new List<WorktreeInfo>();
		foreach (var folder in folders.Where(Directory.Exists))
			foreach (var root in Directory.EnumerateDirectories(folder).Select(FxLayout.Normalize))
				if (WorktreeCard.Load(root) is { } card)
					result.Add(new(root, card, withStatus
						? card.Worktrees.ToDictionary(w => w.Path, w => GitClient.Discover(w.Path == "." ? root : FxLayout.Full(root, w.Path))?.Status().Changes ?? 0)
						: null));
		return result;
	}

	static WorktreeResult NotFoundResult(string directory, string branch)
		=> new(null, branch, [], [FxDiagnostic.Error(NotFound, new(NotFound, branch), fix: FxFix.Run("fx worktree list"))], []);

	/// <summary>
	/// <c>fx worktree mount &lt;branch&gt; &lt;repositories or paths…&gt; [--mandatory]</c>: more of the tree in the
	/// worktree. A repository by its name, its path or its full name (with the workspaces on the way to it); a path left
	/// out (<c>worktree: false</c>), brought back.
	/// </summary>
	public static WorktreeResult Mount(string directory, string branch, IReadOnlyList<string> targets, bool mandatory, IFxProgress? progress = null,
		CancellationToken cancellationToken = default)
	{
		if (Find(directory, branch) is not { } worktree)
			return NotFoundResult(directory, branch);
		var actions = new List<RepositoryAction>();
		var diagnostics = new List<FxDiagnostic>();
		var session = new Session(worktree.Root, worktree.Card, actions, diagnostics, progress, cancellationToken);
		var main = GitClient.Discover(worktree.Root)?.MainRoot() ?? worktree.Root;
		var declared = Declarations(main, new FxNode(main).SelfIdentity().FullName);
		var mounted = new List<string>();
		foreach (var target in targets)
		{
			var normalized = target.Replace('\\', '/').Trim('/');
			if (session.Card.LeftOut.FirstOrDefault(p => p.TrimEnd('/') == normalized) is { } leftOut)
			{
				session.BringBack(leftOut);
				continue;
			}
			if (Resolve(declared, target, diagnostics) is not { } path)
				continue;
			if (session.MountPath(path))
				mounted.Add(path);
		}
		if (mandatory)
			session.Mandatory(mounted.Where(p => WorkspaceManifest.IsWorkspace(FxLayout.Full(worktree.Root, p))).ToList());
		session.Save();
		return new(worktree.Root, branch, actions, diagnostics, SyncEngine.Sync(worktree.Root, new(), progress, cancellationToken));
	}

	/// <summary>A repository of the tree by its path, its full name or its name (if only one has it).</summary>
	static string? Resolve(IReadOnlyList<Declared> declared, string target, List<FxDiagnostic> diagnostics)
	{
		var normalized = target.Replace('\\', '/').TrimEnd('/');
		var found = declared.Where(d => d.Path == normalized || d.FullName == normalized).ToList();
		if (found.Count == 0)
			found = declared.Where(d => d.Repository.Name == normalized || (d.Repository.Aliases ?? []).Contains(normalized)).ToList();
		if (found.Count == 1)
			return found[0].Path;
		diagnostics.Add(FxDiagnostic.Error(UnknownTarget, found.Count == 0 ? new(UnknownTarget, target) : new($"{UnknownTarget}.ambiguous", target, string.Join(", ", found.Select(f => f.Path)))));
		return null;
	}

	/// <summary>
	/// <c>fx worktree unmount &lt;branch&gt; &lt;repositories…&gt;</c>: their worktrees out (and what is mounted inside
	/// them), not if any has uncommitted changes; the branches fx created, deleted if git deletes them safely.
	/// </summary>
	public static WorktreeResult Unmount(string directory, string branch, IReadOnlyList<string> targets, IFxProgress? progress = null, CancellationToken cancellationToken = default)
	{
		if (Find(directory, branch) is not { } worktree)
			return NotFoundResult(directory, branch);
		var actions = new List<RepositoryAction>();
		var diagnostics = new List<FxDiagnostic>();
		var mounts = new List<WorktreeMount>();
		foreach (var target in targets)
		{
			var normalized = target.Replace('\\', '/').Trim('/');
			var found = worktree.Card.Worktrees.Where(w => w.Path == normalized || w.Repository == normalized).ToList();
			if (found.Count != 1)
				diagnostics.Add(FxDiagnostic.Error(UnknownTarget, new(UnknownTarget, target)));
			else if (found[0].Path == ".")
				diagnostics.Add(FxDiagnostic.Error(RootUnmount, new(RootUnmount, branch), fix: FxFix.Run($"fx worktree remove {branch}")));
			else
				mounts.AddRange(worktree.Card.Worktrees.Where(w => w.Path == found[0].Path || w.Path.StartsWith(found[0].Path + "/", StringComparison.OrdinalIgnoreCase)));
		}
		if (diagnostics.Count > 0)
			return new(worktree.Root, branch, actions, diagnostics, []);
		var session = new Session(worktree.Root, worktree.Card, actions, diagnostics, progress, cancellationToken);
		session.Remove(mounts.Distinct().ToList());
		session.Save();
		return new(worktree.Root, branch, actions, diagnostics, SyncEngine.Sync(worktree.Root, new(), progress, cancellationToken));
	}

	/// <summary>
	/// <c>fx worktree remove &lt;branch&gt;</c>: the whole worktree, not if anything in it has uncommitted changes; the
	/// branches fx created, deleted if git deletes them safely (the others, never).
	/// </summary>
	public static WorktreeResult Remove(string directory, string branch, IFxProgress? progress = null, CancellationToken cancellationToken = default)
	{
		if (Find(directory, branch) is not { } worktree)
			return NotFoundResult(directory, branch);
		var actions = new List<RepositoryAction>();
		var diagnostics = new List<FxDiagnostic>();
		var session = new Session(worktree.Root, worktree.Card, actions, diagnostics, progress, cancellationToken);
		if (session.Remove(worktree.Card.Worktrees))
		{
			// what git leaves of it (fx's local files): the folder goes
			if (Directory.Exists(worktree.Root))
				Directory.Delete(worktree.Root, recursive: true);
		}
		else if (Directory.Exists(worktree.Root))
			session.Save();
		return new(worktree.Root, branch, actions, diagnostics, []);
	}

	/// <summary>
	/// In the doctor, of a repository (its main clone): its worktrees out of their place (fx did not make them; an agent
	/// or <c>git worktree add</c> did), and those git takes for lost.
	/// </summary>
	public static void Check(string repository, string name, Action<FxDiagnostic> report)
	{
		if (GitClient.Discover(repository) is not { } git || !FxLayout.SameFolder(git.Root, repository))
			return;
		IReadOnlyList<GitWorktree> worktrees;
		try
		{
			worktrees = git.Worktrees();
		}
		catch (Processes.ProcessException)
		{
			return;
		}
		foreach (var worktree in worktrees.Skip(1))
			if (worktree.Prunable)
				report(FxDiagnostic.Info(Prunable, new(Prunable, name, worktree.Path), repository: name, fix: FxFix.Run($"git -C {git.Root.TrimEnd('\\', '/')} worktree prune")));
			else if (!InItsPlace(worktree.Path))
				report(FxDiagnostic.Warning(Outside, new(Outside, name, worktree.Path, worktree.Branch ?? "-"), repository: name, fix: FxFix.Do($"{Outside}.fix")));
	}
}
