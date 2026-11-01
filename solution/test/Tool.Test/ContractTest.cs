using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fuxion.Tool.Cli;
using Xunit;
using Space = Fuxion.Tool.Test.DocsCheckTest.Space;

namespace Fuxion.Tool.Test;

/// <summary>
/// The contract of fx with who uses it: for each document of
/// <c>--output json</c>, a success and the failures that have their own way, with its exit code; the events of
/// <c>--output ndjson</c>; and the lines of the diagnostics for people (<see cref="ContractProcessTest"/>). Each run is
/// compared with its file in <c>Contract/</c>, with the paths, the versions, the commits, the identities and the times
/// normalized. A change of what they expect is a change of the contract. <c>FX_CONTRACT_WRITE=1</c> writes them again.
/// </summary>
public sealed class ContractTest
{
	// sync and doctor: fx-sync/1, fx-doctor/1

	[Fact(DisplayName = "contract: fx sync and fx doctor, a module with findings, its sync, a configuration that is not valid, no space, wrong usage")]
	public void SyncAndDoctor()
	{
		using var repo = new DotnetTest.Repo(DotnetTest.DesignYamlOfThisFx);
		repo.Git.CommitAll("the library");
		var root = repo.Git.Path;
		Contract.Check("doctor-findings", root, ["doctor", "dotnet", "--output", "json"], (root, "<root>"));
		Contract.Check("sync", root, ["sync", "dotnet", "--output", "json"], (root, "<root>"));
		Contract.Check("doctor-after-sync", root, ["doctor", "dotnet", "--output", "json"], (root, "<root>"));
		Contract.Check("sync-dry-run", root, ["sync", "--dry-run", "--output", "json"], (root, "<root>"));

		using var invalid = new DotnetTest.Repo("schema: 1.0.0\nvariables:\n  - name: A\n    value: [x\n");
		Contract.Check("sync-invalid-config", invalid.Git.Path, ["sync", "--output", "json"], (invalid.Git.Path, "<root>"));
		Contract.Check("doctor-invalid-config", invalid.Git.Path, ["doctor", "--output", "json"], (invalid.Git.Path, "<root>"));

		using var nothing = new TempGitRepository();
		Contract.Check("sync-no-space", nothing.Path, ["sync", "--output", "json"], (nothing.Path, "<root>"));
		Contract.Check("sync-usage", nothing.Path, ["sync", "nope", "--output", "json"], (nothing.Path, "<root>"));
	}

	// the mounts: fx-mount-list/1, fx-mount-status/1, fx-mount-park/1, fx-mount-pull/1, fx-manifest-edit/1, fx-events/1

	/// <summary>A space that mounts a repository from a local origin twice: <c>plus</c>, required, and <c>extra</c>.</summary>
	sealed class Mounted : IDisposable
	{
		public readonly TempGitRepository Meta = new();
		public readonly TempGitRepository Origin = new();

		public Mounted()
		{
			Origin.WriteFile("FxPlus.slnx", "<Solution />\n");
			Origin.CommitAll("c1");
			Meta.WriteFile(Path.Combine("_fx", "space.yaml"), "schema: 1.0.0\nname: test\n");
			Meta.WriteFile(Path.Combine("_fx", "mounts", "mounts.yaml"), $"""
				schema: 1.0.0
				mounts:
				  - name: plus
				    path: plus/repo
				    url: {Url}
				    required: true
				    tags: [dotnet]
				  - name: extra
				    path: extra/repo
				    url: {Url}
				""".Replace("\r\n", "\n"));
			Meta.CommitAll("manifest");
		}

		public string Url => Origin.Path.Replace('\\', '/');

		public (string, string)[] Paths => [(Meta.Path, "<space>"), (Origin.Path, "<origin>")];

		public void Dispose()
		{
			Meta.Dispose();
			Origin.Dispose();
		}
	}

	[Fact(DisplayName = "contract: fx mount and the events of --output ndjson, their success and their failures")]
	public void Mounts()
	{
		using var space = new Mounted();
		var root = space.Meta.Path;
		Contract.Check("events-sync", root, ["sync", "--output", "ndjson"], space.Paths);
		Contract.Check("mount-list", root, ["mount", "list", "--output", "json"], space.Paths);
		Contract.Check("mount-status", root, ["mount", "status", "--output", "json"], space.Paths);
		Contract.Check("mount-pull", root, ["mount", "pull", "--output", "json"], space.Paths);
		Contract.Check("mount-park", root, ["mount", "park", "extra", "--output", "json"], space.Paths);
		Contract.Check("mount-park-required", root, ["mount", "park", "plus", "--output", "json"], space.Paths);
		Contract.Check("mount-unpark", root, ["mount", "unpark", "extra", "--output", "json"], space.Paths);
		Contract.Check("mount-park-unknown", root, ["mount", "park", "nope", "--output", "json"], space.Paths);
		Contract.Check("mount-add-dry-run", root, ["mount", "add", space.Url, "libs/other", "--name", "other", "--dry-run", "--output", "json"], space.Paths);
		Contract.Check("mount-add-taken", root, ["mount", "add", space.Url, "plus/repo", "--output", "json"], space.Paths);
		Contract.Check("mount-remove-unknown", root, ["mount", "remove", "nope", "--output", "json"], space.Paths);
		Contract.Check("mount-add-missing", root, ["mount", "add", "--non-interactive", "--output", "json"], space.Paths);
		Contract.Check("module-enable", root, ["module", "enable", "docs", "--dry-run", "--output", "json"], space.Paths);

		space.Meta.WriteFile(Path.Combine("_fx", "mounts", "mounts.yaml"), "schema: 1.0.0\nmounts:\n  - name: plus\n    path: [x\n");
		Contract.Check("mount-list-invalid-config", root, ["mount", "list", "--output", "json"], space.Paths);
		Contract.Check("sync-invalid-mounts", root, ["sync", "--output", "json"], space.Paths);

		using var nothing = new TempGitRepository();
		Contract.Check("mount-list-no-space", nothing.Path, ["mount", "list", "--output", "json"], (nothing.Path, "<root>"));
		Contract.Check("init", nothing.Path, ["init", "--name", "fresh", "--output", "json"], (nothing.Path, "<root>"));
		Contract.Check("init-again", nothing.Path, ["init", "--name", "fresh", "--output", "json"], (nothing.Path, "<root>"));
	}

	// the worktrees: fx-worktree-add/1, -list/1, -mount/1, -unmount/1, -remove/1

	[Fact(DisplayName = "contract: fx worktree, add, list, unmount, mount and remove, and a branch that has none")]
	public void Worktrees()
	{
		using var space = new Mounted();
		var root = space.Meta.Path;
		Assert.Equal(0, InProcess.Run(["sync"], new StringWriter(), new StringWriter(), root));
		space.Meta.CommitAll("synced");
		var paths = space.Paths;
		Contract.Check("worktree-add", root, ["worktree", "add", "feature/x", "--output", "json"], paths);
		Contract.Check("worktree-list", root, ["worktree", "list", "--output", "json"], paths);
		Contract.Check("worktree-mount", root, ["worktree", "mount", "feature/x", "plus", "--output", "json"], paths);
		Contract.Check("worktree-unmount", root, ["worktree", "unmount", "feature/x", "plus", "--output", "json"], paths);
		Contract.Check("worktree-remove", root, ["worktree", "remove", "feature/x", "--output", "json"], paths);
		Contract.Check("worktree-remove-unknown", root, ["worktree", "remove", "nope", "--output", "json"], paths);
	}

	// fx-self-update/1, fx-describe/1, fx-schemas/1

	[Fact(DisplayName = "contract: fx self update of an fx it does not know, fx describe and fx schema")]
	public void SelfDescribeSchema()
	{
		using var folder = new TempGitRepository();
		// fx installed as a tool of dotnet in a folder: dotnet updates it, with the command fx says (no network); how the
		// fx of the tests is installed depends on its version (local, or not with CI), so it is given
		var tool = Path.Combine(folder.Path, "tools", ".store", "fuxion.tool", "0.7.4", "fuxion.tool.win-x64", "0.7.4", "tools", "any", "win-x64", "fx.exe");
		Contract.Check("self-update-dotnet-tool", folder.Path, ["self", "update", "--output", "json"], (stdout, stderr) =>
		{
			var settings = new GlobalSettings(folder.Path, OutputFormat.Json, false, false);
			return Cli.Self.SelfCommand.Run(new(settings, null, false), new(stdout, stderr, settings), tool, TestContext.Current.CancellationToken);
		}, (folder.Path, "<root>"));
		Contract.Check("describe", folder.Path, ["describe", "--output", "json"], (folder.Path, "<root>"));
		Contract.Check("schemas", folder.Path, ["schema", "--output", "json"], (folder.Path, "<root>"));
		Contract.Check("schema-sync", folder.Path, ["schema", "fx-sync/1"], (folder.Path, "<root>"));
		Contract.Check("schema-unknown", folder.Path, ["schema", "fx-nope/1", "--output", "json"], (folder.Path, "<root>"));
	}

	// the module docs: fx-doc-check/1, -list/1, -show/1, -schemas/1, -edit/1, -entries/1, -key/1

	const string Docs = "schema: 1.0.0\nactors: [editor]\nregisters:\n  - path: ideas/REGISTER-ideas.md\n    prefix: I\n    provider: repo\n";

	[Fact(DisplayName = "contract: fx doc, what reads the catalog: check, list, show and schema, and their failures")]
	public void DocsRead()
	{
		using var git = new TempGitRepository();
		var space = new Space(Docs, git.Path);
		var plan = space.Add("plan", "the-plan", title: "The plan");
		var adr = space.Add("adr", "a-decision", title: "A decision", relations: [new("implemented-by", space.Ids[plan])]);
		space.Add("register", "ideas", title: "Ideas", folder: "ideas");
		git.CommitAll("the catalog");
		var root = git.Path;
		(string, string)[] paths = [(root, "<root>")];
		Contract.Check("doc-check", root, ["doc", "check", "--output", "json"], paths);
		Contract.Check("doc-list", root, ["doc", "list", "--output", "json"], paths);
		Contract.Check("doc-show", root, ["doc", "show", $"_docs/{adr}", "--output", "json"], paths);
		Contract.Check("doc-show-unknown", root, ["doc", "show", "_docs/nope.md", "--output", "json"], paths);
		Contract.Check("doc-schemas", root, ["doc", "schema", "--output", "json"], paths);
		Contract.Check("doc-schema-adr", root, ["doc", "schema", "adr"], paths);
		Contract.Check("doc-schema-unknown", root, ["doc", "schema", "memo", "--output", "json"], paths);
		Contract.Check("doc-entries", root, ["doc", "entry", "list", "_docs/ideas/REGISTER-ideas.md", "--output", "json"], paths);

		File.AppendAllText(space.Full($"_docs/{adr}"), "A [link](nowhere.md).\n");
		space.Write("_docs/DESIGN-loose.md", "# Loose\n");
		Contract.Check("doc-check-errors", root, ["doc", "check", "--output", "json"], paths);

		space.Write("_fx/docs/docs.yaml", "schema: 1.0.0\ncatalog: ../out\n");
		Contract.Check("doc-check-invalid-config", root, ["doc", "check", "--output", "json"], paths);

		using var nothing = new TempGitRepository();
		Contract.Check("doc-check-no-catalog", nothing.Path, ["doc", "check", "--output", "json"], (nothing.Path, "<root>"));
		Contract.Check("doc-list-no-catalog", nothing.Path, ["doc", "list", "--output", "json"], (nothing.Path, "<root>"));
	}

	[Fact(DisplayName = "contract: fx doc, what writes in the catalog, and the keys: a change, a change with errors, no actor, an argument that names nothing")]
	public void DocsWrite()
	{
		using var git = new TempGitRepository();
		var space = new Space(Docs, git.Path);
		var plan = space.Add("plan", "the-plan", title: "The plan");
		space.Add("register", "ideas", title: "Ideas", folder: "ideas");
		space.Add("register", "debts", title: "Debts", folder: "notes");
		git.CommitAll("the catalog");
		var root = git.Path;
		(string, string)[] paths = [(root, "<root>")];
		const string register = "_docs/ideas/REGISTER-ideas.md";
		Contract.Check("doc-new-dry-run", root, ["doc", "new", "design", "The catalog", "--profile", "software", "--actor", "editor", "--dry-run", "--output", "json"], paths);
		Contract.Check("doc-new-no-actor", root, ["doc", "new", "design", "The catalog", "--profile", "software", "--output", "json"], paths);
		Contract.Check("doc-new-unknown-type", root, ["doc", "new", "memo", "The catalog", "--actor", "editor", "--output", "json"], paths);
		Contract.Check("doc-new", root, ["doc", "new", "design", "The catalog", "--profile", "software", "--actor", "editor", "--output", "json"], paths);
		Contract.Check("doc-new-exists", root, ["doc", "new", "design", "The catalog", "--profile", "software", "--actor", "editor", "--output", "json"], paths);
		Contract.Check("doc-status", root, ["doc", "status", $"_docs/{plan}", "in-progress", "--actor", "editor", "--dry-run", "--output", "json"], paths);
		Contract.Check("doc-status-invalid", root, ["doc", "status", $"_docs/{plan}", "nope", "--actor", "editor", "--output", "json"], paths);
		Contract.Check("doc-status-unknown", root, ["doc", "status", "_docs/nope.md", "draft", "--actor", "editor", "--output", "json"], paths);
		Contract.Check("doc-relate-unknown", root, ["doc", "relate", $"_docs/{plan}", "depends-on", "_docs/nope.md", "--actor", "editor", "--output", "json"], paths);
		Contract.Check("doc-entry-add", root, ["doc", "entry", "add", register, "An idea", "--status", "boceto", "--actor", "editor", "--output", "json"], paths);
		Contract.Check("doc-entry-set-unknown", root, ["doc", "entry", "set", register, "I-99", "--status", "hecha", "--actor", "editor", "--output", "json"], paths);
		Contract.Check("doc-entry-set-bad-field", root, ["doc", "entry", "set", register, "I-1", "--field", "prioridad", "--actor", "editor", "--output", "json"], paths);
		Contract.Check("doc-key-dry-run", root, ["doc", "key", register, "Another idea", "--actor", "editor", "--dry-run", "--output", "json"], paths);
		Contract.Check("doc-key-no-actor", root, ["doc", "key", register, "--output", "json"], paths);
		Contract.Check("doc-key-not-keyed", root, ["doc", "key", "_docs/notes/REGISTER-debts.md", "--actor", "editor", "--output", "json"], paths);
	}
}

/// <summary>
/// The contract that changes the process (the language, the variables of the CI): the versions, outside a CI, and the
/// lines of the diagnostics for people, in English and in Spanish.
/// </summary>
[Collection(nameof(ProcessCollection))]
public sealed class ContractProcessTest
{
	// fx-version/1, fx-version-tag/1

	[Fact(DisplayName = "contract: fx version and fx version tag, their success, outside git and with a wrong value")]
	public void Version()
	{
		using var ci = CiEnvironment.Local();
		using var repo = new TempGitRepository();
		repo.Commit("c1");
		repo.Tag("version/1.2.0");
		repo.Commit("c2");
		repo.Branch("feature/x");
		repo.Commit("f1");
		(string, string)[] paths = [(repo.Path, "<root>")];
		Contract.Check("version", repo.Path, ["version", "--output", "json"], paths);
		Contract.Check("version-tag", repo.Path, ["version", "tag", "1.3", "--output", "json"], paths);
		Contract.Check("version-tag-exists", repo.Path, ["version", "tag", "1.3", "--output", "json"], paths);
		Contract.Check("version-tag-usage", repo.Path, ["version", "tag", "nope", "--output", "json"], paths);

		using var folder = new TempGitRepository(init: false);
		Contract.Check("version-no-git", folder.Path, ["version", "--output", "json"], (folder.Path, "<root>"));
	}

	[Fact(DisplayName = "contract: the diagnostics for people, in English and in Spanish")]
	public void Human()
	{
		try
		{
			using var invalid = new DotnetTest.Repo("schema: 1.0.0\nvariables:\n  - name: A\n    value: [x\n");
			using var outdated = new DotnetTest.Repo(DotnetTest.DesignYamlOfThisFx);
			using var space = new ContractMounted();
			using var git = new TempGitRepository();
			var docs = new Space("schema: 1.0.0\nactors: [editor]\n", git.Path);
			var adr = docs.Add("adr", "a-decision", title: "A decision");
			File.AppendAllText(docs.Full($"_docs/{adr}"), "A [link](nowhere.md).\n");
			foreach (var language in new[] { "en", "es" })
			{
				Contract.Check($"human-sync-invalid-config.{language}", invalid.Git.Path, ["sync", "--lang", language], (invalid.Git.Path, "<root>"));
				Contract.Check($"human-sync-outdated.{language}", outdated.Git.Path, ["sync", "dotnet", "--dry-run", "--lang", language], (outdated.Git.Path, "<root>"));
				Contract.Check($"human-mount-add-taken.{language}", space.Root, ["mount", "add", space.Url, "plus/repo", "--lang", language], space.Paths);
				Contract.Check($"human-doc-check-errors.{language}", git.Path, ["doc", "check", "--lang", language], (git.Path, "<root>"));
				Contract.Check($"human-doc-show-unknown.{language}", git.Path, ["doc", "show", "_docs/nope.md", "--lang", language], (git.Path, "<root>"));
			}
		}
		finally
		{
			FxLanguage.Apply("en");
		}
	}

	/// <summary>A space that mounts one repository (as the one of <see cref="ContractTest"/>).</summary>
	sealed class ContractMounted : IDisposable
	{
		readonly TempGitRepository _meta = new();
		readonly TempGitRepository _origin = new();

		public ContractMounted()
		{
			_origin.Commit("c1");
			_meta.WriteFile(Path.Combine("_fx", "space.yaml"), "schema: 1.0.0\nname: test\n");
			_meta.WriteFile(Path.Combine("_fx", "mounts", "mounts.yaml"), $"schema: 1.0.0\nmounts:\n  - name: plus\n    path: plus/repo\n    url: {Url}\n");
			_meta.CommitAll("manifest");
		}

		public string Root => _meta.Path;

		public string Url => _origin.Path.Replace('\\', '/');

		public (string, string)[] Paths => [(_meta.Path, "<space>"), (_origin.Path, "<origin>")];

		public void Dispose()
		{
			_meta.Dispose();
			_origin.Dispose();
		}
	}
}

/// <summary>Runs fx in process and compares what it gives with its file in <c>Contract/</c>.</summary>
static partial class Contract
{
	const string WriteVariable = "FX_CONTRACT_WRITE";

	static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, NewLine = "\n" };

	/// <summary>The contract files of the sources, where <c>FX_CONTRACT_WRITE=1</c> writes them.</summary>
	static string Sources([CallerFilePath] string file = "") => Path.Combine(Path.GetDirectoryName(file)!, "Contract");

	/// <summary>The contract files next to the tests (copied by the build).</summary>
	static readonly string Expected = Path.Combine(AppContext.BaseDirectory, "Contract");

	/// <summary>
	/// Runs fx with <paramref name="args"/> in <paramref name="directory"/>, and compares its exit code, its stdout and its
	/// stderr, normalized, with <c>Contract/&lt;name&gt;.json</c>. <paramref name="paths"/> are the folders of the run and
	/// the token each one is written as.
	/// </summary>
	public static void Check(string name, string directory, string[] args, params (string Path, string Token)[] paths)
		=> Check(name, args, (stdout, stderr) => InProcess.Run(args, stdout, stderr, directory), paths);

	/// <summary>As the other, with fx run by <paramref name="run"/>: a command with what the command line cannot give it.</summary>
	public static void Check(string name, string directory, string[] args, Func<TextWriter, TextWriter, int> run, params (string Path, string Token)[] paths)
		=> Check(name, args, run, paths);

	static void Check(string name, string[] args, Func<TextWriter, TextWriter, int> run, (string Path, string Token)[] paths)
	{
		var stdout = new StringWriter();
		var stderr = new StringWriter();
		var exitCode = run(stdout, stderr);
		var normalizer = new Normalizer(paths);
		var output = args.SkipWhile(a => a != "--output").Skip(1).FirstOrDefault();
		var document = new JsonObject
		{
			["args"] = new JsonArray(args.Select(a => (JsonNode?)JsonValue.Create(normalizer.Text(a))).ToArray()),
			["exitCode"] = exitCode,
			["stdout"] = output switch
			{
				"json" when stdout.ToString().Length > 0 => normalizer.Node(JsonNode.Parse(stdout.ToString())),
				"ndjson" => new JsonArray(Lines(stdout.ToString()).Select(l => normalizer.Node(JsonNode.Parse(l))).ToArray()),
				_ => normalizer.Lines(stdout.ToString())
			},
			["stderr"] = normalizer.Lines(stderr.ToString())
		};
		var actual = document.ToJsonString(Options) + "\n";
		if (Environment.GetEnvironmentVariable(WriteVariable) == "1")
		{
			Directory.CreateDirectory(Sources());
			File.WriteAllText(Path.Combine(Sources(), $"{name}.json"), actual);
			return;
		}
		var expected = Path.Combine(Expected, $"{name}.json");
		Assert.True(File.Exists(expected), $"No contract {name}.json ({WriteVariable}=1 writes it).");
		Assert.Equal(File.ReadAllText(expected).ReplaceLineEndings("\n"), actual);
	}

	static IEnumerable<string> Lines(string text) => text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

	sealed partial class Normalizer((string Path, string Token)[] paths)
	{
		// the longest first, so that a folder inside another is its own
		readonly (string Path, string Token)[] _paths = paths.Where(p => p.Path.Length > 0)
			.SelectMany(p => new[] { (p.Path, p.Token), (p.Path.Replace('\\', '/'), p.Token) })
			.Distinct()
			.OrderByDescending(p => p.Item1.Length)
			.ToArray();

		static readonly string? Informational = typeof(FxApp).Assembly
			.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
			.OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;

		public JsonNode? Node(JsonNode? node)
		{
			switch (node)
			{
				case JsonObject o:
					foreach (var property in o.ToList())
						o[property.Key] = property.Key == "elapsedMs" && property.Value is not null ? 0 : Node(property.Value?.DeepClone());
					return o;
				case JsonArray a:
					var items = a.Select(i => Node(i?.DeepClone())).ToArray();
					return new JsonArray(items);
				case JsonValue v when v.GetValueKind() == JsonValueKind.String:
					return JsonValue.Create(Text(v.GetValue<string>()));
				default:
					return node;
			}
		}

		public JsonArray Lines(string text) => new(Contract.Lines(text).Select(l => (JsonNode?)JsonValue.Create(Text(l))).ToArray());

		public string Text(string text)
		{
			text = text.ReplaceLineEndings("\n");
			foreach (var (path, token) in _paths)
				text = text.Replace(path, token, StringComparison.OrdinalIgnoreCase);
			text = text.Replace(TempGitRepository.TempRoot, "<temp>/", StringComparison.OrdinalIgnoreCase)
				.Replace(TempGitRepository.TempRoot.Replace('\\', '/'), "<temp>/", StringComparison.OrdinalIgnoreCase)
				.Replace('\\', '/');
			if (Informational is { } informational)
				text = text.Replace(informational, "<fx>").Replace(informational.Split('+')[0], "<fx>");
			text = Uuid().Replace(text, "<uuid>");
			text = Instant().Replace(text, "<time>");
			text = Digest().Replace(text, "sha256:<digest>");
			text = Records().Replace(text, "_records/<yyyy>/<mm>/");
			text = Sha().Replace(text, m => m.Value.Any(char.IsAsciiDigit) && m.Value.Any(char.IsAsciiLetter) ? "<sha>" : m.Value);
			return text;
		}

		[GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
		private static partial Regex Uuid();

		[GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})")]
		private static partial Regex Instant();

		[GeneratedRegex("sha256:[0-9a-f]{64}")]
		private static partial Regex Digest();

		[GeneratedRegex(@"_records/\d{4}/\d{2}/")]
		private static partial Regex Records();

		[GeneratedRegex("(?<![0-9A-Za-z])[0-9a-f]{7,40}(?![0-9A-Za-z])")]
		private static partial Regex Sha();
	}
}
