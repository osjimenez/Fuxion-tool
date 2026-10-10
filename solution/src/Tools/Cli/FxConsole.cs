using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Fuxion.Tools.Core.Results;
using Spectre.Console;

namespace Fuxion.Tools.Cli;

/// <summary>
/// Where a command writes (design §10.4; plan O, decision 13): the result on stdout (for people, or a JSON document,
/// or NDJSON events and then the document) and the rest on stderr. What it can do with the terminal is in
/// <see cref="Capabilities"/>.
/// </summary>
public sealed class FxConsole
{
	public const int Cancelled = 130;

	// console output, not HTML: no need to escape +, ', < or non-ASCII
	static readonly JsonSerializerOptions Indented = new(FxJsonContext.Default.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
	static readonly FxJsonContext Json = new(Indented);
	static readonly FxJsonContext Compact = new(new JsonSerializerOptions(Indented) { WriteIndented = false });

	readonly TextWriter _stdout;
	readonly TextWriter _stderr;

	public FxConsole(TextWriter stdout, TextWriter stderr, GlobalSettings settings)
	{
		_stdout = stdout;
		_stderr = stderr;
		Settings = settings;
		// the real console only when the writer is it (in process, as in the tests, it is not a terminal)
		var console = ReferenceEquals(stdout, Console.Out);
		Capabilities = TerminalCapabilities.Detect(settings, Environment.GetEnvironmentVariable,
			inputRedirected: !console || Console.IsInputRedirected, outputRedirected: !console || Console.IsOutputRedirected);
		Out = Create(stdout, Capabilities);
		Err = Create(stderr, Capabilities);
		Progress = settings.Output switch
		{
			OutputFormat.Ndjson => new NdjsonEvents(stdout),
			OutputFormat.Human when settings.Verbose => new StepLines(stderr),
			_ => null
		};
	}

	public GlobalSettings Settings { get; }

	public TerminalCapabilities Capabilities { get; }

	public IAnsiConsole Out { get; }

	public IAnsiConsole Err { get; }

	/// <summary>The progress of the core: NDJSON events; with <c>--verbose</c>, each finished step on stderr; otherwise none.</summary>
	public IFxProgress? Progress { get; }

	static IAnsiConsole Create(TextWriter writer, TerminalCapabilities capabilities) => AnsiConsole.Create(new()
	{
		Out = new AnsiConsoleOutput(writer),
		Ansi = capabilities.CanColor ? AnsiSupport.Detect : AnsiSupport.No,
		ColorSystem = capabilities.CanColor ? ColorSystemSupport.Detect : ColorSystemSupport.NoColors,
		Interactive = InteractionSupport.No
	});

	/// <summary>Detail for <c>--verbose</c>, on stderr.</summary>
	public void Verbose(string message)
	{
		if (Settings.Verbose)
			_stderr.WriteLine(message);
	}

	/// <summary>
	/// Writes the diagnostics on stderr for people (<c>severity code: where</c>, and the fix after an arrow; the
	/// <c>info</c> ones only with <c>--verbose</c>); returns the exit code (1 if any is an error).
	/// </summary>
	public int Report(IReadOnlyList<FxDiagnostic> diagnostics)
	{
		if (!Settings.IsMachine)
			foreach (var d in diagnostics.Where(d => d.Severity != FxSeverity.Info || Settings.Verbose))
				_stderr.WriteLine($"{Texts.Get($"severity.{JsonDiagnostic.SeverityName(d.Severity)}")} {d.Code}: {d.Where}{Fix(d.Fix)}");
		return diagnostics.Any(d => d.Severity == FxSeverity.Error) ? 1 : 0;
	}

	static string Fix(FxFix? fix) => fix switch
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

	/// <summary>Runs a command: a cancellation (Ctrl+C) ends it with <see cref="Cancelled"/>.</summary>
	public int Run(Func<int> command)
	{
		try
		{
			return command();
		}
		catch (OperationCanceledException)
		{
			if (!Settings.IsMachine)
				_stderr.WriteLine(Texts.Get("cancelled"));
			return Cancelled;
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
