using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Fuxion.Tool.Core.Results;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace Fuxion.Tool.Cli.Ui;

/// <summary>What was run from fx ui (the log, F8): when, the action, what it did, whether it was cancelled, and its long and failed steps.</summary>
public sealed record UiLogEntry(DateTime When, UiAction Action, UiOutcome? Outcome, bool Cancelled, IReadOnlyList<string> Steps);

/// <summary>
/// An action running in fx ui (plan O, O4.2): its progress as the live region of the CLI shows it (decision 9), a
/// spinner per step running and the long and failed steps as lines when they finish, at the bottom of the screen. The
/// core reports from its threads; the thread of the screen drains the events (<see cref="Drain"/>).
/// </summary>
sealed class UiRunning(UiAction action) : IFxProgress
{
	readonly ConcurrentQueue<FxEvent> _events = new();
	readonly List<(string Who, string Step)> _active = [];

	public UiAction Action => action;

	public CancellationTokenSource Cancel { get; } = new();

	/// <summary>The long steps and the failed ones, once finished.</summary>
	public List<string> Steps { get; } = [];

	public void Report(FxEvent e) => _events.Enqueue(e);

	static string Esc(string? text) => FxConsole.Escape(text);

	/// <summary>On the thread of the screen: the events since the last call; whether anything changed.</summary>
	public bool Drain()
	{
		var changed = false;
		while (_events.TryDequeue(out var e))
			switch (e)
			{
				case FxStepStarted s:
					_active.Add((s.Repository ?? s.Module, s.Step));
					changed = true;
					break;
				case FxStepFinished f:
					_active.Remove((f.Repository ?? f.Module, f.Step));
					changed = true;
					// after cancelling, the steps cut short are not failures
					if (Cancel.IsCancellationRequested || (f.Succeeded && !LiveSteps.LongSteps.Contains(f.Step)))
						break;
					Steps.Add($"{(f.Succeeded ? "[success]✓[/]" : "[error]✗[/]")} {Esc(f.Repository ?? f.Module)} [muted]· {Esc(f.Step)} · {f.Elapsed.TotalSeconds:0.0} s[/]");
					break;
			}
		return changed;
	}

	/// <summary>Its place at the bottom: the command, the last steps finished and a spinner per step running.</summary>
	public Visual View()
	{
		var lines = Steps.TakeLast(4).Select(l => (Visual)new Markup(l) { Wrap = false }).ToList();
		if (Cancel.IsCancellationRequested)
			lines.Add(new Markup($"[muted]{Esc(Texts.Get("cancelling"))}[/]") { Wrap = false });
		else if (_active.Count == 0)
			lines.Add(new Spinner());
		else
			lines.AddRange(_active.Select(a => (Visual)new HStack(new Spinner(), new Markup($"{Esc(a.Who)} [muted]· {Esc(a.Step)}[/]") { Wrap = false }).Spacing(1)));
		return new Group().TopLeftText($"{Texts.Get("ui.running")}: {action.Command}").HorizontalAlignment(Align.Stretch).Content(new VStack(lines.ToArray()).Spacing(0));
	}
}
