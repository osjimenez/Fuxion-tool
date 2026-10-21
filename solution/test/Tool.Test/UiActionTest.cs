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
		Assert.True(h.OnUi(() => h.Ui.Select(h.Node(acme.Path, "repo-w1/repo-c1/_fx/dotnet"))));
		h.Shows("[F7]");
		h.Key(TerminalKey.F7);
		h.Shows("dotnet.g.props");
		Assert.Contains("fx sync --root repo-w1/repo-c1", h.Screen());
		Assert.False(File.Exists(props));

		h.Key(TerminalKey.Enter);
		h.Until(() => h.Ui.Log.Count == 1 && !h.Ui.Busy, "sync");
		Assert.True(File.Exists(props));
		var entry = h.OnUi(() => h.Ui.Log[0]);
		Assert.Equal("fx sync --root repo-w1/repo-c1", entry.Action.Command);
		Assert.True(entry.Outcome!.Ok);
		// the log, with its command; the tree, read again
		h.Key(TerminalKey.F8);
		h.Until(() => h.Screen().Split('\n').Any(l => l.Contains('✓') && l.Contains("fx sync --root repo-w1/repo-c1")), "the log, with the command");
		h.Until(() => h.Find(acme.Path, "repo-w1/repo-c1/_fx/dotnet/dotnet.g.props") is not null, "the tree, read again: the file written");
	}

	/// <summary>
	/// F9 on <paramref name="relative"/> and Enter on its action <paramref name="index"/>: what it opens (its dialog to
	/// confirm, or its form), waited for.
	/// </summary>
	static UiAction Choose(Harness h, string root, string relative, int index)
	{
		var node = h.Node(root, relative);
		Assert.True(h.OnUi(() => h.Ui.Select(node)), relative);
		h.Key(TerminalKey.F9);
		h.Focused(Texts.Get("ui.actions", node.Name));
		for (var i = 0; i < index; i++)
			h.Key(TerminalKey.Down);
		h.Key(TerminalKey.Enter);
		var action = h.OnUi(() => h.Ui.Actions(node)[index]);
		if (action.Open is null)
			h.Focused(action.Label);
		else
			h.Until(() => h.Ui.Form is not null || h.Ui.Editor is not null, $"what '{action.Command}' opens");
		return action;
	}

	[Fact(DisplayName = "F9, the actions of a node: unmounting parks it, Enter on it mounts it back; pull; Esc closes without doing")]
	public void MountActions()
	{
		var acme = AcmeCommitted();
		Append(Path.Combine(acme.Path, "repo-w1", "_fx", "mounts", "mounts.yaml"), "path: repo-c2\n    required: true", "path: repo-c2");
		var c2 = Path.Combine(acme.Path, "repo-w1", "repo-c2");
		using var h = Open(acme.Path);

		// the actions of a repository: sync, pull and, as it is not mandatory, unmount
		Assert.Equal(["fx sync --root repo-w1/repo-c2", "fx mount pull repo-c2 --root repo-w1", "fx mount park repo-c2 --root repo-w1"],
			h.OnUi(() => h.Ui.Actions(h.Node(acme.Path, "repo-w1/repo-c2")).Where(a => a.Open is null).Select(a => a.Command).ToArray()));
		// Esc: nothing
		var unmount = Choose(h, acme.Path, "repo-w1/repo-c2", 2);
		Assert.Equal("fx mount park repo-c2 --root repo-w1", unmount.Command);
		Assert.Contains(unmount.Command, h.Screen());
		h.Key(TerminalKey.Escape);
		h.Focused(null);
		Assert.True(Directory.Exists(c2));
		Assert.Empty(h.OnUi(() => h.Ui.Log));

		// unmounting parks it
		Choose(h, acme.Path, "repo-w1/repo-c2", 2);
		h.Key(TerminalKey.Enter);
		h.Until(() => h.Ui.Log.Count == 1 && !h.Ui.Busy, "unmount");
		Assert.False(Directory.Exists(c2));
		Assert.True(Directory.Exists(Path.Combine(acme.Path, "repo-w1", "_fx", "~$parking", "repo-c2")));
		h.Until(() => h.Find(acme.Path, "repo-w1/repo-c2")?.Kind == FxTreeKind.Parked, "the tree, read again: repo-c2 parked");

		// Enter on it: mounting it brings it back
		Assert.True(h.OnUi(() => h.Ui.Select(h.Node(acme.Path, "repo-w1/repo-c2"))));
		h.Shows("fx mount unpark repo-c2 --root repo-w1");
		h.Key(TerminalKey.Enter);
		h.AnyDialog();
		h.Key(TerminalKey.Enter);
		h.Until(() => h.Ui.Log.Count == 2 && !h.Ui.Busy, "mount");
		Assert.True(Directory.Exists(c2));
		h.Until(() => h.Find(acme.Path, "repo-w1/repo-c2")?.Kind == FxTreeKind.Repository, "the tree, read again: repo-c2 back");

		// pull, from the menu
		Choose(h, acme.Path, "repo-w1/repo-c1", 1);
		h.Key(TerminalKey.Enter);
		h.Until(() => h.Ui.Log.Count == 3 && !h.Ui.Busy, "pull");
		Assert.Equal("fx mount pull repo-c1 --root repo-w1", h.OnUi(() => h.Ui.Log[0].Action.Command));
	}

	[Fact(DisplayName = "a form adds its edits to the pending changes, each with its command; F10 shows them with their diff; Enter writes them, then offers to sync")]
	public void EditRepository()
	{
		var acme = AcmeCommitted();
		var manifest = Path.Combine(acme.Path, "repo-w1", "_fx", "mounts", "mounts.yaml");
		var before = File.ReadAllText(manifest);
		using var h = Open(acme.Path);
		// F9: its actions, the edits after the ones that act
		Assert.Equal(["fx sync --root repo-w1/repo-c2", "fx mount pull repo-c2 --root repo-w1", "fx mount set|tag|rename repo-c2 --root repo-w1", "fx mount remove repo-c2 --root repo-w1"],
			h.OnUi(() => h.Ui.Actions(h.Node(acme.Path, "repo-w1/repo-c2")).Select(a => a.Command).ToArray()));
		Choose(h, acme.Path, "repo-w1/repo-c2", 2);
		Assert.Equal("dotnet, mobile", h.OnUi(() => h.Ui.Form!["tags"].Value));
		h.OnUi(() =>
		{
			h.Ui.Form!["tags"].Text.Value = "dotnet, mobile, app";
			h.Ui.Form!["required"].Check.Value = false;
			return 0;
		});
		// Ctrl+S, as a terminal sends it
		h.Ctrl(TerminalChar.CtrlS);
		h.Until(() => h.Ui.Form is null, "the form closes with its edits");
		Assert.Equal(["fx mount tag repo-c2 app --root repo-w1", "fx mount set repo-c2 required --unset --root repo-w1"],
			h.OnUi(() => h.Ui.Pending.Edits.Select(e => e.Command).ToArray()));
		Assert.Equal(before, File.ReadAllText(manifest));
		h.Shows("[F10]");

		// F10: the edits and the diff; Enter writes them, and the sync of the tree is offered
		h.Key(TerminalKey.F10);
		h.Shows("-    required: true");
		Assert.Contains("fx mount tag repo-c2 app --root repo-w1", h.Screen());
		h.Key(TerminalKey.Enter);
		h.Until(() => !h.Ui.Pending.Any, "the edits, written");
		var after = File.ReadAllText(manifest).Replace("\r\n", "\n");
		Assert.Contains("tags: [dotnet, mobile, app]", after);
		Assert.Contains("    path: repo-c2\n    tags:", after);
		Assert.Equal(["fx mount set repo-c2 required --unset --root repo-w1", "fx mount tag repo-c2 app --root repo-w1"],
			h.OnUi(() => h.Ui.Log.Select(l => l.Action.Command).ToArray()));
		Assert.Contains("fx sync", h.Focused(Texts.Get("ui.action.sync")));
		h.Key(TerminalKey.Escape);
		h.Focused(null);
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
		Assert.True(h.OnUi(() => h.Ui.Select(h.Node(acme.Path, "repo-w1/repo-c1/_fx/mounts"))));
		h.Key(TerminalKey.Enter);
		h.Until(() => h.Ui.Pending.Any, "the module, turned on (pending)");
		Assert.Equal(["fx module enable mounts --root repo-w1/repo-c1"], h.OnUi(() => h.Ui.Pending.Edits.Select(e => e.Command).ToArray()));
		Assert.False(Directory.Exists(Path.Combine(acme.Path, "repo-w1", "repo-c1", "_fx", "mounts")));

		// a repository nobody declares: Enter opens its form, with its place; Ctrl+S adds it
		Assert.True(h.OnUi(() => h.Ui.Select(h.Node(acme.Path, "extras/tool"))));
		h.Key(TerminalKey.Enter);
		h.Until(() => h.Ui.Form is not null, "the form of the repository");
		Assert.Equal("extras/tool", h.OnUi(() => h.Ui.Form!["path"].Value));
		h.OnUi(() => h.Ui.Form!["url"].Text.Value = "https://example.invalid/tool.git");
		h.Ctrl(TerminalChar.CtrlS);
		h.Until(() => h.Ui.Form is null, "the form closes with its edit");
		Assert.Equal("fx mount add https://example.invalid/tool.git extras/tool", h.OnUi(() => h.Ui.Pending.Edits[^1].Command));

		// Ctrl+Q with changes not written: it asks, and Esc goes back
		h.Ctrl(TerminalChar.CtrlQ);
		h.Focused(Texts.Get("ui.quit.title"));
		h.Key(TerminalKey.Escape);
		h.Focused(null);
		Assert.Equal(2, h.OnUi(() => h.Ui.Pending.Edits.Count));

		// F10: Ctrl+Z takes back the last; Delete, all
		h.Key(TerminalKey.F10);
		h.Focused(Texts.Get("ui.pending.title", 2));
		h.Ctrl(TerminalChar.CtrlZ);
		h.Until(() => h.Ui.Pending.Edits.Count == 1, "Ctrl+Z takes back the last");
		Assert.Equal(["fx module enable mounts --root repo-w1/repo-c1"], h.OnUi(() => h.Ui.Pending.Edits.Select(e => e.Command).ToArray()));
		h.Key(TerminalKey.Delete);
		h.Until(() => !h.Ui.Pending.Any, "Delete throws all away");
		Assert.DoesNotContain("extras/tool", File.ReadAllText(Path.Combine(acme.Path, "_fx", "mounts", "mounts.yaml")));
	}

	[Fact(DisplayName = "the raw YAML of a file of fx: not valid, it says where and nothing is pending; valid, it is pending and written with its line endings; F4 from a form")]
	public void EditYaml()
	{
		var acme = AcmeCommitted();
		var manifest = Path.Combine(acme.Path, "repo-w1", "_fx", "mounts", "mounts.yaml");
		var original = File.ReadAllText(manifest);
		using var h = Open(acme.Path);
		// its actions: syncing its root, pulling, and its YAML by hand
		Assert.Equal(3, h.OnUi(() => h.Ui.Actions(h.Node(acme.Path, "repo-w1/_fx/mounts/mounts.yaml")).Count));
		Choose(h, acme.Path, "repo-w1/_fx/mounts/mounts.yaml", 2);
		Assert.Contains("name: repo-c2", h.OnUi(() => h.Ui.Editor!.Text));

		// not valid: where, and nothing pending
		h.OnUi(() => h.Ui.Editor!.Text = "schema: 1.0.0\nmounts: [\n");
		h.Ctrl(TerminalChar.CtrlS);
		h.Until(() => h.Ui.EditorError is not null, "the editor says what is wrong");
		Assert.False(h.OnUi(() => h.Ui.Pending.Any));
		Assert.NotNull(h.OnUi(() => h.Ui.Editor));

		// valid: pending, by hand; written with the line endings of the file
		h.OnUi(() => h.Ui.Editor!.Text = original.Replace("\r\n", "\n") + "# by hand\n");
		h.Ctrl(TerminalChar.CtrlS);
		h.Until(() => h.Ui.Editor is null, "the editor closes with a valid YAML");
		Assert.EndsWith("_fx/mounts/mounts.yaml", h.OnUi(() => h.Ui.Pending.Edits.Single().Command));
		Assert.True(h.OnUi(() => h.Ui.ApplyPending()));
		var written = File.ReadAllText(manifest);
		Assert.EndsWith("# by hand" + (original.Contains("\r\n") ? "\r\n" : "\n"), written);
		Assert.Equal(original.Contains("\r\n"), written.Contains("\r\n"));
		// the sync it offers, not now
		h.Focused(Texts.Get("ui.action.sync"));
		h.Key(TerminalKey.Escape);
		h.Focused(null);

		// F4 from a form: what it changes, pending first; then its YAML, with it
		Choose(h, acme.Path, "repo-w1/repo-c1", 2);
		h.OnUi(() => h.Ui.Form!["tags"].Text.Value = "dotnet, app, mobile");
		h.Key(TerminalKey.F4);
		h.Until(() => h.Ui.Form is null && h.Ui.Editor is not null, "the YAML, from the form");
		Assert.Equal("fx mount tag repo-c1 mobile --root repo-w1", h.OnUi(() => h.Ui.Pending.Edits.Single().Command));
		Assert.Contains("tags: [dotnet, app, mobile]", h.OnUi(() => h.Ui.Editor!.Text));
		h.Key(TerminalKey.Escape);
		h.Until(() => h.Ui.Editor is null, "Esc closes the YAML");

		// the form again: as the pending changes leave it
		Choose(h, acme.Path, "repo-w1/repo-c1", 2);
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
