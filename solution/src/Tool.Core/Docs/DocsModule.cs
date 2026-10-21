using System.Collections.Generic;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;

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
}
