using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Spectre.Console;

namespace Fuxion.Tools.Cli;

/// <summary>A diagnostic with a stable code (design §10.4).</summary>
public sealed record Diagnostic(string Code, string Severity, string Message)
{
	public static Diagnostic Error(string code, string message) => new(code, "error", message);
}

/// <summary>
/// Where a command writes (design §10.4): the result on stdout (human, with Spectre.Console, or a JSON document with a
/// versioned schema) and the rest on stderr.
/// </summary>
public sealed class FxConsole(TextWriter stdout, TextWriter stderr, GlobalSettings settings)
{
	public GlobalSettings Settings { get; } = settings;

	public IAnsiConsole Out { get; } = Create(stdout, settings.Plain);

	public IAnsiConsole Err { get; } = Create(stderr, settings.Plain);

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

	/// <summary>Writes the diagnostics on stderr in human mode; returns the exit code (1 if any is an error).</summary>
	public int Report(IReadOnlyList<Diagnostic> diagnostics)
	{
		var failed = false;
		foreach (var d in diagnostics)
		{
			failed |= d.Severity == "error";
			if (Settings.Output == OutputFormat.Human)
				stderr.WriteLine($"{d.Severity} {d.Code}: {d.Message}");
		}
		return failed ? 1 : 0;
	}

	/// <summary>Writes a JSON document on stdout: <c>schema</c>, <c>ok</c>, the command's fields and <c>diagnostics</c>.</summary>
	public void WriteJson(string schema, IReadOnlyList<Diagnostic> diagnostics, Action<Utf8JsonWriter>? body = null)
	{
		using var buffer = new MemoryStream();
		// console output, not HTML: no need to escape +, ', < or non-ASCII
		using (var json = new Utf8JsonWriter(buffer, new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
		{
			json.WriteStartObject();
			json.WriteString("schema", schema);
			json.WriteBoolean("ok", !diagnostics.Any(d => d.Severity == "error"));
			body?.Invoke(json);
			json.WriteStartArray("diagnostics");
			foreach (var d in diagnostics)
			{
				json.WriteStartObject();
				json.WriteString("code", d.Code);
				json.WriteString("severity", d.Severity);
				json.WriteString("message", d.Message);
				json.WriteEndObject();
			}
			json.WriteEndArray();
			json.WriteEndObject();
		}
		stdout.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
	}
}

