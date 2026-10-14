using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fuxion.Tools.Cli;
using Fuxion.Tools.Cli.Ui;
using Fuxion.Tools.Core.Explore;
using Fuxion.Tools.Core.Workspace;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Hosting;
using Xunit;

namespace Fuxion.Tools.Test;

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

		public string Screen()
		{
			var screen = new TestScreen(Width, Height);
			screen.Apply(_backend.GetOutText());
			return screen.ToString();
		}

		/// <summary>The line of the screen where <paramref name="text"/> is last.</summary>
		public string LineOf(string text) => Screen().Split('\n').Last(l => l.Contains(text, StringComparison.Ordinal));

		public void Key(TerminalKey key) => _backend.PushEvent(new TerminalKeyEvent { Key = key });

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

	[Fact(DisplayName = "fx ui without a terminal (a pipe, an agent, --output json): wrong usage")]
	public void NoTerminal()
	{
		var acme = AcmeCommitted();
		Assert.Equal(FxConsole.UsageError, FxApp.Run(["ui"], new StringWriter(), new StringWriter(), acme.Path));
		Assert.Equal(FxConsole.UsageError, FxApp.Run(["ui", "--output", "json"], new StringWriter(), new StringWriter(), acme.Path));
	}
}
