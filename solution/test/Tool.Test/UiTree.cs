using System;
using System.Diagnostics;
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
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Hosting;
using Xunit;

namespace Fuxion.Tool.Test;

/// <summary>
/// What the tests of <c>fx ui</c> share: the sample tree and the app in a terminal in memory. The app runs on its thread
/// over the in-memory backend, the test pushes keys, posts work to that thread and reads the screen back. The checks look
/// at what does not depend on the language (names, paths, commands, marks). While the app is open it has the dispatcher
/// of XenoAtom, one per process, to itself (<see cref="InProcess"/>): the rest of the test runs in parallel.
/// </summary>
/// <remarks>
/// A key takes effect when the app reads it, and a slow machine (a runner of the CI, with the other tests running in
/// parallel) may need more than a frame: after a key, a test waits for what it brings (<see cref="Harness.Until"/>,
/// <see cref="Harness.Shows"/>, <see cref="Harness.Focused"/>), never a while. Every wait has its limit
/// (<see cref="Harness.Patience"/>): past it, the test fails saying what it waited for and what the screen showed.
/// </remarks>
public abstract class UiTree : SampleTree
{
	protected const int Width = 160, Height = 50;

	protected sealed class Harness : IDisposable
	{
		/// <summary>How long a wait for the app lasts before the test fails: plenty for a slow runner, never forever.</summary>
		public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

		/// <summary>Whether this thread runs work of the app now: <see cref="OnUi{T}"/> inside it runs it at once.</summary>
		[ThreadStatic]
		static bool _onUi;

		readonly InMemoryTerminalBackend _backend = new(new TerminalSize(Width, Height));
		readonly TerminalSession _session;
		readonly CancellationTokenSource _cts = new();
		readonly Task _loop;
		public readonly TerminalApp App;
		public readonly UiApp Ui;
		readonly IDisposable _alone;

		public Harness(UiApp ui, IDisposable alone)
		{
			Ui = ui;
			_alone = alone;
			_session = Terminal.Open(_backend, new TerminalOptions { ImplicitStartInput = true }, force: true);
			App = new TerminalApp(ui.Root, _session.Instance, new TerminalAppOptions { HostKind = TerminalHostKind.Fullscreen });
			_loop = Task.Run(() => App.Run(_cts.Token));
		}

		/// <summary>Runs <paramref name="work"/> on the thread of the app and waits for it (at most <see cref="Patience"/>).</summary>
		public T OnUi<T>(Func<T> work)
		{
			if (_onUi)
				return work();
			var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
			App.Post(() =>
			{
				_onUi = true;
				try
				{
					done.SetResult(work());
				}
				catch (Exception ex)
				{
					done.SetException(ex);
				}
				finally
				{
					_onUi = false;
				}
			});
			try
			{
				return done.Task.WaitAsync(Patience).GetAwaiter().GetResult();
			}
			catch (TimeoutException)
			{
				throw new TimeoutException($"the thread of the app did not answer in {Patience.TotalSeconds:0} s");
			}
		}

		/// <summary>
		/// What the background found, on the screen, and a frame drawn. Then, until the screen stops changing (two reads
		/// alike, two seconds at most). It does not wait for a key to take effect: after one, wait for what it brings.
		/// </summary>
		public void Settle(int ms = 150)
		{
			OnUi(() => Ui.OnUpdate());
			Thread.Sleep(ms);
			var last = Text().Length;
			for (int i = 0, alike = 0; i < 20 && alike < 2; i++)
			{
				Thread.Sleep(100);
				var now = Text().Length;
				alike = now == last ? alike + 1 : 0;
				last = now;
			}
			OnUi(() => 0);
		}

		/// <summary>Waits until the doctor has put its marks on the tree (it runs git on every repository: twice the patience).</summary>
		public void Doctor() => Until(() => Ui.DoctorDone, "the doctor finishes", Patience * 2);

		/// <summary>
		/// Waits until the screen shows <paramref name="text"/>, and returns it. The text must be one the screen before did
		/// not have.
		/// </summary>
		public string Shows(string text)
		{
			Until(() => Screen().Contains(text, StringComparison.Ordinal), $"the screen shows '{text}'");
			return Screen();
		}

		/// <summary>
		/// Waits until the dialog titled <paramref name="title"/> has the focus (a key opened it), or, with null, until no
		/// dialog has it (a key closed it); returns the screen.
		/// </summary>
		public string Focused(string? title)
		{
			Until(() => FocusedDialog() == title, title is null ? "no dialog has the focus" : $"the dialog '{title}' has the focus");
			return Screen();
		}

		/// <summary>Waits until a dialog has the focus, whichever it is (a key opened it); returns its title.</summary>
		public string AnyDialog()
		{
			Until(() => FocusedDialog() is not null, "a dialog has the focus");
			return FocusedDialog()!;
		}

		/// <summary>The title of the dialog with the focus (the one on top); null if the focus is on the screen behind.</summary>
		public string? FocusedDialog() => OnUi(() =>
		{
			for (var visual = App.FocusedElement; visual is not null; visual = visual.Parent)
				if (visual is Dialog dialog)
					return dialog.Title is TextBlock title ? title.Text : dialog.Title?.ToString() ?? "";
			return null;
		});

		/// <summary>
		/// Waits until <paramref name="condition"/> holds (on the thread of the app, with what the background found on the
		/// screen), and the screen settles. Past <paramref name="patience"/> (<see cref="Patience"/>), the test fails with
		/// what it waited for and the screen.
		/// </summary>
		public void Until(Func<bool> condition, string what, TimeSpan? patience = null)
		{
			var limit = patience ?? Patience;
			var clock = Stopwatch.StartNew();
			while (!OnUi(() =>
			       {
				       Ui.OnUpdate();
				       return condition();
			       }))
			{
				if (clock.Elapsed > limit)
					throw new TimeoutException($"Waited {limit.TotalSeconds:0} s for: {what}. The screen:\n{Screen()}");
				Thread.Sleep(100);
			}
			Settle();
		}

		/// <summary>What the app wrote, read on its thread (it writes it there).</summary>
		string Text() => OnUi(() => _backend.GetOutText());

		public string Screen()
		{
			var screen = new TestScreen(Width, Height);
			screen.Apply(Text());
			return screen.ToString();
		}

		/// <summary>The line of the screen where <paramref name="text"/> is last; empty if it is in none.</summary>
		public string LineOf(string text) => Screen().Split('\n').LastOrDefault(l => l.Contains(text, StringComparison.Ordinal)) ?? "";

		/// <summary>The node of <paramref name="relative"/> in the tree, read on the thread of the app (it reloads it there); null if it has none.</summary>
		public FxTreeNode? Find(string root, string relative) => OnUi(() => Ui.Model.Find(Path.Combine(root, relative)));

		/// <summary>The node of <paramref name="relative"/>, which the tree has.</summary>
		public FxTreeNode Node(string root, string relative) => Find(root, relative) ?? throw new InvalidOperationException($"the tree has no '{relative}'");

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
			_alone.Dispose();
		}
	}

	protected Harness Open(string directory, string? start = null)
	{
		// the app, and the visuals it builds, alone: XenoAtom has one dispatcher per process (InProcess)
		var alone = InProcess.Alone();
		try
		{
			var ui = new UiApp(FxTree.Build(directory)!, start ?? directory);
			ui.Start();
			var harness = new Harness(ui, alone);
			// the first frame, with its header
			harness.Shows("fx ui");
			return harness;
		}
		catch
		{
			alone.Dispose();
			throw;
		}
	}
}
