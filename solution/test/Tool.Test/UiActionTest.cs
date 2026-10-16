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

/// <summary><c>fx ui</c>, the layers to act and to edit (plan O, O4.2 and O4.3): sync, mount, pull, the forms, the raw YAML.</summary>
public sealed class UiActionTest : UiTree
{
	[Fact(DisplayName = "F7 syncs the root of the node: the dialog shows its command and what it would do; Enter does it, and the log keeps it")]
	public void SyncAction()
	{
		var acme = AcmeCommitted();
		var props = Path.Combine(acme.Path, "repo-w1", "repo-c1", "_fx", "dotnet", "dotnet.g.props");
		File.Delete(props);
		using var h = Open(acme.Path);
		Assert.True(h.OnUi(() => h.Ui.Select(Node(h.Ui, acme.Path, "repo-w1/repo-c1/_fx/dotnet"))));
		h.Settle();
		Assert.Contains("[F7]", h.Screen());
		h.Key(TerminalKey.F7);
		h.Until(() => h.Screen().Contains("dotnet.g.props"), "the preview of sync");
		Assert.Contains("fx sync --root repo-w1/repo-c1", h.Screen());
		Assert.False(File.Exists(props));

		h.Key(TerminalKey.Enter);
		h.Until(() => h.Ui.Log.Count == 1, "sync");
		Assert.True(File.Exists(props));
		var entry = h.OnUi(() => h.Ui.Log[0]);
		Assert.Equal("fx sync --root repo-w1/repo-c1", entry.Action.Command);
		Assert.True(entry.Outcome!.Ok);
		// the log, with its command; the tree, read again
		h.Key(TerminalKey.F8);
		h.Settle();
		Assert.Contains("fx sync --root repo-w1/repo-c1", h.LineOf("✓"));
		Assert.NotNull(Node(h.Ui, acme.Path, "repo-w1/repo-c1/_fx/dotnet/dotnet.g.props"));
	}

	[Fact(DisplayName = "F9, the actions of a node: unmounting parks it, Enter on it mounts it back; pull; Esc closes without doing")]
	public void MountActions()
	{
		var acme = AcmeCommitted();
		Append(Path.Combine(acme.Path, "repo-w1", "_fx", "workspace", "workspace.yaml"), "path: repo-c2\n    mount: mandatory", "path: repo-c2\n    mount: manual");
		var c2 = Path.Combine(acme.Path, "repo-w1", "repo-c2");
		using var h = Open(acme.Path);
		void Choose(string relative, int index)
		{
			Assert.True(h.OnUi(() => h.Ui.Select(Node(h.Ui, acme.Path, relative))), relative);
			h.Settle();
			h.Key(TerminalKey.F9);
			h.Settle();
			for (var i = 0; i < index; i++)
				h.Key(TerminalKey.Down);
			h.Key(TerminalKey.Enter);
			h.Settle();
		}

		// the actions of a repository: sync, pull and, as it is not mandatory, unmount
		Assert.Equal(["fx sync --root repo-w1/repo-c2", "fx repo pull --repo repo-c2 --root repo-w1", "fx repo unmount repo-c2 --root repo-w1"],
			h.OnUi(() => h.Ui.Actions(Node(h.Ui, acme.Path, "repo-w1/repo-c2")).Where(a => a.Open is null).Select(a => a.Command).ToArray()));
		// Esc: nothing
		Choose("repo-w1/repo-c2", 2);
		Assert.Contains("fx repo unmount repo-c2 --root repo-w1", h.Screen());
		h.Key(TerminalKey.Escape);
		h.Settle();
		Assert.True(Directory.Exists(c2));
		Assert.Empty(h.OnUi(() => h.Ui.Log));

		// unmounting parks it
		Choose("repo-w1/repo-c2", 2);
		h.Key(TerminalKey.Enter);
		h.Until(() => h.Ui.Log.Count == 1, "unmount");
		Assert.False(Directory.Exists(c2));
		Assert.True(Directory.Exists(Path.Combine(acme.Path, "repo-w1", "_fx", "~$parking", "repo-c2")));
		var parked = Node(h.Ui, acme.Path, "repo-w1/repo-c2");
		Assert.Equal(FxTreeKind.Parked, parked.Kind);

		// Enter on it: mounting it brings it back
		Assert.True(h.OnUi(() => h.Ui.Select(parked)));
		h.Settle();
		Assert.Contains("fx repo mount repo-c2 --root repo-w1", h.Screen());
		h.Key(TerminalKey.Enter);
		h.Settle();
		h.Key(TerminalKey.Enter);
		h.Until(() => h.Ui.Log.Count == 2, "mount");
		Assert.True(Directory.Exists(c2));
		Assert.Equal(FxTreeKind.Repository, Node(h.Ui, acme.Path, "repo-w1/repo-c2").Kind);

		// pull, from the menu
		Choose("repo-w1/repo-c1", 1);
		h.Key(TerminalKey.Enter);
		h.Until(() => h.Ui.Log.Count == 3, "pull");
		Assert.Equal("fx repo pull --repo repo-c1 --root repo-w1", h.OnUi(() => h.Ui.Log[0].Action.Command));
	}

	[Fact(DisplayName = "a form adds its edits to the pending changes, each with its command; F10 shows them with their diff; Enter writes them, then offers to sync")]
	public void EditRepository()
	{
		var acme = AcmeCommitted();
		var manifest = Path.Combine(acme.Path, "repo-w1", "_fx", "workspace", "workspace.yaml");
		var before = File.ReadAllText(manifest);
		using var h = Open(acme.Path);
		var c2 = Node(h.Ui, acme.Path, "repo-w1/repo-c2");
		// F9: its actions, the edits after the ones that act
		Assert.Equal(["fx sync --root repo-w1/repo-c2", "fx repo pull --repo repo-c2 --root repo-w1", "fx repo set|tag|rename repo-c2 --root repo-w1", "fx repo remove repo-c2 --root repo-w1"],
			h.OnUi(() => h.Ui.Actions(c2).Select(a => a.Command).ToArray()));
		Assert.True(h.OnUi(() => h.Ui.Select(c2)));
		h.Settle();
		h.Key(TerminalKey.F9);
		h.Settle();
		h.Key(TerminalKey.Down);
		h.Key(TerminalKey.Down);
		h.Key(TerminalKey.Enter);
		h.Settle();
		Assert.Equal("dotnet, mobile", h.OnUi(() => h.Ui.Form!["tags"].Value));
		h.OnUi(() =>
		{
			h.Ui.Form!["tags"].Text.Value = "dotnet, mobile, app";
			h.Ui.Form!["mandatory"].Check.Value = false;
			return 0;
		});
		// Ctrl+S, as a terminal sends it
		h.Ctrl(TerminalChar.CtrlS);
		h.Settle();
		Assert.Null(h.OnUi(() => h.Ui.Form));
		Assert.Equal(["fx repo tag repo-c2 app --root repo-w1", "fx repo set repo-c2 mount manual --root repo-w1"],
			h.OnUi(() => h.Ui.Pending.Edits.Select(e => e.Command).ToArray()));
		Assert.Equal(before, File.ReadAllText(manifest));
		Assert.Contains("[F10]", h.Screen());

		// F10: the edits and the diff; Enter writes them, and the sync of the tree is offered
		h.Key(TerminalKey.F10);
		h.Until(() => h.Screen().Contains("+    mount: manual"), "the diff");
		Assert.Contains("fx repo tag repo-c2 app --root repo-w1", h.Screen());
		h.Key(TerminalKey.Enter);
		h.Settle();
		Assert.False(h.OnUi(() => h.Ui.Pending.Any));
		var after = File.ReadAllText(manifest);
		Assert.Contains("tags: [dotnet, mobile, app]", after);
		Assert.Contains("mount: manual", after);
		Assert.Equal(["fx repo set repo-c2 mount manual --root repo-w1", "fx repo tag repo-c2 app --root repo-w1"],
			h.OnUi(() => h.Ui.Log.Select(l => l.Action.Command).ToArray()));
		Assert.Contains("fx sync", h.Screen());
		h.Key(TerminalKey.Escape);
		h.Settle();
		Assert.Equal(2, h.OnUi(() => h.Ui.Log.Count));
	}

	[Fact(DisplayName = "Enter on a module not used turns it on, and on a ? opens its form; Ctrl+Z takes back the last, Delete throws all away; Ctrl+Q asks first")]
	public void EditPending()
	{
		var acme = AcmeCommitted();
		var stranger = Path.Combine(acme.Path, "extras", "tool");
		Directory.CreateDirectory(stranger);
		acme.Git("init", "--quiet", stranger);
		using var h = Open(acme.Path);

		// a module not used: Enter turns it on, pending
		Assert.True(h.OnUi(() => h.Ui.Select(Node(h.Ui, acme.Path, "repo-w1/repo-c1/_fx/workspace"))));
		h.Settle();
		h.Key(TerminalKey.Enter);
		h.Settle();
		Assert.Equal(["fx module enable workspace --root repo-w1/repo-c1"], h.OnUi(() => h.Ui.Pending.Edits.Select(e => e.Command).ToArray()));
		Assert.False(Directory.Exists(Path.Combine(acme.Path, "repo-w1", "repo-c1", "_fx", "workspace")));

		// a repository nobody declares: Enter opens its form, with its place; Ctrl+S adds it
		Assert.True(h.OnUi(() => h.Ui.Select(Node(h.Ui, acme.Path, "extras/tool"))));
		h.Settle();
		h.Key(TerminalKey.Enter);
		h.Settle();
		Assert.Equal("extras/tool", h.OnUi(() => h.Ui.Form!["path"].Value));
		h.OnUi(() => h.Ui.Form!["url"].Text.Value = "https://example.invalid/tool.git");
		h.Ctrl(TerminalChar.CtrlS);
		h.Settle();
		Assert.Equal("fx repo add https://example.invalid/tool.git extras/tool", h.OnUi(() => h.Ui.Pending.Edits[^1].Command));

		// Ctrl+Q with changes not written: it asks, and Esc goes back
		h.Ctrl(TerminalChar.CtrlQ);
		h.Settle();
		h.Key(TerminalKey.Escape);
		h.Settle();
		Assert.Equal(2, h.OnUi(() => h.Ui.Pending.Edits.Count));

		// F10: Ctrl+Z takes back the last; Delete, all
		h.Key(TerminalKey.F10);
		h.Settle();
		h.Ctrl(TerminalChar.CtrlZ);
		h.Settle();
		Assert.Equal(["fx module enable workspace --root repo-w1/repo-c1"], h.OnUi(() => h.Ui.Pending.Edits.Select(e => e.Command).ToArray()));
		h.Key(TerminalKey.Delete);
		h.Settle();
		Assert.False(h.OnUi(() => h.Ui.Pending.Any));
		Assert.DoesNotContain("extras/tool", File.ReadAllText(Path.Combine(acme.Path, "_fx", "workspace", "workspace.yaml")));
	}

	[Fact(DisplayName = "the raw YAML of a file of fx: not valid, it says where and nothing is pending; valid, it is pending and written with its line endings; F4 from a form")]
	public void EditYaml()
	{
		var acme = AcmeCommitted();
		var manifest = Path.Combine(acme.Path, "repo-w1", "_fx", "workspace", "workspace.yaml");
		var original = File.ReadAllText(manifest);
		using var h = Open(acme.Path);
		var file = Node(h.Ui, acme.Path, "repo-w1/_fx/workspace/workspace.yaml");
		// its actions: syncing its root, pulling, and its YAML by hand
		Assert.Equal(3, h.OnUi(() => h.Ui.Actions(file).Count));
		Assert.True(h.OnUi(() => h.Ui.Select(file)));
		h.Settle();
		h.Key(TerminalKey.F9);
		h.Settle();
		h.Key(TerminalKey.Down);
		h.Key(TerminalKey.Down);
		h.Key(TerminalKey.Enter);
		h.Settle();
		Assert.Contains("name: repo-c2", h.OnUi(() => h.Ui.Editor!.Text));

		// not valid: where, and nothing pending
		h.OnUi(() => h.Ui.Editor!.Text = "schema: 1.0.0\nrepositories: [\n");
		h.Ctrl(TerminalChar.CtrlS);
		h.Settle();
		Assert.False(h.OnUi(() => h.Ui.Pending.Any));
		Assert.NotNull(h.OnUi(() => h.Ui.Editor));

		// valid: pending, by hand; written with the line endings of the file
		h.OnUi(() => h.Ui.Editor!.Text = original.Replace("\r\n", "\n") + "# by hand\n");
		h.Ctrl(TerminalChar.CtrlS);
		h.Settle();
		Assert.Null(h.OnUi(() => h.Ui.Editor));
		Assert.EndsWith("_fx/workspace/workspace.yaml", h.OnUi(() => h.Ui.Pending.Edits.Single().Command));
		Assert.True(h.OnUi(() => h.Ui.ApplyPending()));
		h.Settle();
		var written = File.ReadAllText(manifest);
		Assert.EndsWith("# by hand" + (original.Contains("\r\n") ? "\r\n" : "\n"), written);
		Assert.Equal(original.Contains("\r\n"), written.Contains("\r\n"));
		h.Key(TerminalKey.Escape);
		h.Settle();

		// F4 from a form: what it changes, pending first; then its YAML, with it
		Assert.True(h.OnUi(() => h.Ui.Select(Node(h.Ui, acme.Path, "repo-w1/repo-c1"))));
		h.Settle();
		h.Key(TerminalKey.F9);
		h.Settle();
		h.Key(TerminalKey.Down);
		h.Key(TerminalKey.Down);
		h.Key(TerminalKey.Enter);
		h.Settle();
		h.OnUi(() => h.Ui.Form!["tags"].Text.Value = "dotnet, app, mobile");
		h.Key(TerminalKey.F4);
		h.Settle();
		Assert.Null(h.OnUi(() => h.Ui.Form));
		Assert.Equal("fx repo tag repo-c1 mobile --root repo-w1", h.OnUi(() => h.Ui.Pending.Edits.Single().Command));
		Assert.Contains("tags: [dotnet, app, mobile]", h.OnUi(() => h.Ui.Editor!.Text));
		h.Key(TerminalKey.Escape);
		h.Settle();

		// the form again: as the pending changes leave it
		h.Key(TerminalKey.F9);
		h.Settle();
		h.Key(TerminalKey.Down);
		h.Key(TerminalKey.Down);
		h.Key(TerminalKey.Enter);
		h.Settle();
		Assert.Equal("dotnet, app, mobile", h.OnUi(() => h.Ui.Form!["tags"].Value));
	}

	[Fact(DisplayName = "fx ui without a terminal (a pipe, an agent, --output json): wrong usage")]
	public void NoTerminal()
	{
		var acme = AcmeCommitted();
		Assert.Equal(FxConsole.UsageError, InProcess.Run(["ui"], new StringWriter(), new StringWriter(), acme.Path));
		Assert.Equal(FxConsole.UsageError, InProcess.Run(["ui", "--output", "json"], new StringWriter(), new StringWriter(), acme.Path));
	}
}
