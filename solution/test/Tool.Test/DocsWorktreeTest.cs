using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fuxion.Tool.Cli.Describe;
using Fuxion.Tool.Core.Docs;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Sync;
using Fuxion.Tool.Core.Worktree;
using Xunit;
using static Fuxion.Tool.Test.Worktrees;
using Space = Fuxion.Tool.Test.DocsCheckTest.Space;

namespace Fuxion.Tool.Test;

/// <summary>
/// The catalog and the worktrees: a space that mounts others leaves its catalog out of them, so that what is not
/// of a change does not go with a branch; from such a worktree <c>fx doc</c> uses the catalog of the main tree, and
/// <c>fx sync</c> and <c>fx doctor</c> have nothing of it to do there; the doctor warns of a catalog in every worktree.
/// </summary>
public sealed class DocsWorktreeTest
{
	const string Register = "_docs/ideas/REGISTER-ideas.md";
	const string Branch = "feature/docs";
	const string SpaceYaml = "schema: 1.0.0\nname: notes\npaths:\n  - path: README.md\n  - path: _docs/\n";

	static readonly SyncOptions Options = new(Offline: true, Skill: FxSkill.Text);

	static IEnumerable<FxDiagnostic> Errors(IEnumerable<FxModuleResult> results) => results.SelectMany(r => r.Diagnostics).Where(d => d.Severity == FxSeverity.Error);

	/// <summary>
	/// A space that mounts others (none yet) with its catalog left out of its worktrees, a register of ideas with keys,
	/// synced and committed; and, if asked, a worktree of it.
	/// </summary>
	sealed class Tree : IDisposable
	{
		public Tree(bool worktree = true)
		{
			Space = new("schema: 1.0.0\nactors: [editor]\nregisters:\n  - path: ideas/REGISTER-ideas.md\n    prefix: I\n", Git.Path);
			Space.Add("register", "ideas", folder: "ideas");
			Space.Write("README.md", "notes\n");
			Space.Write("_fx/space.yaml", SpaceYaml + "    worktree: false\n");
			Space.Write("_fx/mounts/mounts.yaml", "schema: 1.0.0\nmounts: []\n");
			Assert.Empty(Errors(SyncEngine.Sync(Git.Path, Options, cancellationToken: Token)));
			Git.CommitAll("the space");
			if (worktree)
				Root = Ok(WorktreeModule.Add(Git.Path, Branch, null, false, cancellationToken: Token)).Root!;
		}

		public TempGitRepository Git { get; } = new();

		public Space Space { get; }

		/// <summary>The root of the worktree.</summary>
		public string Root { get; } = "";

		public void Dispose() => Git.Dispose();
	}

	[Fact(DisplayName = "from a worktree that leaves it out, the catalog is that of the main tree, said in an info; a path into the worktree's names the same one there")]
	public void Find()
	{
		using var tree = new Tree();
		Assert.False(Directory.Exists(Path.Combine(tree.Root, "_docs")));
		var found = DocsModule.Find(tree.Root);
		var diagnostics = found.Diagnostics;
		var catalog = found.SuccessOrThrow();
		Assert.True(FxLayout.SameFolder(tree.Git.Path, catalog.Root));
		Assert.True(FxLayout.SameFolder(Path.Combine(tree.Root, "_docs"), catalog.WorktreeFolder!));
		var info = Assert.Single(diagnostics);
		Assert.Equal((DocsModule.MainTreeCatalog, FxSeverity.Info), (info.Code, info.Severity));
		Assert.Contains(catalog.Folder, info.Message.English);

		// from its root, from deeper or absolute, into the catalog of the worktree: the register of the main tree
		var deep = Directory.CreateDirectory(Path.Combine(tree.Root, "work", "deep")).FullName;
		Assert.Equal("ideas/REGISTER-ideas.md", Assert.Single(catalog.Select(Register, tree.Root)).Path);
		Assert.Equal("ideas/REGISTER-ideas.md", Assert.Single(catalog.Select($"../../{Register}", deep)).Path);
		Assert.Equal("ideas/REGISTER-ideas.md", Assert.Single(catalog.Select(Path.Combine(tree.Root, Register), deep)).Path);
		Assert.Equal("ideas", catalog.RelativeFolder("_docs/ideas", tree.Root));
		// from deeper in the worktree, the same catalog
		found = DocsModule.Find(deep);
		Assert.True(FxLayout.SameFolder(tree.Git.Path, found.SuccessOrThrow().Root));
		diagnostics = found.Diagnostics;
		Assert.Equal(DocsModule.MainTreeCatalog, Assert.Single(diagnostics).Code);

		// in the main tree, its own, as always
		found = DocsModule.Find(tree.Git.Path);
		var main = found.SuccessOrThrow();
		diagnostics = found.Diagnostics;
		Assert.Null(main.WorktreeFolder);
		Assert.Empty(diagnostics);
	}

	[Fact(DisplayName = "fx doc from a worktree: it writes in the catalog of the main tree and says so; the worktree stays without one")]
	public void Cli()
	{
		using var tree = new Tree();
		var added = DocsCheckTest.Json(tree.Root, "fx-doc-edit/1", "doc", "entry", "add", Register, "Una idea", "--actor", "editor");
		Assert.True(added.GetProperty("written").GetBoolean());
		Assert.Contains(added.GetProperty("diagnostics").EnumerateArray(),
			d => d.GetProperty("code").GetString() == DocsModule.MainTreeCatalog && d.GetProperty("severity").GetString() == "info");
		Assert.Contains("Una idea", tree.Space.Read("_docs/ideas/REGISTER-ideas.meta.json"));

		// for people, a line that says which catalog; a path from deeper in the worktree
		var deep = Directory.CreateDirectory(Path.Combine(tree.Root, "work", "deep")).FullName;
		var (exitCode, stdout, stderr) = DocsCheckTest.Fx(deep, "doc", "entry", "add", $"../../{Register}", "Otra idea", "--actor", "editor");
		Assert.True(exitCode == 0, stderr);
		Assert.Contains("Written", stdout);
		Assert.Contains("This worktree leaves the catalog out", stderr);
		Assert.Contains(Path.Combine(tree.Git.Path, "_docs"), stderr);
		var created = DocsCheckTest.Json(tree.Root, "fx-doc-edit/1", "doc", "new", "adr", "Una decisión", "--actor", "editor");
		Assert.True(File.Exists(Path.Combine(tree.Git.Path, "_docs", created.GetProperty("documents")[0].GetString()!)));
		Assert.False(Directory.Exists(Path.Combine(tree.Root, "_docs")));

		// what reads it, the same catalog
		var entries = DocsCheckTest.Json(tree.Root, "fx-doc-entries/1", "doc", "entry", "list", Register).GetProperty("entries");
		Assert.Equal(["I-1", "I-2"], entries.EnumerateArray().Select(e => e.GetProperty("key").GetString()));
		Assert.Equal(2, DocsCheckTest.Json(tree.Root, "fx-doc-list/1", "doc", "list").GetProperty("documents").GetArrayLength());
		var check = DocsCheckTest.Fx(tree.Root, "doc", "check", "--branches");
		Assert.True(check.ExitCode == 0, check.Stdout + check.Stderr);
		Assert.Contains("This worktree leaves the catalog out", check.Stderr);
		Assert.Equal(0, DocsCheckTest.Fx(tree.Root, "doc", "show", Register).ExitCode);
		Assert.Equal(0, DocsCheckTest.Fx(deep, "doc", "key", $"../../{Register}", "--dry-run", "--actor", "editor").ExitCode);
	}

	[Fact(DisplayName = "fx sync and fx doctor in that worktree: no catalog to check nor views to make; brought back with fx worktree mount, the worktree's own again")]
	public void SyncAndBringBack()
	{
		using var tree = new Tree();
		var doctor = SyncEngine.Doctor(tree.Root, Options, cancellationToken: Token);
		Assert.Empty(Errors(doctor));
		Assert.DoesNotContain(doctor, r => r.Module == DocsModule.Name);
		var sync = SyncEngine.Sync(tree.Root, Options, cancellationToken: Token);
		Assert.Empty(Errors(sync));
		Assert.DoesNotContain(sync, r => r.Module == DocsModule.Name);
		Assert.False(Directory.Exists(Path.Combine(tree.Root, "_docs")));

		Ok(WorktreeModule.Mount(tree.Git.Path, Branch, ["_docs/"], false, cancellationToken: Token));
		Assert.True(File.Exists(Path.Combine(tree.Root, Register)));
		var found = DocsModule.Find(tree.Root);
		var diagnostics = found.Diagnostics;
		var catalog = found.SuccessOrThrow();
		Assert.True(FxLayout.SameFolder(tree.Root, catalog.Root));
		Assert.Null(catalog.WorktreeFolder);
		Assert.Empty(diagnostics);
		Assert.Empty(Assert.Single(SyncEngine.Doctor(tree.Root, Options, cancellationToken: Token), r => r.Module == DocsModule.Name).Diagnostics);
	}

	[Fact(DisplayName = "fx doctor: the catalog of a space that mounts others in its worktrees warns, with the command that leaves it out; not in one that mounts nothing")]
	public void Doctor()
	{
		using var tree = new Tree(worktree: false);
		tree.Space.Write("_fx/space.yaml", SpaceYaml);
		FxModuleResult Docs() => SyncEngine.Doctor(tree.Git.Path, Options, cancellationToken: Token).Single(r => r.Module == DocsModule.Name);
		var warning = Assert.Single(Docs().Diagnostics);
		Assert.Equal((DocsModule.CatalogInWorktrees, FxSeverity.Warning, "_fx/space.yaml", (int?)5), (warning.Code, warning.Severity, warning.File, warning.Line));
		Assert.Equal("fx path add _docs/ --no-worktree", warning.Fix!.Command);

		// its fix, on the path that is there
		var (exitCode, stdout, _) = DocsCheckTest.Fx(tree.Git.Path, warning.Fix.Command!.Split(' ')[1..]);
		Assert.Equal(0, exitCode);
		Assert.Contains("+    worktree: false", stdout);
		Assert.Equal(SpaceYaml + "    worktree: false\n", tree.Space.Read("_fx/space.yaml").Replace("\r\n", "\n"));
		Assert.Empty(Docs().Diagnostics);

		// a space that mounts nothing makes no worktrees that leave paths out
		tree.Space.Write("_fx/space.yaml", SpaceYaml);
		Directory.Delete(tree.Space.Full("_fx/mounts"), true);
		Assert.Empty(Docs().Diagnostics);
	}
}
