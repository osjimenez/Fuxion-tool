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

	sealed record Parent(FxNode Node, FxIdentity Identity, WorkspaceRepository Declaration);

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
		var context = new SyncContext(start.Root, write, options.Adopt, doctor, options.Offline, progress, cancellationToken) { Only = options.Module };
		var run = new Run_(start, context);
		var parent = start.FindParent();
		Process(run, start, parent is { } p ? new Parent(p.Workspace, p.Workspace.SelfIdentity(), p.Declaration) : null, first);

		// The map of FxReference: only the highest workspace on disk makes it (decision 19)
		if (start.IsWorkspace && start.HasModule(DotnetModule.Name) && context.Runs(DotnetModule.Name)
		    && WorkspaceManifest.FindRoot(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(start.Root))!) is null)
		{
			var diagnostics = new FxDiagnostics(progress);
			var outputs = new FxOutputs(start.Root, write, options.Adopt, diagnostics);
			var projects = WorkspacePropsGenerator.Projects(run.Mounted.Select(m => (m.Node, m.Identity)), diagnostics);
			outputs.Generated(start.Full(WorkspacePropsGenerator.FileName), WorkspacePropsGenerator.Generate(projects));
			if (doctor)
				CheckStandalone(run, projects, diagnostics);
			run.Results.Add(new(DotnetModule.Name, start.Root, null, outputs.Files, [], diagnostics.ToList()));
		}
		return run.Results;
	}

	static void Process(Run_ run, FxNode node, Parent? parent, FxDiagnostics? first = null)
	{
		var context = run.Context;
		context.CancellationToken.ThrowIfCancellationRequested();
		var isStart = node == run.Start;
		// who it is: as its parent declares it; a root nobody declares, by its own name
		var found = new List<FxDiagnostic>();
		var identity = parent is null
			? node.SelfIdentity(found)
			: new FxIdentity(parent.Declaration.Name, $"{parent.Identity.FullName}/{parent.Declaration.Name}", parent.Declaration.Tags,
				(parent.Declaration.Aliases ?? []).Select(a => $"{parent.Identity.FullName}/{a}").ToList());
		var label = isStart && node.IsWorkspace ? null : identity.Name;
		var diagnostics = first ?? new FxDiagnostics(context.Progress, label);
		diagnostics.AddRange(found);
		var outputs = new FxOutputs(context.DisplayRoot, context.Write, context.Adopt, diagnostics);
		var actions = new List<RepositoryAction>();
		var at = run.Results.Count;
		run.Results.Add(null!); // the workspace result goes before the results of what is below

		// 1. What it inherits from its parent
		if (parent is not null)
			Inheritance.Run(parent.Node, parent.Identity, node, identity, outputs, diagnostics);

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
					: WorkspaceModule.Repositories(manifest, withStatus: false).Where(r => r.Mounted && r.Present).Select(r => r.Repository).ToList();
				foreach (var repository in mounted.Where(r => r.Inherits))
					Process(run, new FxNode(manifest.FullPath(repository)), new Parent(node, identity, repository));
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

		// 4. What the workspace generates: its .gitignore, which versions the solutions
		if (manifest is not null && context.Runs(WorkspaceModule.Name))
			WorkspaceModule.Outputs(node, manifest, node.Config(), (own?.Solutions ?? []).Select(s => s.Path), context, outputs, diagnostics);

		// in the doctor, what sync would change is out of date
		if (context.Doctor)
			foreach (var file in outputs.Files.Where(f => f.Status == SyncFileStatus.WouldWrite))
				diagnostics.Add(FxDiagnostic.Error(WorkspaceModule.Outdated, new(WorkspaceModule.Outdated), file.Path, fix: FxFix.Run(FxHeader.Sync)));
		if (parent is not null || node.IsWorkspace || diagnostics.Count > 0)
			run.Results[at] = new(WorkspaceModule.Name, context.DisplayRoot, label, outputs.Files, actions, diagnostics.ToList());
		else
			run.Results.RemoveAt(at);
	}

	/// <summary>
	/// A standalone repository (it builds without the workspace, design D-2) cannot reference a project of a repository
	/// that is not: outside the workspace that reference would have to be a package, and that repository publishes
	/// none (design §6). <c>WorkspaceOnly="true"</c> references only exist in the workspace by definition.
	/// </summary>
	static void CheckStandalone(Run_ run, IReadOnlyDictionary<string, (string Path, string Repository)> projects, FxDiagnostics diagnostics)
	{
		var standalone = run.Mounted.ToDictionary(m => m.Identity.Name, m => m.Own?.Standalone ?? false, StringComparer.OrdinalIgnoreCase);
		foreach (var (node, identity, own) in run.Mounted.Where(m => m.Own?.Standalone == true))
		{
			if (GitClient.Discover(node.Root) is not { } git || !FxLayout.SameFolder(git.Root, node.Root))
				continue;
			foreach (var file in git.ListFiles(":(glob)**/*.csproj", ":(glob)**/*.props", ":(glob)**/*.targets"))
				foreach (var (reference, line) in FxReferences(Path.Combine(git.Root, file)))
					if (projects.TryGetValue(reference, out var target) && standalone.TryGetValue(target.Repository, out var isStandalone) && !isStandalone)
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
