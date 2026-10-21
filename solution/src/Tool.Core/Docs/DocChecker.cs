using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;

namespace Fuxion.Tool.Core.Docs;

/// <summary>A document of the catalog that could be read: its files and its sidecar, valid by its schema.</summary>
public sealed record DocDocument(DocFile File, DocSidecar Sidecar)
{
	public DocType Type => DocTypes.Find(Sidecar.Type)!;

	public DocStatusClass Class => Type.ClassOf(Sidecar.Status);
}

/// <summary>What a check found: the documents it could read, and the problems.</summary>
public sealed record DocReport(IReadOnlyList<DocDocument> Documents, IReadOnlyList<FxDiagnostic> Diagnostics);

/// <summary>
/// The validator of the module (the SPEC of the module, 5.2): one for <c>fx doc check</c>, <c>fx doctor</c> and every
/// command that writes. Of a document: its sidecar against its schema, its name and its place for its type, its title,
/// its dates, its relations and its integrity; of the catalog: identities, pairs, slugs, links and the bootstrap list.
/// </summary>
public static partial class DocChecker
{
	public const string InvalidMetadata = "docs.invalid-metadata";
	public const string InvalidType = "docs.invalid-type";
	public const string InvalidProfile = "docs.invalid-profile";
	public const string InvalidStatus = "docs.invalid-status";
	public const string InvalidDates = "docs.invalid-dates";
	public const string InvalidName = "docs.invalid-name";
	public const string InvalidLocation = "docs.invalid-location";
	public const string InvalidEncoding = "docs.invalid-encoding";
	public const string TitleMismatch = "docs.title-mismatch";
	public const string UnknownActor = "docs.unknown-actor";
	public const string MissingSidecar = "docs.missing-sidecar";
	public const string OrphanSidecar = "docs.orphan-sidecar";
	public const string DuplicateId = "docs.duplicate-id";
	public const string InvalidRelation = "docs.invalid-relation";
	public const string MissingTarget = "docs.missing-target";
	public const string BrokenLink = "docs.broken-link";
	public const string Unfinalized = "docs.unfinalized";
	public const string IntegrityMismatch = "docs.integrity-mismatch";
	public const string FrozenChanged = "docs.frozen-changed";
	public const string ViewEdited = "docs.view-edited";
	public const string BootstrapPending = "docs.bootstrap-pending";
	public const string BootstrapStale = "docs.bootstrap-stale";

	/// <summary>The folder of the historical documents, by the year and the month of their creation.</summary>
	public const string Records = "_records";

	[GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
	public static partial Regex SlugPattern();

	/// <summary>
	/// Checks the catalog, or only <paramref name="only"/> (with what of the catalog concerns them: their identities,
	/// their slugs, their relations). The files of the diagnostics, as <paramref name="display"/> shows them.
	/// </summary>
	public static DocReport Check(DocCatalog catalog, Func<string, string> display, IReadOnlyCollection<DocFile>? only = null)
		=> new Run(catalog, display, only).Check();

	/// <summary>The documents of the catalog that can be read, without checking the rest (<c>fx doc list</c>, <c>show</c>).</summary>
	public static IReadOnlyList<DocDocument> Read(DocCatalog catalog)
	{
		var documents = new List<DocDocument>();
		foreach (var file in catalog.Files.Where(f => f is { Markdown: not null, Sidecar: not null }))
			if (ReadSidecar(file.Sidecar!) is { Json: { } json } && DocsSchemas.Validate(json).Count == 0)
				documents.Add(new(file, DocSidecar.From(json)));
		return documents;
	}

	/// <summary>The JSON of the sidecar at <paramref name="path"/>; null if it cannot be read.</summary>
	internal static JsonElement? Json(string path) => ReadSidecar(path).Json;

	/// <summary>A sidecar read: its JSON and its text, or the problem that keeps it from being read.</summary>
	sealed record Sidecar(JsonElement? Json, string? Text, FxText? Problem, string? Code = null, int? Line = null);

	static Sidecar ReadSidecar(string path)
	{
		string text;
		try
		{
			text = DocIntegrity.Utf8.GetString(FxFiles.ReadAllBytes(path));
		}
		catch (DecoderFallbackException)
		{
			return new(null, null, new($"{InvalidEncoding}.sidecar"), InvalidEncoding);
		}
		if (text.StartsWith('﻿'))
			text = text[1..];
		try
		{
			using var json = DocSidecar.Parse(text);
			return new(json.RootElement.Clone(), text, null);
		}
		catch (JsonException ex)
		{
			return new(null, text, new($"{InvalidMetadata}.json", ex.Message), InvalidMetadata, ex.LineNumber is { } line ? (int)line + 1 : null);
		}
	}

	/// <summary>The code of a problem of the schema, by the property it is in.</summary>
	static string CodeOf(string pointer) => pointer.Split('/', 3).ElementAtOrDefault(1) switch
	{
		"type" => InvalidType,
		"profile" => InvalidProfile,
		"status" => InvalidStatus,
		"createdAt" or "updatedAt" => InvalidDates,
		"relations" => InvalidRelation,
		_ => InvalidMetadata
	};

	/// <summary>The line of each value of a JSON text, by its JSON pointer: where a problem of a sidecar is.</summary>
	static Dictionary<string, int> Lines(string json)
	{
		var bytes = Encoding.UTF8.GetBytes(json);
		var breaks = new List<int>();
		for (var i = 0; i < bytes.Length; i++)
			if (bytes[i] == '\n')
				breaks.Add(i);
		int LineOf(long offset)
		{
			var found = breaks.BinarySearch((int)offset);
			return (found < 0 ? ~found : found) + 1;
		}
		var lines = new Dictionary<string, int>(StringComparer.Ordinal);
		var frames = new List<(bool Array, int Index, string Name)>();
		string Pointer() => string.Concat(frames.Select(f => "/" + (f.Array ? f.Index.ToString(CultureInfo.InvariantCulture) : f.Name.Replace("~", "~0").Replace("/", "~1"))));
		var reader = new Utf8JsonReader(bytes);
		try
		{
			while (reader.Read())
				switch (reader.TokenType)
				{
					case JsonTokenType.PropertyName:
						frames[^1] = frames[^1] with { Name = reader.GetString()! };
						lines[Pointer()] = LineOf(reader.TokenStartIndex);
						break;
					case JsonTokenType.EndObject or JsonTokenType.EndArray:
						frames.RemoveAt(frames.Count - 1);
						break;
					default:
						if (frames.Count > 0 && frames[^1].Array)
						{
							frames[^1] = frames[^1] with { Index = frames[^1].Index + 1 };
							lines[Pointer()] = LineOf(reader.TokenStartIndex);
						}
						else if (frames.Count == 0)
							lines[""] = LineOf(reader.TokenStartIndex);
						if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
							frames.Add((reader.TokenType == JsonTokenType.StartArray, -1, ""));
						break;
				}
		}
		catch (JsonException)
		{
			// read already: it is JSON
		}
		return lines;
	}

	sealed class Run(DocCatalog catalog, Func<string, string> display, IReadOnlyCollection<DocFile>? only)
	{
		readonly List<FxDiagnostic> _diagnostics = [];
		readonly HashSet<DocFile> _only = only is null ? [] : [.. only];
		readonly Dictionary<DocFile, Sidecar> _sidecars = [];

		/// <summary>The files of each identity, and the type of each file, as their sidecars say (even an invalid one).</summary>
		readonly Dictionary<string, List<DocFile>> _ids = new(StringComparer.OrdinalIgnoreCase);

		readonly Dictionary<DocFile, string> _types = [];

		bool Selected(DocFile file) => only is null || _only.Contains(file);

		void Add(FxDiagnostic diagnostic) => _diagnostics.Add(diagnostic);

		public DocReport Check()
		{
			foreach (var file in catalog.Files.Where(f => f.Sidecar is not null))
			{
				var sidecar = _sidecars[file] = ReadSidecar(file.Sidecar!);
				if (sidecar.Json is not { ValueKind: JsonValueKind.Object } json)
					continue;
				if (json.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
				{
					if (!_ids.TryGetValue(id.GetString()!, out var files))
						_ids[id.GetString()!] = files = [];
					files.Add(file);
				}
				if (json.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
					_types[file] = type.GetString()!;
			}

			var documents = new List<DocDocument>();
			foreach (var file in catalog.Files.Where(Selected))
			{
				if (file.Sidecar is null)
				{
					if (catalog.IsBootstrap(file))
						Add(FxDiagnostic.Info(BootstrapPending, new(BootstrapPending, file.Path), display(file.Markdown!)));
					else
						Add(FxDiagnostic.Error(MissingSidecar, new(MissingSidecar, file.Path), display(file.Markdown!), fix: FxFix.Run($"fx doc adopt {file.Path}")));
				}
				else if (file.Markdown is null)
					Add(FxDiagnostic.Error(OrphanSidecar, new(OrphanSidecar, file.Path), display(file.Sidecar)));
				else if (_sidecars[file] is { Problem: { } problem } sidecar)
					Add(FxDiagnostic.Error(sidecar.Code!, problem, display(file.Sidecar), sidecar.Line));
				else if (Document(file, _sidecars[file].Json!.Value, _sidecars[file].Text!) is { } document)
					documents.Add(document);
				if (file.Markdown is not null && Text(file) is { } text)
					Links(file, text);
			}

			foreach (var (id, files) in _ids.Where(i => i.Value.Count > 1))
				foreach (var file in files.Where(Selected))
					Add(FxDiagnostic.Error(DuplicateId, new(DuplicateId, id, string.Join(", ", files.Where(f => f != file).Select(f => f.Path))), display(file.Sidecar!)));
			// the slugs of the names of the standard (TYPE-slug), not of any file with a dash
			foreach (var same in catalog.Files.Where(f => f.Formal).GroupBy(f => (f.Folder, f.Slug)).Where(g => g.Count() > 1))
				foreach (var file in same.Where(Selected))
					Add(FxDiagnostic.Error(InvalidName, new($"{InvalidName}.same-slug", file.Slug, string.Join(", ", same.Where(f => f != file).Select(f => f.Path))),
						display(file.Markdown ?? file.Sidecar!)));
			if (only is null)
				foreach (var path in catalog.Config.Bootstrap)
					if (catalog.Files.FirstOrDefault(f => f.Path == path) is not { Markdown: not null, Sidecar: null })
						Add(FxDiagnostic.Warning(BootstrapStale, new(BootstrapStale, path), display(FxLayout.Full(catalog.Root, DocsModule.ConfigFile))));
			return new(documents, _diagnostics);
		}

		/// <summary>The text of the Markdown of <paramref name="file"/>; null (with the problem) if it is not UTF-8.</summary>
		string? Text(DocFile file)
		{
			try
			{
				return DocIntegrity.Normalize(FxFiles.ReadAllBytes(file.Markdown!));
			}
			catch (DecoderFallbackException)
			{
				Add(FxDiagnostic.Error(InvalidEncoding, new(InvalidEncoding, file.Path), display(file.Markdown!)));
				return null;
			}
		}

		DocDocument? Document(DocFile file, JsonElement json, string text)
		{
			var sidecarFile = display(file.Sidecar!);
			var markdownFile = display(file.Markdown!);
			var lines = Lines(text);
			int Line(string pointer)
			{
				for (var at = pointer; ; at = at[..at.LastIndexOf('/')])
					if (lines.TryGetValue(at, out var line) || at.Length == 0)
						return lines.GetValueOrDefault(at, 1);
			}

			// 1. Its type, and its sidecar against the schema of the type
			var typeName = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
			if (typeName is null || DocTypes.Find(typeName) is not { } type)
			{
				Add(FxDiagnostic.Error(InvalidType, typeName is null ? new($"{InvalidType}.missing") : new(InvalidType, typeName, string.Join(", ", DocTypes.All.Select(x => x.Name))),
					sidecarFile, Line("/type")));
				return null;
			}
			var before = _diagnostics.Count;
			foreach (var property in type.Required.Where(p => !json.TryGetProperty(p, out _)))
				Add(FxDiagnostic.Error(CodeOf($"/{property}"),
					property == "profile" ? new($"{InvalidProfile}.missing", type.Name, string.Join(", ", type.Profiles)) : new($"{InvalidMetadata}.missing", property), sidecarFile, 1));
			// the properties that are missing, said above; the rest of the problems, each at its line
			foreach (var error in DocsSchemas.Validate(json).Where(e => !(e.Pointer.Length == 0 && e.Keyword == "required")))
				Add(FxDiagnostic.Error(CodeOf(error.Pointer), Message(error, json, type), sidecarFile, Line(error.Pointer)));
			if (_diagnostics.Count > before)
				return null;
			var sidecar = DocSidecar.From(json);

			// 2. Its name and its place, for its type
			if (type.Name == "readme")
			{
				if (file.Stem != "README")
					Add(FxDiagnostic.Error(InvalidName, new($"{InvalidName}.readme", file.Path), markdownFile));
			}
			else if (!file.Stem.StartsWith($"{type.Token}-", StringComparison.Ordinal))
				Add(FxDiagnostic.Error(InvalidName, new($"{InvalidName}.prefix", file.Path, type.Name, $"{type.Token}-"), markdownFile));
			else if (!SlugPattern().IsMatch(file.Slug))
				Add(FxDiagnostic.Error(InvalidName, new($"{InvalidName}.slug", file.Path, file.Slug), markdownFile));
			var created = DocSidecar.ParseInstant(sidecar.CreatedAt);
			var records = string.Create(CultureInfo.InvariantCulture, $"{Records}/{created:yyyy}/{created:MM}");
			if (type.Temporality == DocTemporality.Historical && file.Folder != records)
				Add(FxDiagnostic.Error(InvalidLocation, new($"{InvalidLocation}.historical", file.Path, type.Name, $"{records}/"), markdownFile));
			else if (type.Temporality == DocTemporality.Stable && file.Folder.Split('/')[0] == Records)
				Add(FxDiagnostic.Error(InvalidLocation, new($"{InvalidLocation}.stable", file.Path, type.Name), markdownFile));

			// 3. Its dates and its actor
			if (created > DocSidecar.ParseInstant(sidecar.UpdatedAt))
				Add(FxDiagnostic.Error(InvalidDates, new($"{InvalidDates}.order", sidecar.CreatedAt, sidecar.UpdatedAt), sidecarFile, Line("/updatedAt")));
			if (catalog.Config.Actors.Count > 0 && !catalog.Config.Actors.Contains(sidecar.CreatedBy))
				Add(FxDiagnostic.Warning(UnknownActor, new(UnknownActor, sidecar.CreatedBy, string.Join(", ", catalog.Config.Actors)), sidecarFile, Line("/createdBy")));

			// 4. Its relations
			for (var i = 0; i < sidecar.Relations.Count; i++)
			{
				var relation = sidecar.Relations[i];
				var line = Line($"/relations/{i}");
				if (string.Equals(relation.TargetId, sidecar.Id, StringComparison.OrdinalIgnoreCase))
					Add(FxDiagnostic.Error(InvalidRelation, new($"{InvalidRelation}.self", relation.Kind), sidecarFile, line));
				else if (!_ids.TryGetValue(relation.TargetId, out var targets))
					Add(FxDiagnostic.Error(MissingTarget, new(MissingTarget, relation.Kind, relation.TargetId), sidecarFile, line));
				else if (_types.GetValueOrDefault(targets[0]) is { } target && !DocRelations.Allows(relation.Kind, type.Name, target))
					Add(FxDiagnostic.Error(InvalidRelation, new(InvalidRelation, relation.Kind, type.Name, target, targets[0].Path, Allowed(DocRelations.Rule(relation.Kind))),
						sidecarFile, line));
			}

			// 5. Its title and its integrity
			byte[] bytes = FxFiles.ReadAllBytes(file.Markdown!);
			string? content = null;
			try
			{
				content = DocIntegrity.Normalize(bytes);
			}
			catch (DecoderFallbackException)
			{
				// said by the links
			}
			if (content is not null)
			{
				if (DocMarkdown.Title(content) is not { } title)
					Add(FxDiagnostic.Error(TitleMismatch, new($"{TitleMismatch}.none", sidecar.Title), markdownFile, 1));
				else if (title.Text != sidecar.Title)
					Add(FxDiagnostic.Error(TitleMismatch, new(TitleMismatch, title.Text, sidecar.Title), markdownFile, title.Line));
				if (DocIntegrity.Hash(DocIntegrity.Utf8.GetBytes(content)) != sidecar.Integrity!.Content)
					Add(type.Name == "view" ? FxDiagnostic.Error(ViewEdited, new(ViewEdited, file.Path), markdownFile, fix: FxFix.Run("fx sync"))
						: type.IsFrozen(sidecar.Status) ? FxDiagnostic.Error(FrozenChanged, new(FrozenChanged, file.Path, sidecar.Status), markdownFile,
							fix: FxFix.Do(new FxText($"{FrozenChanged}.fix", file.Path)))
						: FxDiagnostic.Warning(Unfinalized, new(Unfinalized, file.Path), markdownFile, fix: FxFix.Run($"fx doc finalize {file.Path}")));
			}
			string? metadata = null;
			try
			{
				metadata = DocIntegrity.Metadata(json);
			}
			catch (JcsException ex)
			{
				Add(FxDiagnostic.Error(InvalidMetadata, new($"{InvalidMetadata}.json", ex.Message), sidecarFile));
			}
			if (metadata is not null && metadata != sidecar.Integrity!.Metadata)
				Add(FxDiagnostic.Error(IntegrityMismatch, new(IntegrityMismatch, file.Path), sidecarFile, Line("/integrity/metadata")));
			return new(file, sidecar);
		}

		/// <summary>What a kind of relation joins, for its message: "a spec to a design", "anything to an adr".</summary>
		static string Allowed(DocRelationRule rule)
			=> $"{(rule.Sources is null ? "*" : string.Join("|", rule.Sources))} → {(rule.Targets is null ? "*" : string.Join("|", rule.Targets))}";

		/// <summary>The text of a problem of the schema: its own for what the module knows (a status, a profile, a date), that of the validator for the rest.</summary>
		static FxText Message(DocsSchemaError error, JsonElement json, DocType type)
		{
			var value = json.TryGetProperty(error.Pointer.TrimStart('/'), out var v) ? v.ToString() : "";
			return (error.Pointer, error.Keyword) switch
			{
				("/status", _) => new(InvalidStatus, value, type.Name, string.Join(", ", type.Statuses)),
				("/profile", "unevaluatedProperties") => new($"{InvalidProfile}.none", type.Name),
				("/profile", _) => new(InvalidProfile, value, type.Name, string.Join(", ", type.Profiles)),
				("/createdAt" or "/updatedAt", _) => new($"{InvalidDates}.format", error.Pointer[1..], value),
				_ => new($"{InvalidMetadata}.schema", error.Pointer, error.Message)
			};
		}

		void Links(DocFile file, string text)
		{
			var folder = Path.GetDirectoryName(file.Markdown!)!;
			foreach (var link in DocMarkdown.Links(text))
			{
				if (DocMarkdown.File(link.Text) is not { } path)
					continue;
				var full = Path.GetFullPath(Path.Combine(folder, path));
				if (FxFiles.Exists(full) || FxFiles.DirectoryExists(full))
					continue;
				// outside the catalog, it may be in a mount that is not here
				Add(catalog.Contains(full)
					? FxDiagnostic.Error(BrokenLink, new(BrokenLink, link.Text), display(file.Markdown!), link.Line)
					: FxDiagnostic.Warning(BrokenLink, new($"{BrokenLink}.outside", link.Text), display(file.Markdown!), link.Line));
			}
		}
	}
}
