using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fuxion.Tool.Core.Dotnet;
using Fuxion.Tool.Core.Harness;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Mounts;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Sync;
using Fuxion.Tool.Core.Worktree;
using Fuxion.Tool.Core.Yaml;

namespace Fuxion.Tool.Core.Explore;

/// <summary>Why something reaches a repository, or does not (decision 14.4).</summary>
public enum FxReasonKind
{
	/// <summary>It is for everyone (no <c>applies-to</c>).</summary>
	Everyone,

	/// <summary>By one of its tags (<see cref="FxReason.Values"/>).</summary>
	Tag,

	/// <summary>By its name (<see cref="FxReason.Values"/>, full names).</summary>
	Name,

	/// <summary>A workspace in between: it always receives what comes from above, to pass it on.</summary>
	Conduit,

	/// <summary>Not for it: <see cref="FxReason.Values"/> say for whom (tags and names).</summary>
	NotForIt
}

public sealed record FxReason(FxReasonKind Kind, IReadOnlyList<string> Values);

/// <summary>Something that reaches a repository, or not, and why: a module, an import, a variable, a package or a share.</summary>
/// <param name="What"><c>module</c>, <c>import</c>, <c>variable</c>, <c>package</c> or <c>share</c>.</param>
public sealed record FxApplied(string What, string Name, bool Applies, FxReason Reason, IReadOnlyList<string> From);

/// <summary>A tag of the catalog (plan P, decision 21): whether this root defines it or inherits it, and who has it.</summary>
public sealed record FxTagInfo(FxTag Tag, bool Own, IReadOnlyList<string> From, IReadOnlyList<string> UsedBy);

/// <summary>
/// The tree of <c>fx ui</c> (plan O, decision 14), read from disk and from the manifests, never written: from the highest
/// workspace on disk (in a worktree, its root; decision 14.11). A workspace has its <c>_fx/</c>, what it versions, what
/// it generates, its repositories (mounted, declared and not on disk, parked) and what nobody declares; a repository,
/// its <c>_fx/</c>, the shares it receives and its solutions, and the rest of it on demand (14.6). What reaches each one
/// and why (14.4), the catalog of tags (14.13) and the diagnostics of the doctor on their nodes (14.2).
/// </summary>
public static class FxTree
{
	/// <summary>The tree that <paramref name="directory"/> is in; null outside any root of fx.</summary>
	public static FxTreeNode? Build(string directory)
	{
		var top = MountCopies.Top(directory) ?? FxNode.Find(directory)?.Root;
		if (top is null)
			return null;
		var node = new FxNode(top);
		var identity = node.SelfIdentity();
		return node.HasMounts
			? Workspace(node, identity, null, MountCopies.Survey(top), null)
			: Repository(node, identity, null, true, null);
	}

	static string Clean(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

	static FxTreeNode Workspace(FxNode node, FxIdentity identity, Mount? declaration, MountCopies copies, RepositoryCopy? superseded)
	{
		var tree = new FxTreeNode
		{
			Kind = FxTreeKind.Space,
			Name = declaration?.Name ?? identity.Name,
			Path = Clean(node.Root),
			Identity = identity,
			Declaration = declaration,
			Mounted = true,
			SupersededBy = superseded,
			Worktrees = WorktreeModule.Of(node.Root)
		};
		var inherited = node.Inherited();
		tree.Add(Fx(node, inherited));
		var file = node.Full(MountsManifest.RelativePath);
		var manifest = MountsManifest.Read(node.Root, FxFiles.Exists(file) ? FxFiles.ReadAllText(file) : "");

		// what it versions (the paths of its space.yaml) and what it generates and versions (the .gitignore, its solutions)
		var space = node.OwnConfig();
		foreach (var path in (space.Paths ?? []).Where(p => !p.Exclude))
		{
			var full = node.Full(path.Path);
			if (path.IsFolder ? FxFiles.DirectoryExists(full) : FxFiles.Exists(full))
				Place(tree, path.Path.TrimEnd('/'), path.IsFolder
					? new FxTreeNode { Kind = FxTreeKind.Folder, Name = Last(path.Path), Path = Clean(full), HasMore = true }
					: FileNode(full, inherited));
		}
		var solutions = Solutions(node);
		foreach (var generated in solutions.Prepend(GitignoreGenerator.FileName).Select(node.Full).Where(File.Exists))
			Place(tree, FxLayout.Relative(node.Root, generated), FileNode(generated, inherited));
		// and the files of the coding agents (plan Q), where the paths have not put them already
		foreach (var agent in AgentFiles(node).Where(f => tree.Find(f)?.Path != Clean(f)))
			Place(tree, FxLayout.Relative(node.Root, agent), FileNode(agent, inherited));

		// its repositories, where they go
		foreach (var state in MountsModule.Repositories(manifest, withStatus: false, copies: copies))
		{
			var repo = state.Repository;
			var childIdentity = new FxIdentity(repo.Name, $"{identity.FullName}/{repo.Name}", repo.Tags, repo.Aliases);
			FxTreeNode child;
			if (state.Present)
			{
				var sub = new FxNode(state.FullPath);
				child = repo.Inherits && sub.HasMounts
					? Workspace(sub, childIdentity, repo, copies, state.SupersededBy)
					: Repository(sub, childIdentity, repo, state.Mounted, state.SupersededBy);
			}
			else
				child = new FxTreeNode
				{
					Kind = state.Parked ? FxTreeKind.Parked : FxTreeKind.Declared,
					Name = repo.Name,
					Path = Clean(state.FullPath),
					Identity = childIdentity,
					Declaration = repo,
					Mounted = state.Mounted,
					SupersededBy = state.SupersededBy
				};
			Place(tree, repo.Path, child);
		}

		// what is on disk and nobody declares
		foreach (var (relative, repository) in MountsModule.Strangers(node.Root, manifest, space))
			Place(tree, relative.TrimEnd('/'), new FxTreeNode { Kind = FxTreeKind.Unknown, Name = Last(relative), Path = Clean(node.Full(relative)), HasMore = !repository });
		SortAll(tree);
		return tree;
	}

	static FxTreeNode Repository(FxNode node, FxIdentity identity, Mount? declaration, bool mounted, RepositoryCopy? superseded)
	{
		var tree = new FxTreeNode
		{
			Kind = FxTreeKind.Repository,
			Name = declaration?.Name ?? identity.Name,
			Path = Clean(node.Root),
			Identity = identity,
			Declaration = declaration,
			Mounted = mounted,
			SupersededBy = superseded,
			HasMore = true
		};
		var inherited = node.Inherited();
		if (FxFiles.DirectoryExists(node.Full(FxLayout.Folder)))
			tree.Add(Fx(node, inherited));
		foreach (var share in inherited?.Shares ?? [])
			if (FxFiles.Exists(node.Full(share.Destination)))
				Place(tree, share.Destination, FileNode(node.Full(share.Destination), inherited));
		foreach (var solution in FxFiles.EnumerateFiles(node.Root, "*.slnx").Concat(FxFiles.EnumerateFiles(node.Root, "*.sln")))
			Place(tree, Path.GetFileName(solution), FileNode(solution, inherited));
		foreach (var agent in AgentFiles(node))
			Place(tree, FxLayout.Relative(node.Root, agent), FileNode(agent, inherited));
		SortAll(tree);
		return tree;
	}

	/// <summary><c>_fx/</c>: its <c>space.yaml</c>, its modules (on, off, and the ones fx knows it does not use), what it inherits and what is local.</summary>
	static FxTreeNode Fx(FxNode node, FxInherited? inherited)
	{
		var folder = node.Full(FxLayout.Folder);
		var fx = new FxTreeNode { Kind = FxTreeKind.Folder, Name = FxLayout.Folder, Path = Clean(folder) };
		if (!FxFiles.DirectoryExists(folder))
			return fx;
		var modules = ManifestEditor.KnownModules
			.Concat(FxFiles.EnumerateDirectories(folder).Select(Path.GetFileName).OfType<string>().Where(n => !n.StartsWith('.') && !FxLayout.IsLocal(n)))
			.Distinct(StringComparer.OrdinalIgnoreCase);
		foreach (var name in modules)
		{
			var path = node.Full(FxLayout.ModuleFolder(name));
			if (!FxFiles.DirectoryExists(path))
			{
				fx.Add(new FxTreeNode { Kind = FxTreeKind.ModuleOff, Name = name, Path = Clean(path) });
				continue;
			}
			var module = fx.Add(new FxTreeNode
			{
				Kind = FxTreeKind.Module,
				Name = name,
				Path = Clean(path),
				Enabled = name == MountsManifest.ModuleName ? MountsManifest.HasMounts(node.Root) : Enabled(node.Full(FxLayout.ModuleFile(name)))
			});
			AddFolder(module, path, inherited, FxOrigin.Hand);
		}
		var inheritedFolder = node.Full(FxLayout.Inherited);
		if (FxFiles.DirectoryExists(inheritedFolder))
			AddFolder(fx.Add(new FxTreeNode { Kind = FxTreeKind.Folder, Name = ".inherited", Path = Clean(inheritedFolder), Origin = FxOrigin.Inherited }), inheritedFolder, inherited,
				FxOrigin.Inherited);
		foreach (var file in FxFiles.EnumerateFiles(folder))
			fx.Add(FileNode(file, inherited));
		foreach (var local in FxFiles.EnumerateDirectories(folder).Where(d => FxLayout.IsLocal(Path.GetFileName(d))))
			fx.Add(new FxTreeNode { Kind = FxTreeKind.Folder, Name = Path.GetFileName(local), Path = Clean(local), Origin = FxOrigin.Local, HasMore = true });
		SortAll(fx);
		return fx;
	}

	/// <summary>Everything in <paramref name="path"/>, under <paramref name="parent"/>: what is under <c>.inherited/</c> is inherited.</summary>
	static void AddFolder(FxTreeNode parent, string path, FxInherited? inherited, FxOrigin folders)
	{
		foreach (var file in FxFiles.EnumerateFiles(path))
			parent.Add(FileNode(file, inherited));
		foreach (var sub in FxFiles.EnumerateDirectories(path))
		{
			var local = FxLayout.IsLocal(Path.GetFileName(sub));
			var child = parent.Add(new FxTreeNode
			{
				Kind = FxTreeKind.Folder,
				Name = Path.GetFileName(sub),
				Path = Clean(sub),
				Origin = local ? FxOrigin.Local : folders,
				HasMore = local
			});
			if (!local)
				AddFolder(child, sub, inherited, folders);
		}
	}

	/// <summary>A file, and whose it is: local, inherited (and from where), a share received, generated (its header), or by hand.</summary>
	static FxTreeNode FileNode(string path, FxInherited? inherited)
	{
		var full = Clean(path);
		var name = Path.GetFileName(full);
		var origin = FxOrigin.Hand;
		IReadOnlyList<string> from = [];
		var root = FindRootOf(full);
		var relative = root is null ? null : FxLayout.Relative(root, full);
		if (full.Split(Path.DirectorySeparatorChar).Any(FxLayout.IsLocal))
			origin = FxOrigin.Local;
		else if (relative is not null && relative.StartsWith(FxLayout.Inherited + "/", StringComparison.Ordinal))
		{
			origin = FxOrigin.Inherited;
			var inside = relative[(FxLayout.Inherited.Length + 1)..];
			from = inherited?.Files.FirstOrDefault(f => string.Equals(f.Path, inside, StringComparison.OrdinalIgnoreCase))?.From ?? [];
		}
		else if (relative is not null && inherited?.Shares.FirstOrDefault(s => string.Equals(s.Destination, relative, StringComparison.OrdinalIgnoreCase)) is { } share)
		{
			origin = FxOrigin.Share;
			from = [share.From];
		}
		else if (Generated(full))
			origin = FxOrigin.Generated;
		return new FxTreeNode { Kind = FxTreeKind.File, Name = name, Path = full, Origin = origin, From = from };
	}

	/// <summary>The root of fx a file is in: the nearest folder above it with <c>_fx/</c> or a git repository.</summary>
	static string? FindRootOf(string file) => FxLayout.FindRoot(Path.GetDirectoryName(file)!);

	/// <summary>
	/// Where a file of fx comes from (decision 14.3), the nearest first, and only what is on disk: an inherited file, from
	/// the <c>_fx/</c> of the workspaces it comes from (a share kept on its way down, from its source); a share, from its
	/// source in the workspace that gives it; a generated file, from the yaml it is made of; <c>inherited.yaml</c>, from
	/// the manifest that declares the root. Nothing for a file by hand or local.
	/// </summary>
	public static IReadOnlyList<string> Origins(FxTreeNode tree, FxTreeNode file)
	{
		var result = new List<string>();
		if (FindRootOf(file.Path) is not { } root)
			return result;
		var relative = FxLayout.Relative(root, file.Path);
		void Add(string? path)
		{
			if (path is not null && FxFiles.Exists(path) && !result.Contains(Clean(path), StringComparer.OrdinalIgnoreCase))
				result.Add(Clean(path));
		}
		FxTreeNode? Named(string fullName)
			=> tree.Descendants().Prepend(tree).FirstOrDefault(n => n.Kind == FxTreeKind.Space && n.Identity?.FullName == fullName);
		void Shared(FxTreeNode workspace, string destination)
		{
			foreach (var share in SpaceConfig.Load(workspace.Path).Shares ?? [])
				if (SpaceShares.Source(share, destination) is { } source)
					Add(FxLayout.Full(workspace.Path, source));
		}
		switch (file.Origin)
		{
			case FxOrigin.Inherited:
				var inside = relative[(FxLayout.Inherited.Length + 1)..];
				// inherited.yaml: the root as its workspace declares it
				if (relative == FxLayout.InheritedFile)
				{
					for (var at = tree.Find(root)?.Parent; at is not null; at = at.Parent)
						if (at.Kind == FxTreeKind.Space)
						{
							Add(FxLayout.Full(at.Path, MountsManifest.RelativePath));
							break;
						}
					break;
				}
				foreach (var name in file.From.Reverse())
					if (Named(name) is { } workspace)
					{
						// the shares a space in between passes on: the space.yaml of the one that shares them
						Add(FxLayout.Full(workspace.Path, inside == $"{SpaceShares.Folder}/{SpaceShares.Folder}.yaml" ? FxLayout.ConfigFile : $"{FxLayout.Folder}/{inside}"));
						if (inside.StartsWith($"{SpaceShares.Folder}/", StringComparison.Ordinal))
							Shared(workspace, inside[(SpaceShares.Folder.Length + 1)..]);
					}
				break;
			case FxOrigin.Share:
				foreach (var name in file.From)
					if (Named(name) is { } workspace)
						Shared(workspace, relative);
				break;
			case FxOrigin.Generated:
				var parts = relative.Split('/');
				if (parts.Length > 2 && parts[0] == FxLayout.Folder)
				{
					Add(FxLayout.Full(root, FxLayout.ModuleFile(parts[1])));
					Add(FxLayout.Full(root, $"{FxLayout.InheritedModuleFolder(parts[1])}/{parts[1]}.yaml"));
				}
				else if (relative == ".gitignore")
					Add(FxLayout.Full(root, MountsManifest.RelativePath));
				// the files of the coding agents: the harness of the root, and what it inherits
				else if (HarnessModule.Files.Contains(relative) || HarnessModule.Folders.Any(f => relative.StartsWith(f + "/", StringComparison.Ordinal)))
				{
					Add(FxLayout.Full(root, HarnessModule.ConfigFile));
					Add(FxLayout.Full(root, HarnessModule.InheritedConfigFile));
				}
				else if (relative.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
					Add(FxLayout.Full(root, FxLayout.ModuleFile(DotnetModule.Name)));
				break;
		}
		return result;
	}

	static bool Generated(string path)
	{
		if (Path.GetFileName(path).Contains(".g.", StringComparison.OrdinalIgnoreCase))
			return true;
		try
		{
			using var reader = new StreamReader(path);
			var buffer = new char[512];
			var read = reader.Read(buffer, 0, buffer.Length);
			return FxHeader.IsGenerated(new string(buffer, 0, read));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	static bool Enabled(string file)
	{
		if (!FxFiles.Exists(file))
			return true;
		try
		{
			return YamlDocument.Load(FxFiles.ReadAllText(file)) is not YamlMapping map || map["enabled"] is not YamlScalar { Value: "false" };
		}
		catch (YamlSyntaxException)
		{
			return true;
		}
	}

	/// <summary>The solutions a root composes (its <c>dotnet.yaml</c>), relative to it.</summary>
	static IReadOnlyList<string> Solutions(FxNode node)
	{
		var file = node.Full(FxLayout.ModuleFile(DotnetModule.Name));
		try
		{
			return FxFiles.Exists(file) ? DotnetYamlReader.ReadFile(file).Solutions.Select(s => s.Path).ToList() : [];
		}
		catch (DotnetConfigException)
		{
			return [];
		}
	}

	static string Last(string relative) => relative.TrimEnd('/').Split('/').Last();

	/// <summary>Puts <paramref name="child"/> at <paramref name="relative"/> under <paramref name="parent"/>, with the folders on the way.</summary>
	/// <summary>The files of the coding agents a root has (plan Q): <c>AGENTS.md</c>, <c>CLAUDE.md</c>, the rules for Claude and the <c>SKILL.md</c> of each skill.</summary>
	static IEnumerable<string> AgentFiles(FxNode node)
	{
		foreach (var file in HarnessModule.Files.Select(node.Full).Where(FxFiles.Exists))
			yield return file;
		var rules = node.Full(HarnessModule.ClaudeRules);
		if (FxFiles.DirectoryExists(rules))
			foreach (var file in FxFiles.EnumerateFiles(rules, "*.md"))
				yield return file;
		foreach (var root in HarnessModule.SkillRoots.Select(node.Full).Where(FxFiles.DirectoryExists))
			foreach (var skill in FxFiles.EnumerateDirectories(root))
				if (FxFiles.Exists(Path.Combine(skill, HarnessModule.SkillFile)))
					yield return Path.Combine(skill, HarnessModule.SkillFile);
	}

	static void Place(FxTreeNode parent, string relative, FxTreeNode child)
	{
		var segments = relative.Replace('\\', '/').Trim('/').Split('/');
		var at = parent;
		foreach (var segment in segments[..^1])
			at = at.Children.FirstOrDefault(c => c.Kind is FxTreeKind.Folder && string.Equals(c.Name, segment, StringComparison.OrdinalIgnoreCase))
			     ?? at.Add(new FxTreeNode { Kind = FxTreeKind.Folder, Name = segment, Path = Clean(Path.Combine(at.Path, segment)) });
		// a folder of the paths that is also where repositories are: one node
		if (child.Kind == FxTreeKind.Folder && at.Children.FirstOrDefault(c => c.Kind == FxTreeKind.Folder && string.Equals(c.Name, child.Name, StringComparison.OrdinalIgnoreCase)) is { } existing)
		{
			existing.HasMore |= child.HasMore;
			return;
		}
		at.Add(child);
	}

	static void SortAll(FxTreeNode node)
	{
		node.Sort();
		foreach (var child in node.Children.Where(c => c.Kind == FxTreeKind.Folder))
			SortAll(child);
	}

	/// <summary>
	/// The rest of a folder or a repository, what does not matter to fx (decision 14.6): its folders and files that the tree
	/// does not have yet, each with whose it is, but <c>.git</c>.
	/// </summary>
	public static void LoadMore(FxTreeNode node)
	{
		if (!node.HasMore || !FxFiles.DirectoryExists(node.Path))
			return;
		node.HasMore = false;
		var root = FxLayout.FindRoot(node.Path);
		var inherited = root is null ? null : FxInherited.Load(root);
		var known = node.Children.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var sub in FxFiles.EnumerateDirectories(node.Path).Where(d => Path.GetFileName(d) != ".git" && !known.Contains(Clean(d))))
			node.Add(new FxTreeNode
			{
				Kind = FxTreeKind.Folder,
				Name = Path.GetFileName(sub),
				Path = Clean(sub),
				Origin = FxLayout.IsLocal(Path.GetFileName(sub)) ? FxOrigin.Local : FxOrigin.Hand,
				HasMore = true
			});
		foreach (var file in FxFiles.EnumerateFiles(node.Path).Where(f => !known.Contains(Clean(f))))
			node.Add(FileNode(file, inherited));
		node.Sort();
	}

	// what reaches each node, and why (decision 14.4)

	/// <summary>Why <paramref name="appliesTo"/> reaches <paramref name="who"/>, or not.</summary>
	public static FxReason Explain(FxAppliesTo appliesTo, FxIdentity who)
		=> appliesTo.IsAll ? new(FxReasonKind.Everyone, [])
			: appliesTo.Tags.Where(t => who.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList() is { Count: > 0 } tags ? new(FxReasonKind.Tag, tags)
			: appliesTo.Repositories.Where(who.Is).ToList() is { Count: > 0 } names ? new(FxReasonKind.Name, names)
			: new(FxReasonKind.NotForIt, [.. appliesTo.Tags, .. appliesTo.Repositories]);

	/// <summary>
	/// What reaches a repository or a workspace from above and from its own <c>_fx/</c>: the .NET module and its imports,
	/// variables and packages, the harness and its rules and skills (each one, if it is for it, and why), and the shares it
	/// receives.
	/// </summary>
	public static IReadOnlyList<FxApplied> Applies(FxTreeNode repository)
	{
		var result = new List<FxApplied>();
		if (repository.Identity is not { } identity || repository.Kind is not (FxTreeKind.Repository or FxTreeKind.Space))
			return result;
		var node = new FxNode(repository.Path);
		var inherited = node.Inherited();
		var conduit = repository.Kind == FxTreeKind.Space;
		if (Dotnet(node, identity) is { } dotnet)
		{
			var from = inherited?.Files.FirstOrDefault(f => f.Path == $"{DotnetModule.Name}/{DotnetModule.Name}.yaml")?.From ?? [];
			var module = dotnet.AppliesTo ?? FxAppliesTo.All;
			var reason = conduit && !module.Matches(identity) ? new FxReason(FxReasonKind.Conduit, []) : Explain(module, identity);
			result.Add(new("module", DotnetModule.Name, conduit || module.Matches(identity), reason, from));
			foreach (var import in dotnet.Imports)
				result.Add(new("import", string.Join(", ", import.Files), import.AppliesTo.Matches(identity), Explain(import.AppliesTo, identity), from));
			foreach (var variable in dotnet.Variables)
				result.Add(new("variable", variable.Name, variable.AppliesTo.Matches(identity), Explain(variable.AppliesTo, identity), from));
			foreach (var package in dotnet.Packages)
				result.Add(new("package", string.Join(", ", package.Ids), package.AppliesTo.Matches(identity), Explain(package.AppliesTo, identity), from));
		}
		// the harness (plan Q): fx's own rule and skill, everywhere; then the module and each rule and skill
		result.Add(new("rule", HarnessModule.Reserved, true, new(FxReasonKind.Everyone, []), ["fx"]));
		result.Add(new("skill", HarnessModule.Reserved, true, new(FxReasonKind.Everyone, []), ["fx"]));
		if (Harness(node, identity) is { } harness)
		{
			IReadOnlyList<string> From(string @base, string relative)
				=> @base == HarnessModule.InheritedFolder
					? inherited?.Files.FirstOrDefault(f => f.Path == $"{HarnessModule.Name}/{relative}")?.From ?? []
					: [identity.FullName];
			var module = harness.AppliesTo ?? FxAppliesTo.All;
			var reason = conduit && !module.Matches(identity) ? new FxReason(FxReasonKind.Conduit, []) : Explain(module, identity);
			result.Add(new("module", HarnessModule.Name, conduit || module.Matches(identity), reason,
				inherited?.Files.FirstOrDefault(f => f.Path == $"{HarnessModule.Name}/{HarnessModule.Name}.yaml")?.From ?? [identity.FullName]));
			foreach (var rule in harness.Rules)
				result.Add(new("rule", rule.ByPath ? $"{rule.Name} ({string.Join(", ", rule.Paths)})" : rule.Name, rule.AppliesTo.Matches(identity),
					Explain(rule.AppliesTo, identity), From(rule.Base, $"{HarnessModule.RulesFolder}/{rule.Name}.md")));
			foreach (var skill in harness.Skills)
				result.Add(new("skill", skill.Name, skill.AppliesTo.Matches(identity), Explain(skill.AppliesTo, identity),
					From(skill.Base, $"{HarnessModule.SkillsFolder}/{skill.Name}/{HarnessModule.SkillFile}")));
		}
		foreach (var share in inherited?.Shares ?? [])
			result.Add(new("share", share.Destination, true, new(FxReasonKind.Everyone, []), [share.From]));
		return result;
	}

	/// <summary>The harness of a root: what it inherits with its own on top, every rule and skill (each one says whom it is for); null without it.</summary>
	static HarnessConfig? Harness(FxNode node, FxIdentity identity)
	{
		var inheritedYaml = node.Full(HarnessModule.InheritedConfigFile);
		var ownYaml = node.Full(HarnessModule.ConfigFile);
		try
		{
			var inherited = FxFiles.Exists(inheritedYaml) ? HarnessYamlReader.ReadFile(inheritedYaml) : null;
			var own = FxFiles.Exists(ownYaml) ? HarnessYamlReader.ReadFile(ownYaml).Absolute(identity.FullName) : null;
			if (inherited is null && own is null)
				return null;
			return HarnessConfig.Combine((inherited ?? HarnessConfig.Empty).WithBase(HarnessModule.InheritedFolder),
				(own is { Enabled: true } ? own : HarnessConfig.Empty).WithBase(HarnessModule.Folder));
		}
		catch (HarnessConfigException)
		{
			return null;
		}
	}

	/// <summary>Whom the harness of <paramref name="workspace"/> reaches, as a module (its <c>applies-to</c>; everyone, without it).</summary>
	public static FxAppliesTo HarnessAppliesTo(FxTreeNode workspace)
		=> workspace.Identity is { } identity && Harness(new FxNode(workspace.Path), identity) is { } harness ? harness.AppliesTo ?? FxAppliesTo.All : FxAppliesTo.All;

	/// <summary>The .NET of a root: what it inherits with its own on top (its own names made absolute from it); null without it.</summary>
	static DotnetConfig? Dotnet(FxNode node, FxIdentity identity)
	{
		var inheritedYaml = node.Full($"{FxLayout.InheritedModuleFolder(DotnetModule.Name)}/{DotnetModule.Name}.yaml");
		var ownYaml = node.Full(FxLayout.ModuleFile(DotnetModule.Name));
		try
		{
			var inherited = FxFiles.Exists(inheritedYaml) ? DotnetYamlReader.ReadFile(inheritedYaml) : null;
			var own = FxFiles.Exists(ownYaml) ? DotnetYamlReader.ReadFile(ownYaml).Absolute(identity.FullName) : null;
			if (inherited is null && own is null)
				return null;
			return DotnetConfig.Combine(inherited ?? DotnetConfig.Empty, own is { Enabled: true } ? own : DotnetConfig.Empty);
		}
		catch (DotnetConfigException)
		{
			return null;
		}
	}

	/// <summary>
	/// Whom something of <paramref name="workspace"/> reaches (<paramref name="appliesTo"/>, absolute): every repository
	/// declared below it, at any depth, and why; the workspaces in between always receive, to pass it on.
	/// </summary>
	public static IReadOnlyList<(FxTreeNode Repository, bool Applies, FxReason Reason)> Reach(FxTreeNode workspace, FxAppliesTo appliesTo)
		=> workspace.Descendants().Where(n => n.Declaration is not null && n.Identity is not null && !n.Guest)
			.Select(n => n.Kind == FxTreeKind.Space && !appliesTo.Matches(n.Identity!)
				? (n, true, new FxReason(FxReasonKind.Conduit, []))
				: (n, appliesTo.Matches(n.Identity!), Explain(appliesTo, n.Identity!)))
			.ToList();

	/// <summary>The <c>applies-to</c> of the .NET module of a workspace, as it reaches what is below it (absolute).</summary>
	public static FxAppliesTo DotnetAppliesTo(FxTreeNode workspace)
		=> workspace.Identity is { } identity && Dotnet(new FxNode(workspace.Path), identity) is { } dotnet ? dotnet.AppliesTo ?? FxAppliesTo.All : FxAppliesTo.All;

	/// <summary>The catalog of tags of a root (decision 14.13): its own and what it inherits, and the repositories below that have each.</summary>
	public static IReadOnlyList<FxTagInfo> Tags(FxTreeNode root)
	{
		var node = new FxNode(root.Path);
		var own = node.OwnConfig();
		var all = node.Config();
		var from = node.Inherited()?.Files.FirstOrDefault(f => f.Path == FxLayout.ConfigName)?.From ?? [];
		var repositories = root.Descendants().Where(n => n.Declaration is not null).ToList();
		return all.Tags.Select(tag =>
		{
			var isOwn = own.Knows(tag.Name);
			return new FxTagInfo(tag, isOwn, isOwn ? [root.Identity?.FullName ?? root.Name] : from,
				repositories.Where(r => r.Declaration!.Tags.Contains(tag.Name, StringComparer.OrdinalIgnoreCase)).Select(r => r.Identity?.FullName ?? r.Name).ToList());
		}).ToList();
	}

	// the doctor, on the tree (decision 14.2)

	/// <summary>
	/// Puts each diagnostic of <paramref name="results"/> on its node: the file it names, if any; else the repository it is
	/// about, among those its workspace declares; else the root the module ran on.
	/// </summary>
	public static void Attach(FxTreeNode tree, IEnumerable<FxModuleResult> results)
	{
		foreach (var result in results)
		{
			var at = result.Node is { } node ? tree.Find(node) ?? tree : tree;
			foreach (var diagnostic in result.Diagnostics)
			{
				var target = at;
				if (diagnostic.File is { } file)
					target = tree.Find(Path.Combine(result.Root, file)) ?? at;
				else if (diagnostic.Repository is { } name && at.Kind == FxTreeKind.Space)
					target = at.Descendants().FirstOrDefault(n => n.Declaration?.Name == name && n.Identity?.FullName == $"{at.Identity?.FullName}/{name}") ?? at;
				target.Diagnostics.Add(diagnostic);
			}
		}
	}
}
