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

/// <summary>
/// <c>fx ui</c>, the layer to see (plan O, O4.1b), on the sample tree, in a terminal in memory: the app runs on its
/// thread over the in-memory backend, the test pushes keys, posts work to that thread and reads the screen back. The
/// checks look at what does not depend on the language (names, paths, commands, marks): other tests may change it.
/// </summary>
public sealed class UiTest : SampleTree
{
	const int Width = 160, Height = 50;

	sealed class Harness : IDisposable
	{
		readonly InMemoryTerminalBackend _backend = new(new TerminalSize(Width, Height));
		readonly TerminalSession _session;
		readonly CancellationTokenSource _cts = new();
		readonly Task _loop;
		public readonly TerminalApp App;
		public readonly UiApp Ui;

		public Harness(UiApp ui)
		{
			Ui = ui;
			_session = Terminal.Open(_backend, new TerminalOptions { ImplicitStartInput = true }, force: true);
			App = new TerminalApp(ui.Root, _session.Instance, new TerminalAppOptions { HostKind = TerminalHostKind.Fullscreen });
			_loop = Task.Run(() => App.Run(_cts.Token));
		}

		/// <summary>Runs <paramref name="work"/> on the thread of the app and waits for it.</summary>
		public T OnUi<T>(Func<T> work)
		{
			var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
			App.Post(() =>
			{
				try
				{
					done.SetResult(work());
				}
				catch (Exception ex)
				{
					done.SetException(ex);
				}
			});
			return done.Task.WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
		}

		/// <summary>What the background found, on the screen, and a frame drawn.</summary>
		public void Settle(int ms = 150)
		{
			OnUi(() => Ui.OnUpdate());
			Thread.Sleep(ms);
			OnUi(() => 0);
		}

		/// <summary>Waits until the doctor has put its marks on the tree.</summary>
		public void Doctor()
		{
			for (var i = 0; i < 240; i++)
			{
				if (OnUi(() =>
				    {
					    Ui.OnUpdate();
					    return Ui.DoctorDone;
				    }))
				{
					Settle();
					return;
				}
				Thread.Sleep(250);
			}
			throw new TimeoutException("the doctor did not finish");
		}

		/// <summary>Waits until <paramref name="condition"/> holds, with what the background found on the screen.</summary>
		public void Until(Func<bool> condition, string what)
		{
			for (var i = 0; i < 240; i++)
			{
				if (OnUi(() =>
				    {
					    Ui.OnUpdate();
					    return condition();
				    }))
				{
					Settle();
					return;
				}
				Thread.Sleep(250);
			}
			throw new TimeoutException(what);
		}

		public string Screen()
		{
			var screen = new TestScreen(Width, Height);
			screen.Apply(_backend.GetOutText());
			return screen.ToString();
		}

		/// <summary>The line of the screen where <paramref name="text"/> is last.</summary>
		public string LineOf(string text) => Screen().Split('\n').Last(l => l.Contains(text, StringComparison.Ordinal));

		public void Key(TerminalKey key, TerminalModifiers modifiers = TerminalModifiers.None) => _backend.PushEvent(new TerminalKeyEvent { Key = key, Modifiers = modifiers });

		/// <summary>A control key as a terminal sends it: Ctrl+Z is the character 0x1A (<see cref="TerminalChar.CtrlZ"/>).</summary>
		public void Ctrl(char control) => _backend.PushEvent(new TerminalKeyEvent { Key = TerminalKey.Unknown, Char = control, Modifiers = TerminalModifiers.Ctrl });

		public void Dispose()
		{
			Ui.Stop();
			_cts.Cancel();
			try
			{
				_loop.Wait(TimeSpan.FromSeconds(10));
			}
			catch (AggregateException)
			{
			}
			_session.Dispose();
		}
	}

	static FxTreeNode Node(UiApp ui, string root, string relative) => ui.Model.Find(Path.Combine(root, relative))!;

	Harness Open(string directory, string? start = null)
	{
		var ui = new UiApp(FxTree.Build(directory)!, start ?? directory);
		ui.Start();
		var harness = new Harness(ui);
		harness.Settle(300);
		return harness;
	}

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
		Assert.Equal(FxConsole.UsageError, FxApp.Run(["ui"], new StringWriter(), new StringWriter(), acme.Path));
		Assert.Equal(FxConsole.UsageError, FxApp.Run(["ui", "--output", "json"], new StringWriter(), new StringWriter(), acme.Path));
	}
}
