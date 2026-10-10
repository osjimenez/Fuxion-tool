using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;

namespace Fuxion.Tools.Cli;

/// <summary>
/// The questions fx asks for what a command misses, as gh does (plan O, O3.3: the tool adapts to where it runs). Only
/// with a person in front (<see cref="TerminalCapabilities.CanPrompt"/>); elsewhere the command says what is missing
/// and ends with <see cref="FxConsole.UsageError"/>. Inline lists of XenoAtom, not its prompts: its list prompt opens
/// a popup, which only works full screen, and in its multiple one the list keeps Enter for itself (plan O, O1). Esc
/// (the quit key of XenoAtom's inline regions, replaced) and Ctrl+C cancel (<see cref="OperationCanceledException"/>):
/// Ctrl+C as a key, or as the signal that cancels the token of the command. The answer stays as a line on stderr.
/// </summary>
public static class Ask
{
	/// <summary>One of <paramref name="items"/>: ↑↓ and Enter.</summary>
	/// <param name="backend">A terminal of the tests; the real one if <see langword="null"/>.</param>
	public static T One<T>(FxConsole console, string question, IReadOnlyList<T> items, Func<T, string> label, CancellationToken cancellationToken,
		ITerminalBackend? backend = null)
	{
		var list = new OptionList<string>(items.Select(label)) { SelectedIndex = 0 };
		int? chosen = null;
		list.ItemActivated((_, e) => chosen = e.Index);
		Run(question, Texts.Get("ask.one.keys"), list, null, () => chosen is not null, cancellationToken, backend);
		var answer = items[chosen!.Value];
		Answered(console, question, label(answer));
		return answer;
	}

	/// <summary>Some of <paramref name="items"/>: Space marks, Enter confirms (the highlighted one, if none is marked).</summary>
	/// <param name="backend">A terminal of the tests; the real one if <see langword="null"/>.</param>
	public static IReadOnlyList<T> Many<T>(FxConsole console, string question, IReadOnlyList<T> items, Func<T, string> label,
		CancellationToken cancellationToken, ITerminalBackend? backend = null)
	{
		var list = new SelectionList<string>(items.Select(label)) { SelectedIndex = 0 };
		var done = false;
		// the list takes Enter (it marks): a command of the root, which sees the key before the list
		var confirm = new Command
		{
			Id = "fx.ask.confirm",
			LabelMarkup = "Confirm",
			Gesture = new KeyGesture(TerminalKey.Enter),
			Presentation = CommandPresentation.None,
			Execute = _ => done = true
		};
		Run(question, Texts.Get("ask.many.keys"), list, confirm, () => done, cancellationToken, backend);
		var answer = Enumerable.Range(0, items.Count).Where(i => i < list.Checked.Count && list.Checked[i]).Select(i => items[i]).ToList();
		if (answer.Count == 0)
			answer.Add(items[Math.Clamp(list.SelectedIndex, 0, items.Count - 1)]);
		Answered(console, question, string.Join(", ", answer.Select(label)));
		return answer;
	}

	/// <summary>A text, not empty: Enter confirms.</summary>
	/// <param name="backend">A terminal of the tests; the real one if <see langword="null"/>.</param>
	public static string Text(FxConsole console, string question, CancellationToken cancellationToken, ITerminalBackend? backend = null)
	{
		var box = new TextBox();
		// its copy and cut keys would keep Ctrl+C from cancelling
		box.RemoveCommand("TextEditor.Copy");
		box.RemoveCommand("TextEditor.Cut");
		var done = false;
		var confirm = new Command
		{
			Id = "fx.ask.confirm",
			LabelMarkup = "Confirm",
			Gesture = new KeyGesture(TerminalKey.Enter),
			Presentation = CommandPresentation.None,
			Execute = _ => done = !string.IsNullOrWhiteSpace(box.Text)
		};
		Run(question, Texts.Get("ask.text.keys"), box, confirm, () => done, cancellationToken, backend);
		var answer = box.Text!.Trim();
		Answered(console, question, answer);
		return answer;
	}

	/// <summary>The command that does the same without asking, to learn it (as lazygit shows its git commands).</summary>
	public static void Equivalent(FxConsole console, string command) => console.ErrorLine($"[gray]▸ {FxConsole.Escape(command)}[/]");

	static void Run(string question, string keys, Visual input, Command? confirm, Func<bool> done, CancellationToken cancellationToken, ITerminalBackend? backend)
	{
		var cancelled = false;
		Command Cancel(string id, KeyGesture gesture) => new()
		{
			Id = id,
			LabelMarkup = "Cancel",
			Gesture = gesture,
			Presentation = CommandPresentation.None,
			Execute = _ => cancelled = true
		};
		var root = new VStack(Markup($"[accent]?[/] [bold]{AnsiMarkup.Escape(question)}[/] [muted]{AnsiMarkup.Escape(keys)}[/]"), input.MaxHeight(12));
		if (confirm is not null)
			root.AddCommand(confirm);
		var started = false;
		using var session = XenoAtom.Terminal.Terminal.Open(backend, backend is null ? null : new TerminalOptions { ImplicitStartInput = true }, force: backend is not null);
		session.Instance.Live(root, context =>
		{
			if (!started)
			{
				// inline, nothing takes the focus by itself; Esc is the quit key of the region, which goes before
				// everything: replaced; Ctrl+C, global, so that the list does not keep it
				context.App.Focus(input);
				context.App.AddGlobalCommand(Cancel(TerminalApp.DefaultQuitCommandId, new KeyGesture(TerminalKey.Escape)));
				context.App.AddGlobalCommand(Cancel("fx.ask.ctrl-c", new KeyGesture(TerminalChar.CtrlC, TerminalModifiers.Ctrl)));
				started = true;
			}
			return cancelled || done() || cancellationToken.IsCancellationRequested ? TerminalLoopResult.Stop : TerminalLoopResult.Continue;
		}, new TerminalLiveOptions { LoopMode = TerminalLoopMode.Polling, UpdateWaitDuration = TimeSpan.FromMilliseconds(50) });
		// a region that ends with no answer was cancelled, whatever ended it
		cancellationToken.ThrowIfCancellationRequested();
		if (cancelled || !done())
			throw new OperationCanceledException();
	}

	static void Answered(FxConsole console, string question, string answer)
		=> console.ErrorLine($"[cyan]?[/] {FxConsole.Escape(question)} [cyan]{FxConsole.Escape(answer)}[/]");

	// a string, not an interpolated handler: XenoAtom's escapes what goes in the holes, markup included
	static Markup Markup(string markup) => new(markup) { Wrap = false };
}
