using System;
using System.IO;
using System.Linq;
using Fuxion.Tool.Core.Explore;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Mounts;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Sync;
using Fuxion.Tool.Core.Worktree;
using Xunit;

namespace Fuxion.Tool.Test;

/// <summary>The tree of <c>fx ui</c> (plan O, O4.1, decision 14), read from the sample tree.</summary>
public sealed class ExploreTest : SampleTree
{
	static FxTreeNode At(FxTreeNode tree, string relative)
		=> tree.Find(Path.Combine(tree.Path, relative)) is { } found && string.Equals(found.Path, Path.GetFullPath(Path.Combine(tree.Path, relative)).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
			? found
			: throw new InvalidOperationException($"{relative} is not in the tree");

	[Fact(DisplayName = "the tree: disk and manifests in one, from the highest space; each repository as it is; _fx first")]
	public void Tree()
	{
		var acme = Acme();
		// from anywhere inside, the highest space
		var tree = FxTree.Build(Path.Combine(acme.Path, "repo-w1", "repo-c1", "src"))!;
		Assert.Equal((FxTreeKind.Space, "acme", "/acme"), (tree.Kind, tree.Name, tree.Identity!.FullName));
		Assert.Equal("_fx", tree.Children[0].Name);
		Assert.Equal(FxTreeKind.Space, At(tree, "repo-w1").Kind);
		Assert.Equal("/acme/repo-w1/repo-c1", At(tree, "repo-w1/repo-c1").Identity!.FullName);
		Assert.Equal(FxTreeKind.Repository, At(tree, "libs/shared").Kind);
		Assert.True(At(tree, "libs").HasMore);
		Assert.True(At(tree, "_docs").HasMore);
		// repo-w2's guest shared: declared, not cloned, and the copy of acme is used
		var guest = At(tree, "repo-w2/libs/shared");
		Assert.Equal(FxTreeKind.Declared, guest.Kind);
		Assert.True(guest.Guest);
		Assert.Equal("/acme/shared", guest.SupersededBy!.FullName);
		Assert.Equal(FxOrigin.Generated, At(tree, "Acme.slnx").Origin);
		Assert.Equal(FxOrigin.Generated, At(tree, ".gitignore").Origin);
		Assert.Equal(FxOrigin.Hand, At(tree, "README.md").Origin);
		// a repository has what matters to fx, and the rest on demand
		var c1 = At(tree, "repo-w1/repo-c1");
		Assert.True(c1.HasMore);
		Assert.DoesNotContain(c1.Children, c => c.Name == "src");
		FxTree.LoadMore(c1);
		Assert.Contains(c1.Children, c => c.Name == "src" && c.HasMore);
		Assert.Single(c1.Children, c => c.Name == "C1.slnx");
	}

	[Fact(DisplayName = "each file says whose it is: by hand, generated, inherited (from where) or local; modules on and off")]
	public void Origins()
	{
		var acme = Acme();
		var tree = FxTree.Build(acme.Path)!;
		var inherited = At(tree, "repo-w1/repo-c1/_fx/.inherited/dotnet/dotnet.yaml");
		Assert.Equal(FxOrigin.Inherited, inherited.Origin);
		Assert.Equal(["/acme", "/acme/repo-w1"], inherited.From);
		Assert.Equal(FxOrigin.Generated, At(tree, "repo-w1/repo-c1/_fx/dotnet/dotnet.g.props").Origin);
		Assert.Equal(FxOrigin.Hand, At(tree, "repo-w1/repo-c1/_fx/dotnet/dotnet.yaml").Origin);
		Assert.Equal(FxTreeKind.Module, At(tree, "repo-w1/repo-c1/_fx/dotnet").Kind);
		Assert.Equal(FxTreeKind.ModuleOff, At(tree, "repo-w1/repo-c1/_fx/mounts").Kind);
		Assert.Equal(FxTreeKind.Module, At(tree, "_fx/mounts").Kind);
		Assert.Equal(FxOrigin.Local, At(tree, "_fx/mounts/~$state.yaml").Origin);
		Assert.Equal(FxOrigin.Local, At(tree, "_fx/dotnet/~$space.props").Origin);
	}

	[Fact(DisplayName = "where a file comes from: the spaces it inherits from, the nearest first; the source of a share; the yaml of a generated file")]
	public void Origins_WhereFrom()
	{
		var acme = Acme();
		// acme shares a folder with the apps (repo-c1 among them)
		Directory.CreateDirectory(Path.Combine(acme.Path, "cfg", "sub"));
		File.WriteAllText(Path.Combine(acme.Path, "cfg", "sub", "b.txt"), "b\n");
		var space = Path.Combine(acme.Path, "_fx", "space.yaml");
		Append(space, "paths:\n", "paths:\n  - path: cfg/\n");
		File.AppendAllText(space, "\nshares:\n  - path: cfg/\n    destination: build\n    applies-to:\n      tags: [app]\n");
		Assert.Empty(Errors(Sync(acme.Path)));
		var tree = FxTree.Build(acme.Path)!;
		string[] Origins(string relative) => FxTree.Origins(tree, At(tree, relative)).Select(o => FxLayout.Relative(acme.Path, o)).ToArray();

		Assert.Equal(["repo-w1/_fx/dotnet/dotnet.yaml", "_fx/dotnet/dotnet.yaml"], Origins("repo-w1/repo-c1/_fx/.inherited/dotnet/dotnet.yaml"));
		Assert.Equal(["_fx/dotnet/default.props"], Origins("repo-w1/repo-c1/_fx/.inherited/dotnet/default.props"));
		Assert.Equal(["repo-w1/_fx/mounts/mounts.yaml"], Origins("repo-w1/repo-c1/_fx/.inherited/inherited.yaml"));
		// a share: its source; on its way down, too
		Assert.Equal(["cfg/sub/b.txt"], Origins("repo-w1/repo-c1/build/sub/b.txt"));
		Assert.Equal(["cfg/sub/b.txt"], Origins("repo-w1/_fx/.inherited/shares/build/sub/b.txt"));
		Assert.Equal(["_fx/space.yaml"], Origins("repo-w1/_fx/.inherited/shares/shares.yaml"));
		// generated: the yaml it is made of
		Assert.Equal(["repo-w1/repo-c1/_fx/dotnet/dotnet.yaml", "repo-w1/repo-c1/_fx/.inherited/dotnet/dotnet.yaml"], Origins("repo-w1/repo-c1/_fx/dotnet/dotnet.g.props"));
		Assert.Equal(["_fx/dotnet/dotnet.yaml"], Origins("Acme.slnx"));
		Assert.Equal(["_fx/mounts/mounts.yaml"], Origins(".gitignore"));
		Assert.Empty(Origins("README.md"));
	}

	[Fact(DisplayName = "what nobody declares (?), what is parked, what is declared and not on disk")]
	public void Unknown_Parked_Declared()
	{
		var acme = Acme();
		Directory.CreateDirectory(Path.Combine(acme.Path, "notes"));
		File.WriteAllText(Path.Combine(acme.Path, "notes", "todo.md"), "todo\n");
		new TempGitRepository(path: Path.Combine(acme.Path, "stray")).Dispose();
		Directory.CreateDirectory(Path.Combine(acme.Path, "stray", ".git"));
		// shared, manual and unmounted: parked; then repo-w2's guest is the only copy, mounted and not cloned
		Append(Path.Combine(acme.Path, "_fx", "mounts", "mounts.yaml"), "    path: libs/shared\n    required: true\n", "    path: libs/shared\n");
		Assert.Empty(MountsModule.Park(MountsManifest.Load(acme.Path), ["shared"], parked: true).Diagnostics);

		var tree = FxTree.Build(acme.Path)!;
		Assert.Equal(FxTreeKind.Unknown, At(tree, "notes").Kind);
		Assert.Equal(FxTreeKind.Unknown, At(tree, "stray").Kind);
		var shared = At(tree, "libs/shared");
		Assert.Equal((FxTreeKind.Parked, false), (shared.Kind, shared.Mounted));
		var guest = At(tree, "repo-w2/libs/shared");
		Assert.Equal((FxTreeKind.Declared, true), (guest.Kind, guest.Mounted));
		Assert.Null(guest.SupersededBy);
	}

	[Fact(DisplayName = "what reaches a repository and why: by tag, by name, for everyone, or not for it")]
	public void Applies()
	{
		var acme = Acme();
		var tree = FxTree.Build(acme.Path)!;
		var c1 = FxTree.Applies(At(tree, "repo-w1/repo-c1"));
		var module = Assert.Single(c1, a => a.What == "module");
		Assert.Equal((true, FxReasonKind.Tag), (module.Applies, module.Reason.Kind));
		Assert.Equal(["dotnet"], module.Reason.Values);
		var serilog = Assert.Single(c1, a => a.What == "package" && a.Name == "Serilog");
		Assert.Equal((true, FxReasonKind.Name), (serilog.Applies, serilog.Reason.Kind));
		Assert.Equal(["/acme/repo-w1/repo-c1"], serilog.Reason.Values);
		Assert.Contains(c1, a => a is { What: "package", Name: "xunit.v3", Applies: true, Reason.Kind: FxReasonKind.Everyone });
		Assert.Contains(c1, a => a is { What: "import", Name: "default.props", Applies: true });
		// a space receives it to pass it on, not for it; a repository it is not for does not even receive it (plan R,
		// decision 2)
		Assert.Contains(FxTree.Applies(At(tree, "repo-w1")), a => a is { What: "package", Name: "Serilog", Applies: false, Reason.Kind: FxReasonKind.NotForIt });
		Assert.DoesNotContain(FxTree.Applies(At(tree, "repo-w1/repo-c2")), a => a.What == "package" && a.Name == "Serilog");

		// and the other way: whom the .NET of acme reaches
		var reach = FxTree.Reach(tree, FxTree.DotnetAppliesTo(tree));
		Assert.Contains(reach, r => r.Repository.Name == "repo-c1" && r.Applies && r.Reason.Kind == FxReasonKind.Tag);
		Assert.DoesNotContain(reach, r => r.Repository.Guest);
	}

	[Fact(DisplayName = "what the harness gives a repository: the rule and the skill of fx, and each rule and skill, and why; the yaml behind AGENTS.md")]
	public void AppliesHarness()
	{
		var acme = Clone(_acmeOrigin);
		acme.WriteFile("_fx/harness/harness.yaml", "schema: 1.0.0\nrules:\n  - name: company\n  - name: apps\n    paths: [src/**]\n    applies-to:\n      tags: [app]\nskills:\n  - name: tool\n");
		acme.WriteFile("_fx/harness/rules/company.md", "## Company\n");
		acme.WriteFile("_fx/harness/rules/apps.md", "## Apps\n");
		acme.WriteFile("_fx/harness/skills/tool/SKILL.md", "---\nname: tool\ndescription: The tool.\n---\n\n# tool\n");
		Assert.Empty(Errors(Sync(acme.Path)));
		var tree = FxTree.Build(acme.Path)!;
		var c1 = FxTree.Applies(At(tree, "repo-w1/repo-c1"));
		Assert.Contains(c1, a => a is { What: "rule", Name: "fx", Applies: true });
		Assert.Contains(c1, a => a is { What: "skill", Name: "fx", Applies: true });
		Assert.Contains(c1, a => a is { What: "module", Name: "harness", Applies: true, Reason.Kind: FxReasonKind.Everyone });
		var apps = Assert.Single(c1, a => a.What == "rule" && a.Name == "apps (src/**)");
		Assert.Equal((true, FxReasonKind.Tag, "/acme"), (apps.Applies, apps.Reason.Kind, apps.From.Single()));
		Assert.Contains(c1, a => a is { What: "skill", Name: "tool", Applies: true, Reason.Kind: FxReasonKind.Everyone });
		// repo-c2 is not an app: the rule did not reach it
		Assert.DoesNotContain(FxTree.Applies(At(tree, "repo-w1/repo-c2")), a => a.Name.StartsWith("apps", StringComparison.Ordinal));
		// AGENTS.md is generated from the harness of the repository and what it inherits
		Assert.Equal(["repo-w1/repo-c1/_fx/.inherited/harness/harness.yaml"],
			FxTree.Origins(tree, At(tree, "repo-w1/repo-c1/AGENTS.md")).Select(o => FxLayout.Relative(acme.Path, o)));
	}

	[Fact(DisplayName = "the catalog of tags: its own and what it inherits, and who has each")]
	public void Tags()
	{
		var acme = Acme();
		var tree = FxTree.Build(acme.Path)!;
		var dotnet = Assert.Single(FxTree.Tags(tree), t => t.Tag.Name == "dotnet");
		Assert.True(dotnet.Own);
		Assert.Contains("/acme/repo-w1/repo-c1", dotnet.UsedBy);
		var w1 = FxTree.Tags(At(tree, "repo-w1"));
		Assert.True(Assert.Single(w1, t => t.Tag.Name == "mobile").Own);
		var inherited = Assert.Single(w1, t => t.Tag.Name == "product");
		Assert.Equal((false, "/acme"), (inherited.Own, inherited.From.Single()));
	}

	[Fact(DisplayName = "the doctor on the tree: each problem on its node, and the mark goes up to its parents")]
	public void Doctor()
	{
		var acme = AcmeCommitted();
		File.WriteAllText(Path.Combine(acme.Path, "repo-w1", "repo-c2", "work.txt"), "work\n");
		var tree = FxTree.Build(acme.Path)!;
		FxTree.Attach(tree, SyncEngine.Doctor(acme.Path, new(Offline: true), cancellationToken: TestContext.Current.CancellationToken));
		var c2 = At(tree, "repo-w1/repo-c2");
		Assert.Contains(c2.Diagnostics, d => d.Code == MountsModule.Dirty);
		Assert.DoesNotContain(At(tree, "repo-w1/repo-c1").Diagnostics, d => d.Code == MountsModule.Dirty);
		Assert.Equal(FxSeverity.Warning, At(tree, "repo-w1").Worst);
		Assert.Contains(At(tree, "repo-w1/repo-c1").Diagnostics, d => d.Code == "dotnet.unverifiable");
		Assert.True(At(tree, "repo-w1").Problems >= At(tree, "repo-w1/repo-c1").Problems + At(tree, "repo-w1/repo-c2").Problems);
	}

	[Fact(DisplayName = "a space knows its worktrees; their folder, local")]
	public void Worktrees()
	{
		var acme = AcmeCommitted();
		Assert.DoesNotContain(WorktreeModule.Add(Path.Combine(acme.Path, "libs", "shared"), "fix/t", null, false, cancellationToken: TestContext.Current.CancellationToken).AllDiagnostics,
			d => d.Severity == FxSeverity.Error);
		var tree = FxTree.Build(acme.Path)!;
		Assert.Equal("fix/t", Assert.Single(tree.Worktrees).Card.Branch);
		Assert.Equal(FxOrigin.Local, At(tree, "_fx/~$worktrees").Origin);
		// inside the worktree, the tree is the worktree's
		var inside = FxTree.Build(Path.Combine(acme.Path, "_fx", "~$worktrees", "fix-t", "libs", "shared"))!;
		Assert.EndsWith("fix-t", inside.Path);
		Assert.Equal(FxTreeKind.Repository, At(inside, "libs/shared").Kind);
		Assert.Equal(FxTreeKind.Declared, At(inside, "repo-w1").Kind);
	}
}
