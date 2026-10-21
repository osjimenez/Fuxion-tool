using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fuxion.Tool.Core.Docs;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Xunit;

namespace Fuxion.Tool.Test;

/// <summary>
/// The module of the documentation (plan U, U4): the schemas of the standard inside fx, the types they declare, the
/// validation of a sidecar against them, and <c>docs.yaml</c>.
/// </summary>
public sealed class DocsTest
{
	const string Id = "0192a8f0-7c3e-7b4a-9d2e-3f4a5b6c7d8e";
	const string OtherId = "0192a8f1-0000-7000-8000-000000000001";
	static readonly string Digest = "sha256:" + new string('a', 64);

	/// <summary>A valid sidecar of a plan, the base of the invalid ones.</summary>
	static JsonObject Plan() => new()
	{
		["schemaVersion"] = "1.0",
		["id"] = Id,
		["type"] = "plan",
		["profile"] = "implementation",
		["status"] = "in-progress",
		["title"] = "The module of the documentation",
		["tags"] = new JsonArray("docs", "fx"),
		["createdAt"] = "2026-10-18T10:00:00Z",
		["updatedAt"] = "2026-10-21T10:00:00Z",
		["createdBy"] = "editor",
		["relations"] = new JsonArray(new JsonObject { ["kind"] = "depends-on", ["targetId"] = OtherId }),
		["integrity"] = new JsonObject { ["content"] = Digest, ["metadata"] = Digest }
	};

	/// <summary>The smallest valid sidecar of <paramref name="type"/>: its initial status, its first profile and a generation if it needs them.</summary>
	static JsonObject Minimal(DocType type)
	{
		var sidecar = new JsonObject
		{
			["schemaVersion"] = "1.0",
			["id"] = Id,
			["type"] = type.Name,
			["status"] = type.InitialStatus,
			["title"] = $"A {type.Name}",
			["createdAt"] = "2026-10-21T10:00:00Z",
			["updatedAt"] = "2026-10-21T10:00:00Z",
			["createdBy"] = "editor",
			["integrity"] = new JsonObject { ["content"] = Digest, ["metadata"] = Digest }
		};
		if (type.ProfileRequired)
			sidecar["profile"] = type.Profiles[0];
		if (type.GenerationRequired)
			sidecar["generation"] = new JsonObject { ["recipe"] = "docs.status", ["scope"] = "plans" };
		return sidecar;
	}

	static JsonElement Element(JsonNode node) => JsonDocument.Parse(node.ToJsonString()).RootElement;

	// the schemas and their types

	[Fact(DisplayName = "the schemas of the standard are inside fx: the base, the common definitions, the document and one per type")]
	public void Schemas()
	{
		Assert.Equal("1.0", DocsSchemas.Version);
		Assert.Contains("base.schema.json", DocsSchemas.Texts.Keys);
		Assert.Contains("common.schema.json", DocsSchemas.Texts.Keys);
		Assert.Contains("document.schema.json", DocsSchemas.Texts.Keys);
		Assert.Equal(13, DocsSchemas.Texts.Keys.Count(k => k.StartsWith("types/", StringComparison.Ordinal)));
		Assert.All(DocTypes.All, t => Assert.NotNull(DocsSchemas.ForType(t.Name)));
		Assert.Null(DocsSchemas.ForType("memo"));
	}

	[Fact(DisplayName = "the thirteen types, read from their schemas: historical or stable, with their profiles and generation")]
	public void Types()
	{
		Assert.Equal(["adr", "design", "plan", "readme", "reference", "register", "report", "research", "rfc", "rule", "runbook", "spec", "view"],
			DocTypes.All.Select(t => t.Name));
		Assert.Equal(["adr", "plan", "report", "research", "rfc"], DocTypes.All.Where(t => t.Temporality == DocTemporality.Historical).Select(t => t.Name));
		Assert.Equal(["design", "plan", "reference", "register", "report", "rule", "spec", "view"], DocTypes.All.Where(t => t.ProfileRequired).Select(t => t.Name));
		Assert.Equal(["adr", "readme", "research", "rfc", "runbook"], DocTypes.All.Where(t => t.Profiles.Count == 0).Select(t => t.Name));
		Assert.Equal(["register", "report", "view"], DocTypes.All.Where(t => t.GenerationAllowed).Select(t => t.Name));
		Assert.Equal(["view"], DocTypes.All.Where(t => t.GenerationRequired).Select(t => t.Name));
		Assert.Equal("ADR", DocTypes.Find("adr")!.Token);
		Assert.Same(DocTypes.Find("runbook"), DocTypes.FromToken("RUNBOOK"));
		Assert.Null(DocTypes.Find("memo"));
		Assert.Null(DocTypes.FromToken("adr"));
	}

	[Fact(DisplayName = "every status of a type has its class, and only its statuses")]
	public void StatusClasses()
	{
		Assert.All(DocTypes.All, t =>
		{
			Assert.Equal(t.Statuses.Order(StringComparer.Ordinal), t.Classes.Keys.Order(StringComparer.Ordinal));
			Assert.Contains(t.Statuses, s => t.ClassOf(s) == DocStatusClass.Current || t.ClassOf(s) == DocStatusClass.Closed);
		});
		var adr = DocTypes.Find("adr")!;
		Assert.Equal(DocStatusClass.Replaced, adr.ClassOf("superseded"));
		Assert.Equal(DocStatusClass.Closed, DocTypes.Find("plan")!.ClassOf("completed"));
		Assert.Equal(DocStatusClass.Void, DocTypes.Find("rfc")!.ClassOf("withdrawn"));
	}

	[Fact(DisplayName = "a new document starts in draft, or in the first status in force of its type")]
	public void InitialStatus()
	{
		Assert.Equal("draft", DocTypes.Find("adr")!.InitialStatus);
		Assert.Equal("draft", DocTypes.Find("spec")!.InitialStatus);
		Assert.Equal("active", DocTypes.Find("readme")!.InitialStatus);
		Assert.Equal("active", DocTypes.Find("register")!.InitialStatus);
		Assert.Equal("active", DocTypes.Find("view")!.InitialStatus);
	}

	[Theory(DisplayName = "a historical document out of its open statuses is frozen; a stable one never")]
	[InlineData("adr", "draft", false)]
	[InlineData("adr", "accepted", true)]
	[InlineData("adr", "superseded", true)]
	[InlineData("plan", "in-progress", false)]
	[InlineData("plan", "blocked", false)]
	[InlineData("plan", "completed", true)]
	[InlineData("research", "active", false)]
	[InlineData("report", "final", true)]
	[InlineData("design", "deprecated", false)]
	[InlineData("spec", "implemented", false)]
	[InlineData("adr", "no-such-status", false)]
	public void Frozen(string type, string status, bool frozen) => Assert.Equal(frozen, DocTypes.Find(type)!.IsFrozen(status));

	// the validation of a sidecar

	[Fact(DisplayName = "a valid sidecar has no problems; the smallest of each type neither")]
	public void Valid()
	{
		Assert.Empty(DocsSchemas.Validate(Element(Plan())));
		Assert.All(DocTypes.All, t => Assert.Empty(DocsSchemas.Validate(Element(Minimal(t)))));
		// what may be there: generation in a register, tags and relations
		var register = Minimal(DocTypes.Find("register")!);
		register["generation"] = new JsonObject { ["recipe"] = "docs.ideas", ["scope"] = "ideas" };
		Assert.Empty(DocsSchemas.Validate(Element(register)));
	}

	[Theory(DisplayName = "an invalid sidecar: each problem at its place, without the cascade of unevaluatedProperties")]
	[InlineData("profile", null, "", "required")]
	[InlineData("status", "\"accepted\"", "/status", "enum")]
	[InlineData("schemaVersion", "\"2.0\"", "/schemaVersion", "const")]
	[InlineData("id", "\"not-a-uuid\"", "/id", "format")]
	[InlineData("title", "\" \"", "/title", "pattern")]
	[InlineData("tags", "[]", "/tags", "minItems")]
	[InlineData("tags", "[\"Docs\"]", "/tags/0", "pattern")]
	[InlineData("createdAt", "\"2026-10-18T10:00:00+02:00\"", "/createdAt", "pattern")]
	[InlineData("createdAt", "\"yesterday\"", "/createdAt", "format")]
	[InlineData("createdBy", "\"Editor\"", "/createdBy", "pattern")]
	[InlineData("relations", "[{\"kind\":\"likes\",\"targetId\":\"" + OtherId + "\"}]", "/relations/0/kind", "enum")]
	[InlineData("integrity", "{\"content\":\"sha256:abc\",\"metadata\":\"sha256:abc\"}", "/integrity/content", "pattern")]
	[InlineData("integrity", null, "", "required")]
	[InlineData("generation", "{\"recipe\":\"docs.status\",\"scope\":\"plans\"}", "/generation", "unevaluatedProperties")]
	[InlineData("extra", "1", "/extra", "unevaluatedProperties")]
	public void Invalid(string property, string? json, string pointer, string keyword)
	{
		var sidecar = Plan();
		if (json is null)
			sidecar.Remove(property);
		else
			sidecar[property] = JsonNode.Parse(json);
		var errors = DocsSchemas.Validate(Element(sidecar));
		Assert.Contains(errors, e => e.Pointer == pointer && e.Keyword == keyword);
		Assert.DoesNotContain(errors, e => e.Keyword == "unevaluatedProperties" && keyword != "unevaluatedProperties");
	}

	[Fact(DisplayName = "a type without profiles does not take one; a view needs its generation")]
	public void ProfileAndGeneration()
	{
		var adr = Minimal(DocTypes.Find("adr")!);
		adr["profile"] = "implementation";
		Assert.Equal(("/profile", "unevaluatedProperties"), DocsSchemas.Validate(Element(adr)).Select(e => (e.Pointer, e.Keyword)).Single());
		var view = Minimal(DocTypes.Find("view")!);
		view.Remove("generation");
		Assert.Equal(("", "required"), DocsSchemas.Validate(Element(view)).Select(e => (e.Pointer, e.Keyword)).Single());
	}

	[Theory(DisplayName = "a sidecar without a known type: one problem, at /type")]
	[InlineData("{\"type\":\"memo\"}")]
	[InlineData("{\"title\":\"no type\"}")]
	[InlineData("{\"type\":7}")]
	[InlineData("[]")]
	public void UnknownType(string json)
		=> Assert.Equal("/type", Assert.Single(DocsSchemas.Validate(JsonDocument.Parse(json).RootElement)).Pointer);

	// docs.yaml

	[Fact(DisplayName = "docs.yaml: its catalog, its actors and its registers with short keys (repo by default)")]
	public void Config()
	{
		var config = DocsYamlReader.Read("""
			schema: 1.0.0
			catalog: docs/
			actors: [editor, agent.claude]
			registers:
			  - path: ideas
			    prefix: I
			  - path: incidents/
			    prefix: INC
			    provider: git
			bootstrap:
			  - _governance/README.md
			  - fx\SPEC-modulo-docs.md
			""", "docs.yaml");
		Assert.True(config.Enabled);
		Assert.Equal("docs", config.Catalog);
		Assert.Equal(["editor", "agent.claude"], config.Actors);
		Assert.Equal([("ideas", "I", "repo"), ("incidents", "INC", "git")], config.Registers.Select(r => (r.Path, r.Prefix, r.Provider)));
		Assert.Equal(5, config.Registers[0].Line);
		Assert.Equal(["_governance/README.md", "fx/SPEC-modulo-docs.md"], config.Bootstrap);

		var empty = DocsYamlReader.Read("schema: 1.0.0\n", "docs.yaml");
		Assert.Equal((DocsConfig.DefaultCatalog, 0, 0), (empty.Catalog, empty.Actors.Count, empty.Registers.Count));
		Assert.False(DocsYamlReader.Read("schema: 1.0.0\nenabled: false\n", "docs.yaml").Enabled);
	}

	[Theory(DisplayName = "an invalid docs.yaml: each problem with its text and its line")]
	[InlineData("catalog: ../outside", "catalog", 2)]
	[InlineData("catalog: _fx/docs", "catalog", 2)]
	[InlineData("catalog: ~$docs", "catalog", 2)]
	[InlineData("catalog: C:/docs", "catalog", 2)]
	[InlineData("catalog: /docs", "catalog", 2)]
	[InlineData("catalog: docs/./plans", "catalog", 2)]
	[InlineData("registers:\n  - path: ../ideas\n    prefix: I", "register-path", 3)]
	[InlineData("registers:\n  - path: /ideas\n    prefix: I", "register-path", 3)]
	[InlineData("actors: [Editor]", "actor", 2)]
	[InlineData("actors: [editor, editor]", "duplicate-actor", 2)]
	[InlineData("actors:\n  first: editor", "list", 3)]
	[InlineData("registers: ideas", "list", 2)]
	[InlineData("registers:\n  - ideas", "register-entry", 3)]
	[InlineData("registers:\n  - path: ideas", "prefix", 3)]
	[InlineData("registers:\n  - path: ideas\n    prefix: i", "prefix", 4)]
	[InlineData("registers:\n  - path: ideas\n    prefix: I\n    provider: svn", "provider", 5)]
	[InlineData("registers:\n  - path: ideas\n    prefix: I\n  - path: ideas/\n    prefix: J", "duplicate-register", 5)]
	[InlineData("registers:\n  - path: a\n    prefix: I\n  - path: b\n    prefix: I", "duplicate-prefix", 5)]
	[InlineData("enabled: maybe", "enabled", 2)]
	[InlineData("bootstrap: [../README.md]", "bootstrap", 2)]
	[InlineData("bootstrap: [notes.txt]", "bootstrap", 2)]
	[InlineData("bootstrap: [a.md, a.md]", "duplicate-bootstrap", 2)]
	[InlineData("bootstrap:\n  first: a.md", "list", 3)]
	public void InvalidConfig(string yaml, string key, int line)
	{
		var ex = Assert.Throws<DocsConfigException>(() => DocsYamlReader.Read($"schema: 1.0.0\n{yaml}\n", "docs.yaml"));
		var diagnostic = Assert.Single(ex.Diagnostics);
		Assert.Equal((DocsYamlReader.InvalidYaml, $"{DocsYamlReader.InvalidYaml}.{key}", (int?)line), (diagnostic.Code, diagnostic.Message.Key, diagnostic.Line));
	}

	[Fact(DisplayName = "docs.yaml: an unknown key is an error, a warning with a newer minor of its schema")]
	public void UnknownKey()
	{
		var ex = Assert.Throws<DocsConfigException>(() => DocsYamlReader.Read("schema: 1.0.0\nindex: true\n", "docs.yaml"));
		Assert.Equal(FxYamlReader.UnknownKey, Assert.Single(ex.Diagnostics).Message.Key);
		var warnings = new System.Collections.Generic.List<FxDiagnostic>();
		DocsYamlReader.Read("schema: 1.1.0\nindex: true\n", "docs.yaml", warnings);
		Assert.Equal((FxYamlReader.UnknownKey, FxSeverity.Warning), (Assert.Single(warnings).Code, warnings[0].Severity));
	}

	[Fact(DisplayName = "the module: no catalog without its folder or when disabled; without docs.yaml, the defaults")]
	public void Load()
	{
		var root = TempGitRepository.TempFolder("fx-docs-");
		try
		{
			Assert.Null(DocsModule.Load(root));
			Directory.CreateDirectory(Path.Combine(root, "_fx", "docs"));
			Assert.Same(DocsConfig.Default, DocsModule.Load(root));
			File.WriteAllText(Path.Combine(root, "_fx", "docs", "docs.yaml"), "schema: 1.0.0\ncatalog: notes\n");
			Assert.Equal("notes", DocsModule.Load(root)!.Catalog);
			File.WriteAllText(Path.Combine(root, "_fx", "docs", "docs.yaml"), "schema: 1.0.0\nenabled: false\n");
			Assert.Null(DocsModule.Load(root));
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}
}
