using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Fuxion.Tool.Core.Results;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;

namespace Fuxion.Tool.Cli;

/// <summary>
/// The live region of fx (plan O, decision 9): a spinner per step that is running, at the bottom. The long steps
/// (clone, fetch, pull, remote) and the failed ones stay as lines above it when they finish; the rest only pass by.
/// The core reports from its threads; the UI thread drains the events (<see cref="Update"/>): XenoAtom only takes
/// changes from it. Ctrl+C is a key while the region lives (the library reads it as input): a global command of the
/// region cancels, and the region says so until the work stops. Esc, the quit key of XenoAtom's inline regions, does
/// nothing: it would close the region and leave the work running out of sight. The markup goes to XenoAtom as a
/// string: its interpolated overloads escape what goes in the holes, markup included.
/// </summary>
sealed class LiveSteps : IFxProgress
{
	/// <summary>The steps that stay as a line when they finish: they take a while (also in fx ui).</summary>
	internal static readonly HashSet<string> LongSteps = ["clone", "fetch", "pull", "remote"];

	/// <summary>The actions of the core whose step stays as a line above the region: the result does not repeat them.</summary>
	public static readonly HashSet<string> ShownActions = ["cloned", "clone-failed", "fetched", "fetch-failed", "pulled", "failed"];

	readonly ConcurrentQueue<FxEvent> _events = new();
	readonly List<(string Module, string? Repository, string Step, Stopwatch Clock)> _active = [];
	readonly State<int> _version = new(0);
	readonly CancellationTokenSource _cancel;
	readonly Command _cancelCommand;
	bool _registered;

	public LiveSteps(CancellationTokenSource cancel)
	{
		_cancel = cancel;
		Root = new ComputedVisual(() =>
		{
			_ = _version.Value;
			if (_cancel.IsCancellationRequested)
				return Text($"[muted]{AnsiMarkup.Escape(Texts.Get("cancelling"))}[/]");
			return new VStack(_active.Select(a => (Visual)new HStack(
				new Spinner(),
				Text($"{AnsiMarkup.Escape(a.Repository ?? a.Module)} [muted]· {AnsiMarkup.Escape(a.Step)}[/]")).Spacing(1)).ToArray());
		});
		_cancelCommand = new Command
		{
			Id = "fx.cancel",
			LabelMarkup = "Cancel",
			Gesture = new KeyGesture(TerminalChar.CtrlC, TerminalModifiers.Ctrl),
			Presentation = CommandPresentation.None,
			Execute = _ =>
			{
				_cancel.Cancel();
				_version.Value++;
			}
		};
	}

	public Visual Root { get; }

	static Markup Text(string markup) => new(markup) { Wrap = false };

	public void Report(FxEvent e) => _events.Enqueue(e);

	/// <summary>
	/// On the UI thread, each turn of the region: the first time, Ctrl+C as a global command (nothing has the focus
	/// here, so a command of the root would not see it); then the events since the last turn.
	/// </summary>
	public void Update(TerminalRunningContext context)
	{
		if (!_registered)
		{
			context.App.AddGlobalCommand(_cancelCommand);
			context.App.AddGlobalCommand(new Command
			{
				Id = TerminalApp.DefaultQuitCommandId,
				LabelMarkup = "Nothing",
				Gesture = new KeyGesture(TerminalKey.Escape),
				Presentation = CommandPresentation.None,
				Execute = _ => { }
			});
			_registered = true;
		}
		Drain();
	}

	/// <summary>The events since the last call; the long and failed steps, as lines above the region.</summary>
	public void Drain()
	{
		var changed = false;
		while (_events.TryDequeue(out var e))
			switch (e)
			{
				case FxStepStarted s:
					_active.Add((s.Module, s.Repository, s.Step, Stopwatch.StartNew()));
					changed = true;
					break;
				case FxStepFinished f:
					var index = _active.FindIndex(a => a.Module == f.Module && a.Repository == f.Repository && a.Step == f.Step);
					if (index >= 0)
						_active.RemoveAt(index);
					changed = true;
					// after a Ctrl+C, the steps cut short are not failures
					if (_cancel.IsCancellationRequested)
						break;
					if (f.Succeeded && !LongSteps.Contains(f.Step))
						break;
					string line = $"{(f.Succeeded ? "[success]✓[/]" : "[error]✗[/]")} {AnsiMarkup.Escape(f.Repository ?? f.Module),-10} {AnsiMarkup.Escape(f.Step)} [muted]{f.Elapsed.TotalSeconds:0.0} s[/]";
					XenoAtom.Terminal.Terminal.WriteMarkupLine(line);
					break;
			}
		if (changed)
			_version.Value++;
	}
}
