using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Fuxion.Tools.Core.Dotnet;
using Fuxion.Tools.Core.Git;
using Fuxion.Tools.Core.Layout;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Workspace;

namespace Fuxion.Tools.Core.Sync;

/// <summary>
/// <c>fx sync</c> and <c>fx doctor</c> (plan P, decision 17): on the root of fx where they run and everything mounted
/// below, never above. Each root first gets what it inherits from its parent (one level up, read-only), then the
/// workspace mounts its repositories and each one does the same; the solutions are composed on the way back, from the
/// bottom up. It never commits, never pushes, never overwrites a file without the header.
/// </summary>
public static class SyncEngine
{
	public const string Requires = "fx.requires";
	public const string StandaloneReference = "dotnet.standalone-reference";

	public static IReadOnlyList<FxModuleResult> Sync(string directory, SyncOptions options, IFxProgress? progress = null, CancellationToken cancellationToken = default)
		=> Run(directory, options, doctor: false, progress, cancellationToken);

	public static IReadOnlyList<FxModuleResult> Doctor(string directory, SyncOptions options, IFxProgress? progress = null, CancellationToken cancellationToken = default)
		=> Run(directory, options with { DryRun = true }, doctor: true, progress, cancellationToken);

	public const string TooDeep = "workspace.too-deep";

	/// <summary>How many workspaces above a repository Fuxion.Tools.Sdk looks at (its <c>Sdk.props</c>).</summary>
	const int MaxDepth = 5;

	/// <summary>The parent of a root: on disk, or read from its remote (then, the branch read).</summary>
	sealed record Parent(FxNode Node, FxIdentity Identity, WorkspaceRepository Declaration, string? Ref = null);

	sealed class Run_(FxNode start, SyncContext context)
	{
		public FxNode Start => start;
		public SyncContext Context => context;
		public List<FxModuleResult> Results { get; } = [];
		public List<(FxNode Node, FxIdentity Identity, DotnetConfig? Own)> Mounted { get; } = [];
	}

	static IReadOnlyList<FxModuleResult> Run(string directory, SyncOptions options, bool doctor, IFxProgress? progress, CancellationToken cancellationToken)
	{
		if (FxNode.Find(directory) is not { } start)
			return [];
		cancellationToken.ThrowIfCancellationRequested();
		var write = !options.DryRun;
		// a run that does not write leaves what it would write in a layer of its own (over the one in place, if any):
		// each level reads what the one above would leave, as it would read it once written (FxFiles.Stage)
		using var staged = write ? null : FxFiles.Overlay([]);
		var first = new FxDiagnostics(progress);

		// a fx older than the one this tree requires reads, but writes nothing (decision 4)
		var found = new List<FxDiagnostic>();
		var config = start.Config(found);
		first.AddRange(found);
		if (options.FxVersion is { } version && config.RequiresNewer(version))
		{
			first.Add(FxDiagnostic.Error(Requires, new(Requires, config.Requires, version), FxLayout.ConfigFile, fix: FxFix.Do($"{Requires}.fix")));
			write = false;
		}
		var context = new SyncContext(start.Root, write, options.Adopt, doctor, options.Offline, progress, cancellationToken)
		{
			Only = options.Module,
			OnlyHere = options.OnlyHere
		};
		var run = new Run_(start, context);
		Process(run, start, FindParent(start, options, progress, first, cancellationToken), first);

		// The map of FxReference: only the highest workspace on disk makes it (decision 19), from all it has mounted; in a
		// worktree, its root (a closed tree, decision 28)
		if (!options.OnlyHere && start.IsWorkspace && start.HasModule(DotnetModule.Name) && context.Runs(DotnetModule.Name)
		    && WorkspaceCopies.Top(start.Root) is { } top && FxLayout.SameFolder(top, start.Root))
		{
			var diagnostics = new FxDiagnostics(progress);
			var outputs = new FxOutputs(start.Root, write, options.Adopt, diagnostics);
			var projects = WorkspacePropsGenerator.Projects(run.Mounted.Select(m => (m.Node, m.Identity)), diagnostics);
			outputs.Generated(start.Full(WorkspacePropsGenerator.FileName), WorkspacePropsGenerator.Generate(projects));
			if (doctor)
				CheckStandalone(run, projects, diagnostics);
			run.Results.Add(new(DotnetModule.Name, start.Root, null, outputs.Files, [], diagnostics.ToList()) { Node = start.Root });
		}
		return run.Results;
	}

	/// <summary>
	/// The parent of the root where the run starts: the workspace on disk that declares it; if there is none (a clone on
	/// its own) or <c>--parent-ref</c> asks for a branch, the one its <c>inherited.yaml</c> names, read from its remote.
	/// Its declaration there is found by the URL of the repository (or by its name, or an old one).
	/// </summary>
	static Parent? FindParent(FxNode start, SyncOptions options, IFxProgress? progress, FxDiagnostics diagnostics, CancellationToken cancellationToken)
	{
		var found = start.FindParent();
		Parent? onDisk = found is { } f ? new(f.Workspace, f.Workspace.SelfIdentity(), f.Declaration) : null;
		if (options.ParentRef is null && onDisk is not null)
			return onDisk;
		// from its remote: its main branch, or the one asked for
		if (start.Inherited() is not { Parent.Url: { } url } inherited)
			return onDisk;
		if (RemoteParent.Read(url, options.ParentRef, options.Offline, progress, diagnostics, cancellationToken) is not { } remote)
			return onDisk;
		var file = remote.Node.Full(WorkspaceManifest.RelativePath);
		var manifest = WorkspaceManifest.Read(remote.Node.Root, FxFiles.Exists(file) ? FxFiles.ReadAllText(file) : "");
		var origin = GitClient.Discover(start.Root)?.RemoteUrl("origin");
		var declaration = manifest.Repositories.FirstOrDefault(r => origin is not null && WorkspaceModule.SameUrl(r.Url, origin))
		                  ?? manifest.Repositories.FirstOrDefault(r => r.Name == inherited.Repository.Name || (r.Aliases ?? []).Contains(inherited.Repository.Name));
		if (declaration is null)
		{
			diagnostics.Add(FxDiagnostic.Warning(RemoteParent.NotDeclared, new(RemoteParent.NotDeclared, inherited.Parent.FullName, inherited.Repository.Name)));
			return null;
		}
		return new(remote.Node, remote.Node.SelfIdentity(), declaration, remote.Branch);
	}

	static void Process(Run_ run, FxNode node, Parent? parent, FxDiagnostics? first = null)
	{
		var context = run.Context;
		context.CancellationToken.ThrowIfCancellationRequested();
		var isStart = node == run.Start;
		// who it is: as its parent declares it (its old names too, with the old names of the parent); a root nobody
		// declares, by its own name
		var found = new List<FxDiagnostic>();
		FxIdentity identity;
		if (parent is null)
			identity = node.SelfIdentity(found);
		else
		{
			var fullName = $"{parent.Identity.FullName}/{parent.Declaration.Name}";
			var aliases = new[] { parent.Identity.FullName }.Concat(parent.Identity.Aliases ?? [])
				.SelectMany(p => new[] { parent.Declaration.Name }.Concat(parent.Declaration.Aliases ?? []).Select(n => $"{p}/{n}"))
				.Where(a => a != fullName)
				.Distinct()
				.ToList();
			identity = new(parent.Declaration.Name, fullName, parent.Declaration.Tags, aliases);
		}
		var label = isStart && node.IsWorkspace ? null : identity.Name;
		var diagnostics = first ?? new FxDiagnostics(context.Progress, label);
		diagnostics.AddRange(found);
		var outputs = new FxOutputs(context.DisplayRoot, context.Write, context.Adopt, diagnostics);
		var actions = new List<RepositoryAction>();
		var at = run.Results.Count;
		run.Results.Add(null!); // the workspace result goes before the results of what is below

		// Fuxion.Tools.Sdk looks for the highest workspace up to five levels above a repository
		if (context.Doctor && identity.FullName.Count(c => c == '/') - 1 > MaxDepth)
			diagnostics.Add(FxDiagnostic.Error(TooDeep, new(TooDeep, identity.FullName, MaxDepth)));

		// 1. What it inherits from its parent (without one at hand, the shares it has stay)
		var shared = parent is not null
			? Inheritance.Run(parent.Node, parent.Identity, node, identity, outputs, diagnostics, parent.Ref)
			: node.Inherited()?.Shares.Select(s => s.Destination).ToList() ?? [];

		// 2. A workspace: its repositories, each with the same sync
		WorkspaceManifest? manifest = null;
		if (node.IsWorkspace)
		{
			try
			{
				manifest = WorkspaceManifest.Load(node.Root);
			}
			catch (WorkspaceManifestException ex)
			{
				diagnostics.AddRange(ex.Diagnostics.Select(d => d with { File = d.File is null ? null : outputs.Display(node.Full(d.File)) }));
			}
			if (manifest is not null)
			{
				var mounted = context.Runs(WorkspaceModule.Name)
					? WorkspaceModule.Mount(manifest, context, diagnostics, actions)
					: WorkspaceModule.Repositories(manifest, withStatus: false, copies: WorkspaceCopies.Survey(manifest.Root))
						.Where(r => r.Mounted && r.Present && r.SupersededBy is null)
						.Select(r => r.Repository)
						.ToList();
				// with --only, this level alone
				if (!context.OnlyHere)
					foreach (var repository in mounted)
						if (repository.Inherits)
							Process(run, new FxNode(manifest.FullPath(repository)), new Parent(node, identity, repository));
						else
							Guest(run, manifest, identity, repository);
			}
		}

		// 3. Its .NET (its solutions, now that those below are composed)
		DotnetConfig? own = null;
		if (context.Runs(DotnetModule.Name))
		{
			var dotnet = DotnetModule.Run(node, identity, label, context);
			if (dotnet is not null)
				run.Results.Add(dotnet);
			try
			{
				own = DotnetModule.Configuration(node, identity, [])?.Own;
			}
			catch (DotnetConfigException)
			{
				// already reported by the module
			}
		}
		run.Mounted.Add((node, identity, own));

		// 4. What the workspace generates: its .gitignore, which versions the solutions and the shares it receives
		if (manifest is not null && context.Runs(WorkspaceModule.Name))
			WorkspaceModule.Outputs(node, identity, manifest, node.Config(), (own?.Solutions ?? []).Select(s => s.Path), shared, context, outputs, diagnostics);

		// in the doctor, what sync would change is out of date
		if (context.Doctor)
			foreach (var file in outputs.Files.Where(f => f.Status == SyncFileStatus.WouldWrite))
				diagnostics.Add(FxDiagnostic.Error(WorkspaceModule.Outdated, new(WorkspaceModule.Outdated), file.Path, fix: FxFix.Run(FxHeader.Sync)));
		if (parent is not null || node.IsWorkspace || diagnostics.Count > 0)
			run.Results[at] = new(WorkspaceModule.Name, context.DisplayRoot, label, outputs.Files, actions, diagnostics.ToList()) { Node = node.Root };
		else
			run.Results.RemoveAt(at);
	}

	/// <summary>
	/// A guest (decision 18): the workspace uses it, so its projects go into the map, but it inherits from its own parent
	/// and nothing of it is processed here. Its own .NET configuration is read, for the doctor.
	/// </summary>
	static void Guest(Run_ run, WorkspaceManifest manifest, FxIdentity workspace, WorkspaceRepository repository)
	{
		var node = new FxNode(manifest.FullPath(repository));
		var identity = new FxIdentity(repository.Name, $"{workspace.FullName}/{repository.Name}", repository.Tags);
		DotnetConfig? own = null;
		try
		{
			own = DotnetModule.Configuration(node, identity, [])?.Own;
		}
		catch (DotnetConfigException)
		{
			// its own sync reports it
		}
		run.Mounted.Add((node, identity, own));
	}

	/// <summary>
	/// A standalone repository (it builds without the workspace, design D-2) cannot reference a project of a repository
	/// that is not: outside the workspace that reference would have to be a package, and that repository publishes
	/// none (design §6). <c>WorkspaceOnly="true"</c> references only exist in the workspace by definition.
	/// </summary>
	static void CheckStandalone(Run_ run, IReadOnlyDictionary<string, (string Path, string Repository)> projects, FxDiagnostics diagnostics)
	{
		// whether the repository of a project is standalone: the deepest mounted root that contains it (names can repeat in a tree)
		bool? Standalone(string project) => run.Mounted
			.Where(m => project.StartsWith(m.Node.Root, StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(m => m.Node.Root.Length)
			.Select(m => (bool?)(m.Own?.Standalone ?? false))
			.FirstOrDefault();
		foreach (var (node, identity, own) in run.Mounted.Where(m => m.Own?.Standalone == true))
		{
			if (GitClient.Discover(node.Root) is not { } git || !FxLayout.SameFolder(git.Root, node.Root))
				continue;
			foreach (var file in git.ListFiles(":(glob)**/*.csproj", ":(glob)**/*.props", ":(glob)**/*.targets"))
				foreach (var (reference, line) in FxReferences(Path.Combine(git.Root, file)))
					if (projects.TryGetValue(reference, out var target) && Standalone(target.Path) == false)
						diagnostics.Add(FxDiagnostic.Error(StandaloneReference, new(StandaloneReference, identity.Name, reference, target.Repository),
							FxLayout.Relative(run.Start.Root, Path.Combine(git.Root, file)), line, identity.Name, FxFix.Do($"{StandaloneReference}.fix")));
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
}
