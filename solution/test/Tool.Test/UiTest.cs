using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fuxion.Tool.Cli;
using Fuxion.Tool.Cli.Ui;
using Fuxion.Tool.Core.Explore;
using Fuxion.Tool.Core.Mounts;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Worktree;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Hosting;
using Xunit;

namespace Fuxion.Tool.Test;

/// <summary><c>fx ui</c>, the layer to see (plan O, O4.1b): the tree, the doctor, the panels, going to an origin, worktrees.</summary>
public sealed class UiTest : UiTree
{
	[Fact(DisplayName = "the first frame: the tree from the highest space, the node where it starts, the panel of the space and the keys")]
	public void FirstFrame()
	{
		var acme = AcmeCommitted();
		using var h = Open(acme.Path, Path.Combine(acme.Path, "repo-w1", "repo-c1"));
		// started inside repo-c1: it is selected, and its panel says who it is
		var screen = h.Shows("/acme/repo-w1/repo-c1");
		Assert.Equal("repo-c1", h.OnUi(() => h.Ui.Selected?.Name));
		Assert.Contains("fx ui", screen);
		Assert.Contains("/acme", screen);
		foreach (var name in new[] { "_fx", "repo-w1", "repo-c1", "repo-w2", "libs" })
			Assert.Contains(name, screen);
		foreach (var key in new[] { "F2", "F3", "F5" })
			Assert.Contains(key, screen);
	}

	[Fact(DisplayName = "the doctor in the tree: its mark on the node and on its parents, and its list; Enter goes to the node")]
	public void Doctor()
	{
		var acme = AcmeCommitted();
		File.WriteAllText(Path.Combine(acme.Path, "repo-w1", "repo-c2", "work.txt"), "work\n");
		using var h = Open(acme.Path);
		h.Doctor();
		h.OnUi(() => h.Ui.Select(h.Node(acme.Path, "repo-w1/repo-c2")));
		// git (*1: the change) and the doctor (!) on repo-c2, the doctor on repo-w1 too
		h.Until(() => h.LineOf(" repo-c2") is var c2 && c2.Contains("*1") && c2.Contains('!') && h.LineOf(" repo-w1").Contains('!'), "the marks of git and of the doctor");

		// F2: the list of problems; Enter on one goes to its node
		h.Key(TerminalKey.F2);
		h.Shows("mounts.dirty");
		var index = h.OnUi(() => h.Ui.Problems.Items.ToList().FindIndex(p => p.Diagnostic.Code == MountsModule.Dirty));
		Assert.True(index >= 0);
		h.OnUi(() =>
		{
			h.Ui.Select(h.Ui.Model);
			h.Ui.Problems.SelectedIndex = index;
			h.App.Focus(h.Ui.Problems);
			return 0;
		});
		h.Key(TerminalKey.Enter);
		h.Until(() => h.Ui.Selected?.Name == "repo-c2", "Enter goes to the node of the problem");
	}

	[Fact(DisplayName = "the panels: a copy that is not used says which is, a module not used says how to turn it on, a file says whose it is")]
	public void Panels()
	{
		var acme = AcmeCommitted();
		using var h = Open(acme.Path);
		// the screen once it shows what only the panel of that node has
		string Show(string relative, string only)
		{
			Assert.True(h.OnUi(() => h.Ui.Select(h.Node(acme.Path, relative))), relative);
			return h.Shows(only);
		}
		// the copy that is not used: nothing to clone it
		var ghost = Show("repo-w2/libs/shared", "/acme/shared");
		Assert.Contains("/acme/shared", ghost);
		Assert.DoesNotContain("fx sync", ghost);
		Assert.Contains("fx module enable mounts", Show("repo-w1/repo-c1/_fx/mounts", "fx module enable mounts"));
		Assert.Contains("fx sync", Show("Acme.slnx", "<Solution>"));
		// the content, a line each
		var solution = h.LineOf("<Solution>");
		Assert.DoesNotContain("<Folder", solution);
		Assert.DoesNotContain("<Project", solution);
		Assert.Contains("/acme", Show("repo-w1/repo-c1/_fx/.inherited/dotnet/dotnet.yaml", "CoreFrameworks"));
		// the catalog of tags, on space.yaml
		var tags = Show("_fx/space.yaml", "library");
		Assert.Contains("library", tags);
		Assert.Contains("repo-c1", tags);
	}

	[Fact(DisplayName = "F3: what does not matter to fx, and the local files; the rest of a repository, when it is opened")]
	public void All()
	{
		var acme = AcmeCommitted();
		using var h = Open(acme.Path);
		var state = h.Node(acme.Path, "_fx/mounts/~$state.yaml");
		Assert.False(h.OnUi(() => h.Ui.Select(state)));
		h.Key(TerminalKey.F3);
		h.Until(() => h.Ui.Select(state), "F3 shows the local files");
		h.Shows("~$state.yaml");

		// repo-c1 opened: its src, loaded
		var c1 = h.Node(acme.Path, "repo-w1/repo-c1");
		h.OnUi(() =>
		{
			h.Ui.Select(c1);
			var tree = h.Ui.Tree.SelectedNode!;
			tree.IsExpanded = true;
			tree.Children.Last().IsExpanded = true;
			return 0;
		});
		h.Until(() => c1.Children.Any(c => c.Name == "src"), "the rest of repo-c1, loaded");
		Assert.True(h.OnUi(() => h.Ui.Select(c1.Children.Single(c => c.Name == "src"))));
	}

	[Fact(DisplayName = "F4 goes where a file comes from, loading the folders on the way; Alt+← comes back")]
	public void GoToOrigin()
	{
		var acme = Acme();
		// acme shares a note of its docs (a folder loaded only when opened) with repo-c1
		Directory.CreateDirectory(Path.Combine(acme.Path, "_docs", "guide"));
		File.WriteAllText(Path.Combine(acme.Path, "_docs", "guide", "notes.txt"), "notes\n");
		File.AppendAllText(Path.Combine(acme.Path, "_fx", "space.yaml"),
			"\nshares:\n  - path: _docs/guide/notes.txt\n    destination: notes.txt\n    applies-to:\n      repositories: [repo-w1/repo-c1]\n");
		Assert.Empty(Errors(Sync(acme.Path)));
		using var h = Open(acme.Path);
		bool At(string path) => string.Equals(h.Ui.Selected?.Path, path, StringComparison.OrdinalIgnoreCase);

		// an inherited file: its panel says where it comes from, the nearest first, and F4 goes there
		var inherited = h.Node(acme.Path, "repo-w1/repo-c1/_fx/.inherited/dotnet/dotnet.yaml");
		Assert.True(h.OnUi(() => h.Ui.Select(inherited)));
		Assert.Contains("[F4]", h.Shows("repo-w1/_fx/dotnet/dotnet.yaml"));
		h.Key(TerminalKey.F4);
		h.Until(() => At(Path.Combine(acme.Path, "repo-w1", "_fx", "dotnet", "dotnet.yaml")), "F4 goes where the file comes from");
		// a file by hand comes from nowhere: no F4
		h.Until(() => h.Screen() is var screen && !screen.Contains("[F4]") && screen.Contains("[Alt+"), "the keys of a file by hand: Alt+←, no F4");
		h.Key(TerminalKey.Left, TerminalModifiers.Alt);
		h.Until(() => At(inherited.Path), "Alt+← comes back");

		// a share: its source, in a folder of the space not loaded yet
		Assert.True(h.OnUi(() => h.Ui.Select(h.Node(acme.Path, "repo-w1/repo-c1/notes.txt"))));
		h.Key(TerminalKey.F4);
		h.Until(() => At(Path.Combine(acme.Path, "_docs", "guide", "notes.txt")), "F4 goes to the source of the share");
		// shown in the tree, inside its folder now loaded
		h.Until(() => h.Screen().Split('\n').Any(l => l.StartsWith('→') && l.Contains("notes.txt")), "the source, selected in its folder");
	}

	[Fact(DisplayName = "a worktree opens as its own tree, with its mark on top; Alt+← or F6 go back to the main one")]
	public void OpenWorktree()
	{
		var acme = AcmeCommitted();
		Assert.DoesNotContain(WorktreeModule.Add(Path.Combine(acme.Path, "libs", "shared"), "fix/t", null, false, cancellationToken: TestContext.Current.CancellationToken)
			.AllDiagnostics, d => d.Severity == FxSeverity.Error);
		using var h = Open(acme.Path);
		var root = Path.TrimEndingDirectorySeparator(Assert.Single(h.OnUi(() => h.Ui.Model.Worktrees)).Root);
		bool Tree(string path) => string.Equals(h.Ui.Model.Path, path, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("worktree fix/t", h.Screen());
		Assert.DoesNotContain("[F6]", h.Screen());

		h.OnUi(() => h.Ui.OpenTree(root));
		Assert.Contains("[F6]", h.Shows("worktree fix/t"));
		Assert.True(h.OnUi(() => Tree(root)));
		Assert.Equal("fix/t", h.OnUi(() => h.Ui.Worktree?.Branch));
		// what the worktree does not have comes with fx worktree mount, not with fx sync
		Assert.True(h.OnUi(() => h.Ui.Select(h.Node(root, "repo-w1"))));
		h.Shows("fx worktree mount fix/t repo-w1");
		// Enter does it, once confirmed
		h.Key(TerminalKey.Enter);
		h.AnyDialog();
		h.Key(TerminalKey.Enter);
		h.Until(() => h.Ui.Log.Count == 1 && !h.Ui.Busy, "worktree mount");
		Assert.True(h.OnUi(() => h.Ui.Log[0].Outcome!.Ok));
		h.Until(() => h.Find(root, "repo-w1")?.Kind == FxTreeKind.Space, "the tree, read again: repo-w1 mounted");
		h.Key(TerminalKey.Left, TerminalModifiers.Alt);
		h.Until(() => Tree(acme.Path) && !h.Screen().Contains("worktree fix/t"), "Alt+← goes back to the main tree");

		// F6: from the worktree to its main tree, as it was (its doctor is not run again)
		h.OnUi(() => h.Ui.OpenTree(root));
		h.Shows("worktree fix/t");
		h.Key(TerminalKey.F6);
		h.Until(() => Tree(acme.Path) && h.Ui.Worktree is null, "F6 goes back to the main tree");
	}
}
