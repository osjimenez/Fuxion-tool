using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Sync;

namespace Fuxion.Tool.Core.Docs;

/// <summary>
/// The module of the documentation of fx (plan U; its SPEC, <c>_docs/fx/SPEC-modulo-docs.md</c>): the catalog of a space,
/// by the documentation standard (<c>_docs/_governance/</c>). Each document is a pair, its Markdown and its sidecar
/// <c>.meta.json</c>, with its identity, its integrity and its relations; the module creates, checks, finalizes, adopts and
/// moves them, and generates the views. Its configuration is <c>_fx/docs/docs.yaml</c>; it does not go down: each space has
/// its own catalog.
/// </summary>
public static class DocsModule
{
	public const string Name = "docs";
	public const string NoCatalog = "docs.no-catalog";

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
	/// there up (a repository mounted in a space uses the one of the space, if it has none). None, with the problem in
	/// <paramref name="diagnostics"/>.
	/// </summary>
	public static DocCatalog? Find(string directory, ICollection<FxDiagnostic> diagnostics)
	{
		// a root ends with its separator: its parent is that of the folder without it
		for (var root = FxLayout.FindRoot(directory); root is not null;
		     root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root)) is { } parent ? FxLayout.FindRoot(parent) : null)
		{
			DocsConfig? config;
			try
			{
				config = Load(root, diagnostics);
			}
			catch (DocsConfigException ex)
			{
				foreach (var d in ex.Diagnostics)
					diagnostics.Add(d);
				return null;
			}
			if (config is not null)
				return DocCatalog.Scan(root, config);
		}
		diagnostics.Add(FxDiagnostic.Error(NoCatalog, new(NoCatalog), fix: FxFix.Run($"fx module enable {Name}")));
		return null;
	}

	/// <summary>
	/// The catalog of <paramref name="node"/>, if it has one: in <c>fx doctor</c>, its check; in <c>fx sync</c>, its views
	/// generated (the SPEC of the module, 11.3).
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
		if (config is null && diagnostics.Count == 0)
			return null;
		var files = new List<SyncFile>();
		if (config is not null)
			context.Progress.Step(Name, repository, context.Doctor ? "check" : "views", () =>
			{
				var catalog = DocCatalog.Scan(node.Root, config);
				if (context.Doctor)
					diagnostics.AddRange(DocChecker.Check(catalog, Display).Diagnostics);
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
				File.WriteAllText(markdown, expected, new UTF8Encoding(false));
				File.WriteAllText(view.File.Sidecar!, sidecar.ToJson(), new UTF8Encoding(false));
			}
			files.Add(new(display(markdown), status));
			files.Add(new(display(view.File.Sidecar!), status));
		}
	}
}
