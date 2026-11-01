using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Yaml;

namespace Fuxion.Tool.Core.Docs;

/// <summary>A register that uses short keys: its prefix and the provider that reserves them.</summary>
/// <param name="Path">The register, relative to the catalog.</param>
/// <param name="Provider"><c>repo</c> (the next free one in the catalog) or <c>git</c> (an atomic reservation on the remote).</param>
public sealed record DocsKeyedRegister(string Path, string Prefix, string Provider, int Line);

/// <summary>
/// <c>_fx/docs/docs.yaml</c>, the module of the documentation of a space: where its catalog is, who may be the
/// actor of a change, and the registers that use short keys. A space without the folder <c>_fx/docs/</c> has no catalog;
/// without the file, the defaults.
/// </summary>
public sealed record DocsConfig(string Catalog, IReadOnlyList<string> Actors, IReadOnlyList<DocsKeyedRegister> Registers)
{
	public const string DefaultCatalog = "_docs";

	public bool Enabled { get; init; } = true;

	/// <summary>
	/// The documents of the bootstrap regime of the standard: Markdown without a sidecar yet, relative to the
	/// catalog, that the module adopts and takes out of the list. Any other Markdown without its sidecar is an error.
	/// </summary>
	public IReadOnlyList<string> Bootstrap { get; init; } = [];

	/// <summary>
	/// The language of what fx writes in the catalog (the views, the note of a correction), whoever runs it: the same bytes
	/// on every machine. One of those of fx, <c>en</c> by default.
	/// </summary>
	public string Language { get; init; } = DefaultLanguage;

	/// <summary>The culture of <see cref="Language"/>, to write the texts of fx in it.</summary>
	public CultureInfo Culture => Language == DefaultLanguage ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(Language);

	public const string DefaultLanguage = "en";

	public static readonly IReadOnlyList<string> Languages = ["en", "es"];

	public static DocsConfig Default { get; } = new(DefaultCatalog, [], []);
}

/// <summary>
/// Reads <c>docs.yaml</c>: the configuration with its warnings, or an error (<see cref="HttpStatusCode.Conflict"/>) with every
/// problem found, each with its line.
/// </summary>
public static partial class DocsYamlReader
{
	public const string InvalidYaml = "docs.invalid-yaml";

	public static readonly IReadOnlyList<string> Providers = ["repo", "git"];

	/// <summary>An actor, as the metadata model of the standard names them (<c>createdBy</c>).</summary>
	[GeneratedRegex("^[a-z][a-z0-9]*(?:[._-][a-z0-9]+)*$")]
	public static partial Regex ActorPattern();

	[GeneratedRegex("^[A-Z][A-Z0-9]*$")]
	private static partial Regex PrefixPattern();

	public static Response<DocsConfig> ReadFile(string path) => Read(FxFiles.ReadAllText(path), path);

	public static Response<DocsConfig> Read(string yaml, string source)
	{
		var reader = new FxYamlReader(InvalidYaml, source);
		if (reader.Load(yaml) is not YamlMapping map)
			return reader.Fail(HttpStatusCode.Conflict);
		reader.Keys(map, "schema", "enabled", "catalog", "actors", "registers", "bootstrap", "language");

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
		var language = FxYamlReader.Scalar(map["language"]) ?? DocsConfig.DefaultLanguage;
		// the language is checked once the rest is valid
		if (!reader.HasErrors && !DocsConfig.Languages.Contains(language))
			reader.Error(map["language"], "language", language, string.Join(", ", DocsConfig.Languages));
		return reader.Outcome(new DocsConfig(catalog, actors, registers) { Enabled = enabled, Bootstrap = bootstrap, Language = language }, HttpStatusCode.Conflict);
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
