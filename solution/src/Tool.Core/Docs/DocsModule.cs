using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Fuxion.Tool.Core.Git;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Sync;
using Fuxion.Tool.Core.Worktree;

namespace Fuxion.Tool.Core.Docs;

/// <summary>
/// The module of the documentation of fx (plan U; its SPEC, <c>_docs/fx/SPEC-modulo-docs.md</c>): the catalog of a space,
/// by the documentation standard (<c>_docs/_governance/</c>). Each document is a pair, its Markdown and its sidecar
/// <c>.meta.json</c>, with its identity, its integrity and its relations; the module creates, checks, finalizes, adopts and
/// moves them, and generates the views. Its configuration is <c>_fx/docs/docs.yaml</c>; it does not go down: each space has
/// its own catalog. A space that mounts others leaves it out of its worktrees (I-58): what is not of a change does not go
/// with a branch, and from a worktree <c>fx doc</c> uses the catalog of the main tree, the one every branch shares.
/// </summary>
public static class DocsModule
{
	public const string Name = "docs";
	public const string NoCatalog = "docs.no-catalog";
	public const string CatalogInWorktrees = "docs.catalog-in-worktrees";
	public const string MainTreeCatalog = "docs.main-tree-catalog";

	public static string Folder => FxLayout.ModuleFolder(Name);
	public static string ConfigFile => FxLayout.ModuleFile(Name);

	/// <summary>
	/// The configuration of the space at <paramref name="root"/>: null if it has no catalog (no <c>_fx/docs/</c>, or
	/// <c>enabled: false</c>); without <c>docs.yaml</c>, the defaults. An invalid one throws a <see cref="DocsConfigException"/>.
	/// </summary>
	public static DocsConfig? Load(string root, ICollection<FxDiagnostic>? warnings = null)
	{
		if (!FxFiles.DirectoryExists(FxLayout.Full(root, Folder)))
			return null;
		var file = FxLayout.Full(root, ConfigFile);
		var config = FxFiles.Exists(file) ? DocsYamlReader.ReadFile(file, warnings) : DocsConfig.Default;
		return config.Enabled ? config : null;
	}

	/// <summary>
	/// The catalog of the commands run in <paramref name="directory"/>: that of the nearest root of fx that has one, from
	/// there up (a repository mounted in a space uses the one of the space, if it has none). In a worktree that leaves it
	/// out, that of its main tree (I-58), said in an info. None, with the problem in <paramref name="diagnostics"/>.
	/// </summary>
	public static DocCatalog? Find(string directory, ICollection<FxDiagnostic> diagnostics)
	{
		// a root ends with its separator: its parent is that of the folder without it
		for (var root = FxLayout.FindRoot(directory); root is not null;
		     root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root)) is { } parent ? FxLayout.FindRoot(parent) : null)
		{
			if (!TryLoad(root, diagnostics, out var config))
				return null;
			if (config is null)
				continue;
			// the main clone of the worktree, at its place or parked: its catalog, if it has one
			if (LeftOut(root, config) && GitClient.Discover(root)?.MainRoot() is { } main && !FxLayout.SameFolder(main, root))
			{
				if (!TryLoad(main, diagnostics, out var shared))
					return null;
				if (shared is not null)
				{
					var catalog = DocCatalog.Scan(main, shared, FxLayout.Full(root, config.Catalog));
					diagnostics.Add(FxDiagnostic.Info(MainTreeCatalog, new(MainTreeCatalog, catalog.Folder)));
					return catalog;
				}
			}
			return DocCatalog.Scan(root, config);
		}
		diagnostics.Add(FxDiagnostic.Error(NoCatalog, new(NoCatalog), fix: FxFix.Run($"fx module enable {Name}")));
		return null;
	}

	/// <summary><see cref="Load"/>, its problems in <paramref name="diagnostics"/>: false if <c>docs.yaml</c> is invalid.</summary>
	static bool TryLoad(string root, ICollection<FxDiagnostic> diagnostics, out DocsConfig? config)
	{
		try
		{
			config = Load(root, diagnostics);
			return true;
		}
		catch (DocsConfigException ex)
		{
			foreach (var d in ex.Diagnostics)
				diagnostics.Add(d);
			config = null;
			return false;
		}
	}

	/// <summary>
	/// Whether the root of fx at <paramref name="root"/> is in a worktree of fx that leaves its catalog out (its card, a
	/// path with <c>worktree: false</c>; I-58): there the catalog is that of the main tree. Not one brought back with
	/// <c>fx worktree mount</c>.
	/// </summary>
	static bool LeftOut(string root, DocsConfig config)
	{
		if (WorktreeCard.FindRoot(root) is not { } worktree || WorktreeCard.Load(worktree) is not { } card)
			return false;
		var relative = FxLayout.Relative(worktree, root).TrimEnd('/');
		return card.LeavesOut(relative is "." or "" ? config.Catalog : $"{relative}/{config.Catalog}");
	}

	/// <summary>
	/// The entry of <c>paths</c> that versions the catalog of <paramref name="node"/>, if it is a space that mounts others and
	/// does not leave it out of its worktrees (no <c>worktree: false</c>): each worktree would carry a copy of the catalog,
	/// on its branch (I-58). Null if it leaves it out, or does not version it.
	/// </summary>
	static SpacePath? InWorktrees(FxNode node, DocsConfig config)
	{
		if (!node.HasMounts)
			return null;
		var space = node.OwnConfig();
		var folder = $"{config.Catalog}/";
		return space.Entry(folder) is { Exclude: false } entry && !space.LeavesOut(folder) ? entry : null;
	}

	/// <summary>
	/// The catalog of <paramref name="node"/>, if it has one: in <c>fx doctor</c>, its check; in <c>fx sync</c>, its views
	/// generated (the SPEC of the module, 11.3). In a worktree that leaves it out, nothing: it is that of the main tree.
	/// </summary>
	public static FxModuleResult? Run(FxNode node, string? repository, SyncContext context)
	{
		if (!node.HasModule(Name))
			return null;
		var diagnostics = new FxDiagnostics(context.Progress, repository);
		string Display(string path) => FxLayout.Relative(context.DisplayRoot, path);
		var warnings = new List<FxDiagnostic>();
		DocsConfig? config = null;
		try
		{
			config = Load(node.Root, warnings);
		}
		catch (DocsConfigException ex)
		{
			warnings.AddRange(ex.Diagnostics);
		}
		diagnostics.AddRange(warnings.Select(d => d with { File = d.File is null ? null : Display(d.File) }));
		// nothing to check and no views to make: fx doc uses the catalog of the main tree
		if (config is not null && LeftOut(node.Root, config))
			config = null;
		if (config is null && diagnostics.Count == 0)
			return null;
		var files = new List<SyncFile>();
		if (config is not null)
			context.Progress.Step(Name, repository, context.Doctor ? "check" : "views", () =>
			{
				var catalog = DocCatalog.Scan(node.Root, config);
				if (context.Doctor)
				{
					// a catalog in every worktree, on the branch of each (I-58): out of them, with the command that does it
					if (InWorktrees(node, config) is { } entry)
						diagnostics.Add(FxDiagnostic.Warning(CatalogInWorktrees, new(CatalogInWorktrees, $"{config.Catalog}/"), Display(node.Full(FxLayout.ConfigFile)),
							entry.Line > 0 ? entry.Line : null, fix: FxFix.Run($"fx path add {config.Catalog}/ --no-worktree")));
					diagnostics.AddRange(DocChecker.Check(catalog, Display).Diagnostics);
				}
				else
					Views(catalog, context, files, diagnostics, Display);
				return true;
			});
		return new(Name, context.DisplayRoot, repository, files, [], diagnostics.ToList()) { Node = node.Root };
	}

	/// <summary>
	/// <c>fx sync</c>: each view of the catalog as its recipe makes it, finalized now; the same, untouched. One edited by
	/// hand is not replaced, unless <c>--adopt</c> (the fx way: never overwrite what is not its own without asking).
	/// </summary>
	static void Views(DocCatalog catalog, SyncContext context, List<SyncFile> files, FxDiagnostics diagnostics, Func<string, string> display)
	{
		var documents = DocChecker.Read(catalog);
		var now = DateTimeOffset.UtcNow;
		foreach (var view in documents.Where(d => d.Type.Name == "view" && d.Sidecar.Generation is not null))
		{
			var markdown = view.File.Markdown!;
			var expected = DocViews.Generate(catalog, documents, view.Sidecar.Title, view.Sidecar.Generation!, markdown, out var problem);
			if (problem is not null)
			{
				diagnostics.Add(FxDiagnostic.Error(problem.Key.StartsWith(DocViews.UnknownRecipe, StringComparison.Ordinal) ? DocViews.UnknownRecipe : DocViews.InvalidScope,
					problem, display(view.File.Sidecar!)));
				continue;
			}
			string? current;
			try
			{
				current = DocIntegrity.Normalize(FxFiles.ReadAllBytes(markdown));
			}
			catch (DecoderFallbackException)
			{
				current = null;
			}
			if (current == expected)
			{
				files.Add(new(display(markdown), SyncFileStatus.Unchanged));
				continue;
			}
			if ((current is null || DocIntegrity.Content(current) != view.Sidecar.Integrity!.Content) && !context.Adopt)
			{
				files.Add(new(display(markdown), SyncFileStatus.Refused));
				diagnostics.Add(FxDiagnostic.Error(DocChecker.ViewEdited, new(DocChecker.ViewEdited, view.File.Path), display(markdown), fix: FxFix.Run("fx sync --adopt")));
				continue;
			}
			// never before it was created, whatever the clock says
			var updated = DocSidecar.Instant(now > DocSidecar.ParseInstant(view.Sidecar.CreatedAt) ? now : DocSidecar.ParseInstant(view.Sidecar.CreatedAt));
			var sidecar = (view.Sidecar with { UpdatedAt = updated }).Seal(DocIntegrity.Content(expected!));
			var status = context.Write ? SyncFileStatus.Written : SyncFileStatus.WouldWrite;
			if (context.Write)
			{
				// its pair, all or nothing, as every change of the catalog
				var change = new DocChange(catalog, display);
				change.Set(markdown, expected!);
				change.Set(view.File.Sidecar!, sidecar.ToJson());
				if (!change.Write())
				{
					diagnostics.AddRange(change.Diagnostics);
					status = SyncFileStatus.Refused;
				}
			}
			files.Add(new(display(markdown), status));
			files.Add(new(display(view.File.Sidecar!), status));
		}
	}
}
