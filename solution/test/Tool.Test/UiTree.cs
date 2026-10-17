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
/// What the tests of <c>fx ui</c> share: the sample tree and the app in a terminal in memory. The app runs on its thread
/// over the in-memory backend, the test pushes keys, posts work to that thread and reads the screen back. The checks look
/// at what does not depend on the language (names, paths, commands, marks). While the app is open it has the dispatcher
/// of XenoAtom, one per process, to itself (<see cref="InProcess"/>): the rest of the test runs in parallel.
/// </summary>
public abstract class UiTree : SampleTree
{
	protected const int Width = 160, Height = 50;

	protected sealed class Harness : IDisposable
	{
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

		/// <summary>
		/// What the background found, on the screen, and a frame drawn. Then, until the screen stops changing (two reads
		/// alike, two seconds at most): on a slow machine (a runner of the CI) the app may still be reading the keys pushed
		/// and drawing what they bring.
		/// </summary>
		public void Settle(int ms = 150)
		{
			OnUi(() => Ui.OnUpdate());
			Thread.Sleep(ms);
			var last = _backend.GetOutText().Length;
			for (int i = 0, alike = 0; i < 20 && alike < 2; i++)
			{
				Thread.Sleep(100);
				var now = _backend.GetOutText().Length;
				alike = now == last ? alike + 1 : 0;
				last = now;
			}
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

		/// <summary>
		/// Waits until the screen shows <paramref name="text"/>, and returns it: a slow machine (a runner of the CI) may need
		/// more than a frame to draw what a key or a selection brings. The text must be one the screen before did not have.
		/// </summary>
		public string Shows(string text)
		{
			Until(() => Screen().Contains(text, StringComparison.Ordinal), $"the screen shows '{text}'");
			return Screen();
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
			_alone.Dispose();
		}
	}

	protected static FxTreeNode Node(UiApp ui, string root, string relative) => ui.Model.Find(Path.Combine(root, relative))!;

	protected Harness Open(string directory, string? start = null)
	{
		// the app, and the visuals it builds, alone: XenoAtom has one dispatcher per process (InProcess)
		var alone = InProcess.Alone();
		try
		{
			var ui = new UiApp(FxTree.Build(directory)!, start ?? directory);
			ui.Start();
			var harness = new Harness(ui, alone);
			harness.Settle(300);
			return harness;
		}
		catch
		{
			alone.Dispose();
			throw;
		}
	}
}
