using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Fuxion.Tool.Core.Docs;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Xunit;
using Space = Fuxion.Tool.Test.DocsCheckTest.Space;

namespace Fuxion.Tool.Test;

/// <summary>
/// The adoption and the moves of the module (plan U, U4 step 5; the SPEC of the module, 5.5 and 5.6): the bootstrap
/// documents, the batches of the manifests, <c>move</c>, and the links of the catalog that follow what moves.
/// </summary>
public sealed class DocsAdoptTest
{
	static readonly DateTimeOffset Now = DocsCheckTest.At.AddDays(1);

	static Func<string, string> Display(Space space) => p => FxLayout.Relative(space.Root, p);

	static DocSidecar Sidecar(Space space, string path)
	{
		using var json = DocSidecar.Parse(space.Read($"_docs/{Space.Sidecar(path)}"));
		return DocSidecar.From(json.RootElement);
	}

	static List<string> Codes(DocChange change) => change.Diagnostics.Where(d => d.Severity != FxSeverity.Info).Select(d => d.Code).ToList();

	static string Ok(DocChange change) => string.Join("; ", change.Diagnostics.Select(d => $"{d.Code} {d.File}:{d.Line} {d.Message.English}"));

	// the block of status

	[Fact(DisplayName = "the block of status of a bootstrap document: in lines of a code block, or in values between backticks")]
	public void Header()
	{
		Assert.Equal(new DocHeader("adr", null, "accepted", "2026-10-18"),
			DocMarkdown.Header("# A decision\n\n## Estado de este documento\n\n- Tipo: `adr`. Estado: `accepted`.\n- Creación: `2026-10-18` (la de la decisión).\n\n## Contexto\n\n- Tipo: `rfc`.\n"));
		Assert.Equal(new DocHeader("rule", "standard", "active", null),
			DocMarkdown.Header("# A rule\n\n## Estado de este documento\n\n```text\ntype: rule\nprofile: standard\nstatus: active\n```\n"));
		Assert.Equal(new DocHeader("rfc", null, "review", "2026-10-21T19:44:04Z"),
			DocMarkdown.Header("# An RFC\n\n## Estado de la RFC\n\n- Estado: `review`.\n- Creación: `2026-10-21T19:44:04Z`.\n- Tipo: `rfc`.\n- Corrección anotada (19-oct): nada.\n"));
		Assert.Equal(new DocHeader(null, null, null, null), DocMarkdown.Header("# A readme\n\nText: `not a header`.\n"));
	}

	// the bootstrap documents

	sealed class Bootstrap : IDisposable
	{
		public Space Space { get; } = new("""
			schema: 1.0.0
			actors: [editor]
			# the documents of the bootstrap regime
			bootstrap:
			  - _records/2026/10/ADR-una-decision.md   # the first one
			  - _governance/RULE-layout.md
			  - README.md
			""");

		public Bootstrap()
		{
			Space.Write("_docs/_records/2026/10/ADR-una-decision.md", "# Una decisión\n\n## Estado de este documento\n\n- Tipo: `adr`. Estado: `accepted`.\n- Creación: `2026-10-18`.\n\nTexto.\n");
			Space.Write("_docs/_governance/RULE-layout.md", "# Layout\n\n## Estado de este documento\n\n```text\ntype: rule\nprofile: standard\nstatus: active\n```\n");
			Space.Write("_docs/README.md", "# Notas\n");
		}

		public void Dispose() => Space.Dispose();
	}

	[Fact(DisplayName = "adopt: a bootstrap document gets its sidecar from its block of status, its Markdown untouched, and leaves the list")]
	public void AdoptBootstrap()
	{
		using var bootstrap = new Bootstrap();
		var space = bootstrap.Space;
		var adr = "_records/2026/10/ADR-una-decision.md";
		var before = space.Read($"_docs/{adr}");
		var change = DocAdoption.Bootstrap(space.Catalog(), Display(space), [space.Catalog().Files.Single(f => f.Path == adr)], null, null, null, null, "editor", Now);
		Assert.True(change.Write(), Ok(change));
		Assert.Equal(before, space.Read($"_docs/{adr}"));
		var sidecar = Sidecar(space, adr);
		Assert.Equal(("adr", "accepted", "Una decisión", "2026-10-18T00:00:00Z", "2026-10-22T10:00:00Z", "editor"),
			(sidecar.Type, sidecar.Status, sidecar.Title, sidecar.CreatedAt, sidecar.UpdatedAt, sidecar.CreatedBy));
		Assert.Equal("""
			schema: 1.0.0
			actors: [editor]
			# the documents of the bootstrap regime
			bootstrap:
			  - _governance/RULE-layout.md
			  - README.md
			""".Replace("\r\n", "\n"), space.Read("_fx/docs/docs.yaml").Replace("\r\n", "\n"));
		Assert.Equal(2, space.Problems().Count(p => p.Code == DocChecker.BootstrapPending));
		Assert.DoesNotContain(space.Problems(), p => p.Code != DocChecker.BootstrapPending);
		Assert.Equal([DocAdoption.Adopted], Codes(DocAdoption.Bootstrap(space.Catalog(), Display(space), [space.Catalog().Files.Single(f => f.Path == adr)], null, null,
			null, null, "editor", Now)));
	}

	[Fact(DisplayName = "adopt: all the bootstrap list, or nothing; without a date, one has to be given; the options win over the block")]
	public void AdoptAll()
	{
		using var bootstrap = new Bootstrap();
		var space = bootstrap.Space;
		var change = DocAdoption.Bootstrap(space.Catalog(), Display(space), null, null, null, null, null, "editor", Now);
		Assert.Equal([DocAdoption.CreatedMissing, DocAdoption.CreatedMissing], Codes(change));
		Assert.False(change.Write());
		// the date given goes to all of them: first those without one, then the rest with theirs
		var undated = space.Catalog().Files.Where(f => f.Path is "_governance/RULE-layout.md" or "README.md").ToList();
		Assert.True(DocAdoption.Bootstrap(space.Catalog(), Display(space), undated, null, null, null, "2026-08-24", "editor", Now).Write());
		Assert.True(DocAdoption.Bootstrap(space.Catalog(), Display(space), null, null, null, null, null, "editor", Now).Write());
		Assert.Empty(space.Problems());
		Assert.Equal(("rule", "standard", "active", "2026-08-24T00:00:00Z"),
			(Sidecar(space, "_governance/RULE-layout.md").Type, Sidecar(space, "_governance/RULE-layout.md").Profile, Sidecar(space, "_governance/RULE-layout.md").Status,
				Sidecar(space, "_governance/RULE-layout.md").CreatedAt));
		Assert.Equal("2026-10-18T00:00:00Z", Sidecar(space, "_records/2026/10/ADR-una-decision.md").CreatedAt);
		Assert.Equal(("readme", "active"), (Sidecar(space, "README.md").Type, Sidecar(space, "README.md").Status));
		Assert.DoesNotContain("bootstrap:\n  -", space.Read("_fx/docs/docs.yaml"));
		Assert.Equal(DocEditor.Unchanged,
			Assert.Single(DocAdoption.Bootstrap(space.Catalog(), Display(space), null, null, null, null, null, "editor", Now).Diagnostics).Code);
	}

	// the manifests

	/// <summary>A catalog of today: Markdown without sidecars, a document already adopted that links to them, and its manifest.</summary>
	sealed class Batch : IDisposable
	{
		public Space Space { get; } = new("schema: 1.0.0\nactors: [editor]\nbootstrap: [RoadMap.md]\n");

		public string Design { get; }

		public string Frozen { get; }

		public Batch()
		{
			Space.Write("_docs/RoadMap.md", "# Roadmap\n\nSee [faces](notes/Faces.md#intro).\n");
			Space.Write("_docs/notes/Faces.md", "# Faces\n\n## 1. Intro\n\nBack to the [roadmap](../RoadMap.md).\n\n## 9. Notas abiertas\n\n- An open note, see [the roadmap](<../RoadMap.md>).\n\n### 9.1 Deeper\n\nMore.\n\n## 10. End\n\nThe end.\n");
			Space.Write("_docs/analyzers/README.md", "# Analyzers\n\nThe rule [FX001](FX001.md) and the [old notes](old.md).\n");
			Space.Write("_docs/analyzers/FX001.md", "# FX001\n");
			Space.Write("_docs/analyzers/old.md", "# Old\n");
			File.WriteAllBytes(Space.Full("_docs/analyzers/latin.md"), [.. Encoding.Latin1.GetBytes("# Depuración\n\nAñadir trazas.\n")]);
			Design = Space.Add("design", "x", folder: "fx", body: "The [roadmap](../RoadMap.md) and the [faces](../notes/Faces.md).");
			Frozen = Space.Add("adr", "old", status: "accepted", body: "From the [roadmap](../../../RoadMap.md).");
		}

		public string Manifest(string targets)
		{
			Space.Write("adopcion.yaml", $"schema: 1.0.0\nbatch: test\ntargets:\n{targets}");
			return Space.Full("adopcion.yaml");
		}

		public const string Targets = """
			  - action: adopt
			    from: _docs/RoadMap.md
			    type: plan
			    profile: rollout
			    status: cancelled
			    title: Hoja de ruta
			    path: _docs/_records/2026/01/PLAN-hoja-de-ruta.md
			    created: 2026-01-16
			    tags: [roadmap, build]
			    relations:
			      - kind: related-to
			        to: _docs/fx/DESIGN-x.md
			    notes: For people.
			  - action: adopt
			    from: _docs/notes/Faces.md
			    type: research
			    status: draft
			    title: Faces
			    path: _docs/_records/2026/01/RESEARCH-faces.md
			    created: 2026-01-16
			  - action: extract
			    from: _docs/notes/Faces.md
			    sections: ["9. Notas abiertas"]
			    type: research
			    status: draft
			    title: Las notas abiertas de Faces
			    path: _docs/_records/2026/01/RESEARCH-notas-faces.md
			    created: 2026-01-16
			    relations:
			      - kind: derived-from
			        to: _docs/_records/2026/01/RESEARCH-faces.md
			  - action: skill-material
			    from: _docs/analyzers/README.md
			    for: I-37
			    path: _docs/fx/DESIGN-x/analizadores.md
			  - action: skill-material
			    from: _docs/analyzers/FX001.md
			    path: _docs/fx/DESIGN-x/FX001.md
			  - action: skill-material
			    from: _docs/analyzers/latin.md
			    path: _docs/fx/DESIGN-x/depurar.md
			    repair: encoding
			  - action: drop
			    from: _docs/analyzers/old.md
			    reason: Superseded.

			""";

		public DocChange Apply(string manifest)
		{
			var diagnostics = new List<FxDiagnostic>();
			var targets = DocAdoption.Read(manifest, "adopcion.yaml", diagnostics);
			if (targets is null)
			{
				var change = new DocChange(Space.Catalog(), Display(Space));
				change.Diagnostics.AddRange(diagnostics);
				return change;
			}
			return DocAdoption.Apply(Space.Catalog(), Display(Space), targets, "editor", Now);
		}

		public void Dispose() => Space.Dispose();
	}

	[Fact(DisplayName = "adopt --manifest: moves, extracts, annexes and drops at once; titles, relations and links follow; the rest of the catalog too")]
	public void Manifest()
	{
		using var batch = new Batch();
		var space = batch.Space;
		var designBefore = Sidecar(space, batch.Design);
		var change = batch.Apply(batch.Manifest(Batch.Targets.Replace("    notes: For people.\n", "    notes: For people.\n")));
		Assert.True(change.Ok, Ok(change));
		Assert.Contains(new DocFileMove("_docs/RoadMap.md", "_docs/_records/2026/01/PLAN-hoja-de-ruta.md"), change.Moved);
		Assert.Equal(["_docs/analyzers/old.md"], change.Removed);
		Assert.True(change.Write(), Ok(change));

		// the plan: moved, its H1 its title, its links from where it is now, its relation by identity
		Assert.False(File.Exists(space.Full("_docs/RoadMap.md")));
		Assert.Equal("# Hoja de ruta\n\nSee [faces](RESEARCH-faces.md#intro).\n", space.Read("_docs/_records/2026/01/PLAN-hoja-de-ruta.md"));
		var plan = Sidecar(space, "_records/2026/01/PLAN-hoja-de-ruta.md");
		Assert.Equal(("plan", "rollout", "cancelled", "2026-01-16T00:00:00Z", "editor"), (plan.Type, plan.Profile, plan.Status, plan.CreatedAt, plan.CreatedBy));
		Assert.Equal(["build", "roadmap"], plan.Tags.Order());
		Assert.Equal([new DocRelation("related-to", space.Ids[batch.Design])], plan.Relations);

		// the research and the extract, with its sections and its relation to the research
		Assert.Contains("Back to the [roadmap](PLAN-hoja-de-ruta.md).", space.Read("_docs/_records/2026/01/RESEARCH-faces.md"));
		Assert.Equal("# Las notas abiertas de Faces\n\n## 9. Notas abiertas\n\n- An open note, see [the roadmap](<PLAN-hoja-de-ruta.md>).\n\n### 9.1 Deeper\n\nMore.\n",
			space.Read("_docs/_records/2026/01/RESEARCH-notas-faces.md"));
		Assert.Equal(Sidecar(space, "_records/2026/01/RESEARCH-faces.md").Id, Assert.Single(Sidecar(space, "_records/2026/01/RESEARCH-notas-faces.md").Relations).TargetId);

		// the annexes, out of the catalog; their links follow, Latin-1 made UTF-8; the dropped one, gone
		Assert.Equal("# Analyzers\n\nThe rule [FX001](FX001.md) and the [old notes](../../analyzers/old.md).\n", space.Read("_docs/fx/DESIGN-x/analizadores.md"));
		Assert.Equal("# Depuración\n\nAñadir trazas.\n", space.Read("_docs/fx/DESIGN-x/depurar.md"));
		Assert.False(File.Exists(space.Full("_docs/analyzers/old.md")));

		// the document already adopted: its links follow, sealed again, its updatedAt kept; also a frozen one
		Assert.Equal("# The x\n\nThe [roadmap](../_records/2026/01/PLAN-hoja-de-ruta.md) and the [faces](../_records/2026/01/RESEARCH-faces.md).\n",
			space.Read($"_docs/{batch.Design}"));
		Assert.Equal(designBefore.UpdatedAt, Sidecar(space, batch.Design).UpdatedAt);
		Assert.Contains("[roadmap](../01/PLAN-hoja-de-ruta.md)", space.Read($"_docs/{batch.Frozen}"));
		// the bootstrap list, without the roadmap
		Assert.DoesNotContain("RoadMap", space.Read("_fx/docs/docs.yaml"));
		Assert.DoesNotContain(space.Problems(), p => p.Code != DocChecker.BrokenLink);
	}

	[Fact(DisplayName = "adopt --manifest: a source dropped while a document links to it, nothing is written")]
	public void ManifestDroppedLinked()
	{
		using var batch = new Batch();
		var change = batch.Apply(batch.Manifest("""
			  - action: drop
			    from: _docs/RoadMap.md

			"""));
		// two in the faces, one in the design, one in the adr
		Assert.Equal([DocChecker.BrokenLink, DocChecker.BrokenLink, DocChecker.BrokenLink, DocChecker.BrokenLink], Codes(change));
		Assert.False(change.Write());
		Assert.True(File.Exists(batch.Space.Full("_docs/RoadMap.md")));
	}

	[Theory(DisplayName = "adopt --manifest: a wrong target stops all of them")]
	[InlineData("  - action: move\n    from: _docs/RoadMap.md\n", DocAdoption.InvalidManifest)]
	[InlineData("  - action: register-entry\n    from: _docs/RoadMap.md\n    register: _docs/ideas/REGISTER-ideas.md\n    title: An idea\n", DocAdoption.InvalidManifest)]
	[InlineData("  - action: adopt\n    from: _docs/Nothing.md\n    type: plan\n    profile: rollout\n    title: X\n    path: _docs/_records/2026/01/PLAN-x.md\n    created: 2026-01-16\n", DocAdoption.InvalidManifest)]
	[InlineData("  - action: adopt\n    from: _docs/RoadMap.md\n    type: plan\n    profile: rollout\n    title: X\n    path: _docs/_records/2026/01/PLAN-x.md\n    created: January\n", DocAdoption.InvalidManifest)]
	[InlineData("  - action: adopt\n    from: _docs/RoadMap.md\n    type: memo\n    title: X\n    path: _docs/_records/2026/01/MEMO-x.md\n    created: 2026-01-16\n", DocAdoption.InvalidManifest)]
	[InlineData("  - action: adopt\n    from: _docs/RoadMap.md\n    type: plan\n    profile: rollout\n    title: X\n    path: outside/PLAN-x.md\n    created: 2026-01-16\n", DocAdoption.InvalidManifest)]
	[InlineData("  - action: adopt\n    from: _docs/RoadMap.md\n    type: plan\n    profile: rollout\n    title: X\n    path: _docs/_records/2026/01/PLAN-x.md\n    created: 2026-01-16\n    relations:\n      - kind: related-to\n        to: I-20\n", DocAdoption.InvalidManifest)]
	[InlineData("  - action: adopt\n    from: _docs/RoadMap.md\n    type: plan\n    profile: rollout\n    title: X\n    path: _docs/_records/2026/02/PLAN-x.md\n    created: 2026-01-16\n", DocChecker.InvalidLocation)]
	[InlineData("  - action: adopt\n    from: _docs/RoadMap.md\n    type: plan\n    title: X\n    path: _docs/_records/2026/01/PLAN-x.md\n    created: 2026-01-16\n", DocChecker.InvalidProfile)]
	[InlineData("  - action: extract\n    from: _docs/notes/Faces.md\n    sections: [\"7. Missing\"]\n    type: research\n    title: X\n    path: _docs/_records/2026/01/RESEARCH-x.md\n    created: 2026-01-16\n", DocAdoption.InvalidManifest)]
	[InlineData("  - action: adopt\n    from: _docs/RoadMap.md\n    type: plan\n    profile: rollout\n    title: X\n    path: _docs/_records/2026/01/PLAN-x.md\n    created: 2026-01-16\n  - action: drop\n    from: _docs/RoadMap.md\n", DocAdoption.InvalidManifest)]
	[InlineData("  - action: adopt\n    from: _docs/RoadMap.md\n    type: plan\n    profile: rollout\n    title: X\n    path: _docs/_records/2026/01/PLAN-x.md\n    created: 2026-01-16\n    owner: someone\n", DocAdoption.InvalidManifest)]
	public void ManifestRefused(string targets, string code)
	{
		using var batch = new Batch();
		var change = batch.Apply(batch.Manifest(targets));
		Assert.Contains(code, Codes(change));
		Assert.False(change.Write());
		Assert.True(File.Exists(batch.Space.Full("_docs/RoadMap.md")));
	}

	[Fact(DisplayName = "adopt --manifest: a source outside the catalog (a mount read only) is copied, and stays")]
	public void ManifestOutside()
	{
		using var batch = new Batch();
		batch.Space.Write("lineage/old/repo/notes.md", "# Notes\n");
		var change = batch.Apply(batch.Manifest("""
			  - action: adopt
			    from: lineage/old/repo/notes.md
			    type: research
			    title: Las notas de antes
			    path: _docs/_records/2026/01/RESEARCH-notas-de-antes.md
			    created: 2026-01-16

			"""));
		Assert.True(change.Write(), Ok(change));
		Assert.Equal("# Las notas de antes\n", batch.Space.Read("_docs/_records/2026/01/RESEARCH-notas-de-antes.md"));
		Assert.Equal("# Notes\n", batch.Space.Read("lineage/old/repo/notes.md"));
	}

	// move

	[Fact(DisplayName = "move: a stable document to another folder, with its annexes; every link follows it, and what was clean keeps its updatedAt")]
	public void Move()
	{
		using var space = new Space("schema: 1.0.0\nactors: [editor]\n");
		var rule = space.Add("rule", "y", folder: "norms");
		var design = space.Add("design", "x", folder: "notes", body: "See the [rule](../norms/RULE-y.md#part) and the [diagram](DESIGN-x/diagram.png).");
		space.Write("_docs/notes/DESIGN-x/diagram.png", "png");
		space.Write("_docs/notes/DESIGN-x/extra.md", "# Extra\n\nThe [rule](../../norms/RULE-y.md) and [the design](../DESIGN-x.md).\n");
		var other = space.Add("adr", "uses-x", status: "accepted", body: "It uses [x](../../../notes/DESIGN-x.md) and its [extra](<../../../notes/DESIGN-x/extra.md>).");
		var before = Sidecar(space, other);
		var change = DocEditor.Move(space.Catalog(), Display(space), space.Catalog().Files.Single(f => f.Path == design), "systems/core", space.Root);
		Assert.True(change.Write(), Ok(change));
		Assert.False(Directory.Exists(space.Full("_docs/notes")));
		Assert.Equal("# The x\n\nSee the [rule](../../norms/RULE-y.md#part) and the [diagram](DESIGN-x/diagram.png).\n", space.Read("_docs/systems/core/DESIGN-x.md"));
		Assert.Equal("png", space.Read("_docs/systems/core/DESIGN-x/diagram.png"));
		Assert.Equal("# Extra\n\nThe [rule](../../../norms/RULE-y.md) and [the design](../DESIGN-x.md).\n", space.Read("_docs/systems/core/DESIGN-x/extra.md"));
		Assert.Equal("# The uses-x\n\nIt uses [x](../../../systems/core/DESIGN-x.md) and its [extra](<../../../systems/core/DESIGN-x/extra.md>).\n", space.Read($"_docs/{other}"));
		Assert.Equal(before.UpdatedAt, Sidecar(space, other).UpdatedAt);
		Assert.Equal(space.Ids[design], Sidecar(space, "systems/core/DESIGN-x.md").Id);
		Assert.Empty(space.Problems());
	}

	[Fact(DisplayName = "move: not a historical one, not into _records, not where its name is taken; to where it is, nothing")]
	public void MoveRefused()
	{
		using var space = new Space("schema: 1.0.0\nactors: [editor]\n");
		var adr = space.Add("adr", "x");
		var design = space.Add("design", "x", folder: "notes");
		space.Add("design", "x", folder: "taken", stem: "DESIGN-x");
		DocFile File(string path) => space.Catalog().Files.Single(f => f.Path == path);
		Assert.Equal([DocEditor.HistoricalMove], Codes(DocEditor.Move(space.Catalog(), Display(space), File(adr), "notes", space.Root)));
		Assert.Equal([DocEditor.InvalidFolder], Codes(DocEditor.Move(space.Catalog(), Display(space), File(design), "_records/2026/10", space.Root)));
		Assert.Equal([DocEditor.NameTaken], Codes(DocEditor.Move(space.Catalog(), Display(space), File(design), "taken", space.Root)));
		var same = DocEditor.Move(space.Catalog(), Display(space), File(design), "notes", space.Root);
		Assert.Equal((true, false), (same.Ok, same.Changed));
	}

	[Fact(DisplayName = "the links when files move: anchors, <>, %20 and ./ stay; a link that led nowhere, untouched")]
	public void Rewrite()
	{
		using var space = new Space();
		space.Write("_docs/a/b c.md", "# B\n");
		space.Write("_docs/a/d.md", "# D\n");
		var from = space.Full("_docs/a/x.md");
		var to = space.Full("_docs/z/w/x.md");
		var moves = new Dictionary<string, string> { [Path.GetFullPath(space.Full("_docs/a/d.md"))] = Path.GetFullPath(space.Full("_docs/z/d.md")) };
		Assert.Equal("[b](../../a/b%20c.md#s) [d](<../d.md>) [e](./nothing.md) [w](https://x/y.md) `[c](d.md)`",
			DocLinks.Rewrite("[b](b%20c.md#s) [d](<d.md>) [e](./nothing.md) [w](https://x/y.md) `[c](d.md)`", from, to, moves));
		// ./ stays where the path still goes down
		Assert.Equal("[d](../z/d.md) [d](./e/d.md#here)", DocLinks.Rewrite("[d](./d.md)", from, from, moves) + " "
		                                                  + DocLinks.Rewrite("[d](./d.md#here)", from, from, new Dictionary<string, string> { [Path.GetFullPath(space.Full("_docs/a/d.md"))] = Path.GetFullPath(space.Full("_docs/a/e/d.md")) }));
	}

	// the command line

	[Fact(DisplayName = "fx doc adopt --manifest and fx doc move: --dry-run lists what moves and goes; then written")]
	public void Cli()
	{
		using var batch = new Batch();
		var space = batch.Space;
		var manifest = batch.Manifest(Batch.Targets);
		var dry = DocsCheckTest.Json(space.Root, "fx-doc-edit/1", "doc", "adopt", "--manifest", manifest, "--actor", "editor", "--dry-run");
		Assert.True(dry.GetProperty("ok").GetBoolean(), dry.ToString());
		Assert.Contains(dry.GetProperty("moved").EnumerateArray(), m => m.GetProperty("to").GetString() == "_docs/_records/2026/01/PLAN-hoja-de-ruta.md");
		Assert.Equal(["_docs/analyzers/old.md"], dry.GetProperty("removed").EnumerateArray().Select(r => r.GetString()));
		Assert.True(File.Exists(space.Full("_docs/RoadMap.md")));
		var (exitCode, stdout, _) = DocsCheckTest.Fx(space.Root, "doc", "adopt", "--manifest", "adopcion.yaml", "--actor", "editor");
		Assert.Equal(0, exitCode);
		Assert.Contains("moved _docs/RoadMap.md → _docs/_records/2026/01/PLAN-hoja-de-ruta.md", stdout);
		Assert.Equal(2, DocsCheckTest.Fx(space.Root, "doc", "adopt", "--manifest", "adopcion.yaml", "README.md").ExitCode);
		Assert.Equal(0, DocsCheckTest.Fx(space.Root, "doc", "move", batch.Design, "systems").ExitCode);
		Assert.True(File.Exists(space.Full("_docs/systems/DESIGN-x/analizadores.md")));
	}
}
