using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fuxion.Tool.Cli.Describe;
using Fuxion.Tool.Core.Docs;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Sync;
using Xunit;

namespace Fuxion.Tool.Test;

/// <summary>
/// The check of the module of the documentation (plan U, U4 step 3; the SPEC of the module, 5.2): each rule of a
/// document and of the catalog, valid and invalid; <c>fx doctor</c>; and <c>fx doc check|list|show|schema</c>.
/// </summary>
public sealed class DocsCheckTest
{
	static readonly DateTimeOffset At = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);

	static (string Code, string? File, int? Line) P(string code, string? file, int? line = null) => (code, file, line);

	/// <summary>A space with a catalog in <c>_docs</c>, where documents are added valid unless told otherwise.</summary>
	sealed class Space : IDisposable
	{
		public Space(string docs = "schema: 1.0.0\n")
		{
			Root = TempGitRepository.TempFolder("fx-docs-");
			Write("_fx/space.yaml", "schema: 1.0.0\nname: notes\n");
			Write("_fx/docs/docs.yaml", docs);
		}

		public string Root { get; }

		/// <summary>The identity of each document added, by its path in the catalog.</summary>
		public Dictionary<string, string> Ids { get; } = [];

		public void Dispose() => Directory.Delete(Root, true);

		public string Full(string relative) => Path.Combine(Root, relative);

		public void Write(string relative, string text)
		{
			var path = Full(relative);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, text);
		}

		public string Read(string relative) => File.ReadAllText(Full(relative));

		/// <summary>A document, its pair sealed: its path in the catalog.</summary>
		public string Add(string type, string slug, string? title = null, string? folder = null, string? status = null, DateTimeOffset? created = null,
			DateTimeOffset? updated = null, IReadOnlyList<DocRelation>? relations = null, string? body = null, string? markdown = null, string? id = null, string? stem = null)
		{
			var t = DocTypes.Find(type)!;
			var at = created ?? At;
			folder ??= t.Temporality == DocTemporality.Historical ? string.Create(CultureInfo.InvariantCulture, $"_records/{at.UtcDateTime:yyyy}/{at.UtcDateTime:MM}") : "";
			stem ??= type == "readme" ? "README" : $"{t.Token}-{slug}";
			var path = folder.Length == 0 ? $"{stem}.md" : $"{folder}/{stem}.md";
			title ??= $"The {slug}";
			markdown ??= $"# {title}\n\n{body ?? "Text."}\n";
			var sidecar = new DocSidecar
			{
				Id = id ?? DocSidecar.NewId(at),
				Type = type,
				Profile = t.ProfileRequired ? t.Profiles[0] : null,
				Status = status ?? t.InitialStatus,
				Title = title,
				CreatedAt = DocSidecar.Instant(at),
				UpdatedAt = DocSidecar.Instant(updated ?? at),
				CreatedBy = "editor",
				Relations = relations ?? [],
				Generation = t.GenerationRequired ? new("docs.status", "_docs") : null
			}.Seal(DocIntegrity.Content(markdown));
			Write($"_docs/{path}", markdown);
			Write($"_docs/{Sidecar(path)}", sidecar.ToJson());
			Ids[path] = sidecar.Id;
			return path;
		}

		public static string Sidecar(string path) => path[..^".md".Length] + DocSidecar.Extension;

		public DocCatalog Catalog() => DocCatalog.Scan(Root, DocsModule.Load(Root)!);

		public DocReport Check(params string[] only)
		{
			var catalog = Catalog();
			return DocChecker.Check(catalog, p => FxLayout.Relative(Root, p), only.Length == 0 ? null : only.SelectMany(o => catalog.Select(o, Root)).ToList());
		}

		public List<(string Code, string? File, int? Line)> Problems(params string[] only) => Check(only).Diagnostics.Select(d => (d.Code, d.File, d.Line)).ToList();

		/// <summary>The sidecar of <paramref name="path"/>, changed by <paramref name="change"/> and written indented as it was.</summary>
		public void Change(string path, Action<JsonObject> change)
		{
			var json = JsonNode.Parse(Read($"_docs/{Sidecar(path)}"))!.AsObject();
			change(json);
			Write($"_docs/{Sidecar(path)}", json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
		}
	}

	// a valid catalog

	[Fact(DisplayName = "a valid catalog: a readme, documents in their places, relations of the right types; no problem")]
	public void Valid()
	{
		using var space = new Space();
		space.Add("readme", "", "Notes");
		var adr = space.Add("adr", "use-sidecars", status: "accepted");
		var design = space.Add("design", "the-catalog", folder: "docs-system");
		space.Add("spec", "the-module", folder: "docs-system", relations: [new("realized-by", space.Ids[design])]);
		space.Add("rfc", "a-module", relations: [new("decided-by", space.Ids[adr])]);
		space.Add("view", "status");
		var report = space.Check();
		Assert.Empty(report.Diagnostics);
		Assert.Equal(6, report.Documents.Count);
	}

	[Fact(DisplayName = "every type: its smallest document, in its place, is valid")]
	public void EveryType()
	{
		using var space = new Space();
		foreach (var type in DocTypes.All)
			space.Add(type.Name, $"a-{type.Name}", folder: type.Name == "readme" ? "readme" : null);
		Assert.Empty(space.Problems());
		Assert.Equal(13, space.Check().Documents.Count);
	}

	// pairs and the bootstrap list

	[Fact(DisplayName = "pairs: a Markdown without sidecar is an error, unless it is a bootstrap one; a sidecar alone too; a stale entry of the list, a warning")]
	public void Pairs()
	{
		using var space = new Space("schema: 1.0.0\nbootstrap:\n  - _governance/RULE-layout.md\n  - _governance/RULE-gone.md\n");
		space.Write("_docs/_governance/RULE-layout.md", "# Layout\n");
		space.Write("_docs/notes/DESIGN-loose.md", "# Loose\n");
		var adr = space.Add("adr", "orphan");
		File.Delete(space.Full($"_docs/{adr}"));
		// not part of the catalog: hidden and local folders, and what is not a document
		space.Write("_docs/.archive/DESIGN-old.md", "# Old\n");
		space.Write("_docs/~$scratch/DESIGN-tmp.md", "# Tmp\n");
		space.Write("_docs/notes/diagram.png", "png");
		var problems = space.Problems();
		Assert.Equal(4, problems.Count);
		Assert.Contains(P(DocChecker.BootstrapPending, "_docs/_governance/RULE-layout.md"), problems);
		Assert.Contains(P(DocChecker.MissingSidecar, "_docs/notes/DESIGN-loose.md"), problems);
		Assert.Contains(P(DocChecker.OrphanSidecar, "_docs/_records/2026/10/ADR-orphan.meta.json"), problems);
		Assert.Contains(P(DocChecker.BootstrapStale, "_fx/docs/docs.yaml"), problems);
	}

	// the sidecar

	[Fact(DisplayName = "a sidecar that is not JSON, repeats a property or is not UTF-8: its problem, with its line if it has one")]
	public void UnreadableSidecar()
	{
		using var space = new Space();
		var a = space.Add("adr", "not-json");
		space.Write($"_docs/{Space.Sidecar(a)}", "{\n  \"id\":\n}\n");
		var b = space.Add("adr", "repeated");
		space.Write($"_docs/{Space.Sidecar(b)}", space.Read($"_docs/{Space.Sidecar(b)}").Replace("  \"status\": \"draft\",\n", "  \"status\": \"draft\",\n  \"status\": \"draft\",\n"));
		var c = space.Add("adr", "latin");
		File.WriteAllBytes(space.Full($"_docs/{Space.Sidecar(c)}"), [0x7B, 0xE9, 0x7D]);
		var problems = space.Problems();
		Assert.Equal(3, problems.Count);
		Assert.Contains(P(DocChecker.InvalidMetadata, $"_docs/{Space.Sidecar(a)}", 3), problems);
		Assert.Contains(problems, p => p.Code == DocChecker.InvalidMetadata && p.File == $"_docs/{Space.Sidecar(b)}");
		Assert.Contains(P(DocChecker.InvalidEncoding, $"_docs/{Space.Sidecar(c)}"), problems);
	}

	[Theory(DisplayName = "a sidecar against its schema: each problem with its code, and nothing else of it is checked")]
	[InlineData("adr", "type", "\"memo\"", DocChecker.InvalidType)]
	[InlineData("adr", "type", null, DocChecker.InvalidType)]
	[InlineData("plan", "profile", null, DocChecker.InvalidProfile)]
	[InlineData("plan", "profile", "\"bogus\"", DocChecker.InvalidProfile)]
	[InlineData("adr", "profile", "\"implementation\"", DocChecker.InvalidProfile)]
	[InlineData("adr", "status", "\"completed\"", DocChecker.InvalidStatus)]
	[InlineData("adr", "status", null, DocChecker.InvalidStatus)]
	[InlineData("adr", "createdAt", "\"2026-10-21T12:00:00+02:00\"", DocChecker.InvalidDates)]
	[InlineData("adr", "updatedAt", null, DocChecker.InvalidDates)]
	[InlineData("adr", "title", null, DocChecker.InvalidMetadata)]
	[InlineData("adr", "id", "\"not-a-uuid\"", DocChecker.InvalidMetadata)]
	[InlineData("adr", "tags", "[\"Bad Tag\"]", DocChecker.InvalidMetadata)]
	[InlineData("adr", "relations", "[{\"kind\":\"likes\",\"targetId\":\"0192a8f1-0000-7000-8000-000000000001\"}]", DocChecker.InvalidRelation)]
	[InlineData("adr", "relations", "[{\"kind\":\"informs\"}]", DocChecker.InvalidRelation)]
	[InlineData("adr", "extra", "1", DocChecker.InvalidMetadata)]
	[InlineData("view", "generation", null, DocChecker.InvalidMetadata)]
	[InlineData("plan", "generation", "{\"recipe\":\"docs.status\",\"scope\":\"_docs\"}", DocChecker.InvalidMetadata)]
	public void Schema(string type, string property, string? json, string code)
	{
		using var space = new Space();
		var path = space.Add(type, "doc");
		space.Change(path, s =>
		{
			s.Remove(property);
			if (json is not null)
				s[property] = JsonNode.Parse(json);
		});
		var report = space.Check();
		Assert.Equal(code, Assert.Single(report.Diagnostics).Code);
		Assert.Equal($"_docs/{Space.Sidecar(path)}", report.Diagnostics[0].File);
		Assert.Empty(report.Documents);
	}

	[Fact(DisplayName = "a problem of the schema says the line of its property, and its own text for a status")]
	public void SchemaLine()
	{
		using var space = new Space();
		var path = space.Add("adr", "doc");
		space.Change(path, s => s["status"] = "completed");
		var problem = Assert.Single(space.Check().Diagnostics);
		// {, schemaVersion, id, type, status
		Assert.Equal((DocChecker.InvalidStatus, 5), (problem.Code, problem.Line));
		Assert.Equal("'completed' is not a status of adr: accepted, draft, rescinded, superseded.", problem.Message.English);
	}

	// name and place

	[Theory(DisplayName = "the name of a document: README for a readme; for the rest, the type in capitals and a slug in kebab-case")]
	[InlineData("adr", "ADR-Use_Sidecars", ".slug")]
	[InlineData("adr", "ADR-", ".slug")]
	[InlineData("design", "RULE-catalog", ".prefix")]
	[InlineData("design", "design-catalog", ".prefix")]
	[InlineData("design", "README", ".prefix")]
	[InlineData("readme", "INDEX", ".readme")]
	public void Name(string type, string stem, string text)
	{
		using var space = new Space();
		space.Add(type, "x", stem: stem, folder: DocTypes.Find(type)!.Temporality == DocTemporality.Historical ? null : "notes");
		var problem = Assert.Single(space.Check().Diagnostics);
		Assert.Equal((DocChecker.InvalidName, $"{DocChecker.InvalidName}{text}"), (problem.Code, problem.Message.Key));
	}

	[Fact(DisplayName = "the place: a historical one in _records/AAAA/MM of its createdAt in UTC (also at the end of a month), a stable one outside")]
	public void Location()
	{
		using var space = new Space();
		// 31-oct at 23:30 in UTC-2 is 1-nov in UTC
		var late = new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.FromHours(-2));
		space.Add("adr", "end-of-month", created: late);
		Assert.True(File.Exists(space.Full("_docs/_records/2026/11/ADR-end-of-month.md")));
		Assert.Empty(space.Problems());

		var october = space.Add("adr", "in-october", created: late, folder: "_records/2026/10");
		var root = space.Add("rfc", "at-the-root", folder: "");
		var deep = space.Add("plan", "too-deep", folder: "_records/2026/10/more");
		var stable = space.Add("design", "a-design", folder: "_records/2026/10");
		var problems = space.Problems();
		Assert.Equal(4, problems.Count);
		Assert.All(problems, p => Assert.Equal(DocChecker.InvalidLocation, p.Code));
		Assert.Equal(new[] { october, root, deep, stable }.Select(p => $"_docs/{p}").Order(), problems.Select(p => p.File!).Order());
	}

	[Fact(DisplayName = "the same slug in a folder is an error for both; in two folders, valid")]
	public void SameSlug()
	{
		using var space = new Space();
		var adr = space.Add("adr", "catalog");
		var rfc = space.Add("rfc", "catalog");
		space.Add("design", "catalog", folder: "a");
		space.Add("design", "catalog", folder: "b");
		// names outside the standard have no slug: what they lack is their sidecar
		space.Write("_docs/c/error-pending.md", "# Error\n");
		space.Write("_docs/c/warning-pending.md", "# Warning\n");
		var problems = space.Problems().Where(p => p.Code != DocChecker.MissingSidecar).ToList();
		Assert.Equal([P(DocChecker.InvalidName, $"_docs/{adr}"), P(DocChecker.InvalidName, $"_docs/{rfc}")], problems.Order());
	}

	// title, dates, actor

	[Fact(DisplayName = "the title is the first H1, outside code; without H1 or with another one, an error at its line")]
	public void Title()
	{
		using var space = new Space();
		space.Add("adr", "fenced", title: "Real", markdown: "```md\n# Not the title\n```\n\n# Real\n\nText.\n");
		space.Add("adr", "closing", title: "Closed", markdown: "# Closed ##\n");
		var other = space.Add("adr", "other", title: "One", markdown: "Intro.\n\n# Another\n");
		var none = space.Add("adr", "none", title: "None", markdown: "## Only a section\n");
		var problems = space.Problems();
		Assert.Equal([P(DocChecker.TitleMismatch, $"_docs/{none}", 1), P(DocChecker.TitleMismatch, $"_docs/{other}", 3)], problems.Order());
	}

	[Fact(DisplayName = "createdAt after updatedAt is an error; an actor outside docs.yaml, a warning (none declared: any)")]
	public void DatesAndActor()
	{
		using var space = new Space("schema: 1.0.0\nactors: [someone]\n");
		var path = space.Add("adr", "back-in-time", updated: At.AddDays(-1));
		var problems = space.Problems();
		Assert.Contains(P(DocChecker.InvalidDates, $"_docs/{Space.Sidecar(path)}", 8), problems);
		Assert.Contains(problems, p => p.Code == DocChecker.UnknownActor);
		Assert.Equal(FxSeverity.Warning, space.Check().Diagnostics.Single(d => d.Code == DocChecker.UnknownActor).Severity);
		space.Write("_fx/docs/docs.yaml", "schema: 1.0.0\n");
		Assert.DoesNotContain(space.Problems(), p => p.Code == DocChecker.UnknownActor);
	}

	// relations

	[Fact(DisplayName = "relations: to a document that exists, not itself, and of a kind its types allow")]
	public void Relations()
	{
		using var space = new Space();
		var design = space.Add("design", "a-design", folder: "notes");
		var id = DocSidecar.NewId(At);
		var self = space.Add("adr", "self", id: id, relations: [new("related-to", id)]);
		var missing = space.Add("adr", "missing", relations: [new("depends-on", DocSidecar.NewId(At))]);
		var types = space.Add("rfc", "decided-by-a-design", relations: [new("decided-by", space.Ids[design])]);
		var problems = space.Problems();
		Assert.Equal(3, problems.Count);
		// the relation is at line 11: {, 8 properties, "relations": [ and its {
		Assert.Contains(P(DocChecker.InvalidRelation, $"_docs/{Space.Sidecar(self)}", 11), problems);
		Assert.Contains(P(DocChecker.MissingTarget, $"_docs/{Space.Sidecar(missing)}", 11), problems);
		Assert.Contains(P(DocChecker.InvalidRelation, $"_docs/{Space.Sidecar(types)}", 11), problems);
	}

	[Theory(DisplayName = "the rules of the relations, from the schemas: what each kind joins")]
	[InlineData("decided-by", "rfc", "adr", true)]
	[InlineData("decided-by", "rfc", "design", false)]
	[InlineData("implemented-by", "spec", "plan", true)]
	[InlineData("implemented-by", "spec", "runbook", false)]
	[InlineData("operated-by", "design", "runbook", true)]
	[InlineData("realized-by", "spec", "design", true)]
	[InlineData("realized-by", "rfc", "design", false)]
	[InlineData("summarizes", "view", "plan", true)]
	[InlineData("summarizes", "adr", "plan", false)]
	[InlineData("related-to", "adr", "readme", true)]
	[InlineData("supersedes", "rule", "rule", true)]
	public void RelationRules(string kind, string source, string target, bool allowed)
	{
		Assert.Equal(15, DocRelations.Kinds.Count);
		Assert.Equal(allowed, DocRelations.Allows(kind, source, target));
	}

	[Fact(DisplayName = "an identity used twice: an error in both sidecars")]
	public void DuplicateId()
	{
		using var space = new Space();
		var id = DocSidecar.NewId(At);
		var a = space.Add("adr", "first", id: id);
		var b = space.Add("design", "second", folder: "notes", id: id);
		Assert.Equal([P(DocChecker.DuplicateId, $"_docs/{Space.Sidecar(a)}"), P(DocChecker.DuplicateId, $"_docs/{Space.Sidecar(b)}")], space.Problems().Order());
	}

	// integrity

	[Fact(DisplayName = "integrity: edited, unfinalized (a warning); frozen, an error; a view edited by hand, an error; the metadata by hand, an error")]
	public void Integrity()
	{
		using var space = new Space();
		var open = space.Add("adr", "open");
		var frozen = space.Add("adr", "frozen", status: "accepted");
		var completed = space.Add("plan", "completed", status: "completed");
		var stable = space.Add("design", "deprecated", folder: "notes", status: "deprecated");
		var view = space.Add("view", "status");
		var metadata = space.Add("rfc", "metadata");
		foreach (var path in new[] { open, frozen, completed, stable, view })
			File.AppendAllText(space.Full($"_docs/{path}"), "More.\n");
		space.Write($"_docs/{Space.Sidecar(metadata)}", space.Read($"_docs/{Space.Sidecar(metadata)}").Replace("\"editor\"", "\"someone\""));
		var report = space.Check();
		Assert.Equal(new[]
			{
				P(DocChecker.Unfinalized, $"_docs/{open}"), P(DocChecker.FrozenChanged, $"_docs/{frozen}"), P(DocChecker.FrozenChanged, $"_docs/{completed}"),
				P(DocChecker.Unfinalized, $"_docs/{stable}"), P(DocChecker.ViewEdited, $"_docs/{view}"), P(DocChecker.IntegrityMismatch, $"_docs/{Space.Sidecar(metadata)}", 12)
			}.Order(),
			report.Diagnostics.Select(d => (d.Code, d.File, d.Line)).Order());
		Assert.Equal(FxSeverity.Warning, report.Diagnostics.Single(d => d.File == $"_docs/{open}").Severity);
		Assert.Equal($"fx doc finalize {open}", report.Diagnostics.Single(d => d.File == $"_docs/{open}").Fix!.Command);
	}

	[Fact(DisplayName = "integrity: a Markdown saved again with BOM and CRLF is the same; one that is not UTF-8, an error")]
	public void IntegrityNormalized()
	{
		using var space = new Space();
		var path = space.Add("adr", "crlf", status: "accepted", body: "Línea.");
		File.WriteAllBytes(space.Full($"_docs/{path}"), [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(space.Read($"_docs/{path}").Replace("\n", "\r\n"))]);
		Assert.Empty(space.Problems());
		File.WriteAllBytes(space.Full($"_docs/{path}"), [.. Encoding.UTF8.GetBytes("# The crlf\n\nL"), 0xED, .. Encoding.UTF8.GetBytes("nea.\n")]);
		Assert.Equal([P(DocChecker.InvalidEncoding, $"_docs/{path}")], space.Problems());
	}

	// links

	[Fact(DisplayName = "links: the relative ones must lead somewhere (inside the catalog an error, outside a warning); not URLs, anchors or code")]
	public void Links()
	{
		using var space = new Space();
		space.Add("design", "y", folder: "b");
		space.Write("_docs/b/picture.png", "png");
		var path = space.Add("design", "x", folder: "a", body: string.Join("\n",
			"[to y](../b/DESIGN-y.md) and [to its part](../b/DESIGN-y.md#part) and ![a picture](../b/picture.png)", // 3
			"[missing](../b/DESIGN-none.md)", // 4
			"[outside](../../elsewhere/file.md)", // 5
			"[web](https://example.org) [mail](mailto:a@b.c) [anchor](#section) [folder](../b/)", // 6
			"```", // 7
			"[in code](none.md)", // 8
			"```", // 9
			"`[in a span](none.md)` and ![no picture](none.png)", // 10
			"[ref]: ./none.md", // 11
			"[spaces](<../b/a file.txt>) [escaped](../b/a%20file.txt)")); // 12
		var problems = space.Problems();
		Assert.Equal(new[]
			{
				P(DocChecker.BrokenLink, $"_docs/{path}", 4), P(DocChecker.BrokenLink, $"_docs/{path}", 5), P(DocChecker.BrokenLink, $"_docs/{path}", 10),
				P(DocChecker.BrokenLink, $"_docs/{path}", 11), P(DocChecker.BrokenLink, $"_docs/{path}", 12), P(DocChecker.BrokenLink, $"_docs/{path}", 12)
			},
			problems.Order());
		Assert.Equal(FxSeverity.Warning, space.Check().Diagnostics.Single(d => d.Line == 5).Severity);
		space.Write("_docs/b/a file.txt", "a file");
		Assert.Equal(4, space.Problems().Count);
	}

	[Fact(DisplayName = "DocMarkdown: the first H1 without closing hashes; the links outside code, with their line")]
	public void Markdown()
	{
		Assert.Equal(new DocMarkdownItem("C# and `code`", 2), DocMarkdown.Title("\n# C# and `code` #\n# Second\n"));
		Assert.Null(DocMarkdown.Title("~~~\n# fenced\n~~~\n#NoSpace\n"));
		Assert.Equal(new DocMarkdownItem("After", 5), DocMarkdown.Title("````\n```\n# in\n````\n# After\n"));
		Assert.Equal(["a.md", "b.md#x", "c d.md", "e.md"],
			DocMarkdown.Links("[a](a.md) [b](b.md#x \"title\") [c](<c d.md>)\n[e]: e.md\n[f](http://x) [g](/abs.md)").Select(l => l.Text));
		Assert.Equal("c d.md", DocMarkdown.File("c%20d.md#part"));
		Assert.Null(DocMarkdown.File("?query"));
	}

	// selection

	[Fact(DisplayName = "a document is named by its path (Markdown, sidecar or stem; from here or from the catalog), by its identity, or by a folder")]
	public void Select()
	{
		using var space = new Space();
		var adr = space.Add("adr", "one");
		var design = space.Add("design", "two", folder: "notes/deep");
		var catalog = space.Catalog();
		var ids = DocChecker.Read(catalog).ToDictionary(d => d.Sidecar.Id, d => d.File);
		Assert.Equal(adr, Assert.Single(catalog.Select(adr, space.Root)).Path);
		Assert.Equal(adr, Assert.Single(catalog.Select($"_docs/{adr}", space.Root)).Path);
		Assert.Equal(adr, Assert.Single(catalog.Select(Space.Sidecar(adr), space.Root)).Path);
		Assert.Equal(adr, Assert.Single(catalog.Select(adr[..^3], space.Root)).Path);
		Assert.Equal(design, Assert.Single(catalog.Select("DESIGN-two.md", space.Full("_docs/notes/deep"))).Path);
		Assert.Equal(design, Assert.Single(catalog.Select(space.Ids[design], space.Root, ids)).Path);
		Assert.Equal(design, Assert.Single(catalog.Select("notes", space.Root)).Path);
		Assert.Equal(2, catalog.Select(".", space.Full("_docs")).Count);
		Assert.Empty(catalog.Select("nothing.md", space.Root));
		Assert.Empty(catalog.Select("../outside.md", space.Full("_docs")));
	}

	[Fact(DisplayName = "checking some documents: only their problems, with what of the catalog concerns them")]
	public void CheckSome()
	{
		using var space = new Space();
		var one = space.Add("adr", "one");
		var two = space.Add("adr", "two");
		File.AppendAllText(space.Full($"_docs/{one}"), "More.\n");
		File.AppendAllText(space.Full($"_docs/{two}"), "More.\n");
		Assert.Equal([P(DocChecker.Unfinalized, $"_docs/{one}")], space.Problems(one));
		Assert.Equal(2, space.Problems().Count);
	}

	// fx doctor

	[Fact(DisplayName = "fx doctor: the module docs checks the catalog of the space; disabled, it does not appear")]
	public void Doctor()
	{
		using var space = new Space();
		space.Write("_docs/DESIGN-loose.md", "# Loose\n");
		var options = new SyncOptions(Offline: true, Skill: FxSkill.Text);
		var docs = Assert.Single(SyncEngine.Doctor(space.Root, options, cancellationToken: TestContext.Current.CancellationToken), r => r.Module == DocsModule.Name);
		Assert.Equal(P(DocChecker.MissingSidecar, "_docs/DESIGN-loose.md"), docs.Diagnostics.Select(d => (d.Code, d.File, d.Line)).Single());
		space.Write("_fx/docs/docs.yaml", "schema: 1.0.0\nenabled: false\n");
		Assert.DoesNotContain(SyncEngine.Doctor(space.Root, options, cancellationToken: TestContext.Current.CancellationToken), r => r.Module == DocsModule.Name);
		space.Write("_fx/docs/docs.yaml", "schema: 1.0.0\ncatalog: ../out\n");
		var invalid = Assert.Single(SyncEngine.Doctor(space.Root, options, cancellationToken: TestContext.Current.CancellationToken), r => r.Module == DocsModule.Name);
		Assert.Equal((DocsYamlReader.InvalidYaml, "_fx/docs/docs.yaml"), (invalid.Diagnostics.Single().Code, invalid.Diagnostics.Single().File));
	}

	// the command line

	static (int ExitCode, string Stdout, string Stderr) Fx(string directory, params string[] args)
	{
		var stdout = new StringWriter();
		var stderr = new StringWriter();
		var exitCode = InProcess.Run([.. args, "--lang", "en"], stdout, stderr, directory);
		return (exitCode, stdout.ToString(), stderr.ToString());
	}

	static JsonElement Json(string directory, string schema, params string[] args)
	{
		var (_, stdout, _) = Fx(directory, [.. args, "--output", "json"]);
		var document = JsonDocument.Parse(stdout).RootElement;
		Assert.Equal(schema, document.GetProperty("schema").GetString());
		DescribeTest.Fits(JsonDocument.Parse(Fx(directory, "schema", schema).Stdout).RootElement, document, schema);
		return document;
	}

	[Fact(DisplayName = "fx doc check: the catalog, how many were checked, those with problems and the problems; exit 1 with errors")]
	public void CliCheck()
	{
		using var space = new Space();
		var good = space.Add("adr", "good");
		var bad = space.Add("adr", "bad");
		File.AppendAllText(space.Full($"_docs/{bad}"), "More.\n");
		space.Write("_docs/DESIGN-loose.md", "# Loose\n");
		var check = Json(space.Root, "fx-doc-check/1", "doc", "check");
		Assert.False(check.GetProperty("ok").GetBoolean());
		Assert.Equal("_docs", check.GetProperty("catalog").GetString());
		Assert.Equal(3, check.GetProperty("checked").GetInt32());
		Assert.Equal(["DESIGN-loose.md", bad], check.GetProperty("withProblems").EnumerateArray().Select(p => p.GetString()));
		Assert.Equal(1, Fx(space.Root, "doc", "check").ExitCode);

		var one = Json(space.Full("_docs/_records"), "fx-doc-check/1", "doc", "check", $"2026/10/{Path.GetFileName(good)}");
		Assert.True(one.GetProperty("ok").GetBoolean());
		Assert.Equal(1, one.GetProperty("checked").GetInt32());
		var (exitCode, stdout, _) = Fx(space.Root, "doc", "check", bad);
		Assert.Equal(0, exitCode);
		Assert.Contains($"! {bad}", stdout);
		Assert.Contains("1 document(s) checked: 1 with problems.", stdout);
	}

	[Fact(DisplayName = "fx doc check: a document that is not there, a usage error (2); no catalog, an error that says how to make one")]
	public void CliCheckErrors()
	{
		using var space = new Space();
		var (exitCode, _, stderr) = Fx(space.Root, "doc", "check", "nothing.md");
		Assert.Equal(2, exitCode);
		Assert.Contains("'nothing.md' names no document of the catalog _docs.", stderr);

		// a repository mounted in the space, without a catalog of its own, uses that of the space
		space.Write("lib/repo/_fx/space.yaml", "schema: 1.0.0\nname: lib\n");
		Assert.Equal("_docs", Json(space.Full("lib/repo"), "fx-doc-check/1", "doc", "check").GetProperty("catalog").GetString());
		// the module off: no catalog, here or above
		space.Write("_fx/docs/docs.yaml", "schema: 1.0.0\nenabled: false\n");
		Assert.Equal(DocsModule.NoCatalog, Json(space.Full("lib/repo"), "fx-doc-check/1", "doc", "check").GetProperty("diagnostics")[0].GetProperty("code").GetString());

		var empty = TempGitRepository.TempFolder("fx-test-");
		try
		{
			var check = Json(empty, "fx-doc-check/1", "doc", "check");
			var problem = check.GetProperty("diagnostics")[0];
			Assert.Equal((DocsModule.NoCatalog, "fx module enable docs"), (problem.GetProperty("code").GetString(), problem.GetProperty("fix").GetProperty("command").GetString()));
			Assert.Equal(1, Fx(empty, "doc", "check").ExitCode);
		}
		finally
		{
			Directory.Delete(empty, true);
		}
	}

	[Fact(DisplayName = "fx doc list: the documents that can be read, with filters by type, status, class, tag and relation")]
	public void CliList()
	{
		using var space = new Space();
		var adr = space.Add("adr", "accepted", status: "accepted");
		space.Add("adr", "draft");
		var design = space.Add("design", "a-design", folder: "notes");
		space.Add("rfc", "proposal", relations: [new("decided-by", space.Ids[adr])]);
		space.Change(design, s => s["tags"] = new JsonArray("docs"));
		space.Write("_docs/DESIGN-loose.md", "# Loose\n");
		string?[] Paths(params string[] filters) => Json(space.Root, "fx-doc-list/1", ["doc", "list", .. filters]).GetProperty("documents").EnumerateArray()
			.Select(d => d.GetProperty("path").GetString()).ToArray();
		Assert.Equal(4, Paths().Length);
		Assert.Equal([design], Paths("--tag", "docs"));
		Assert.Equal([adr, "_records/2026/10/ADR-draft.md"], Paths("--type", "adr"));
		Assert.Equal([adr], Paths("--class", "current"));
		Assert.Equal(["_records/2026/10/RFC-proposal.md"], Paths("--relation", "decided-by"));
		Assert.Equal(2, Fx(space.Root, "doc", "list", "--class", "nope").ExitCode);
		var first = Json(space.Root, "fx-doc-list/1", "doc", "list", "--status", "accepted").GetProperty("documents")[0];
		Assert.Equal(("adr", "accepted", "current", space.Ids[adr]),
			(first.GetProperty("type").GetString(), first.GetProperty("status").GetString(), first.GetProperty("class").GetString(), first.GetProperty("id").GetString()));
		Assert.Contains("The accepted", Fx(space.Root, "doc", "list").Stdout);
	}

	[Fact(DisplayName = "fx doc show: a document with its relations and the inverse ones, by path or by identity")]
	public void CliShow()
	{
		using var space = new Space();
		var adr = space.Add("adr", "decision", status: "accepted");
		var rfc = space.Add("rfc", "proposal", relations: [new("decided-by", space.Ids[adr])]);
		var show = Json(space.Root, "fx-doc-show/1", "doc", "show", space.Ids[adr]);
		Assert.Equal(adr, show.GetProperty("document").GetProperty("path").GetString());
		Assert.Empty(show.GetProperty("relations").EnumerateArray());
		var inverse = show.GetProperty("inverse")[0];
		Assert.Equal(("decided-by", rfc, "The proposal"), (inverse.GetProperty("kind").GetString(), inverse.GetProperty("path").GetString(), inverse.GetProperty("title").GetString()));
		var relation = Json(space.Root, "fx-doc-show/1", "doc", "show", $"_docs/{rfc}").GetProperty("relations")[0];
		Assert.Equal((space.Ids[adr], adr), (relation.GetProperty("id").GetString(), relation.GetProperty("path").GetString()));
		Assert.Contains("decided-by ← The proposal", Fx(space.Root, "doc", "show", adr).Stdout);
		Assert.Equal(2, Fx(space.Root, "doc", "show", "_records").ExitCode);
	}

	[Fact(DisplayName = "fx doc schema: the types of the standard and the kinds of relation; with a type, its JSON Schema as it is")]
	public void CliSchema()
	{
		using var space = new Space();
		var schemas = Json(space.Root, "fx-doc-schemas/1", "doc", "schema");
		Assert.Equal("1.0", schemas.GetProperty("version").GetString());
		Assert.Equal(13, schemas.GetProperty("types").GetArrayLength());
		var plan = schemas.GetProperty("types").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "plan");
		Assert.Equal(("historical", "draft", true, "allowed"), (plan.GetProperty("temporality").GetString(), plan.GetProperty("initialStatus").GetString(),
			plan.GetProperty("profileRequired").GetBoolean(), schemas.GetProperty("types").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "report")
				.GetProperty("generation").GetString()));
		Assert.Equal(15, schemas.GetProperty("relations").GetArrayLength());
		var adr = Fx(space.Root, "doc", "schema", "adr");
		Assert.Equal(0, adr.ExitCode);
		Assert.Equal("https://schemas.fuxion.invalid/document/v1.0/types/adr.schema.json", JsonDocument.Parse(adr.Stdout).RootElement.GetProperty("$id").GetString());
		Assert.Equal(2, Fx(space.Root, "doc", "schema", "memo").ExitCode);
	}
}
