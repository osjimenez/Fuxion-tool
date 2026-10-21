using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;

namespace Fuxion.Tool.Core.Docs;

/// <summary>
/// The operations that write in a catalog (the SPEC of the module, 5.1 and 5.3): <c>new</c>, <c>finalize</c>,
/// <c>status</c> and <c>relate</c>. Each works out a <see cref="DocChange"/>, checked with the validator of the module;
/// the command writes it, or shows it (<c>--dry-run</c>).
/// </summary>
public static partial class DocEditor
{
	public const string ActorMissing = "docs.actor-missing";
	public const string NameTaken = "docs.name-taken";
	public const string InvalidTransition = "docs.invalid-transition";
	public const string InvalidTitle = "docs.invalid-title";
	public const string InvalidFolder = "docs.invalid-folder";
	public const string Unchanged = "docs.unchanged";
	public const string Correction = "docs.correction";
	public const string ViewsBySync = "docs.views-by-sync";
	public const string HistoricalMove = "docs.historical-move";

	/// <summary>The environment variable of the actor, when <c>--actor</c> does not say it (plan U, decision 20).</summary>
	public const string ActorVariable = "FX_DOCS_ACTOR";

	/// <summary>The letters with marks of the Latin alphabets, by the letter a slug writes for them.</summary>
	public static readonly IReadOnlyList<(string Marked, char Plain)> Letters =
	[
		("àáâãäåāăą", 'a'), ("çćĉċč", 'c'), ("ďđ", 'd'), ("èéêëēĕėęě", 'e'), ("ĝğġģ", 'g'), ("ĥħ", 'h'), ("ìíîïĩīĭįıİ", 'i'), ("ĵ", 'j'), ("ķ", 'k'),
		("ĺļľŀł", 'l'), ("ñńņň", 'n'), ("òóôõöøōŏő", 'o'), ("ŕŗř", 'r'), ("śŝşš", 's'), ("ţťŧ", 't'), ("ùúûüũūŭůűų", 'u'), ("ŵ", 'w'), ("ýÿŷ", 'y'),
		("źżž", 'z')
	];

	static char Unmarked(char c)
	{
		foreach (var (marked, plain) in Letters)
			if (marked.Contains(c))
				return plain;
		return c;
	}

	/// <summary>
	/// The slug a title proposes (the layout rule of the standard, 5.2): lower case ASCII letters and digits, its marks
	/// taken out (<c>á</c> → <c>a</c>, <c>ñ</c> → <c>n</c>), anything else a single dash. The same on every system.
	/// </summary>
	public static string ProposeSlug(string title)
	{
		var slug = new StringBuilder();
		foreach (var c in title.ToLowerInvariant())
		{
			var plain = Unmarked(c);
			if (plain is >= 'a' and <= 'z' or >= '0' and <= '9')
				slug.Append(plain);
			else if (c is 'ß')
				slug.Append("ss");
			else if (c is 'æ' or 'œ')
				slug.Append(c == 'æ' ? "ae" : "oe");
			else if (slug.Length > 0 && slug[^1] != '-')
				slug.Append('-');
		}
		return slug.ToString().Trim('-');
	}

	/// <summary>The actor of a change: given, and one of the actors of <c>docs.yaml</c> (plan U, decision 20).</summary>
	internal static bool Actor(DocChange change, string? actor)
	{
		if (string.IsNullOrWhiteSpace(actor))
			change.Error(ActorMissing, new(ActorMissing, ActorVariable));
		else if (!change.Catalog.Config.Actors.Contains(actor))
			change.Error(DocChecker.UnknownActor, new($"{DocChecker.UnknownActor}.write", actor, change.Catalog.Config.Actors.Count == 0 ? "-" : string.Join(", ", change.Catalog.Config.Actors)),
				change.Display(FxLayout.Full(change.Catalog.Root, DocsModule.ConfigFile)));
		return change.Ok;
	}

	/// <summary>
	/// <c>fx doc new</c>: the pair of a new document, its Markdown with its H1 and its sidecar sealed, in the place of its
	/// type: a historical one in <c>_records/AAAA/MM</c> of <paramref name="now"/>, a stable one in
	/// <paramref name="folder"/> (the root of the catalog by default). Not where its name or its slug are taken.
	/// </summary>
	public static DocChange New(DocCatalog catalog, Func<string, string> display, string currentDirectory, string typeName, string title, string? slug, string? profile,
		string? folder, IReadOnlyList<string> tags, string? actor, DateTimeOffset now)
	{
		var change = new DocChange(catalog, display);
		var type = DocTypes.Find(typeName)!;
		if (!Actor(change, actor))
			return change;
		title = title.Trim();
		if (title.Length == 0 || title.Contains('\n') || title.Contains('\r'))
			change.Error(InvalidTitle, new(InvalidTitle));
		if (type.GenerationRequired)
			change.Error(ViewsBySync, new(ViewsBySync, type.Name));
		if (type.ProfileRequired && profile is null)
			change.Error(DocChecker.InvalidProfile, new($"{DocChecker.InvalidProfile}.missing", type.Name, string.Join(", ", type.Profiles)));
		else if (profile is not null && type.Profiles.Count == 0)
			change.Error(DocChecker.InvalidProfile, new($"{DocChecker.InvalidProfile}.none", type.Name));
		else if (profile is not null && !type.Profiles.Contains(profile))
			change.Error(DocChecker.InvalidProfile, new(DocChecker.InvalidProfile, profile, type.Name, string.Join(", ", type.Profiles)));
		slug ??= ProposeSlug(title);
		if (type.Name != "readme" && !DocChecker.SlugPattern().IsMatch(slug))
			change.Error(DocChecker.InvalidName, new($"{DocChecker.InvalidName}.new-slug", slug));

		string? relative;
		if (type.Temporality == DocTemporality.Historical)
		{
			if (folder is not null)
				change.Error(InvalidFolder, new($"{InvalidFolder}.historical", type.Name));
			var utc = now.UtcDateTime;
			relative = string.Create(CultureInfo.InvariantCulture, $"{DocChecker.Records}/{utc:yyyy}/{utc:MM}");
		}
		else if ((relative = folder is null ? "" : catalog.RelativeFolder(folder, currentDirectory)) is null)
			change.Error(InvalidFolder, new(InvalidFolder, folder, FxLayout.Relative(catalog.Root, catalog.Folder)));
		else if (relative.Split('/')[0] == DocChecker.Records)
			change.Error(InvalidFolder, new($"{InvalidFolder}.records", type.Name));
		if (!change.Ok)
			return change;

		var stem = type.Name == "readme" ? "README" : $"{type.Token}-{slug}";
		var path = relative!.Length == 0 ? $"{stem}.md" : $"{relative}/{stem}.md";
		var markdown = catalog.Full(path);
		var sidecarPath = markdown[..^".md".Length] + DocSidecar.Extension;
		if (FxFiles.Exists(markdown) || FxFiles.Exists(sidecarPath))
			change.Error(NameTaken, new(NameTaken, path));
		else if (type.Name != "readme")
		{
			if (catalog.Files.FirstOrDefault(f => f.Folder == relative && f.Formal && f.Slug == slug) is { } sameSlug)
				change.Error(NameTaken, new($"{NameTaken}.slug", slug, sameSlug.Path));
			else if (catalog.Files.FirstOrDefault(f => f.Stem == stem) is { } sameName)
				change.Error(NameTaken, new($"{NameTaken}.name", stem, sameName.Path));
		}
		if (!change.Ok)
			return change;

		var text = $"# {title}\n";
		var sidecar = new DocSidecar
		{
			Id = DocSidecar.NewId(now),
			Type = type.Name,
			Profile = profile,
			Status = type.InitialStatus,
			Title = title,
			Tags = tags.Distinct(StringComparer.Ordinal).ToList(),
			CreatedAt = DocSidecar.Instant(now),
			UpdatedAt = DocSidecar.Instant(now),
			CreatedBy = actor!
		}.Seal(DocIntegrity.Content(text));
		change.Set(markdown, text);
		change.Set(sidecarPath, sidecar.ToJson());
		change.Verify();
		return change;
	}

	/// <summary>A document to change: readable (its sidecar valid), with the bytes of its Markdown.</summary>
	sealed record Loaded(DocDocument Document, byte[] Markdown)
	{
		public DocSidecar Sidecar => Document.Sidecar;
		public DocType Type => Document.Type;
		public string Path => Document.File.Path;
		public string Content => DocIntegrity.Content(Markdown);

		/// <summary>Whether its Markdown changed since it was finalized.</summary>
		public bool Edited => Content != Sidecar.Integrity!.Content;
	}

	/// <summary>
	/// <paramref name="file"/>, to change, from <paramref name="report"/> (a check of it, or of all those the change
	/// takes); if it cannot be read, its errors.
	/// </summary>
	static Loaded? Load(DocChange change, DocFile file, DocReport? report = null)
	{
		report ??= DocChecker.Check(change.Catalog, change.Display, [file]);
		if (report.Documents.FirstOrDefault(d => d.File == file) is not { } document)
		{
			var own = new[] { file.Markdown, file.Sidecar }.OfType<string>().Select(change.Display).ToList();
			change.Diagnostics.AddRange(report.Diagnostics.Where(d => d.Severity == FxSeverity.Error && d.File is not null && own.Contains(d.File)));
			return null;
		}
		try
		{
			var markdown = FxFiles.ReadAllBytes(file.Markdown!);
			DocIntegrity.Normalize(markdown);
			return new(document, markdown);
		}
		catch (DecoderFallbackException)
		{
			change.Error(DocChecker.InvalidEncoding, new(DocChecker.InvalidEncoding, file.Path), change.Display(file.Markdown!));
			return null;
		}
	}

	/// <summary>Writes the sidecar of <paramref name="document"/>, finalized at <paramref name="now"/>: its content as it is, and its metadata.</summary>
	static void Save(DocChange change, Loaded document, DocSidecar sidecar, DateTimeOffset now, string? markdown = null)
	{
		if (markdown is not null)
			change.Set(document.Document.File.Markdown!, markdown);
		var finalized = (sidecar with { UpdatedAt = DocSidecar.Instant(now) }).Seal(markdown is null ? document.Content : DocIntegrity.Content(markdown));
		change.Set(document.Document.File.Sidecar!, finalized.ToJson());
	}

	/// <summary>A frozen document whose content changed, or a view edited by hand: it cannot be finalized as it is.</summary>
	static bool Blocked(DocChange change, Loaded document)
	{
		if (!document.Edited)
			return false;
		var file = change.Display(document.Document.File.Markdown!);
		if (document.Type.Name == "view")
			change.Diagnostics.Add(FxDiagnostic.Error(DocChecker.ViewEdited, new(DocChecker.ViewEdited, document.Path), file, fix: FxFix.Run("fx sync")));
		else if (document.Type.IsFrozen(document.Sidecar.Status))
			change.Diagnostics.Add(FxDiagnostic.Error(DocChecker.FrozenChanged, new(DocChecker.FrozenChanged, document.Path, document.Sidecar.Status), file,
				fix: FxFix.Do(new FxText($"{DocChecker.FrozenChanged}.fix", document.Path))));
		else
			return false;
		return true;
	}

	/// <summary>
	/// <c>fx doc finalize</c> (the SPEC of the module, 5.3): the documents whose content or metadata changed (of
	/// <paramref name="files"/>, or of the whole catalog), with their integrity again and <c>updatedAt</c> at
	/// <paramref name="now"/>; the rest, untouched. A frozen one whose content changed, only with a
	/// <paramref name="correction"/>, which goes at the end of its Markdown.
	/// </summary>
	public static DocChange Finalize(DocCatalog catalog, Func<string, string> display, IReadOnlyList<DocFile>? files, string? correction, DateTimeOffset now)
	{
		var change = new DocChange(catalog, display);
		var all = files is null;
		files ??= catalog.Files.Where(f => f is { Markdown: not null, Sidecar: not null }).ToList();
		// one check for all of them: each one reads the whole catalog
		var report = DocChecker.Check(catalog, display, files);
		foreach (var file in files)
		{
			if (Load(change, file, report) is not { } document)
				continue;
			var metadataEdited = DocChecker.Json(file.Sidecar!) is { } json && DocIntegrity.Metadata(json) != document.Sidecar.Integrity!.Metadata;
			if (correction is not null)
			{
				if (!document.Type.IsFrozen(document.Sidecar.Status))
				{
					change.Error(Correction, new($"{Correction}.not-frozen", document.Path, document.Sidecar.Status), display(file.Markdown!));
					continue;
				}
				// the note at the end, with the line ends of the file
				var text = Encoding.UTF8.GetString(document.Markdown);
				var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
				var note = new FxText(Correction, now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), correction.Trim()).ToString();
				Save(change, document, document.Sidecar, now, $"{text.TrimEnd('\r', '\n')}{newLine}{newLine}> {note}{newLine}");
				continue;
			}
			if (!document.Edited && !metadataEdited)
			{
				if (!all)
					change.Diagnostics.Add(FxDiagnostic.Info(Unchanged, new($"{Unchanged}.finalized", document.Path)));
				continue;
			}
			if (!Blocked(change, document))
				Save(change, document, document.Sidecar, now);
		}
		if (change.Ok && !change.Changed && all)
			change.Diagnostics.Add(FxDiagnostic.Info(Unchanged, new($"{Unchanged}.nothing-to-finalize")));
		change.Verify();
		return change;
	}

	/// <summary>
	/// <c>fx doc status</c>: the document in <paramref name="status"/>, if its type allows the transition (plan U,
	/// decision 21), and finalized.
	/// </summary>
	public static DocChange SetStatus(DocCatalog catalog, Func<string, string> display, DocFile file, string status, DateTimeOffset now)
	{
		var change = new DocChange(catalog, display);
		if (Load(change, file) is not { } document)
			return change;
		var type = document.Type;
		var from = document.Sidecar.Status;
		if (!type.Statuses.Contains(status))
			change.Error(DocChecker.InvalidStatus, new(DocChecker.InvalidStatus, status, type.Name, string.Join(", ", type.Statuses)));
		else if (status == from)
			change.Diagnostics.Add(FxDiagnostic.Info(Unchanged, new($"{Unchanged}.status", document.Path, status)));
		else if (!type.CanMove(from, status))
			change.Error(InvalidTransition, new(InvalidTransition, document.Path, from, status), display(file.Sidecar!));
		else if (!Blocked(change, document))
			Save(change, document, document.Sidecar with { Status = status }, now);
		change.Verify();
		return change;
	}

	/// <summary>
	/// <c>fx doc relate</c>: a relation of <paramref name="file"/> to <paramref name="target"/>, added or taken out, kept
	/// in the source only (the metadata model, 8). <c>supersedes</c> also puts the target in its status of the class
	/// replaced, if its type has one, in the same change. Both, finalized.
	/// </summary>
	public static DocChange Relate(DocCatalog catalog, Func<string, string> display, DocFile file, string kind, DocFile target, bool remove, DateTimeOffset now)
	{
		var change = new DocChange(catalog, display);
		if (Load(change, file) is not { } source || Load(change, target) is not { } other)
			return change;
		var relation = new DocRelation(kind, other.Sidecar.Id);
		var has = source.Sidecar.Relations.Any(r => r.Kind == kind && string.Equals(r.TargetId, relation.TargetId, StringComparison.OrdinalIgnoreCase));
		var sourceFile = display(file.Sidecar!);
		if (!DocRelations.Kinds.Contains(kind))
			change.Error(DocChecker.InvalidRelation, new($"{DocChecker.InvalidMetadata}.schema", "/relations", kind));
		else if (string.Equals(source.Sidecar.Id, other.Sidecar.Id, StringComparison.OrdinalIgnoreCase))
			change.Error(DocChecker.InvalidRelation, new($"{DocChecker.InvalidRelation}.self", kind), sourceFile);
		else if (remove ? !has : has)
			change.Diagnostics.Add(FxDiagnostic.Info(Unchanged, new(remove ? $"{Unchanged}.no-relation" : $"{Unchanged}.relation", source.Path, kind, other.Path)));
		else if (!remove && !DocRelations.Allows(kind, source.Type.Name, other.Type.Name))
			change.Error(DocChecker.InvalidRelation, new(DocChecker.InvalidRelation, kind, source.Type.Name, other.Type.Name, other.Path, Allowed(DocRelations.Rule(kind))),
				sourceFile);
		else if (!Blocked(change, source))
		{
			var relations = remove
				? source.Sidecar.Relations.Where(r => !(r.Kind == kind && string.Equals(r.TargetId, relation.TargetId, StringComparison.OrdinalIgnoreCase))).ToList()
				: [.. source.Sidecar.Relations, relation];
			Save(change, source, source.Sidecar with { Relations = relations }, now);
			// the target of supersedes, replaced
			if (!remove && kind == "supersedes" && other.Type.SupersededStatus is { } replaced && other.Sidecar.Status != replaced)
			{
				if (!other.Type.CanMove(other.Sidecar.Status, replaced))
					change.Error(InvalidTransition, new(InvalidTransition, other.Path, other.Sidecar.Status, replaced), display(target.Sidecar!));
				else if (!Blocked(change, other))
					Save(change, other, other.Sidecar with { Status = replaced }, now);
			}
		}
		change.Verify();
		return change;
	}

	/// <summary>
	/// <c>fx doc move</c> (the SPEC of the module, 5.6): a stable document to another folder of the catalog, its pair and its
	/// annexes, with the links of the catalog to them, and theirs, rewritten. Its name stays; a historical one never moves.
	/// Moving is not an edit: whatever was clean is sealed again with its <c>updatedAt</c> kept.
	/// </summary>
	public static DocChange Move(DocCatalog catalog, Func<string, string> display, DocFile file, string folder, string currentDirectory)
	{
		var change = new DocChange(catalog, display);
		if (Load(change, file) is not { } document)
			return change;
		if (document.Type.Temporality == DocTemporality.Historical)
		{
			change.Error(HistoricalMove, new(HistoricalMove, document.Path, document.Type.Name), display(file.Markdown!));
			return change;
		}
		var relative = catalog.RelativeFolder(folder, currentDirectory);
		if (relative is null)
			change.Error(InvalidFolder, new(InvalidFolder, folder, FxLayout.Relative(catalog.Root, catalog.Folder)));
		else if (relative.Split('/')[0] == DocChecker.Records)
			change.Error(InvalidFolder, new($"{InvalidFolder}.records", document.Type.Name));
		else if (relative == file.Folder)
			change.Diagnostics.Add(FxDiagnostic.Info(Unchanged, new($"{Unchanged}.move", document.Path, relative.Length == 0 ? "." : relative)));
		if (!change.Ok || relative == file.Folder)
			return change;

		var path = relative!.Length == 0 ? $"{file.Stem}.md" : $"{relative}/{file.Stem}.md";
		var markdown = catalog.Full(path);
		var sidecar = markdown[..^".md".Length] + DocSidecar.Extension;
		if (FxFiles.Exists(markdown) || FxFiles.Exists(sidecar))
			change.Error(NameTaken, new(NameTaken, path));
		else if (file.Formal && catalog.Files.FirstOrDefault(f => f.Folder == relative && f.Formal && f.Slug == file.Slug) is { } sameSlug)
			change.Error(NameTaken, new($"{NameTaken}.slug", file.Slug, sameSlug.Path));
		if (!change.Ok)
			return change;

		var annexes = catalog.Annexes(file);
		var moves = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
		{
			[file.Markdown!] = markdown,
			[file.Sidecar!] = sidecar
		};
		if (FxFiles.DirectoryExists(annexes))
			moves[annexes] = markdown[..^".md".Length];
		var text = Encoding.UTF8.GetString(document.Markdown);
		var moved = DocLinks.Rewrite(text, file.Markdown!, markdown, moves);
		change.Move(file.Markdown!, markdown, moved);
		change.MoveBytes(file.Sidecar!, sidecar, moved != text && DocLinks.Resealed(file, moved) is { } resealed ? Encoding.UTF8.GetBytes(resealed) : FxFiles.ReadAllBytes(file.Sidecar!));
		if (FxFiles.DirectoryExists(annexes))
			foreach (var annex in FxFiles.EnumerateFiles(annexes, "*", System.IO.SearchOption.AllDirectories))
			{
				var to = DocLinks.Where(annex, moves)!;
				if (annex.EndsWith(".md", StringComparison.Ordinal))
					change.Move(annex, to, DocLinks.Rewrite(Encoding.UTF8.GetString(FxFiles.ReadAllBytes(annex)), annex, to, moves), document: false);
				else
					change.MoveBytes(annex, to, FxFiles.ReadAllBytes(annex), document: false);
			}
		DocLinks.Relink(change, moves);
		change.Verify();
		return change;
	}

	static string Allowed(DocRelationRule rule)
		=> $"{(rule.Sources is null ? "*" : string.Join("|", rule.Sources))} → {(rule.Targets is null ? "*" : string.Join("|", rule.Targets))}";
}
