using System.Collections.Generic;
using System.IO;
using System.Linq;
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

	/// <summary><c>fx doctor</c>: the check of the catalog of <paramref name="node"/>, if it has one.</summary>
	public static FxModuleResult? Run(FxNode node, string? repository, SyncContext context)
	{
		if (!context.Doctor || !node.HasModule(Name))
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
		if (config is not null)
			context.Progress.Step(Name, repository, "check", () =>
			{
				diagnostics.AddRange(DocChecker.Check(DocCatalog.Scan(node.Root, config), Display).Diagnostics);
				return true;
			});
		return new(Name, context.DisplayRoot, repository, [], [], diagnostics.ToList()) { Node = node.Root };
	}
}
