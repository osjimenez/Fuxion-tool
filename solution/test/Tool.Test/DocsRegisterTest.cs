using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fuxion.Tool.Core.Docs;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Xunit;
using Space = Fuxion.Tool.Test.DocsCheckTest.Space;

namespace Fuxion.Tool.Test;

/// <summary>
/// The registers and their entries (plan U, U4 step 6a; decision 18): the entries in the sidecar of the register, checked
/// with it; <c>fx doc entry add|set|close|list</c>; relations to entries; and <c>register-entry</c> in the manifests.
/// </summary>
public sealed class DocsRegisterTest
{
	const string Actors = "schema: 1.0.0\nactors: [editor]\n";
	static readonly DateTimeOffset Now = DocsCheckTest.At.AddDays(1);

	static Func<string, string> Display(Space space) => p => FxLayout.Relative(space.Root, p);

	static DocSidecar Sidecar(Space space, string path)
	{
		using var json = DocSidecar.Parse(space.Read($"_docs/{Space.Sidecar(path)}"));
		return DocSidecar.From(json.RootElement);
	}

	static DocFile File(Space space, string path) => space.Catalog().Files.Single(f => f.Path == path);

	static List<string> Codes(DocChange change) => change.Diagnostics.Where(d => d.Severity != FxSeverity.Info).Select(d => d.Code).ToList();

	static string Ok(DocChange change) => string.Join("; ", change.Diagnostics.Select(d => $"{d.Code} {d.File}:{d.Line} {d.Message.English}"));

	static JsonElement Json(string json)
	{
		using var document = JsonDocument.Parse(json);
		return document.RootElement.Clone();
	}

	/// <summary>A register of ideas (profile backlog) in <c>ideas/</c>, with a research next to it.</summary>
	static (Space Space, string Register, string Research) Ideas()
	{
		var space = new Space(Actors);
		var register = space.Add("register", "ideas", folder: "ideas");
		var research = space.Add("research", "faces");
		return (space, register, research);
	}

	static DocChange Add(Space space, string register, string title, DocEntryEdit? edit = null)
		=> DocEditor.AddEntry(space.Catalog(), Display(space), space.Root, File(space, register), title, edit ?? new(), Now);

	// the schema and the check

	[Fact(DisplayName = "an entry lives in the sidecar of a register: in order, with its fields sorted, and valid by the schema")]
	public void Entries()
	{
		var (space, register, research) = Ideas();
		using var _ = space;
		var change = Add(space, register, "Fuxion.Mcp: un servidor MCP", new()
		{
			Key = "I-64",
			Status = "boceto",
			Summary = "Que una aplicación exponga un MCP generado sobre su modelo.",
			Tags = ["mcp"],
			Created = "2026-01-16",
			Fields = [KeyValuePair.Create("prioridad", (JsonElement?)Json("2")), KeyValuePair.Create("bloque", (JsonElement?)Json("\"Hub\"")),
				KeyValuePair.Create("espera", (JsonElement?)Json("[\"I-19\"]"))],
			Relations = [("related-to", research)]
		});
		Assert.True(change.Write(), Ok(change));
		var entry = Assert.Single(Sidecar(space, register).Entries);
		Assert.Equal(("fuxion-mcp-un-servidor-mcp", "I-64", "boceto", "2026-01-16T00:00:00Z", true), (entry.Name, entry.Key, entry.Status, entry.CreatedAt, entry.Open));
		Assert.Equal(["bloque", "espera", "prioridad"], JsonNode.Parse(space.Read($"_docs/{Space.Sidecar(register)}"))!["entries"]![0]!["fields"]!.AsObject().Select(f => f.Key));
		Assert.Equal([new DocRelation("related-to", space.Ids[research])], entry.Relations);
		Assert.Equal("2026-10-22T10:00:00Z", Sidecar(space, register).UpdatedAt);
		Assert.Empty(space.Problems());
	}

	[Theory(DisplayName = "an entry against the schema: what it must have, and the form of each field")]
	[InlineData("name", null)]
	[InlineData("status", "\"En diseño\"")]
	[InlineData("key", "\"i-20\"")]
	[InlineData("createdAt", "\"2026-01-16\"")]
	[InlineData("fields", "{\"Prioridad\": 1}")]
	[InlineData("fields", "{\"nested\": {\"a\": 1}}")]
	[InlineData("extra", "1")]
	public void EntrySchema(string property, string? json)
	{
		var (space, register, _) = Ideas();
		using var _ = space;
		Assert.True(Add(space, register, "An idea").Write());
		space.Change(register, s =>
		{
			var entry = s["entries"]![0]!.AsObject();
			entry.Remove(property);
			if (json is not null)
				entry[property] = JsonNode.Parse(json);
		});
		var problem = Assert.Single(space.Check().Diagnostics);
		Assert.Equal(DocChecker.InvalidMetadata, problem.Code);
	}

	[Fact(DisplayName = "the check of the entries: names and keys once in a register, closed after born, relations with their targets; identities unique with the documents")]
	public void EntryCheck()
	{
		var (space, register, research) = Ideas();
		using var _ = space;
		Assert.True(Add(space, register, "One", new() { Key = "I-1" }).Write());
		Assert.True(Add(space, register, "Two", new() { Key = "I-2" }).Write());
		space.Change(register, s =>
		{
			var entries = s["entries"]!.AsArray();
			entries[1]!["name"] = "one";
			entries[1]!["key"] = "I-1";
			entries[1]!["closedAt"] = "2020-01-01T00:00:00Z";
			entries[1]!["relations"] = new JsonArray(new JsonObject { ["kind"] = "related-to", ["targetId"] = DocSidecar.NewId(Now) });
			entries[0]!["id"] = space.Ids[research];
		});
		var codes = space.Problems().Select(p => p.Code).ToList();
		Assert.Contains(DocChecker.InvalidName, codes);
		Assert.Contains(DocChecker.InvalidMetadata, codes);
		Assert.Contains(DocChecker.InvalidDates, codes);
		Assert.Contains(DocChecker.MissingTarget, codes);
		Assert.Equal(2, codes.Count(c => c == DocChecker.DuplicateId));
		Assert.Contains(DocChecker.IntegrityMismatch, codes);
	}

	[Fact(DisplayName = "entries only in registers: another type does not take them")]
	public void EntriesOnlyInRegisters()
	{
		using var space = new Space(Actors);
		var adr = space.Add("adr", "x");
		Assert.Equal([DocEditor.NotARegister], Codes(Add(space, adr, "An idea")));
		space.Change(adr, s => s["entries"] = new JsonArray());
		Assert.Equal(DocChecker.InvalidMetadata, Assert.Single(space.Check().Diagnostics).Code);
	}

	// add, set, close

	[Fact(DisplayName = "entry add: its name from its title unless it says one, unique; its key unique; open by default")]
	public void AddEntry()
	{
		var (space, register, _) = Ideas();
		using var _ = space;
		Assert.True(Add(space, register, "Una idea", new() { Key = "I-7" }).Write());
		Assert.Equal(("una-idea", "open"), (Sidecar(space, register).Entries[0].Name, Sidecar(space, register).Entries[0].Status));
		Assert.Equal([DocEditor.EntryTaken], Codes(Add(space, register, "Una idea")));
		Assert.Equal([DocEditor.EntryTaken], Codes(Add(space, register, "Otra", new() { Key = "I-7" })));
		Assert.Equal([DocChecker.InvalidName], Codes(Add(space, register, "Otra", new() { Name = "Otra Idea" })));
		Assert.Equal([DocEditor.UnknownTarget], Codes(Add(space, register, "Otra", new() { Relations = [("related-to", "nothing.md")] })));
		Assert.Equal([DocChecker.InvalidMetadata], Codes(Add(space, register, "Otra", new() { Status = "En diseño" })));
	}

	[Fact(DisplayName = "entry set and close: what it says changes, the rest stays; closed once, now")]
	public void SetAndClose()
	{
		var (space, register, research) = Ideas();
		using var _ = space;
		Assert.True(Add(space, register, "Una idea", new()
		{
			Key = "I-7",
			Tags = ["a"],
			Fields = [KeyValuePair.Create("prioridad", (JsonElement?)Json("2")), KeyValuePair.Create("tema", (JsonElement?)Json("\"Docs\""))]
		}).Write());
		Assert.True(Add(space, register, "Otra idea").Write());
		var set = DocEditor.SetEntry(space.Catalog(), Display(space), space.Root, File(space, register), "I-7", new()
		{
			Title = "La idea",
			Status = "en-diseno",
			Tags = ["b"],
			Fields = [KeyValuePair.Create<string, JsonElement?>("prioridad", null), KeyValuePair.Create("bloque", (JsonElement?)Json("\"Hub\""))],
			Relations = [("informs", research), ("depends-on", "otra-idea")]
		}, Now);
		Assert.False(set.Ok);
		// another entry, by its name: only by its key or identity, as a target
		Assert.Equal([DocEditor.UnknownTarget], Codes(set));
		var other = Sidecar(space, register).Entries[1].Id;
		Assert.True(DocEditor.SetEntry(space.Catalog(), Display(space), space.Root, File(space, register), "I-7", new()
		{
			Title = "La idea",
			Status = "en-diseno",
			Tags = ["b"],
			Fields = [KeyValuePair.Create<string, JsonElement?>("prioridad", null), KeyValuePair.Create("bloque", (JsonElement?)Json("\"Hub\""))],
			Relations = [("informs", research), ("depends-on", other)]
		}, Now).Write());
		var entry = Sidecar(space, register).Entries[0];
		Assert.Equal(("una-idea", "La idea", "en-diseno"), (entry.Name, entry.Title, entry.Status));
		Assert.Equal(["a", "b"], entry.Tags.Order());
		Assert.Equal(["bloque", "tema"], entry.Fields.Select(f => f.Key).Order());
		Assert.Equal(2, entry.Relations.Count);
		Assert.Empty(space.Problems());
		Assert.True(DocEditor.SetEntry(space.Catalog(), Display(space), space.Root, File(space, register), "una-idea", new() { Unrelate = [("depends-on", other)] }, Now).Write());
		Assert.Single(Sidecar(space, register).Entries[0].Relations);

		Assert.True(DocEditor.CloseEntry(space.Catalog(), Display(space), File(space, register), "I-7", "hecha", Now).Write());
		Assert.Equal(("hecha", "2026-10-22T10:00:00Z", false), (Sidecar(space, register).Entries[0].Status, Sidecar(space, register).Entries[0].ClosedAt, Sidecar(space, register).Entries[0].Open));
		Assert.Equal($"{DocEditor.Unchanged}.closed",
			Assert.Single(DocEditor.CloseEntry(space.Catalog(), Display(space), File(space, register), "I-7", null, Now).Diagnostics).Message.Key);
		Assert.Equal([DocEditor.UnknownEntry], Codes(DocEditor.CloseEntry(space.Catalog(), Display(space), File(space, register), "I-99", null, Now)));
		Assert.Empty(space.Problems());
	}

	[Fact(DisplayName = "relate to an entry: by its key; an entry is no target for a kind with types")]
	public void RelateToEntry()
	{
		var (space, register, research) = Ideas();
		using var _ = space;
		Assert.True(Add(space, register, "Una idea", new() { Key = "I-7" }).Write());
		var entry = DocEditor.EntryTarget(space.Catalog(), "I-7")!;
		Assert.True(DocEditor.RelateToEntry(space.Catalog(), Display(space), File(space, research), "informs", entry, false, Now).Write());
		Assert.Equal([new DocRelation("informs", entry)], Sidecar(space, research).Relations);
		Assert.Empty(space.Problems());
		var adr = space.Add("rfc", "x");
		Assert.Equal([DocChecker.InvalidRelation], Codes(DocEditor.RelateToEntry(space.Catalog(), Display(space), File(space, adr), "decided-by", entry, false, Now)));
	}

	// the manifests

	[Fact(DisplayName = "register-entry: entries of a register of the manifests or of the catalog; their aliases resolve; statuses in kebab-case; fields from YAML")]
	public void Manifest()
	{
		using var space = new Space(Actors);
		var existing = space.Add("register", "debts", folder: "notes");
		Assert.True(DocEditor.AddEntry(space.Catalog(), Display(space), space.Root, File(space, existing), "A debt", new() { Key = "D-1" }, Now).Write());
		space.Write("_docs/ideas/README.md", "# Ideas\n\nThe board.\n");
		space.Write("adopcion.yaml", """
			schema: 1.0.0
			batch: test
			targets:
			  - action: adopt
			    from: _docs/ideas/README.md
			    type: register
			    profile: backlog
			    status: active
			    title: Ideas
			    path: _docs/ideas/REGISTER-ideas.md
			    created: 2026-10-04
			  - action: register-entry
			    register: _docs/ideas/REGISTER-ideas.md
			    key: I-20
			    title: Licencias y protección comercial
			    status: En diseño
			    created: 2026-10-04
			    summary: Cómo se licencia y se protege lo comercial.
			    fields:
			      prioridad: 2
			      bloque: "Mejoras y features"
			      ref: "007"
			      espera: [I-19]
			      revisada: true
			  - action: register-entry
			    register: _docs/ideas/REGISTER-ideas.md
			    title: Una idea de enero
			    status: Hecha
			    created: 2026-01-16
			    closed: 2026-02-01
			    relations:
			      - kind: related-to
			        to: I-20
			      - kind: depends-on
			        to: D-1
			  - action: register-entry
			    register: _docs/notes/REGISTER-debts.md
			    title: Another debt
			    created: 2026-10-04

			""");
		var diagnostics = new List<FxDiagnostic>();
		var targets = DocAdoption.Read(space.Full("adopcion.yaml"), "adopcion.yaml", diagnostics)!;
		var change = DocAdoption.Apply(space.Catalog(), Display(space), targets, "editor", Now);
		Assert.True(change.Write(), Ok(change));
		var ideas = Sidecar(space, "ideas/REGISTER-ideas.md").Entries;
		Assert.Equal(("licencias-y-proteccion-comercial", "I-20", "en-diseno"), (ideas[0].Name, ideas[0].Key, ideas[0].Status));
		var fields = ideas[0].Fields.ToDictionary(f => f.Key, f => f.Value.ValueKind);
		Assert.Equal((JsonValueKind.Number, JsonValueKind.String, JsonValueKind.String, JsonValueKind.Array, JsonValueKind.True),
			(fields["prioridad"], fields["bloque"], fields["ref"], fields["espera"], fields["revisada"]));
		Assert.Equal(("hecha", "2026-02-01T00:00:00Z"), (ideas[1].Status, ideas[1].ClosedAt));
		Assert.Equal([ideas[0].Id, Sidecar(space, existing).Entries[0].Id], ideas[1].Relations.Select(r => r.TargetId).OrderBy(r => r == ideas[0].Id ? 0 : 1));
		Assert.Equal(["a-debt", "another-debt"], Sidecar(space, existing).Entries.Select(e => e.Name));
		Assert.Empty(space.Problems());
	}

	[Theory(DisplayName = "register-entry: what stops a manifest")]
	[InlineData("  - action: register-entry\n    register: _docs/notes/REGISTER-none.md\n    title: X\n    created: 2026-10-04\n")]
	[InlineData("  - action: register-entry\n    register: _docs/notes/REGISTER-debts.md\n    title: A debt\n    created: 2026-10-04\n")]
	[InlineData("  - action: register-entry\n    register: _docs/notes/REGISTER-debts.md\n    key: D-1\n    title: X\n    created: 2026-10-04\n")]
	[InlineData("  - action: register-entry\n    register: _docs/notes/REGISTER-debts.md\n    title: X\n    created: 2026-10-04\n    relations:\n      - kind: related-to\n        to: I-99\n")]
	[InlineData("  - action: register-entry\n    register: _docs/notes/ADR-x.md\n    title: X\n    created: 2026-10-04\n")]
	public void ManifestRefused(string targets)
	{
		using var space = new Space(Actors);
		var existing = space.Add("register", "debts", folder: "notes");
		space.Add("adr", "x", folder: "notes", created: DocsCheckTest.At);
		Assert.True(DocEditor.AddEntry(space.Catalog(), Display(space), space.Root, File(space, existing), "A debt", new() { Key = "D-1" }, Now).Write());
		space.Write("adopcion.yaml", $"schema: 1.0.0\ntargets:\n{targets}");
		var diagnostics = new List<FxDiagnostic>();
		var change = DocAdoption.Apply(space.Catalog(), Display(space), DocAdoption.Read(space.Full("adopcion.yaml"), "adopcion.yaml", diagnostics)!, "editor", Now);
		Assert.Contains(DocAdoption.InvalidManifest, Codes(change));
		Assert.False(change.Write());
	}

	// the command line

	[Fact(DisplayName = "fx doc entry add, set, close and list; fx doc relate to an entry by its key")]
	public void Cli()
	{
		var (space, register, research) = Ideas();
		using var _ = space;
		var add = DocsCheckTest.Json(space.Root, "fx-doc-edit/1", "doc", "entry", "add", register, "Una idea", "--key", "I-7", "--field", "prioridad=2",
			"--field", "tema=Docs", "--field", "espera=[\"I-1\"]", "--relation", $"informs={research}");
		Assert.True(add.GetProperty("written").GetBoolean(), add.ToString());
		Assert.Equal(0, DocsCheckTest.Fx(space.Root, "doc", "entry", "set", register, "I-7", "--status", "boceto", "--field", "tema=").ExitCode);
		Assert.Equal(0, DocsCheckTest.Fx(space.Root, "doc", "entry", "add", register, "Otra idea").ExitCode);
		Assert.Equal(0, DocsCheckTest.Fx(space.Root, "doc", "entry", "close", register, "otra-idea", "--status", "descartada").ExitCode);
		var list = DocsCheckTest.Json(space.Root, "fx-doc-entries/1", "doc", "entry", "list", register, "--open");
		var entry = Assert.Single(list.GetProperty("entries").EnumerateArray());
		Assert.Equal(("I-7", "boceto", 2), (entry.GetProperty("key").GetString(), entry.GetProperty("status").GetString(), entry.GetProperty("fields").GetProperty("prioridad").GetInt32()));
		Assert.False(entry.GetProperty("fields").TryGetProperty("tema", out var _));
		Assert.Equal(("informs", research, "The faces"), (entry.GetProperty("relations")[0].GetProperty("kind").GetString(), entry.GetProperty("relations")[0].GetProperty("path").GetString(),
			entry.GetProperty("relations")[0].GetProperty("title").GetString()));
		Assert.Equal(2, DocsCheckTest.Json(space.Root, "fx-doc-entries/1", "doc", "entry", "list", register).GetProperty("entries").GetArrayLength());
		Assert.Contains("I-7", DocsCheckTest.Fx(space.Root, "doc", "entry", "list", register).Stdout);
		Assert.Equal(0, DocsCheckTest.Fx(space.Root, "doc", "relate", research, "related-to", "I-7").ExitCode);
		Assert.True(DocsCheckTest.Json(space.Root, "fx-doc-check/1", "doc", "check").GetProperty("ok").GetBoolean());
		Assert.Equal(2, DocsCheckTest.Fx(space.Root, "doc", "entry", "add", register, "X", "--field", "nada").ExitCode);
	}
}
