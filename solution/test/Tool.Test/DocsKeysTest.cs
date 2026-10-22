using System;
using System.IO;
using System.Linq;
using Fuxion.Tool.Core.Docs;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using Xunit;
using Space = Fuxion.Tool.Test.DocsCheckTest.Space;

namespace Fuxion.Tool.Test;

/// <summary>
/// The keys of the entries (plan U, U4 step 6c; the SPEC of the module, 5.7): the provider <c>repo</c> (the next one,
/// looking at every branch), <c>git</c> (an atomic reservation on the remote, provisional without it) and
/// <c>fx doc key</c>.
/// </summary>
public sealed class DocsKeysTest
{
	static readonly DateTimeOffset Now = DocsCheckTest.At.AddDays(1);

	static Func<string, string> Display(Space space) => p => FxLayout.Relative(space.Root, p);

	static string Docs(string provider) => $"schema: 1.0.0\nactors: [editor]\nregisters:\n  - path: ideas/REGISTER-ideas.md\n    prefix: I\n    provider: {provider}\n";

	const string Register = "ideas/REGISTER-ideas.md";

	static DocFile File(Space space) => space.Catalog().Files.Single(f => f.Path == Register);

	static DocSidecar Sidecar(Space space)
	{
		using var json = DocSidecar.Parse(space.Read($"_docs/{Space.Sidecar(Register)}"));
		return DocSidecar.From(json.RootElement);
	}

	static DocChange Add(Space space, string title, string? key = null, bool reserve = true)
		=> DocEditor.AddEntry(space.Catalog(), Display(space), space.Root, File(space), title, new() { Key = key }, Now, "editor", reserve);

	/// <summary>A space in a git repository, with the register of ideas, its first commit made.</summary>
	static (TempGitRepository Git, Space Space) Repository(string provider)
	{
		var git = new TempGitRepository();
		var space = new Space(Docs(provider), git.Path);
		space.Add("register", "ideas", folder: "ideas");
		git.CommitAll("the register");
		return (git, space);
	}

	[Fact(DisplayName = "repo: the next after the highest of the register, here and in every branch git has")]
	public void RepoProvider()
	{
		var (git, space) = Repository("repo");
		using var repository = git;
		Assert.True(Add(space, "One", "I-1").Write());
		Assert.True(Add(space, "Three", "I-3").Write());
		git.CommitAll("two ideas");
		git.Branch("other");
		Assert.True(Add(space, "Seven", "I-7").Write());
		git.CommitAll("another idea, in another branch");
		git.Checkout("main");
		Assert.Equal(["I-1", "I-3"], Sidecar(space).Entries.Select(e => e.Key));
		var change = Add(space, "Eight");
		Assert.True(change.Write());
		Assert.Equal("I-8", Sidecar(space).Entries.Last().Key);
		Assert.Empty(space.Problems());
	}

	[Fact(DisplayName = "git: a reservation on the remote, atomic, with who and what for; the next one sees it; a dry run reserves nothing")]
	public void GitProvider()
	{
		var (git, space) = Repository("git");
		using var repository = git;
		using var remote = new TempGitRepository(init: false);
		remote.Git("init", "--quiet", "--bare");
		git.SetOrigin(remote.Path);
		Assert.True(Add(space, "Una idea").Write());
		Assert.Equal("I-1", Sidecar(space).Entries[0].Key);
		Assert.Contains("refs/fx/keys/I-1", remote.Git("for-each-ref", "--format=%(refname)"));
		Assert.Equal("editor\nfx: I-1, of ideas/REGISTER-ideas.md: Una idea", remote.Git("log", "-1", "--format=%an%n%s", "refs/fx/keys/I-1").Trim());
		Assert.Contains("refs/fx/keys/I-1", git.Git("for-each-ref", "--format=%(refname)", "refs/fx/keys"));

		// another copy that does not know it: the remote says it is taken
		git.Git("update-ref", "-d", "refs/fx/keys/I-1");
		var dry = Add(space, "Otra", reserve: false);
		Assert.True(dry.Ok);
		Assert.DoesNotContain("refs/fx/keys/I-2", remote.Git("for-each-ref", "--format=%(refname)"));
		Assert.True(Add(space, "Otra").Write());
		Assert.Equal("I-2", Sidecar(space).Entries[1].Key);
		Assert.Empty(space.Problems());
	}

	[Fact(DisplayName = "git without its remote: a provisional key, a warning; fx doc key --confirm reserves it later")]
	public void Provisional()
	{
		var (git, space) = Repository("git");
		using var repository = git;
		git.SetOrigin(Path.Combine(git.Path, "..", "no-such-remote-" + Guid.NewGuid().ToString("N")[..8]));
		var change = Add(space, "Sin red");
		Assert.Equal(DocKeys.ProvisionalKey, Assert.Single(change.Diagnostics).Code);
		Assert.True(change.Write());
		var key = Sidecar(space).Entries[0].Key!;
		Assert.Matches("^I-~[0-9a-f]{4}$", key);
		Assert.Equal((DocKeys.ProvisionalKey, FxSeverity.Warning), space.Check().Diagnostics.Select(d => (d.Code, d.Severity)).Single());
		Assert.Equal([DocKeys.KeyFailed], DocEditor.ConfirmKeys(space.Catalog(), Display(space), File(space), "editor", Now, true, out _).Diagnostics.Select(d => d.Code));

		using var remote = new TempGitRepository(init: false);
		remote.Git("init", "--quiet", "--bare");
		git.Git("remote", "set-url", "origin", remote.Path);
		var confirm = DocEditor.ConfirmKeys(space.Catalog(), Display(space), File(space), "editor", Now, true, out var confirmed);
		Assert.True(confirm.Write());
		Assert.Equal((key, "I-1"), confirmed.Single());
		Assert.Equal("I-1", Sidecar(space).Entries[0].Key);
		Assert.Empty(space.Problems());
		Assert.Equal($"{DocEditor.Unchanged}.no-provisional",
			Assert.Single(DocEditor.ConfirmKeys(space.Catalog(), Display(space), File(space), "editor", Now, true, out _).Diagnostics).Message.Key);
	}

	[Fact(DisplayName = "the check of the keys: of the prefix of their register; a register of docs.yaml that is not one, an error")]
	public void Check()
	{
		using var space = new Space(Docs("repo") + "  - path: notes/REGISTER-none.md\n    prefix: N\n");
		space.Add("register", "ideas", folder: "ideas");
		// refused as it is written
		var refused = DocEditor.AddEntry(space.Catalog(), Display(space), space.Root, File(space), "Otra", new() { Key = "X-1" }, Now);
		Assert.Contains(DocKeys.InvalidKey, refused.Diagnostics.Select(d => d.Code));
		Assert.False(refused.Write());
		// and found, if it gets there
		Assert.True(DocEditor.AddEntry(space.Catalog(), Display(space), space.Root, File(space), "Otra", new() { Key = "I-1" }, Now).Write());
		space.Change(Register, s => s["entries"]![0]!["key"] = "X-1");
		var problems = space.Problems();
		Assert.Contains(problems, p => p.Code == DocKeys.InvalidKey && p.File == $"_docs/{Space.Sidecar(Register)}");
		Assert.Contains(problems, p => p.Code == DocKeys.NotKeyed && p.File == "_fx/docs/docs.yaml");
	}

	[Fact(DisplayName = "fx doc key: the next key, reserved; --dry-run, the one it would give; a register without keys, an error")]
	public void Cli()
	{
		var (git, space) = Repository("repo");
		using var repository = git;
		var key = DocsCheckTest.Json(space.Root, "fx-doc-key/1", "doc", "key", Register, "Una idea", "--actor", "editor");
		Assert.Equal(("I-1", false, "repo"), (key.GetProperty("keys")[0].GetProperty("key").GetString(), key.GetProperty("keys")[0].GetProperty("provisional").GetBoolean(),
			key.GetProperty("provider").GetString()));
		Assert.Contains("I-1", DocsCheckTest.Fx(space.Root, "doc", "key", Register, "--actor", "editor", "--dry-run").Stdout);
		var other = space.Add("register", "debts", folder: "notes");
		Assert.Equal(1, DocsCheckTest.Fx(space.Root, "doc", "key", other, "--actor", "editor").ExitCode);
		Assert.Equal(1, DocsCheckTest.Fx(space.Root, "doc", "key", Register).ExitCode);
		Assert.Equal(0, DocsCheckTest.Fx(space.Root, "doc", "entry", "add", Register, "Una idea").ExitCode);
		Assert.Equal("I-1", Sidecar(space).Entries[0].Key);
		Assert.Contains("no provisional key", DocsCheckTest.Fx(space.Root, "doc", "key", Register, "--confirm", "--actor", "editor").Stdout);
	}
}
