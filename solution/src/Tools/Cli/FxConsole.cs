using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Fuxion.Tools.Core.Results;
using XenoAtom.Ansi;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;

namespace Fuxion.Tools.Cli;

/// <summary>
/// Where a command writes (design §10.4; plan O, decisions 4, 9 and 13): the result on stdout (for people, or a JSON
/// document, or NDJSON events and then the document) and the rest on stderr. For people, with XenoAtom.Terminal.UI:
/// markup lines and visuals (tables) with colours where they can be shown, and the live region where it can animate
/// (<see cref="WithProgress{T}"/>). What fx can do with the terminal is in <see cref="Capabilities"/>.
/// </summary>
public sealed partial class FxConsole
{
	/// <summary>The exit code of a usage error: an unknown command, a missing argument with no one to ask for it.</summary>
	public const int UsageError = 2;

	public const int Cancelled = 130;

	// console output, not HTML: no need to escape +, ', < or non-ASCII
	static readonly JsonSerializerOptions Indented = new(FxJsonContext.Default.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
	static readonly FxJsonContext Json = new(Indented);
	static readonly FxJsonContext Compact = new(new JsonSerializerOptions(Indented) { WriteIndented = false });

	readonly TextWriter _stdout;
	readonly TextWriter _stderr;
	readonly bool _isConsole;

	public FxConsole(TextWriter stdout, TextWriter stderr, GlobalSettings settings)
	{
		_stdout = stdout;
		_stderr = stderr;
		Settings = settings;
		// the real console only when the writer is it (in process, as in the tests, it is not a terminal)
		_isConsole = ReferenceEquals(stdout, Console.Out);
		Capabilities = TerminalCapabilities.Detect(settings, Environment.GetEnvironmentVariable,
			inputRedirected: !_isConsole || Console.IsInputRedirected, outputRedirected: !_isConsole || Console.IsOutputRedirected);
		Width = _isConsole && !Console.IsOutputRedirected ? Math.Max(40, SafeWindowWidth())
			: int.TryParse(Environment.GetEnvironmentVariable("COLUMNS"), out var columns) && columns >= 40 ? columns : 120;
		Progress = settings.Output switch
		{
			OutputFormat.Ndjson => new NdjsonEvents(stdout),
			OutputFormat.Human when settings.Verbose && !Capabilities.CanAnimate => new StepLines(stderr),
			_ => null
		};
	}

	static int SafeWindowWidth()
	{
		try
		{
			return Console.WindowWidth;
		}
		catch (IOException)
		{
			return 120;
		}
	}

	public GlobalSettings Settings { get; }

	public TerminalCapabilities Capabilities { get; }

	/// <summary>The width the visuals are laid out at: the terminal's, or <c>COLUMNS</c>, or 120.</summary>
	public int Width { get; }

	/// <summary>The progress of the core without a live region: NDJSON events, or each finished step on stderr (<c>--verbose</c>).</summary>
	public IFxProgress? Progress { get; }

	/// <summary>Whether <see cref="WithProgress{T}"/> showed the live region, so its long steps are already on screen.</summary>
	public bool ShowedLiveRegion { get; private set; }

	/// <summary>
	/// The actions to write as the result: after the live region, not those whose step it left on screen (plan O,
	/// decision 9: the progress stays above, the result does not repeat it).
	/// </summary>
	public IEnumerable<RepositoryAction> NotShown(IEnumerable<RepositoryAction> actions)
		=> ShowedLiveRegion ? actions.Where(a => !LiveSteps.ShownActions.Contains(a.Action)) : actions;

	/// <summary>A line of XenoAtom markup on stdout (<c>[green]✓[/]</c>), with colours or plain. Escape what is not fx's with <see cref="Escape"/>.</summary>
	public void Line(string markup = "") => _stdout.WriteLine(Render(markup));

	/// <summary>A line of markup on stderr.</summary>
	public void ErrorLine(string markup) => _stderr.WriteLine(Render(markup));

	public static string Escape(string? text) => text is null ? "" : AnsiMarkup.Escape(text);

	string Render(string markup)
		=> markup.Length == 0 ? "" : AnsiMarkup.Render(markup, AnsiCapabilities.Default with { AnsiEnabled = Capabilities.CanColor }, markup.Length + 32);

	/// <summary>
	/// A visual (a table, a grid…) on stdout, laid out at <see cref="Width"/> on an in-memory terminal: it works the same
	/// with or without a terminal (with the output redirected the real one has no width; plan O, O1).
	/// </summary>
	public void Write(Visual visual)
	{
		var backend = new InMemoryTerminalBackend(new TerminalSize(Width, 1000));
		using (var session = XenoAtom.Terminal.Terminal.Open(backend, new TerminalOptions { ImplicitStartInput = false }, force: true))
			session.Instance.Write(visual);
		var text = backend.GetOutText();
		if (!Capabilities.CanColor)
			text = Escapes().Replace(text, "");
		// the private modes of the renderer (synchronized output) are not output
		text = PrivateModes().Replace(text, "");
		foreach (var line in text.Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).Reverse().SkipWhile(l => l.Length == 0).Reverse())
			_stdout.WriteLine(line);
	}

	[GeneratedRegex(@"\x1b\[[0-9;?]*[A-Za-z]")]
	private static partial Regex Escapes();

	[GeneratedRegex(@"\x1b\[\?[0-9;]*[hl]")]
	private static partial Regex PrivateModes();

	/// <summary>Detail for <c>--verbose</c>, on stderr.</summary>
	public void Verbose(string message)
	{
		if (Settings.Verbose)
			_stderr.WriteLine(message);
	}

	/// <summary>
	/// Runs the work of the core: in a terminal that can animate, on a background thread while a live region shows each
	/// step as it runs, its long steps and its failures staying above it (decision 9); elsewhere, here, with
	/// <see cref="Progress"/>. Ctrl+C cancels either way.
	/// </summary>
	public T WithProgress<T>(Func<IFxProgress?, CancellationToken, T> work, CancellationToken cancellationToken)
	{
		if (!Capabilities.CanAnimate)
			return work(Progress, cancellationToken);
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var live = new LiveSteps(cancel);
		ShowedLiveRegion = true;
		var task = Task.Run(() => work(live, cancel.Token), cancel.Token);
		using (XenoAtom.Terminal.Terminal.Open())
			XenoAtom.Terminal.Terminal.Live(live.Root, context =>
			{
				live.Update(context);
				return task.IsCompleted ? TerminalLoopResult.Stop : TerminalLoopResult.Continue;
			}, new TerminalLiveOptions { LoopMode = TerminalLoopMode.Polling, UpdateWaitDuration = TimeSpan.FromMilliseconds(50) });
		live.Drain();
		return task.GetAwaiter().GetResult();
	}

	/// <summary>
	/// Writes the diagnostics on stderr for people (<c>severity code: where</c>, and the fix after an arrow; the
	/// <c>info</c> ones only with <c>--verbose</c>); returns the exit code (1 if any is an error).
	/// </summary>
	public int Report(IReadOnlyList<FxDiagnostic> diagnostics, bool write = true)
	{
		if (write && !Settings.IsMachine)
			foreach (var d in diagnostics.Where(d => d.Severity != FxSeverity.Info || Settings.Verbose))
				ErrorLine($"{SeverityMarkup(d.Severity)} {Escape(d.Code)}: {Escape(d.Where)}{Escape(Fix(d.Fix))}");
		return ExitCode(diagnostics);
	}

	/// <summary>
	/// A command misses what only a person could be asked for, and there is no one to ask (plan O, O3.3): what is
	/// missing and how to give it, on stderr; <see cref="UsageError"/>.
	/// </summary>
	public int Missing(string what, string usage)
	{
		ErrorLine($"[red]{Escape(Texts.Get("usage.missing", what))}[/] {Escape(usage)}");
		return UsageError;
	}

	public static int ExitCode(IEnumerable<FxDiagnostic> diagnostics) => diagnostics.Any(d => d.Severity == FxSeverity.Error) ? 1 : 0;

	static string SeverityMarkup(FxSeverity severity)
	{
		var name = Escape(Texts.Get($"severity.{JsonDiagnostic.SeverityName(severity)}"));
		return severity switch
		{
			FxSeverity.Error => $"[red]{name}[/]",
			FxSeverity.Warning => $"[yellow]{name}[/]",
			_ => $"[gray]{name}[/]"
		};
	}

	public static string Fix(FxFix? fix) => fix switch
	{
		null => "",
		{ Command: { } command, Hint: { } hint } => $" → {command} ({hint.ToString().TrimEnd('.')})",
		{ Command: { } command } => $" → {command}",
		{ Hint: { } hint } => $" → {hint}",
		_ => ""
	};

	/// <summary>Writes the document of the command on stdout: indented (JSON), or on one line (the last of NDJSON).</summary>
	public void WriteJson<T>(T document, Func<FxJsonContext, JsonTypeInfo<T>> typeInfo)
	{
		lock (_stdout)
			_stdout.WriteLine(JsonSerializer.Serialize(document, typeInfo(Settings.Output == OutputFormat.Ndjson ? Compact : Json)));
	}

	/// <summary>
	/// Runs a command with a token that Ctrl+C cancels; the cancellation ends it with <see cref="Cancelled"/>. Ctrl+C
	/// comes as a signal (outside the live region, where it is a key): the first one cancels, and the command stops
	/// as soon as it can (git is killed); a second one ends fx at once, as usual.
	/// </summary>
	public int Run(Func<CancellationToken, int> command, CancellationToken cancellationToken = default)
	{
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		ConsoleCancelEventHandler? onCtrlC = null;
		if (_isConsole)
		{
			onCtrlC = (_, e) =>
			{
				e.Cancel = !cancel.IsCancellationRequested;
				cancel.Cancel();
			};
			Console.CancelKeyPress += onCtrlC;
		}
		try
		{
			return command(cancel.Token);
		}
		catch (OperationCanceledException)
		{
			if (!Settings.IsMachine)
				ErrorLine($"[gray]{Escape(Texts.Get("cancelled"))}[/]");
			return Cancelled;
		}
		finally
		{
			if (onCtrlC is not null)
				Console.CancelKeyPress -= onCtrlC;
		}
	}

	public static bool Ok(IEnumerable<FxDiagnostic> diagnostics) => !diagnostics.Any(d => d.Severity == FxSeverity.Error);

	/// <summary>The steps of the core, one line each when they finish (<c>--verbose</c>, plain output).</summary>
	sealed class StepLines(TextWriter writer) : IFxProgress
	{
		public void Report(FxEvent e)
		{
			if (e is not FxStepFinished step)
				return;
			lock (writer)
				writer.WriteLine($"{step.Module} {step.Repository ?? "-"} {step.Step}: {Texts.Get(step.Succeeded ? "step.ok" : "step.failed")} ({step.Elapsed.TotalSeconds:0.0} s)");
		}
	}

	/// <summary>
	/// <c>--output ndjson</c>: a first line with the schema of the events, then one line per event, from any thread;
	/// the command writes the last line, its document.
	/// </summary>
	sealed class NdjsonEvents : IFxProgress
	{
		public const string Schema = "fx-events/1";
		readonly TextWriter _writer;

		public NdjsonEvents(TextWriter writer)
		{
			_writer = writer;
			Write(new JsonStartEvent("start", Schema), Compact.JsonStartEvent);
		}

		public void Report(FxEvent e)
		{
			switch (e)
			{
				case FxStepStarted s:
					Write(new JsonStepEvent("step-started", s.Module, s.Repository, s.Step, null, null), Compact.JsonStepEvent);
					break;
				case FxStepFinished f:
					Write(new JsonStepEvent("step-finished", f.Module, f.Repository, f.Step, f.Succeeded, (long)f.Elapsed.TotalMilliseconds), Compact.JsonStepEvent);
					break;
				case FxDiagnosticFound d:
					Write(new JsonDiagnosticEvent("diagnostic", JsonDiagnostic.From(d.Diagnostic)), Compact.JsonDiagnosticEvent);
					break;
			}
		}

		void Write<T>(T value, JsonTypeInfo<T> typeInfo)
		{
			lock (_writer)
				_writer.WriteLine(JsonSerializer.Serialize(value, typeInfo));
		}
	}
}
