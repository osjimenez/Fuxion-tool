using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fuxion.Tool.Cli;
using Fuxion.Tool.Cli.Ui;
using Fuxion.Tool.Core.Explore;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Workspace;
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
	[Fact(DisplayName = "the first frame: the tree from the highest workspace, the node where it starts, the panel of the workspace and the keys")]
	public void FirstFrame()
	{
		var acme = AcmeCommitted();
		using var h = Open(acme.Path, Path.Combine(acme.Path, "repo-w1", "repo-c1"));
		var screen = h.Screen();
		Assert.Contains("fx ui", screen);
		Assert.Contains("/acme", screen);
		foreach (var name in new[] { "_fx", "repo-w1", "repo-c1", "repo-w2", "libs" })
			Assert.Contains(name, screen);
		// started inside repo-c1: it is selected, and its panel says who it is
		Assert.Equal("repo-c1", h.OnUi(() => h.Ui.Selected?.Name));
		Assert.Contains("/acme/repo-w1/repo-c1", screen);
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
		h.OnUi(() => h.Ui.Select(Node(h.Ui, acme.Path, "repo-w1/repo-c2")));
		h.Settle();
		// git (*1: the change) and the doctor (!) on repo-c2, the doctor on repo-w1 too
		Assert.Contains("*1", h.LineOf(" repo-c2"));
		Assert.Contains("!", h.LineOf(" repo-c2"));
		Assert.Contains("!", h.LineOf(" repo-w1"));

		// F2: the list of problems; Enter on one goes to its node
		h.Key(TerminalKey.F2);
		h.Settle();
		var index = h.OnUi(() =>
		{
			var items = h.Ui.Problems.Items.ToList();
			return items.FindIndex(p => p.Diagnostic.Code == WorkspaceModule.Dirty);
		});
		Assert.True(index >= 0);
		Assert.Contains("workspace.dirty", h.Screen());
		h.OnUi(() =>
		{
			h.Ui.Select(h.Ui.Model);
			h.Ui.Problems.SelectedIndex = index;
			h.App.Focus(h.Ui.Problems);
			return 0;
		});
		h.Key(TerminalKey.Enter);
		h.Settle();
		Assert.Equal("repo-c2", h.OnUi(() => h.Ui.Selected?.Name));
	}

	[Fact(DisplayName = "the panels: a copy that is not used says which is, a module not used says how to turn it on, a file says whose it is")]
	public void Panels()
	{
		var acme = AcmeCommitted();
		using var h = Open(acme.Path);
		string Show(string relative)
		{
			Assert.True(h.OnUi(() => h.Ui.Select(Node(h.Ui, acme.Path, relative))), relative);
			h.Settle();
			return h.Screen();
		}
		// the copy that is not used: nothing to clone it
		var ghost = Show("repo-w2/libs/shared");
		Assert.Contains("/acme/shared", ghost);
		Assert.DoesNotContain("fx sync", ghost);
		Assert.Contains("fx module enable workspace", Show("repo-w1/repo-c1/_fx/workspace"));
		Assert.Contains("fx sync", Show("Acme.slnx"));
		// the content, a line each
		var solution = h.LineOf("<Solution>");
		Assert.DoesNotContain("<Folder", solution);
		Assert.DoesNotContain("<Project", solution);
		Assert.Contains("/acme", Show("repo-w1/repo-c1/_fx/.inherited/dotnet/dotnet.yaml"));
		// the catalog of tags, on fx.yaml
		var tags = Show("_fx/fx.yaml");
		Assert.Contains("library", tags);
		Assert.Contains("repo-c1", tags);
	}

	[Fact(DisplayName = "F3: what does not matter to fx, and the local files; the rest of a repository, when it is opened")]
	public void All()
	{
		var acme = AcmeCommitted();
		using var h = Open(acme.Path);
		var state = Node(h.Ui, acme.Path, "_fx/workspace/~$state.yaml");
		Assert.False(h.OnUi(() => h.Ui.Select(state)));
		h.Key(TerminalKey.F3);
		h.Settle();
		Assert.True(h.OnUi(() => h.Ui.Select(state)));
		h.Settle();
		Assert.Contains("~$state.yaml", h.Screen());

		// repo-c1 opened: its src, loaded
		var c1 = Node(h.Ui, acme.Path, "repo-w1/repo-c1");
		h.OnUi(() =>
		{
			h.Ui.Select(c1);
			var tree = h.Ui.Tree.SelectedNode!;
			tree.IsExpanded = true;
			tree.Children.Last().IsExpanded = true;
			return 0;
		});
		h.Settle();
		Assert.Contains(c1.Children, c => c.Name == "src");
		Assert.True(h.OnUi(() => h.Ui.Select(c1.Children.Single(c => c.Name == "src"))));
	}

	[Fact(DisplayName = "F4 goes where a file comes from, loading the folders on the way; Alt+← comes back")]
	public void GoToOrigin()
	{
		var acme = Acme();
		// acme shares a note of its docs (a folder loaded only when opened) with repo-c1
		Directory.CreateDirectory(Path.Combine(acme.Path, "_docs", "guide"));
		File.WriteAllText(Path.Combine(acme.Path, "_docs", "guide", "notes.txt"), "notes\n");
		File.AppendAllText(Path.Combine(acme.Path, "_fx", "workspace", "workspace.yaml"),
			"\nshares:\n  - path: _docs/guide/notes.txt\n    destination: notes.txt\n    applies-to:\n      repositories: [repo-w1/repo-c1]\n");
		Assert.Empty(Errors(Sync(acme.Path)));
		using var h = Open(acme.Path);
		string? Selected() => h.OnUi(() => h.Ui.Selected?.Path);

		// an inherited file: its panel says where it comes from, the nearest first, and F4 goes there
		var inherited = Node(h.Ui, acme.Path, "repo-w1/repo-c1/_fx/.inherited/dotnet/dotnet.yaml");
		Assert.True(h.OnUi(() => h.Ui.Select(inherited)));
		h.Settle();
		Assert.Contains("repo-w1/_fx/dotnet/dotnet.yaml", h.Screen());
		Assert.Contains("[F4]", h.Screen());
		h.Key(TerminalKey.F4);
		h.Settle();
		Assert.Equal(Path.Combine(acme.Path, "repo-w1", "_fx", "dotnet", "dotnet.yaml"), Selected(), ignoreCase: true);
		// a file by hand comes from nowhere: no F4
		Assert.DoesNotContain("[F4]", h.Screen());
		Assert.Contains("[Alt+", h.Screen());
		h.Key(TerminalKey.Left, TerminalModifiers.Alt);
		h.Settle();
		Assert.Equal(inherited.Path, Selected());

		// a share: its source, in a folder of the workspace not loaded yet
		Assert.True(h.OnUi(() => h.Ui.Select(Node(h.Ui, acme.Path, "repo-w1/repo-c1/notes.txt"))));
		h.Key(TerminalKey.F4);
		h.Settle();
		Assert.Equal(Path.Combine(acme.Path, "_docs", "guide", "notes.txt"), Selected(), ignoreCase: true);
		// shown in the tree, inside its folder now loaded
		Assert.Contains("notes.txt", h.Screen().Split('\n').Single(l => l.StartsWith('→')));
	}

	[Fact(DisplayName = "a worktree opens as its own tree, with its mark on top; Alt+← or F6 go back to the main one")]
	public void OpenWorktree()
	{
		var acme = AcmeCommitted();
		Assert.DoesNotContain(WorktreeModule.Add(Path.Combine(acme.Path, "libs", "shared"), "fix/t", null, false, cancellationToken: TestContext.Current.CancellationToken)
			.AllDiagnostics, d => d.Severity == FxSeverity.Error);
		using var h = Open(acme.Path);
		var root = Path.TrimEndingDirectorySeparator(Assert.Single(h.Ui.Model.Worktrees).Root);
		string Tree() => h.OnUi(() => h.Ui.Model.Path);
		Assert.DoesNotContain("worktree fix/t", h.Screen());
		Assert.DoesNotContain("[F6]", h.Screen());

		h.OnUi(() => h.Ui.OpenTree(root));
		h.Settle();
		Assert.Equal(root, Tree());
		Assert.Equal("fix/t", h.OnUi(() => h.Ui.Worktree?.Branch));
		Assert.Contains("worktree fix/t", h.Screen());
		Assert.Contains("[F6]", h.Screen());
		// what the worktree does not have comes with fx worktree mount, not with fx sync
		Assert.True(h.OnUi(() => h.Ui.Select(Node(h.Ui, root, "repo-w1"))));
		h.Settle();
		Assert.Contains("fx worktree mount fix/t repo-w1", h.Screen());
		// Enter does it, once confirmed
		h.Key(TerminalKey.Enter);
		h.Settle();
		h.Key(TerminalKey.Enter);
		h.Until(() => h.Ui.Log.Count == 1, "worktree mount");
		Assert.True(h.OnUi(() => h.Ui.Log[0].Outcome!.Ok));
		Assert.Equal(FxTreeKind.Workspace, Node(h.Ui, root, "repo-w1").Kind);
		h.Key(TerminalKey.Left, TerminalModifiers.Alt);
		h.Settle();
		Assert.Equal(acme.Path, Tree(), ignoreCase: true);
		Assert.DoesNotContain("worktree fix/t", h.Screen());

		// F6: from the worktree to its main tree, as it was (its doctor is not run again)
		h.OnUi(() => h.Ui.OpenTree(root));
		h.Settle();
		h.Key(TerminalKey.F6);
		h.Settle();
		Assert.Equal(acme.Path, Tree(), ignoreCase: true);
		Assert.Null(h.OnUi(() => h.Ui.Worktree));
	}
}
