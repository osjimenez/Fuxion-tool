using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Yaml;

namespace Fuxion.Tool.Core.Docs;

/// <summary>A register that uses short keys (the SPEC of the module, 5.7): its prefix and the provider that reserves them.</summary>
/// <param name="Path">The register, relative to the catalog.</param>
/// <param name="Provider"><c>repo</c> (the next free one in the catalog) or <c>git</c> (an atomic reservation on the remote).</param>
public sealed record DocsKeyedRegister(string Path, string Prefix, string Provider, int Line);

/// <summary>
/// <c>_fx/docs/docs.yaml</c>, the module of the documentation of a space (plan U): where its catalog is, who may be the
/// actor of a change, and the registers that use short keys. A space without the folder <c>_fx/docs/</c> has no catalog;
/// without the file, the defaults.
/// </summary>
public sealed record DocsConfig(string Catalog, IReadOnlyList<string> Actors, IReadOnlyList<DocsKeyedRegister> Registers)
{
	public const string DefaultCatalog = "_docs";

	public bool Enabled { get; init; } = true;

	/// <summary>
	/// The documents of the bootstrap regime (the standard, DESIGN §12): Markdown without a sidecar yet, relative to the
	/// catalog, that the module adopts and takes out of the list. Any other Markdown without its sidecar is an error.
	/// </summary>
	public IReadOnlyList<string> Bootstrap { get; init; } = [];

	public static DocsConfig Default { get; } = new(DefaultCatalog, [], []);
}

/// <summary>An invalid <c>docs.yaml</c>: every problem found, each with its line.</summary>
public sealed class DocsConfigException(IReadOnlyList<FxDiagnostic> diagnostics)
	: Exception(string.Join(Environment.NewLine, diagnostics.Select(d => d.WhereIn(CultureInfo.InvariantCulture))))
{
	public IReadOnlyList<FxDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>Reads <c>docs.yaml</c>: its warnings go to the list of <see cref="Read"/>; its errors, in a <see cref="DocsConfigException"/>.</summary>
public static partial class DocsYamlReader
{
	public const string InvalidYaml = "docs.invalid-yaml";

	public static readonly IReadOnlyList<string> Providers = ["repo", "git"];

	/// <summary>An actor, as the metadata model of the standard names them (<c>createdBy</c>).</summary>
	[GeneratedRegex("^[a-z][a-z0-9]*(?:[._-][a-z0-9]+)*$")]
	public static partial Regex ActorPattern();

	[GeneratedRegex("^[A-Z][A-Z0-9]*$")]
	private static partial Regex PrefixPattern();

	public static DocsConfig ReadFile(string path, ICollection<FxDiagnostic>? warnings = null) => Read(FxFiles.ReadAllText(path), path, warnings);

	public static DocsConfig Read(string yaml, string source, ICollection<FxDiagnostic>? warnings = null)
	{
		var reader = new FxYamlReader(InvalidYaml, source);
		var map = reader.Load(yaml);
		if (map is null)
			throw new DocsConfigException(reader.Diagnostics);
		reader.Keys(map, "schema", "enabled", "catalog", "actors", "registers", "bootstrap");

		var written = FxYamlReader.Scalar(map["catalog"]);
		var catalog = written is null ? DocsConfig.DefaultCatalog : Relative(written) ?? written;
		if (written is not null && (Relative(written) is null || catalog.Split('/')[0] == FxLayout.Folder || catalog.Split('/').Any(FxLayout.IsLocal)))
			reader.Error(map["catalog"], "catalog", written);

		if (map["actors"] is YamlMapping notAListOfActors)
			reader.Error(notAListOfActors, "list", "actors");
		var actors = FxYamlReader.List(map["actors"]);
		foreach (var actor in actors.Where(a => a.Length > 64 || !ActorPattern().IsMatch(a)))
			reader.Error(map["actors"], "actor", actor);
		foreach (var duplicate in actors.GroupBy(a => a).Where(g => g.Count() > 1))
			reader.Error(map["actors"], "duplicate-actor", duplicate.Key);

		var registers = new List<DocsKeyedRegister>();
		if (map["registers"] is YamlSequence list)
			foreach (var item in list.Items)
			{
				if (item is not YamlMapping entry || FxYamlReader.Scalar(entry["path"]) is not { Length: > 0 } path)
				{
					reader.Error(item, "register-entry");
					continue;
				}
				reader.Keys(entry, "path", "prefix", "provider");
				var prefix = FxYamlReader.Scalar(entry["prefix"]) ?? "";
				if (!PrefixPattern().IsMatch(prefix))
					reader.Error(entry["prefix"] ?? item, "prefix", path, prefix);
				var provider = FxYamlReader.Scalar(entry["provider"]) ?? "repo";
				if (!Providers.Contains(provider))
					reader.Error(entry["provider"], "provider", path, provider, string.Join(", ", Providers));
				if (Relative(path) is not { } relative || relative.Split('/').Any(FxLayout.IsLocal))
					reader.Error(entry["path"], "register-path", path);
				registers.Add(new(Relative(path) ?? path, prefix, provider, item.Line));
			}
		else if (map["registers"] is { } notAList && notAList is not YamlScalar { Value: "" })
			reader.Error(notAList, "list", "registers");
		foreach (var duplicate in registers.GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			reader.Diagnostics.Add(FxDiagnostic.Error(InvalidYaml, new($"{InvalidYaml}.duplicate-register", duplicate.Key), source, duplicate.Last().Line));
		foreach (var duplicate in registers.GroupBy(r => r.Prefix, StringComparer.Ordinal).Where(g => g.Key.Length > 0 && g.Count() > 1))
			reader.Diagnostics.Add(FxDiagnostic.Error(InvalidYaml, new($"{InvalidYaml}.duplicate-prefix", duplicate.Key), source, duplicate.Last().Line));

		if (map["bootstrap"] is YamlMapping notAListOfDocuments)
			reader.Error(notAListOfDocuments, "list", "bootstrap");
		var bootstrap = FxYamlReader.List(map["bootstrap"]).Select(b => Relative(b) ?? b).ToList();
		foreach (var document in bootstrap.Where(b => Relative(b) is null || !b.EndsWith(".md", StringComparison.Ordinal)))
			reader.Error(map["bootstrap"], "bootstrap", document);
		foreach (var duplicate in bootstrap.GroupBy(b => b, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
			reader.Error(map["bootstrap"], "duplicate-bootstrap", duplicate.Key);

		var enabled = reader.Enabled(map);

		if (reader.HasErrors)
			throw new DocsConfigException(reader.Diagnostics);
		foreach (var warning in reader.Diagnostics)
			warnings?.Add(warning);
		return new(catalog, actors, registers) { Enabled = enabled, Bootstrap = bootstrap };
	}

	/// <summary>
	/// <paramref name="path"/> as a folder relative to its base, the same on every system: with '/', without a root, a
	/// drive, '.' or '..'; null if it is not one.
	/// </summary>
	static string? Relative(string path)
	{
		var relative = path.Replace('\\', '/').TrimEnd('/');
		return relative.Length == 0 || relative.StartsWith('/') || relative.Contains(':') || relative.Split('/').Any(s => s is "" or "." or "..") ? null : relative;
	}
}
