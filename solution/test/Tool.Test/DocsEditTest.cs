using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Fuxion.Tool.Core.Docs;
using Fuxion.Tool.Core.Layout;
using Xunit;
using Space = Fuxion.Tool.Test.DocsCheckTest.Space;

namespace Fuxion.Tool.Test;

/// <summary>
/// What writes in a catalog (plan U, U4 step 4; the SPEC of the module, 5.1 and 5.3): <c>new</c>, <c>finalize</c>,
/// <c>status</c> and <c>relate</c>, each all or nothing, checked with the validator of the module; and their commands.
/// </summary>
public sealed class DocsEditTest
{
	const string Actors = "schema: 1.0.0\nactors: [editor]\n";
	static readonly DateTimeOffset At = DocsCheckTest.At;
	static readonly DateTimeOffset Later = At.AddDays(2);

	static Func<string, string> Display(Space space) => p => FxLayout.Relative(space.Root, p);

	static DocChange New(Space space, string type, string title, string? slug = null, string? profile = null, string? folder = null, IReadOnlyList<string>? tags = null,
		string? actor = "editor", string? currentDirectory = null)
		=> DocEditor.New(space.Catalog(), Display(space), currentDirectory ?? space.Root, type, title, slug, profile, folder, tags ?? [], actor, At);

	static DocFile File(Space space, string path) => space.Catalog().Files.Single(f => f.Path == path);

	static DocSidecar Sidecar(Space space, string path)
	{
		using var json = DocSidecar.Parse(space.Read($"_docs/{Space.Sidecar(path)}"));
		return DocSidecar.From(json.RootElement);
	}

	static List<string> Codes(DocChange change) => change.Diagnostics.Select(d => d.Code).ToList();

	// new

	[Fact(DisplayName = "new: a historical document in _records/AAAA/MM of now, its H1 and its sidecar sealed, valid; written all at once")]
	public void NewHistorical()
	{
		using var space = new Space(Actors);
		var change = New(space, "adr", "Usar sidecars", tags: ["docs", "agents", "docs"]);
		Assert.True(change.Ok, string.Join("; ", change.Diagnostics.Select(d => d.Message.English)));
		Assert.Equal(["_records/2026/10/ADR-usar-sidecars.md"], change.Documents);
		Assert.Equal(["_docs/_records/2026/10/ADR-usar-sidecars.md", "_docs/_records/2026/10/ADR-usar-sidecars.meta.json"], change.Files.Select(f => f.File));
		Assert.False(System.IO.File.Exists(space.Full("_docs/_records/2026/10/ADR-usar-sidecars.md")));
		Assert.True(change.Write());
		Assert.Equal("# Usar sidecars\n", space.Read("_docs/_records/2026/10/ADR-usar-sidecars.md"));
		var sidecar = Sidecar(space, "_records/2026/10/ADR-usar-sidecars.md");
		Assert.Equal(("adr", "draft", "Usar sidecars", "editor", "2026-10-21T10:00:00Z", "2026-10-21T10:00:00Z"),
			(sidecar.Type, sidecar.Status, sidecar.Title, sidecar.CreatedBy, sidecar.CreatedAt, sidecar.UpdatedAt));
		Assert.Equal(["agents", "docs"], sidecar.Tags.Order());
		Assert.Equal(7, Guid.Parse(sidecar.Id).Version);
		Assert.Empty(space.Problems());
	}

	[Theory(DisplayName = "new: the slug a title proposes, the same on every system")]
	[InlineData("Usar sidecars", "usar-sidecars")]
	[InlineData("Ñandú: ¿qué es? (2026)", "nandu-que-es-2026")]
	[InlineData("  El módulo `docs` de `fx`  ", "el-modulo-docs-de-fx")]
	[InlineData("Æther & Straße — Œuvre", "aether-strasse-oeuvre")]
	[InlineData("Łódź, Kraków; İstanbul", "lodz-krakow-istanbul")]
	public void ProposeSlug(string title, string slug)
	{
		Assert.Equal(slug, DocEditor.ProposeSlug(title));
		Assert.All(DocEditor.Letters, l => Assert.All(l.Marked, c => Assert.Equal(l.Plain.ToString(), DocEditor.ProposeSlug(c.ToString()))));
	}

	[Fact(DisplayName = "new: a stable document in the folder given (from here or from the catalog), at the root by default; a readme is README.md")]
	public void NewStable()
	{
		using var space = new Space(Actors);
		Assert.Equal(["DESIGN-the-catalog.md"], New(space, "design", "The catalog", profile: "software").Documents);
		Assert.Equal(["notes/deep/DESIGN-the-catalog.md"], New(space, "design", "The catalog", profile: "software", folder: "notes/deep").Documents);
		Directory.CreateDirectory(space.Full("_docs/notes"));
		Assert.Equal(["notes/RULE-one.md"], New(space, "rule", "One", profile: "standard", folder: ".", currentDirectory: space.Full("_docs/notes")).Documents);
		var readme = New(space, "readme", "Notes", folder: "notes");
		Assert.Equal(["notes/README.md"], readme.Documents);
		Assert.True(readme.Write());
		Assert.True(New(space, "readme", "Deep", folder: "notes/deep").Write());
		Assert.Empty(space.Problems());
	}

	[Theory(DisplayName = "new: what it refuses, writing nothing")]
	[InlineData("plan", "A plan", null, null, null, DocChecker.InvalidProfile)]
	[InlineData("plan", "A plan", "bogus", null, null, DocChecker.InvalidProfile)]
	[InlineData("adr", "An adr", "implementation", null, null, DocChecker.InvalidProfile)]
	[InlineData("adr", "An adr", null, "notes", null, DocEditor.InvalidFolder)]
	[InlineData("design", "A design", "software", "_records/2026/10", null, DocEditor.InvalidFolder)]
	[InlineData("design", "A design", "software", "../outside", null, DocEditor.InvalidFolder)]
	[InlineData("design", "A design", "software", ".hidden", null, DocEditor.InvalidFolder)]
	[InlineData("adr", "An adr", null, null, "Bad_Slug", DocChecker.InvalidName)]
	[InlineData("adr", "¿?", null, null, null, DocChecker.InvalidName)]
	[InlineData("adr", " ", null, null, null, DocEditor.InvalidTitle)]
	[InlineData("adr", "Two\nlines", null, null, null, DocEditor.InvalidTitle)]
	[InlineData("view", "Status", "status", null, null, DocEditor.ViewsBySync)]
	public void NewRefused(string type, string title, string? profile, string? folder, string? slug, string code)
	{
		using var space = new Space(Actors);
		var change = New(space, type, title, slug, profile, folder);
		Assert.Contains(code, Codes(change));
		Assert.False(change.Write());
		Assert.Empty(space.Catalog().Files);
	}

	[Fact(DisplayName = "new: the actor, given and one of docs.yaml; an invalid tag, refused by the validator")]
	public void NewActorAndTags()
	{
		using var space = new Space(Actors);
		Assert.Equal([DocEditor.ActorMissing], Codes(New(space, "adr", "An adr", actor: null)));
		Assert.Equal([DocChecker.UnknownActor], Codes(New(space, "adr", "An adr", actor: "someone")));
		Assert.Equal(DocChecker.InvalidMetadata, Assert.Single(New(space, "adr", "An adr", tags: ["Bad Tag"]).Diagnostics).Code);
		using var none = new Space();
		Assert.Equal([DocChecker.UnknownActor], Codes(New(none, "adr", "An adr")));
	}

	[Fact(DisplayName = "new: not where its file, its slug in the folder or its name in the catalog are taken")]
	public void NewTaken()
	{
		using var space = new Space(Actors);
		space.Add("adr", "usar-sidecars");
		space.Add("design", "the-catalog", folder: "notes");
		Assert.Equal([DocEditor.NameTaken], Codes(New(space, "adr", "Usar sidecars")));
		Assert.Equal([DocEditor.NameTaken], Codes(New(space, "rfc", "Usar sidecars")));
		Assert.Equal([DocEditor.NameTaken], Codes(New(space, "design", "The catalog", profile: "software", folder: "other")));
		Assert.True(New(space, "design", "The catalog", profile: "software", folder: "other", slug: "the-other-catalog").Ok);
	}

	[Fact(DisplayName = "a change does not write if a file it writes changed on disk since it was read")]
	public void ChangedOnDisk()
	{
		using var space = new Space(Actors);
		var path = space.Add("adr", "open");
		System.IO.File.AppendAllText(space.Full($"_docs/{path}"), "More.\n");
		var change = DocEditor.Finalize(space.Catalog(), Display(space), [File(space, path)], null, Later);
		var before = space.Read($"_docs/{Space.Sidecar(path)}");
		System.IO.File.AppendAllText(space.Full($"_docs/{Space.Sidecar(path)}"), " ");
		Assert.False(change.Write());
		Assert.Equal(DocChange.ChangedOnDisk, Assert.Single(change.Diagnostics).Code);
		Assert.Equal(before + " ", space.Read($"_docs/{Space.Sidecar(path)}"));
	}

	// finalize

	[Fact(DisplayName = "finalize: the documents that changed, their integrity again and updatedAt now; the rest untouched")]
	public void FinalizeChanged()
	{
		using var space = new Space(Actors);
		var edited = space.Add("adr", "edited");
		var untouched = space.Add("adr", "untouched");
		var tagged = space.Add("design", "tagged", folder: "notes");
		System.IO.File.AppendAllText(space.Full($"_docs/{edited}"), "More.\n");
		space.Change(tagged, s => s["tags"] = new System.Text.Json.Nodes.JsonArray("zeta", "alpha"));
		var before = space.Read($"_docs/{Space.Sidecar(untouched)}");
		var change = DocEditor.Finalize(space.Catalog(), Display(space), null, null, Later);
		Assert.Equal([edited, tagged], change.Documents.Order());
		Assert.True(change.Write());
		Assert.Equal(before, space.Read($"_docs/{Space.Sidecar(untouched)}"));
		Assert.Equal("2026-10-23T10:00:00Z", Sidecar(space, edited).UpdatedAt);
		Assert.Equal(["alpha", "zeta"], Sidecar(space, tagged).Tags);
		Assert.Empty(space.Problems());
		var nothing = DocEditor.Finalize(space.Catalog(), Display(space), null, null, Later);
		Assert.Equal((false, $"{DocEditor.Unchanged}.nothing-to-finalize"), (nothing.Changed, Assert.Single(nothing.Diagnostics).Message.Key));
		Assert.Equal($"{DocEditor.Unchanged}.finalized",
			Assert.Single(DocEditor.Finalize(space.Catalog(), Display(space), [File(space, edited)], null, Later).Diagnostics).Message.Key);
	}

	[Fact(DisplayName = "finalize: a frozen one whose content changed, only with a correction, which goes at its end with its line ends; a view edited, never")]
	public void FinalizeFrozen()
	{
		using var space = new Space(Actors);
		var frozen = space.Add("adr", "frozen", status: "accepted");
		var open = space.Add("adr", "open");
		var view = space.Add("view", "status");
		var text = space.Read($"_docs/{frozen}").Replace("\n", "\r\n") + "A fix.\r\n";
		System.IO.File.WriteAllText(space.Full($"_docs/{frozen}"), text);
		System.IO.File.AppendAllText(space.Full($"_docs/{view}"), "More.\n");
		Assert.Equal([DocChecker.FrozenChanged], Codes(DocEditor.Finalize(space.Catalog(), Display(space), [File(space, frozen)], null, Later)));
		Assert.Equal([DocChecker.ViewEdited], Codes(DocEditor.Finalize(space.Catalog(), Display(space), [File(space, view)], null, Later)));
		Assert.Equal([DocEditor.Correction], Codes(DocEditor.Finalize(space.Catalog(), Display(space), [File(space, open)], "A note.", Later)));
		Assert.True(DocEditor.Finalize(space.Catalog(), Display(space), [File(space, frozen)], "A fix of the text.", Later).Write());
		// the note, in the language of the user
		var note = new Core.Results.FxText(DocEditor.Correction, "2026-10-23", "A fix of the text.").ToString();
		Assert.Equal($"{text}\r\n> {note}\r\n", space.Read($"_docs/{frozen}"));
		Assert.DoesNotContain(space.Problems(), p => p.File == $"_docs/{frozen}");
	}

	[Fact(DisplayName = "finalize: a document that cannot be read gives its errors, and nothing is written")]
	public void FinalizeUnreadable()
	{
		using var space = new Space(Actors);
		var good = space.Add("adr", "good");
		var bad = space.Add("adr", "bad");
		System.IO.File.AppendAllText(space.Full($"_docs/{good}"), "More.\n");
		space.Write($"_docs/{Space.Sidecar(bad)}", "{");
		var change = DocEditor.Finalize(space.Catalog(), Display(space), [File(space, good), File(space, bad)], null, Later);
		Assert.Equal([DocChecker.InvalidMetadata], Codes(change));
		Assert.False(change.Write());
	}

	// status

	[Theory(DisplayName = "status: only to another status of its type, never back to draft, and a frozen one never open again")]
	[InlineData("adr", "draft", "accepted", true)]
	[InlineData("adr", "accepted", "superseded", true)]
	[InlineData("adr", "accepted", "draft", false)]
	[InlineData("plan", "draft", "in-progress", true)]
	[InlineData("plan", "in-progress", "blocked", true)]
	[InlineData("plan", "in-progress", "draft", false)]
	[InlineData("plan", "completed", "in-progress", false)]
	[InlineData("plan", "completed", "cancelled", true)]
	[InlineData("design", "deprecated", "review", true)]
	[InlineData("spec", "approved", "draft", false)]
	public void Transitions(string type, string from, string to, bool allowed)
	{
		Assert.Equal(allowed, DocTypes.Find(type)!.CanMove(from, to));
		using var space = new Space(Actors);
		var path = space.Add(type, "doc", folder: DocTypes.Find(type)!.Temporality == DocTemporality.Stable ? "notes" : null, status: from);
		var change = DocEditor.SetStatus(space.Catalog(), Display(space), File(space, path), to, Later);
		Assert.Equal(allowed, change.Ok);
		if (!allowed)
			Assert.Equal([DocEditor.InvalidTransition], Codes(change));
		else
		{
			Assert.True(change.Write());
			Assert.Equal((to, "2026-10-23T10:00:00Z"), (Sidecar(space, path).Status, Sidecar(space, path).UpdatedAt));
			Assert.Empty(space.Problems());
		}
	}

	[Fact(DisplayName = "status: the same one changes nothing; one of another type, an error; it finalizes an open edit, and a frozen edit blocks it")]
	public void Status()
	{
		using var space = new Space(Actors);
		var adr = space.Add("adr", "doc");
		Assert.Equal($"{DocEditor.Unchanged}.status", Assert.Single(DocEditor.SetStatus(space.Catalog(), Display(space), File(space, adr), "draft", Later).Diagnostics).Message.Key);
		Assert.Equal([DocChecker.InvalidStatus], Codes(DocEditor.SetStatus(space.Catalog(), Display(space), File(space, adr), "completed", Later)));
		System.IO.File.AppendAllText(space.Full($"_docs/{adr}"), "Decided.\n");
		Assert.True(DocEditor.SetStatus(space.Catalog(), Display(space), File(space, adr), "accepted", Later).Write());
		Assert.Empty(space.Problems());
		System.IO.File.AppendAllText(space.Full($"_docs/{adr}"), "Changed.\n");
		Assert.Equal([DocChecker.FrozenChanged], Codes(DocEditor.SetStatus(space.Catalog(), Display(space), File(space, adr), "superseded", Later)));
	}

	// relate

	[Fact(DisplayName = "relate: added in the source only, once; taken out; of a kind its types allow, not to itself")]
	public void Relate()
	{
		using var space = new Space(Actors);
		var adr = space.Add("adr", "decision");
		var rfc = space.Add("rfc", "proposal");
		var design = space.Add("design", "a-design", folder: "notes");
		var targetBefore = space.Read($"_docs/{Space.Sidecar(adr)}");
		var add = DocEditor.Relate(space.Catalog(), Display(space), File(space, rfc), "decided-by", File(space, adr), false, Later);
		Assert.Equal([rfc], add.Documents);
		Assert.True(add.Write());
		Assert.Equal(targetBefore, space.Read($"_docs/{Space.Sidecar(adr)}"));
		Assert.Equal([new DocRelation("decided-by", space.Ids[adr])], Sidecar(space, rfc).Relations);
		Assert.Empty(space.Problems());
		Assert.Equal($"{DocEditor.Unchanged}.relation",
			Assert.Single(DocEditor.Relate(space.Catalog(), Display(space), File(space, rfc), "decided-by", File(space, adr), false, Later).Diagnostics).Message.Key);
		Assert.Equal([DocChecker.InvalidRelation], Codes(DocEditor.Relate(space.Catalog(), Display(space), File(space, rfc), "decided-by", File(space, design), false, Later)));
		Assert.Equal([DocChecker.InvalidRelation], Codes(DocEditor.Relate(space.Catalog(), Display(space), File(space, rfc), "related-to", File(space, rfc), false, Later)));
		Assert.True(DocEditor.Relate(space.Catalog(), Display(space), File(space, rfc), "decided-by", File(space, adr), true, Later).Write());
		Assert.Empty(Sidecar(space, rfc).Relations);
		Assert.Equal($"{DocEditor.Unchanged}.no-relation",
			Assert.Single(DocEditor.Relate(space.Catalog(), Display(space), File(space, rfc), "decided-by", File(space, adr), true, Later).Diagnostics).Message.Key);
	}

	[Fact(DisplayName = "relate supersedes: the target goes to its status of the class replaced in the same change, if its type has one")]
	public void Supersedes()
	{
		using var space = new Space(Actors);
		var old = space.Add("adr", "old", status: "accepted");
		var @new = space.Add("adr", "new", status: "accepted");
		var change = DocEditor.Relate(space.Catalog(), Display(space), File(space, @new), "supersedes", File(space, old), false, Later);
		Assert.Equal([@new, old], change.Documents.Order());
		Assert.True(change.Write());
		Assert.Equal("superseded", Sidecar(space, old).Status);
		Assert.Empty(space.Problems());
		// a plan has no status of the class replaced: only the relation
		var plan = space.Add("plan", "old-plan", status: "completed");
		var newPlan = space.Add("plan", "new-plan");
		Assert.Equal([newPlan], DocEditor.Relate(space.Catalog(), Display(space), File(space, newPlan), "supersedes", File(space, plan), false, Later).Documents);
	}

	// the command line

	[Fact(DisplayName = "fx doc new: --dry-run shows the files and writes nothing; then written, and the catalog is valid")]
	public void CliNew()
	{
		using var space = new Space(Actors);
		var dry = DocsCheckTest.Json(space.Root, "fx-doc-edit/1", "doc", "new", "adr", "Usar sidecars", "--actor", "editor", "--tag", "docs", "--dry-run");
		Assert.Equal((true, true, false), (dry.GetProperty("ok").GetBoolean(), dry.GetProperty("changed").GetBoolean(), dry.GetProperty("written").GetBoolean()));
		Assert.Equal(2, dry.GetProperty("files").GetArrayLength());
		Assert.Contains("+# Usar sidecars", dry.GetProperty("files")[0].GetProperty("diff").GetString());
		Assert.Empty(space.Catalog().Files);
		var (exitCode, stdout, _) = DocsCheckTest.Fx(space.Root, "doc", "new", "adr", "Usar sidecars", "--actor", "editor");
		Assert.Equal(0, exitCode);
		var path = Assert.Single(space.Catalog().Files).Path;
		Assert.Contains($"Written: {path}.", stdout);
		Assert.True(DocsCheckTest.Json(space.Root, "fx-doc-check/1", "doc", "check").GetProperty("ok").GetBoolean());
		Assert.Equal(1, DocsCheckTest.Fx(space.Root, "doc", "new", "adr", "Usar sidecars", "--actor", "editor").ExitCode);
		Assert.Equal(1, DocsCheckTest.Fx(space.Root, "doc", "new", "adr", "Otra").ExitCode);
	}

	[Fact(DisplayName = "fx doc status, relate and finalize: by path or identity; a document that is not there, a usage error")]
	public void CliEdit()
	{
		using var space = new Space(Actors);
		var adr = space.Add("adr", "decision");
		var rfc = space.Add("rfc", "proposal");
		Assert.Equal(0, DocsCheckTest.Fx(space.Root, "doc", "status", adr, "accepted").ExitCode);
		var relate = DocsCheckTest.Json(space.Root, "fx-doc-edit/1", "doc", "relate", $"_docs/{rfc}", "decided-by", space.Ids[adr]);
		Assert.True(relate.GetProperty("written").GetBoolean());
		Assert.Equal([rfc], relate.GetProperty("documents").EnumerateArray().Select(d => d.GetString()));
		System.IO.File.AppendAllText(space.Full($"_docs/{rfc}"), "More.\n");
		Assert.Contains("Written:", DocsCheckTest.Fx(space.Root, "doc", "finalize").Stdout);
		Assert.Contains("Nothing to finalize", DocsCheckTest.Fx(space.Root, "doc", "finalize").Stdout);
		Assert.True(DocsCheckTest.Json(space.Root, "fx-doc-check/1", "doc", "check").GetProperty("ok").GetBoolean());
		Assert.Equal(2, DocsCheckTest.Fx(space.Root, "doc", "status", "nothing.md", "accepted").ExitCode);
		Assert.Equal(2, DocsCheckTest.Fx(space.Root, "doc", "relate", rfc, "likes", adr).ExitCode);
		Assert.Equal(1, DocsCheckTest.Fx(space.Root, "doc", "status", adr, "draft").ExitCode);
	}
}

/// <summary>The actor from the environment (plan U, decision 20): it changes the process, so it runs alone.</summary>
[Collection(nameof(ProcessCollection))]
public sealed class DocsActorTest
{
	[Fact(DisplayName = "fx doc new: the actor from FX_DOCS_ACTOR when --actor does not say it")]
	public void ActorFromEnvironment()
	{
		using var space = new Space("schema: 1.0.0\nactors: [editor]\n");
		var previous = Environment.GetEnvironmentVariable(DocEditor.ActorVariable);
		Environment.SetEnvironmentVariable(DocEditor.ActorVariable, "editor");
		try
		{
			Assert.Equal(0, DocsCheckTest.Fx(space.Root, "doc", "new", "adr", "From the environment").ExitCode);
			Assert.Contains("\"createdBy\": \"editor\"", space.Read("_docs/_records/" + DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy'/'MM", System.Globalization.CultureInfo.InvariantCulture) + "/ADR-from-the-environment.meta.json"));
		}
		finally
		{
			Environment.SetEnvironmentVariable(DocEditor.ActorVariable, previous);
		}
	}
}
