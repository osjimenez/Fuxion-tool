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
/// Where a command writes (design §10.4): the result on stdout (human, with Spectre.Console, or a JSON document with a
/// versioned schema) and the rest on stderr.
/// </summary>
public sealed class FxConsole(TextWriter stdout, TextWriter stderr, GlobalSettings settings)
{
	// console output, not HTML: no need to escape +, ', < or non-ASCII
	static readonly FxJsonContext Json = new(new JsonSerializerOptions(FxJsonContext.Default.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

	public GlobalSettings Settings { get; } = settings;

	public IAnsiConsole Out { get; } = Create(stdout, settings.Plain);

	public IAnsiConsole Err { get; } = Create(stderr, settings.Plain);

	/// <summary>The progress of the core: with <c>--verbose</c>, each finished step on stderr; otherwise none.</summary>
	public IFxProgress? Progress { get; } = settings.Verbose ? new StepLines(stderr) : null;

	public static FxJsonContext JsonContext => Json;

	static IAnsiConsole Create(TextWriter writer, bool plain) => AnsiConsole.Create(new()
	{
		Out = new AnsiConsoleOutput(writer),
		Ansi = plain ? AnsiSupport.No : AnsiSupport.Detect,
		ColorSystem = plain ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect,
		Interactive = InteractionSupport.No
	});

	/// <summary>Detail for <c>--verbose</c>, on stderr.</summary>
	public void Verbose(string message)
	{
		if (Settings.Verbose)
			stderr.WriteLine(message);
	}

	/// <summary>
	/// Writes the diagnostics on stderr in human mode (<c>severity code: where</c>, and the fix after an arrow); returns
	/// the exit code (1 if any is an error).
	/// </summary>
	public int Report(IReadOnlyList<FxDiagnostic> diagnostics)
	{
		if (Settings.Output == OutputFormat.Human)
			foreach (var d in diagnostics)
				stderr.WriteLine($"{JsonDiagnostic.SeverityName(d.Severity)} {d.Code}: {d.Where}{Fix(d.Fix)}");
		return diagnostics.Any(d => d.Severity == FxSeverity.Error) ? 1 : 0;
	}

	static string Fix(FxFix? fix) => fix switch
	{
		null => "",
		{ Command: { } command, Hint: { } hint } => $" → {command} ({hint.TrimEnd('.')})",
		{ Command: { } command } => $" → {command}",
		{ Hint: { } hint } => $" → {hint}",
		_ => ""
	};

	/// <summary>Writes a JSON document on stdout.</summary>
	public void WriteJson<T>(T document, JsonTypeInfo<T> typeInfo) => stdout.WriteLine(JsonSerializer.Serialize(document, typeInfo));

	public static bool Ok(IEnumerable<FxDiagnostic> diagnostics) => !diagnostics.Any(d => d.Severity == FxSeverity.Error);

	/// <summary>The steps of the core, one line each when they finish (<c>--verbose</c>, until the rich output of plan O).</summary>
	sealed class StepLines(TextWriter writer) : IFxProgress
	{
		readonly object _gate = new();

		public void Report(FxEvent e)
		{
			if (e is not FxStepFinished step)
				return;
			lock (_gate)
				writer.WriteLine($"{step.Module} {step.Repository ?? "-"} {step.Step}: {(step.Succeeded ? "ok" : "failed")} ({step.Elapsed.TotalSeconds:0.0} s)");
		}
	}
}
