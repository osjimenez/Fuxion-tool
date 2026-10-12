using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Fuxion.Tools.Cli;
using Fuxion.Tools.Core.Results;
using Fuxion.Tools.Core.Text;
using Fuxion.Tools.Core.Workspace;
using Xunit;

namespace Fuxion.Tools.Test;

/// <summary>The commands that edit the configuration (plan O, O3.4; plan P): only what they must, the rest of the file as it was.</summary>
public sealed class ManifestEditorTest
{
	const string Manifest = """
		# The workspace manifest.
		schema: 1.0.0
		paths:
		  - path: _docs/
		repositories:
		  - name: oss
		    url: https://example.org/oss.git  # the public one
		    path: oss/repo
		    mount: mandatory
		    tags: [dotnet, public]
		  # plus needs oss
		  - name: plus
		    url: https://example.org/plus.git
		    path: plus/repo
		    depends-on: [oss]

		""";

	/// <summary>A workspace in a temporary folder, with <see cref="Manifest"/> (or another one) and its .NET module.</summary>
	sealed class Workspace : IDisposable
	{
		public Workspace(string manifest = Manifest)
		{
			Root = Directory.CreateTempSubdirectory("fx-test-").FullName;
			Directory.CreateDirectory(Path.Combine(Root, "_fx", "workspace"));
			File.WriteAllText(ManifestEditor.ManifestPath(Root), manifest.Replace("\r\n", "\n"));
			Directory.CreateDirectory(Path.Combine(Root, "_fx", "dotnet"));
			File.WriteAllText(Path.Combine(Root, "_fx", "dotnet", "dotnet.yaml"), "schema: 1.0.0\n# the .NET of the workspace\n");
		}

		public string Root { get; }

		public string Text => File.ReadAllText(ManifestEditor.ManifestPath(Root));

		public void Dispose() => Directory.Delete(Root, true);
	}

	/// <summary>The text after the change, which must be valid and different.</summary>
	static string After(ManifestChange change)
	{
		Assert.True(change.Ok, string.Join("; ", change.Diagnostics.Select(d => d.Message.English)));
		Assert.True(change.Changed);
		return change.After.Text;
	}

	// LineDiff

	[Fact(DisplayName = "diff: a change in the middle, with two lines of context")]
	public void Diff_Change()
	{
		var hunk = Assert.Single(LineDiff.Compute("a\nb\nc\nd\ne\nf\ng\n", "a\nb\nc\nX\ne\nf\ng\n"));
		Assert.Equal("@@ -2,5 +2,5 @@\n b\n c\n-d\n+X\n e\n f\n", LineDiff.Unified([hunk]));
	}

	[Fact(DisplayName = "diff: changes far apart are two hunks; an addition at the end; nothing changed, no hunk")]
	public void Diff_Hunks()
	{
		var lines = string.Concat(Enumerable.Range(1, 20).Select(i => $"{i}\n"));
		var changed = string.Concat(Enumerable.Range(1, 20).Select(i => i switch { 2 => "two\n", 18 => "eighteen\n", _ => $"{i}\n" }));
		Assert.Equal(2, LineDiff.Compute(lines, changed).Count);
		Assert.Equal("@@ -2,1 +2,2 @@\n b\n+c\n", LineDiff.Unified(LineDiff.Compute("a\nb\n", "a\nb\nc\n", context: 1)));
		Assert.Empty(LineDiff.Compute("a\r\nb\r\n", "a\nb\n"));
	}

	// repositories

	[Theory(DisplayName = "the name from the URL: last segment, lowercase, without .git")]
	[InlineData("https://github.com/osjimenez/Fuxion-docs.git", "fuxion-docs")]
	[InlineData("git@github.com:org/My_Repo.git", "my-repo")]
	[InlineData(@"C:\repos\Lab\", "lab")]
	public void NameFromUrl(string url, string name) => Assert.Equal(name, ManifestEditor.NameFromUrl(url));

	[Fact(DisplayName = "repo add: at the end, where the user says, the name from the URL, only what differs from the defaults")]
	public void AddRepository()
	{
		using var ws = new Workspace();
		var text = After(ManifestEditor.AddRepository(ws.Root, "https://example.org/Fuxion-Lab.git", "apps/fuxion-lab", tags: ["dotnet"]));
		Assert.EndsWith("""
			    depends-on: [oss]
			  - name: fuxion-lab
			    url: https://example.org/Fuxion-Lab.git
			    path: apps/fuxion-lab
			    tags: [dotnet]

			""".Replace("\r\n", "\n"), text);
		Assert.StartsWith(Manifest.Replace("\r\n", "\n").TrimEnd('\n'), text);
		// nothing is written until asked
		Assert.Equal(Manifest.Replace("\r\n", "\n"), ws.Text);
	}

	[Theory(DisplayName = "repo add: into a manifest without repositories; a guest")]
	[InlineData("schema: 1.0.0\npaths:\n  - path: _docs/\n")]
	[InlineData("schema: 1.0.0\npaths:\n  - path: _docs/\nrepositories: []\n")]
	public void AddRepository_First(string manifest)
	{
		using var ws = new Workspace(manifest);
		var text = After(ManifestEditor.AddRepository(ws.Root, "https://example.org/lab.git", "lab", mandatory: true, inherits: false));
		Assert.EndsWith("repositories:\n  - name: lab\n    url: https://example.org/lab.git\n    path: lab\n    mount: mandatory\n    inherits: false\n", text);
	}

	[Fact(DisplayName = "repo add: a name that exists, or a path the manifest rejects, is an error and nothing changes")]
	public void AddRepository_Invalid()
	{
		using var ws = new Workspace();
		var exists = ManifestEditor.AddRepository(ws.Root, "https://example.org/other.git", "other", name: "plus");
		Assert.False(exists.Ok);
		Assert.Equal(ManifestEditor.RepositoryExists, Assert.Single(exists.Diagnostics).Code);
		var reserved = ManifestEditor.AddRepository(ws.Root, "https://example.org/x.git", "_fx/x");
		Assert.Contains(reserved.Diagnostics, d => d.Code == WorkspaceManifest.InvalidManifest && d.Severity == FxSeverity.Error);
	}

	[Fact(DisplayName = "repo remove: the item and the comment above it; the clone stays, with a warning")]
	public void RemoveRepository()
	{
		using var ws = new Workspace(Manifest.Replace("    depends-on: [oss]\n", ""));
		Directory.CreateDirectory(Path.Combine(ws.Root, "plus", "repo"));
		var change = ManifestEditor.RemoveRepository(ws.Root, "plus");
		var text = After(change);
		Assert.DoesNotContain("plus", text);
		Assert.EndsWith("    tags: [dotnet, public]\n", text);
		Assert.Equal(ManifestEditor.CloneKept, Assert.Single(change.Diagnostics).Code);
		Assert.True(Directory.Exists(Path.Combine(ws.Root, "plus", "repo")));
	}

	[Fact(DisplayName = "repo remove: not while others depend on it; an unknown name")]
	public void RemoveRepository_Invalid()
	{
		using var ws = new Workspace();
		var needed = ManifestEditor.RemoveRepository(ws.Root, "oss");
		Assert.Equal(ManifestEditor.RepositoryNeeded, Assert.Single(needed.Diagnostics).Code);
		Assert.Contains("plus", needed.Diagnostics[0].Message.English);
		Assert.Equal(WorkspaceModule.UnknownRepository, Assert.Single(ManifestEditor.RemoveRepository(ws.Root, "nope").Diagnostics).Code);
	}

	[Fact(DisplayName = "repo tag: added to the list, or the key where it goes; removed, and the key with the last one")]
	public void TagRepository()
	{
		using var ws = new Workspace();
		Assert.Contains("    tags: [dotnet, public, windows]\n", After(ManifestEditor.TagRepository(ws.Root, "oss", ["windows", "dotnet"])));
		Assert.EndsWith("    depends-on: [oss]\n    tags: [dotnet]\n", After(ManifestEditor.TagRepository(ws.Root, "plus", ["dotnet"])));
		Assert.Contains("    tags: [public]\n", After(ManifestEditor.TagRepository(ws.Root, "oss", ["dotnet"], remove: true)));
		Assert.Contains("    mount: mandatory\n  # plus needs oss\n", After(ManifestEditor.TagRepository(ws.Root, "oss", ["dotnet", "public"], remove: true)));

		var already = ManifestEditor.TagRepository(ws.Root, "oss", ["public"]);
		Assert.False(already.Changed);
		Assert.Equal(FxSeverity.Info, Assert.Single(already.Diagnostics).Severity);
		var missing = ManifestEditor.TagRepository(ws.Root, "oss", ["nope"], remove: true);
		Assert.False(missing.Changed);
		Assert.Equal(ManifestEditor.NotTagged, Assert.Single(missing.Diagnostics).Code);
	}

	[Fact(DisplayName = "repo set: a value in place (its comment stays), a key where it goes, a list item by item, a key out")]
	public void SetRepository()
	{
		using var ws = new Workspace();
		Assert.Contains("    url: https://example.org/oss2.git  # the public one\n", After(ManifestEditor.SetRepository(ws.Root, "oss", "url", ["https://example.org/oss2.git"])));
		Assert.Contains("    path: plus/repo\n    mount: mandatory\n    depends-on: [oss]\n",
			After(ManifestEditor.SetRepository(ws.Root, "plus", "mount", ["mandatory"])));
		Assert.Contains("    mount: mandatory\n    inherits: false\n    tags: [dotnet, public]\n", After(ManifestEditor.SetRepository(ws.Root, "oss", "inherits", ["false"])));
		Assert.Contains("    mount: mandatory\n    depends-on: [plus]\n    tags: [dotnet, public]\n", After(ManifestEditor.SetRepository(ws.Root, "oss", "depends-on", ["plus"])));
		Assert.DoesNotContain("depends-on", After(ManifestEditor.SetRepository(ws.Root, "plus", "depends-on", [])));

		// a list, item by item: it keeps its style
		using var block = new Workspace(Manifest.Replace("    depends-on: [oss]\n", "    depends-on:\n      - oss\n")
			+ "  - name: lab\n    url: https://example.org/lab.git\n    path: lab/repo\n");
		Assert.Contains("    depends-on:\n      - lab\n", After(ManifestEditor.SetRepository(block.Root, "plus", "depends-on", ["lab"])));
	}

	[Fact(DisplayName = "repo set: a key it does not change, a value a key does not take, several for one; moving a clone warns")]
	public void SetRepository_Invalid()
	{
		using var ws = new Workspace();
		Assert.Equal(ManifestEditor.UnknownKey, Assert.Single(ManifestEditor.SetRepository(ws.Root, "oss", "name", ["x"]).Diagnostics).Code);
		Assert.Equal(ManifestEditor.InvalidValue, Assert.Single(ManifestEditor.SetRepository(ws.Root, "oss", "mount", ["always"]).Diagnostics).Code);
		Assert.Equal(ManifestEditor.InvalidValue, Assert.Single(ManifestEditor.SetRepository(ws.Root, "oss", "url", ["a", "b"]).Diagnostics).Code);
		Assert.Contains(ManifestEditor.SetRepository(ws.Root, "plus", "depends-on", ["nope"]).Diagnostics, d => d.Code == WorkspaceManifest.InvalidManifest);
		Directory.CreateDirectory(Path.Combine(ws.Root, "oss", "repo"));
		var moved = ManifestEditor.SetRepository(ws.Root, "oss", "path", ["public/repo"]);
		Assert.Contains("    path: public/repo\n", After(moved));
		Assert.Equal(ManifestEditor.CloneNotMoved, Assert.Single(moved.Diagnostics).Code);
	}

	// paths

	[Fact(DisplayName = "path add and remove: only the manifest, as objects; exclude and worktree when asked")]
	public void Paths()
	{
		using var ws = new Workspace();
		Assert.Contains("  - path: _docs/\n  - path: _temp/\n", After(ManifestEditor.AddPath(ws.Root, "_temp/")));
		Assert.Contains("  - path: _docs/drafts/\n    exclude: true\n", After(ManifestEditor.AddPath(ws.Root, "_docs/drafts/", exclude: true)));
		Assert.Contains("  - path: assets/\n    worktree: false\n", After(ManifestEditor.AddPath(ws.Root, "assets/", worktree: false)));
		Assert.DoesNotContain("_docs/", After(ManifestEditor.RemovePath(ws.Root, "_docs/")));
		Assert.False(ManifestEditor.AddPath(ws.Root, "_docs/").Changed);
		Assert.False(ManifestEditor.RemovePath(ws.Root, "_nope/").Changed);
		Assert.False(ManifestEditor.AddPath(ws.Root, "../outside/").Ok);
		Assert.False(ManifestEditor.AddPath(ws.Root, "oss/repo/docs/").Ok);
		Assert.False(Directory.Exists(Path.Combine(ws.Root, "_temp")));

		using var none = new Workspace("schema: 1.0.0\nrepositories: []\n");
		Assert.Contains("paths:\n  - path: README.md\n", After(ManifestEditor.AddPath(none.Root, "README.md")));
	}

	// modules

	[Fact(DisplayName = "module enable: creates its folder with its yaml; with tags, its applies-to; on an enabled one, the tags in place")]
	public void EnableModule()
	{
		using var ws = new Workspace();
		Directory.Delete(Path.Combine(ws.Root, "_fx", "dotnet"), true);
		var created = ManifestEditor.EnableModule(ws.Root, "dotnet", ["dotnet"]);
		Assert.Equal("_fx/dotnet/dotnet.yaml", created.File);
		Assert.Equal("schema: 1.0.0\napplies-to:\n  tags: [dotnet]\n", After(created));
		ManifestEditor.Write(ws.Root, created);
		Assert.True(File.Exists(Path.Combine(ws.Root, "_fx", "dotnet", "dotnet.yaml")));

		Assert.Contains("applies-to:\n  tags: [dotnet, fuxion-build]\n", After(ManifestEditor.EnableModule(ws.Root, "dotnet", ["dotnet", "fuxion-build"])));
		Assert.False(ManifestEditor.EnableModule(ws.Root, "dotnet", []).Changed);
		Assert.Equal(ManifestEditor.UnknownModule, Assert.Single(ManifestEditor.EnableModule(ws.Root, "python", []).Diagnostics).Code);
	}

	[Fact(DisplayName = "module disable: enabled: false, its configuration kept; enable takes it out")]
	public void DisableModule()
	{
		using var ws = new Workspace();
		var disabled = ManifestEditor.DisableModule(ws.Root, "dotnet");
		Assert.Equal("schema: 1.0.0\nenabled: false\n# the .NET of the workspace\n", After(disabled));
		ManifestEditor.Write(ws.Root, disabled);
		Assert.False(ManifestEditor.DisableModule(ws.Root, "dotnet").Changed);
		Assert.Equal("schema: 1.0.0\n# the .NET of the workspace\n", After(ManifestEditor.EnableModule(ws.Root, "dotnet", [])));

		Directory.Delete(Path.Combine(ws.Root, "_fx", "dotnet"), true);
		Assert.False(ManifestEditor.DisableModule(ws.Root, "dotnet").Changed);
	}

	// CLI

	static (int ExitCode, string Out, string Err) Fx(string root, params string[] args)
	{
		var stdout = new StringWriter();
		var stderr = new StringWriter();
		var exitCode = FxApp.Run(args, stdout, stderr, root);
		return (exitCode, stdout.ToString(), stderr.ToString());
	}

	[Fact(DisplayName = "fx repo add: the diff, written; --dry-run writes nothing; the next step")]
	public void Cli_Add()
	{
		using var ws = new Workspace();
		var dry = Fx(ws.Root, "repo", "add", "https://example.org/lab.git", "lab", "--dry-run");
		Assert.Equal(0, dry.ExitCode);
		Assert.Contains("_fx/workspace/workspace.yaml", dry.Out);
		Assert.Contains("+  - name: lab", dry.Out);
		Assert.Contains("Dry run", dry.Out);
		Assert.DoesNotContain("lab", ws.Text);

		var run = Fx(ws.Root, "repo", "add", "https://example.org/lab.git", "lab");
		Assert.Equal(0, run.ExitCode);
		Assert.Contains("Next: fx repo mount lab", run.Out);
		Assert.Contains("  - name: lab", ws.Text);
	}

	[Fact(DisplayName = "fx module disable --output json: fx-manifest-edit/1, with the file and the diff")]
	public void Cli_Json()
	{
		using var ws = new Workspace();
		var run = Fx(ws.Root, "module", "disable", "dotnet", "--output", "json");
		Assert.Equal(0, run.ExitCode);
		var json = JsonDocument.Parse(run.Out).RootElement;
		Assert.Equal(Fuxion.Tools.Cli.Repo.ManifestCommands.Schema, json.GetProperty("schema").GetString());
		Assert.True(json.GetProperty("changed").GetBoolean());
		Assert.True(json.GetProperty("written").GetBoolean());
		Assert.Contains("+enabled: false", json.GetProperty("diff").GetString());
		Assert.Contains("enabled: false", File.ReadAllText(Path.Combine(ws.Root, "_fx", "dotnet", "dotnet.yaml")));
	}

	[Fact(DisplayName = "an invalid change: exit code 1, the error, and the manifest as it was")]
	public void Cli_Invalid()
	{
		using var ws = new Workspace();
		var run = Fx(ws.Root, "repo", "remove", "oss");
		Assert.Equal(1, run.ExitCode);
		Assert.Contains(ManifestEditor.RepositoryNeeded, run.Err);
		Assert.Equal(Manifest.Replace("\r\n", "\n"), ws.Text);
	}

	[Theory(DisplayName = "what is missing, with no terminal to ask for it: exit code 2")]
	[InlineData("repo add")]
	[InlineData("repo add https://example.org/lab.git")]
	[InlineData("repo remove")]
	[InlineData("repo tag oss")]
	[InlineData("repo set oss")]
	[InlineData("repo set oss mount")]
	[InlineData("module enable")]
	[InlineData("path add")]
	[InlineData("path remove")]
	public void Cli_Missing(string command)
	{
		using var ws = new Workspace();
		var run = Fx(ws.Root, command.Split(' '));
		Assert.Equal(FxConsole.UsageError, run.ExitCode);
		Assert.Contains("Missing", run.Err);
		Assert.Equal(Manifest.Replace("\r\n", "\n"), ws.Text);
	}
}
