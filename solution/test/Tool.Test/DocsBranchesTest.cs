using System;
using System.IO;
using System.Linq;
using Fuxion.Tool.Core.Docs;
using Fuxion.Tool.Core.Layout;
using Xunit;
using Space = Fuxion.Tool.Test.DocsCheckTest.Space;

namespace Fuxion.Tool.Test;

/// <summary>
/// The catalog against the remote branches (plan U, U4 step 6d; the SPEC of the module, 5.1 and 5.2): what another branch
/// creates must not collide with what is here; <c>fx doc check --branches</c> and <c>--fetch</c>, and <c>new</c>.
/// </summary>
public sealed class DocsBranchesTest
{
	const string Actors = "schema: 1.0.0\nactors: [editor]\n";
	static readonly DateTimeOffset Now = DocsCheckTest.At.AddDays(1);
	const string Register = "ideas/REGISTER-ideas.md";

	static Func<string, string> Display(Space space) => p => FxLayout.Relative(space.Root, p);

	/// <summary>A space in a git repository with a bare remote: its catalog, with a register and a design, pushed.</summary>
	sealed class Repository : IDisposable
	{
		public Repository()
		{
			Remote.Git("init", "--quiet", "--bare", "--initial-branch=main");
			Git.SetOrigin(Remote.Path);
			Space = new(Actors, Git.Path);
			Space.Add("register", "ideas", folder: "ideas");
			Space.Add("design", "d", folder: "notes");
			Git.CommitAll("the catalog");
			Git.Git("push", "--quiet", "origin", "main");
		}

		public TempGitRepository Git { get; } = new();
		public TempGitRepository Remote { get; } = new(init: false);
		public Space Space { get; }

		/// <summary>Work in another branch, pushed and gone from here: only the remote has it.</summary>
		public void Elsewhere(string branch, Action work)
		{
			Git.Branch(branch);
			work();
			Git.CommitAll($"in {branch}");
			Git.Git("push", "--quiet", "origin", branch);
			Git.Checkout("main");
			Git.Git("branch", "--quiet", "-D", branch);
		}

		public DocChange Entry(string title, string? key) => DocEditor.AddEntry(Space.Catalog(), Display(Space), Space.Root,
			Space.Catalog().Files.Single(f => f.Path == Register), title, new() { Key = key }, Now);

		public string[] Collisions()
		{
			var catalog = Space.Catalog();
			return DocBranches.Collisions(catalog, DocChecker.Read(catalog), DocBranches.Read(catalog), Display(Space)).Select(d => d.Message.Key).Order().ToArray();
		}

		public void Dispose()
		{
			Git.Dispose();
			Remote.Dispose();
		}
	}

	[Fact(DisplayName = "--branches: the same path, the same slug, the same identity in a copy, an entry with the name or the key of another")]
	public void Collisions()
	{
		using var repository = new Repository();
		var space = repository.Space;
		var design = space.Ids["notes/DESIGN-d.md"];
		repository.Elsewhere("other", () =>
		{
			space.Add("adr", "x");
			space.Add("design", "e", folder: "notes", id: design);
			Assert.True(repository.Entry("Cinco", "I-5").Write());
		});
		// the copy collides already: the branch has it, and still the document it copies
		Assert.Equal([$"{DocBranches.BranchCollision}.id"], repository.Collisions());
		space.Add("adr", "x");
		space.Add("rfc", "x");
		Assert.True(repository.Entry("Cinco", "I-6").Write());
		Assert.True(repository.Entry("Otra", "I-5").Write());
		Assert.Equal(new[] { "path", "slug", "id", "entry", "key" }.Select(k => $"{DocBranches.BranchCollision}.{k}").Order(), repository.Collisions());
	}

	[Fact(DisplayName = "--branches: a document another branch moved, or what it has the same as here, does not collide")]
	public void NoCollisions()
	{
		using var repository = new Repository();
		var space = repository.Space;
		repository.Elsewhere("moved", () =>
		{
			var change = DocEditor.Move(space.Catalog(), Display(space), space.Catalog().Files.Single(f => f.Path == "notes/DESIGN-d.md"), "systems", space.Root);
			Assert.True(change.Write());
			space.Add("adr", "only-there");
		});
		Assert.Empty(repository.Collisions());
	}

	[Fact(DisplayName = "new: not a name that a remote branch has")]
	public void New()
	{
		using var repository = new Repository();
		var space = repository.Space;
		repository.Elsewhere("other", () => space.Add("adr", "una-decision", created: DocsCheckTest.At));
		var change = DocEditor.New(space.Catalog(), Display(space), space.Root, "adr", "Una decisión", null, null, null, [], "editor", DocsCheckTest.At);
		Assert.Equal([DocEditor.NameTaken], change.Diagnostics.Select(d => d.Code));
		Assert.Equal($"{DocEditor.NameTaken}.branch", change.Diagnostics[0].Message.Key);
		Assert.True(DocEditor.New(space.Catalog(), Display(space), space.Root, "adr", "Otra decisión", null, null, null, [], "editor", DocsCheckTest.At).Ok);
	}

	[Fact(DisplayName = "fx doc check --branches, and --fetch for what another copy pushed")]
	public void Cli()
	{
		using var repository = new Repository();
		var space = repository.Space;
		using var copy = new TempGitRepository(init: false);
		copy.Git("clone", "--quiet", repository.Remote.Path, ".");
		var other = new Space(Actors, copy.Path);
		other.Add("adr", "x");
		copy.CommitAll("in the other copy");
		copy.Git("push", "--quiet", "origin", "main");
		space.Add("adr", "x");
		Assert.True(DocsCheckTest.Json(space.Root, "fx-doc-check/1", "doc", "check", "--branches").GetProperty("ok").GetBoolean());
		var fetched = DocsCheckTest.Json(space.Root, "fx-doc-check/1", "doc", "check", "--fetch");
		Assert.False(fetched.GetProperty("ok").GetBoolean());
		Assert.Equal(DocBranches.BranchCollision, fetched.GetProperty("diagnostics")[0].GetProperty("code").GetString());
		Assert.Equal(0, DocsCheckTest.Fx(space.Root, "doc", "check").ExitCode);
		Assert.Equal(1, DocsCheckTest.Fx(space.Root, "doc", "check", "--branches").ExitCode);
	}
}
