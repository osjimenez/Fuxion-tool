using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Yaml;

namespace Fuxion.Tool.Core.Docs;

/// <summary>A relation of a target of a manifest: its kind, and the document it goes to by its path from the root of the space.</summary>
public sealed record DocManifestRelation(string Kind, string To, int Line);

/// <summary>
/// A target of an adoption manifest (the skill <c>doc-adoption</c>; the SPEC of the module, 12.4): what becomes of a
/// source. Its paths, from the root of the space. Its notes, reasons and the like are for people, not for the module.
/// </summary>
/// <param name="File">The manifest, as the diagnostics show it.</param>
public sealed record DocManifestTarget(string Action, IReadOnlyList<string> From, string File, int Line)
{
	public string? Type { get; init; }
	public string? Profile { get; init; }
	public string? Status { get; init; }
	public string? Title { get; init; }
	public string? Path { get; init; }
	public string? Created { get; init; }
	public IReadOnlyList<string> Tags { get; init; } = [];
	public IReadOnlyList<DocManifestRelation> Relations { get; init; } = [];
	public IReadOnlyList<string> Sections { get; init; } = [];

	/// <summary><c>encoding</c>: a source that is not UTF-8 is read as Latin-1 and written in UTF-8.</summary>
	public string? Repair { get; init; }
}

/// <summary>
/// The adoption (the SPEC of the module, 5.5): the documents of the bootstrap regime, which get their sidecar where they
/// are; and the batches of the manifests, applied at once, all or nothing.
/// </summary>
public static partial class DocAdoption
{
	public const string InvalidManifest = "docs.invalid-manifest";
	public const string Adopted = "docs.adopted";
	public const string CreatedMissing = "docs.created-missing";

	public static readonly IReadOnlyList<string> Actions = ["adopt", "extract", "register-entry", "skill-material", "drop"];

	[GeneratedRegex(@"^I-\d+$")]
	private static partial Regex Alias();

	static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	/// <summary>An instant of a sidecar from a date (<c>2026-01-16</c>, at its start in UTC) or an instant; null if it is neither.</summary>
	public static string? Instant(string value)
	{
		if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
			return $"{value}T00:00:00Z";
		return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
			&& value.Contains('T')
				? DocSidecar.Instant(at)
				: null;
	}

	/// <summary><c>docs.yaml</c> without <paramref name="adopted"/> in its bootstrap list: adopted, they leave it.</summary>
	static void Unlist(DocChange change, IReadOnlyCollection<string> adopted)
	{
		var file = FxLayout.Full(change.Catalog.Root, DocsModule.ConfigFile);
		if (adopted.Count == 0 || !FxFiles.Exists(file))
			return;
		var editor = YamlEditor.Parse(FxFiles.ReadAllText(file));
		if (editor.Resolve("bootstrap") is not YamlSequence list)
			return;
		for (var i = list.Items.Count - 1; i >= 0; i--)
			if (list.Items[i] is YamlScalar item && adopted.Contains(item.Value.Trim().Replace('\\', '/').TrimEnd('/')))
				editor = editor.Apply(editor.Remove($"bootstrap[{i}]"));
		change.Set(file, editor.Text, document: false);
	}

	/// <summary>
	/// <c>fx doc adopt</c> without a manifest: Markdown without a sidecar, where it is (the bootstrap documents of
	/// <c>docs.yaml</c>, or <paramref name="files"/>), get their sidecar, without changing the Markdown. Their title is
	/// their H1; their type, profile, status and creation, those given, or what their block of status says (the type,
	/// also from their name; the status, the initial one of their type).
	/// </summary>
	public static DocChange Bootstrap(DocCatalog catalog, Func<string, string> display, IReadOnlyList<DocFile>? files, string? type, string? profile, string? status,
		string? created, string? actor, DateTimeOffset now)
	{
		var change = new DocChange(catalog, display);
		if (!DocEditor.Actor(change, actor))
			return change;
		files ??= catalog.Files.Where(catalog.IsBootstrap).ToList();
		var adopted = new List<string>();
		foreach (var file in files)
		{
			if (file.Sidecar is not null || file.Markdown is null)
			{
				change.Error(Adopted, new(Adopted, file.Path), display(file.Sidecar ?? file.Markdown!));
				continue;
			}
			string text;
			try
			{
				text = DocIntegrity.Utf8.GetString(FxFiles.ReadAllBytes(file.Markdown));
			}
			catch (DecoderFallbackException)
			{
				change.Error(DocChecker.InvalidEncoding, new(DocChecker.InvalidEncoding, file.Path), display(file.Markdown));
				continue;
			}
			var where = display(file.Markdown);
			var header = DocMarkdown.Header(text);
			var typeName = type ?? header.Type ?? (file.Stem == "README" ? "readme" : file.Stem.IndexOf('-') is var dash and > 0 ? DocTypes.FromToken(file.Stem[..dash])?.Name : null);
			if (typeName is null || DocTypes.Find(typeName) is not { } docType)
			{
				change.Error(DocChecker.InvalidType, typeName is null ? new($"{DocChecker.InvalidType}.missing") : new(DocChecker.InvalidType, typeName, string.Join(", ", DocTypes.All.Select(t => t.Name))), where);
				continue;
			}
			if (DocMarkdown.Title(text) is not { } title)
			{
				change.Error(DocChecker.TitleMismatch, new($"{DocChecker.TitleMismatch}.adopt"), where);
				continue;
			}
			if ((created ?? header.Created) is not { } when)
			{
				change.Error(CreatedMissing, new(CreatedMissing, file.Path), where);
				continue;
			}
			if (Instant(when) is not { } createdAt)
			{
				change.Error(DocChecker.InvalidDates, new($"{DocChecker.InvalidDates}.format", "created", when), where);
				continue;
			}
			var sidecar = new DocSidecar
			{
				Id = DocSidecar.NewId(DocSidecar.ParseInstant(createdAt)),
				Type = docType.Name,
				Profile = profile ?? header.Profile,
				Status = status ?? header.Status ?? docType.InitialStatus,
				Title = title.Text,
				CreatedAt = createdAt,
				UpdatedAt = DocSidecar.Instant(now),
				CreatedBy = actor!
			}.Seal(DocIntegrity.Content(text));
			change.Set(file.Markdown[..^".md".Length] + DocSidecar.Extension, sidecar.ToJson());
			if (catalog.IsBootstrap(file))
				adopted.Add(file.Path);
		}
		if (change.Ok && files.Count == 0)
			change.Diagnostics.Add(FxDiagnostic.Info(DocEditor.Unchanged, new($"{DocEditor.Unchanged}.nothing-to-adopt")));
		if (change.Ok)
			Unlist(change, adopted);
		change.Verify();
		return change;
	}

	/// <summary>The targets of the manifest at <paramref name="path"/>; null, with its problems, if it cannot be read.</summary>
	public static IReadOnlyList<DocManifestTarget>? Read(string path, string display, ICollection<FxDiagnostic> diagnostics)
	{
		var reader = new FxYamlReader(InvalidManifest, display);
		var map = reader.Load(FxFiles.ReadAllText(path));
		if (map is null)
		{
			foreach (var d in reader.Diagnostics)
				diagnostics.Add(d);
			return null;
		}
		reader.Keys(map, "schema", "batch", "sources", "targets", "synergies", "doubts", "editorial");
		var targets = new List<DocManifestTarget>();
		if (map["targets"] is not YamlSequence list)
			reader.Error(map["targets"], "targets");
		else
			foreach (var item in list.Items)
			{
				if (item is not YamlMapping entry)
				{
					reader.Error(item, "target");
					continue;
				}
				reader.Keys(entry, "action", "from", "type", "profile", "status", "title", "path", "created", "tags", "relations", "sections", "register", "notes", "for",
					"reason", "repair", "rewrite");
				if (FxYamlReader.Scalar(entry["action"]) is not { } action || !Actions.Contains(action))
				{
					reader.Error(entry["action"] ?? item, "action", FxYamlReader.Scalar(entry["action"]) ?? "", string.Join(", ", Actions));
					continue;
				}
				var relations = new List<DocManifestRelation>();
				if (entry["relations"] is YamlSequence items)
					foreach (var relation in items.Items)
					{
						if (relation is not YamlMapping r || FxYamlReader.Scalar(r["kind"]) is not { } kind || FxYamlReader.Scalar(r["to"]) is not { } to)
						{
							reader.Error(relation, "relation");
							continue;
						}
						reader.Keys(r, "kind", "to");
						relations.Add(new(kind, to, relation.Line));
					}
				targets.Add(new(action, FxYamlReader.List(entry["from"]), display, item.Line)
				{
					Type = FxYamlReader.Scalar(entry["type"]),
					Profile = FxYamlReader.Scalar(entry["profile"]),
					Status = FxYamlReader.Scalar(entry["status"]),
					Title = FxYamlReader.Scalar(entry["title"]),
					Path = FxYamlReader.Scalar(entry["path"]),
					Created = FxYamlReader.Scalar(entry["created"]),
					Tags = FxYamlReader.List(entry["tags"]),
					Relations = relations,
					Sections = FxYamlReader.List(entry["sections"]),
					Repair = FxYamlReader.Scalar(entry["repair"])
				});
			}
		foreach (var d in reader.Diagnostics)
			diagnostics.Add(d);
		return reader.HasErrors ? null : targets;
	}

	/// <summary>The text of a source, its byte order mark out; with <c>repair: encoding</c>, read as Latin-1 if it is not UTF-8. Null if it cannot be read.</summary>
	static string? Source(string full, string? repair)
	{
		var bytes = FxFiles.ReadAllBytes(full);
		string text;
		try
		{
			text = DocIntegrity.Utf8.GetString(bytes);
		}
		catch (DecoderFallbackException) when (repair == "encoding")
		{
			text = Encoding.Latin1.GetString(bytes);
		}
		catch (DecoderFallbackException)
		{
			return null;
		}
		return text.StartsWith('﻿') ? text[1..] : text;
	}

	/// <summary>
	/// <c>fx doc adopt --manifest</c>: every target of the manifests at once, all or nothing (the SPEC of the module, 5.5).
	/// <c>adopt</c> moves a source of the catalog to its path (one from outside, as a mount that is read only, is copied),
	/// its H1 as its title; <c>extract</c> makes a document of some sections of a source, which stays as it was;
	/// <c>skill-material</c> moves a source to an annex; <c>drop</c> removes a source of the catalog. The relations go
	/// from paths to identities, and the links of the whole catalog follow what moves. What a source left in the bootstrap
	/// list leaves it.
	/// </summary>
	public static DocChange Apply(DocCatalog catalog, Func<string, string> display, IReadOnlyList<DocManifestTarget> targets, string? actor, DateTimeOffset now)
	{
		var change = new DocChange(catalog, display);
		if (!DocEditor.Actor(change, actor))
			return change;
		string Full(string path) => FxLayout.Full(catalog.Root, path);
		void Error(DocManifestTarget target, string key, params object?[] args)
			=> change.Error(InvalidManifest, new($"{InvalidManifest}.{key}", args), target.File, target.Line);

		// 1. each target, checked; what moves, what goes, and the identity of each new document
		var news = new Dictionary<string, (DocManifestTarget Target, DocType Type, string CreatedAt, string Id)>(Paths);
		var moves = new Dictionary<string, string>(Paths);
		var removed = new HashSet<string>(Paths);
		var sidecars = catalog.Files.Where(f => f.Sidecar is not null && f.Markdown is not null).ToDictionary(f => f.Markdown!, Paths);
		foreach (var target in targets)
		{
			if (target.Action == "register-entry")
			{
				Error(target, "register-entry");
				continue;
			}
			if (target.From.Count == 0 || target.Action != "drop" && target.From.Count > 1)
			{
				Error(target, "from");
				continue;
			}
			foreach (var from in target.From.Where(f => !FxFiles.Exists(Full(f))))
				Error(target, "source", from);
			if (target.Action == "drop")
			{
				foreach (var from in target.From.Select(Full).Where(catalog.Contains))
					if (!removed.Add(from) || moves.ContainsKey(from))
						Error(target, "twice", target.From[0]);
				continue;
			}
			var source = Full(target.From[0]);
			if (target.Path is null || !target.Path.EndsWith(".md", StringComparison.Ordinal) || !catalog.Contains(Full(target.Path)))
			{
				Error(target, "path", target.Path ?? "", FxLayout.Relative(catalog.Root, catalog.Folder));
				continue;
			}
			var path = Full(target.Path);
			if (news.ContainsKey(path) || targets.Any(t => t != target && t.Action == "skill-material" && t.Path is not null && Paths.Equals(Full(t.Path), path)))
				Error(target, "same-path", target.Path);
			else if (FxFiles.Exists(path) && !Paths.Equals(path, source))
				Error(target, "taken", target.Path);
			if (target.Action == "adopt" && sidecars.ContainsKey(source))
				Error(target, "adopted", target.From[0]);
			if (target.Action is "adopt" or "skill-material" && catalog.Contains(source) && !Paths.Equals(source, path))
			{
				if (moves.ContainsKey(source) || removed.Contains(source))
					Error(target, "twice", target.From[0]);
				moves[source] = path;
			}
			if (target.Action == "skill-material")
				continue;
			if (target.Action == "extract" && target.Sections.Count == 0)
				Error(target, "sections");
			if (target.Type is null || DocTypes.Find(target.Type) is not { } type)
			{
				Error(target, "type", target.Type ?? "", string.Join(", ", DocTypes.All.Select(t => t.Name)));
				continue;
			}
			if (string.IsNullOrWhiteSpace(target.Title) || target.Title.Contains('\n'))
				Error(target, "title");
			if (target.Created is null || Instant(target.Created) is not { } createdAt)
			{
				Error(target, "created", target.Created ?? "");
				continue;
			}
			news[path] = (target, type, createdAt, DocSidecar.NewId(DocSidecar.ParseInstant(createdAt)));
		}
		foreach (var source in removed.Where(moves.ContainsKey))
			change.Error(InvalidManifest, new($"{InvalidManifest}.twice", display(source)));
		if (!change.Ok)
			return change;

		// 2. the relations, from paths to identities: a document of the manifests, or one of the catalog
		string? Identity(DocManifestTarget target, DocManifestRelation relation)
		{
			if (Alias().IsMatch(relation.To))
			{
				change.Error(InvalidManifest, new($"{InvalidManifest}.alias", relation.To), target.File, relation.Line);
				return null;
			}
			var to = Full(relation.To);
			if (news.TryGetValue(to, out var known))
				return known.Id;
			if (sidecars.TryGetValue(to, out var file) && DocChecker.Json(file.Sidecar!) is { } json && json.TryGetProperty("id", out var id))
				return id.GetString();
			change.Error(InvalidManifest, new($"{InvalidManifest}.relation-target", relation.To), target.File, relation.Line);
			return null;
		}

		// 3. what each target writes
		var unlisted = new List<string>();
		foreach (var (path, (target, type, createdAt, id)) in news)
		{
			var source = Full(target.From[0]);
			if (Source(source, target.Repair) is not { } text)
			{
				change.Error(DocChecker.InvalidEncoding, new($"{InvalidManifest}.encoding", target.From[0]), target.File, target.Line);
				continue;
			}
			if (target.Action == "extract")
			{
				var sections = target.Sections.Select(s => (Heading: s, Text: DocMarkdown.Section(text, s))).ToList();
				foreach (var missing in sections.Where(s => s.Text is null))
					Error(target, "section", missing.Heading, target.From[0]);
				text = $"# {target.Title!.Trim()}\n\n{string.Join("\n", sections.Select(s => s.Text))}";
			}
			else if (DocMarkdown.Title(text)?.Text != target.Title!.Trim())
				text = DocMarkdown.WithTitle(text, target.Title.Trim());
			text = DocLinks.Rewrite(text, source, path, moves);
			var relations = target.Relations.Select(r => (r.Kind, Id: Identity(target, r))).Where(r => r.Id is not null).Select(r => new DocRelation(r.Kind, r.Id!)).ToList();
			var sidecar = new DocSidecar
			{
				Id = id,
				Type = type.Name,
				Profile = target.Profile,
				Status = target.Status ?? type.InitialStatus,
				Title = target.Title!.Trim(),
				Tags = target.Tags.Distinct(StringComparer.Ordinal).ToList(),
				CreatedAt = createdAt,
				UpdatedAt = DocSidecar.Instant(now),
				CreatedBy = actor!,
				Relations = relations
			}.Seal(DocIntegrity.Content(text));
			if (moves.TryGetValue(source, out var to) && Paths.Equals(to, path))
				change.Move(source, path, text);
			else
				change.Set(path, text);
			change.Set(path[..^".md".Length] + DocSidecar.Extension, sidecar.ToJson());
			if (catalog.Contains(source) && catalog.Config.Bootstrap.Contains(FxLayout.Relative(catalog.Folder, source)))
				unlisted.Add(FxLayout.Relative(catalog.Folder, source));
		}
		foreach (var target in targets.Where(t => t.Action == "skill-material"))
		{
			var source = Full(target.From[0]);
			var path = Full(target.Path!);
			var content = source.EndsWith(".md", StringComparison.Ordinal)
				? Source(source, target.Repair) is { } text ? Encoding.UTF8.GetBytes(DocLinks.Rewrite(text, source, path, moves)) : null
				: FxFiles.ReadAllBytes(source);
			if (content is null)
				change.Error(DocChecker.InvalidEncoding, new($"{InvalidManifest}.encoding", target.From[0]), target.File, target.Line);
			else if (moves.ContainsKey(source))
				change.MoveBytes(source, path, content, document: false);
			else
				change.SetBytes(path, content, document: false);
		}
		foreach (var source in removed)
			change.Delete(source);

		// 4. the links of the catalog: what goes leaves none behind; to what moves, they follow it
		foreach (var markdown in catalog.Markdowns().Where(m => !removed.Contains(m) && DocLinks.Where(m, moves) is null && !change.Touches(m)))
			foreach (var link in DocLinks.To(change.Text(markdown)!, markdown, removed))
				change.Error(DocChecker.BrokenLink, new($"{DocChecker.BrokenLink}.dropped", link.Target), display(markdown), link.Line);
		if (change.Ok)
		{
			DocLinks.Relink(change, moves);
			Unlist(change, unlisted);
		}
		change.Verify();
		return change;
	}
}
