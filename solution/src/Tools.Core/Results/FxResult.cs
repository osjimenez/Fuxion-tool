using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Fuxion.Tools.Core.Results;

/// <summary>How serious a diagnostic is: only errors make a command fail.</summary>
public enum FxSeverity
{
	/// <summary>Worth knowing, nothing to fix (the lesson of brew doctor: not every finding is a warning).</summary>
	Info,
	Warning,
	Error
}

/// <summary>How to fix a diagnostic: a command to run, or what to do by hand.</summary>
public sealed record FxFix(string? Command = null, FxText? Hint = null)
{
	public static FxFix Run(string command) => new(command);

	public static FxFix Do(FxText hint) => new(Hint: hint);

	public static FxFix Do(string key) => new(Hint: new FxText(key));
}

/// <summary>
/// A problem or a finding of a module (plan O, decision 13), with a stable code. <see cref="Module"/> is the first part
/// of the code; <see cref="Repository"/>, the repository it is about (none: the workspace, or the repository the module
/// ran in); <see cref="File"/> and <see cref="Line"/>, where.
/// </summary>
public sealed record FxDiagnostic(string Code, FxSeverity Severity, FxText Message, string? File = null, int? Line = null)
{
	public string? Repository { get; init; }

	public FxFix? Fix { get; init; }

	/// <summary>The module, from the code (<c>dotnet.unused-package</c> → <c>dotnet</c>).</summary>
	public string Module => Code.IndexOf('.') is var dot and > 0 ? Code[..dot] : Code;

	public static FxDiagnostic Error(string code, FxText message, string? file = null, int? line = null, string? repository = null, FxFix? fix = null)
		=> new(code, FxSeverity.Error, message, file, line) { Repository = repository, Fix = fix };

	public static FxDiagnostic Warning(string code, FxText message, string? file = null, int? line = null, string? repository = null, FxFix? fix = null)
		=> new(code, FxSeverity.Warning, message, file, line) { Repository = repository, Fix = fix };

	public static FxDiagnostic Info(string code, FxText message, string? file = null, int? line = null, string? repository = null, FxFix? fix = null)
		=> new(code, FxSeverity.Info, message, file, line) { Repository = repository, Fix = fix };

	/// <summary><c>file(line): message</c>, or just the message, in the language of the user.</summary>
	public string Where => WhereIn(CultureInfo.CurrentUICulture);

	/// <summary><c>file(line): message</c>, or just the message, in <paramref name="culture"/>.</summary>
	public string WhereIn(CultureInfo culture)
	{
		var message = Message.ToString(culture);
		return File is null ? message : Line is null ? $"{File}: {message}" : $"{File}({Line}): {message}";
	}
}

/// <summary>
/// The diagnostics a module collects: each one also goes to the progress channel as soon as it is added
/// (<see cref="FxDiagnosticFound"/>), with the repository the module runs in when it says none. Thread-safe, for the
/// work done in parallel.
/// </summary>
public sealed class FxDiagnostics(IFxProgress? progress = null, string? repository = null) : IReadOnlyList<FxDiagnostic>
{
	readonly List<FxDiagnostic> _items = [];
	readonly object _gate = new();

	public FxDiagnostics() : this(null, null) { }

	public int Count
	{
		get
		{
			lock (_gate)
				return _items.Count;
		}
	}

	public FxDiagnostic this[int index]
	{
		get
		{
			lock (_gate)
				return _items[index];
		}
	}

	public void Add(FxDiagnostic diagnostic)
	{
		// the repository the module runs in, unless the diagnostic says another
		if (repository is not null && diagnostic.Repository is null)
			diagnostic = diagnostic with { Repository = repository };
		lock (_gate)
			_items.Add(diagnostic);
		progress?.Report(new FxDiagnosticFound(diagnostic));
	}

	public void AddRange(IEnumerable<FxDiagnostic> diagnostics)
	{
		foreach (var diagnostic in diagnostics)
			Add(diagnostic);
	}

	public IEnumerator<FxDiagnostic> GetEnumerator()
	{
		lock (_gate)
			return _items.ToList().GetEnumerator();
	}

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public enum SyncFileStatus
{
	Unchanged,
	Written,

	/// <summary>Would be written (dry run, or doctor).</summary>
	WouldWrite,

	/// <summary>Not written: the file is not fx's (no GENERATED header).</summary>
	Refused
}

/// <summary>A file fx owns and what happened to it.</summary>
public sealed record SyncFile(string Path, SyncFileStatus Status);

/// <summary>What happened to a repository: cloned, fetched, missing, pulled…</summary>
public sealed record RepositoryAction(string Repository, string Action, FxText? Detail = null);

/// <summary>
/// What a module did, or would do with <c>--dry-run</c> (plan O, decision 13): the files it owns, what it did to the
/// repositories and what it found. <see cref="Repository"/> is the repository it ran in (none: the workspace).
/// </summary>
public sealed record FxModuleResult(
	string Module,
	string Root,
	string? Repository,
	IReadOnlyList<SyncFile> Files,
	IReadOnlyList<RepositoryAction> Actions,
	IReadOnlyList<FxDiagnostic> Diagnostics)
{
	public bool Failed => Diagnostics.Any(d => d.Severity == FxSeverity.Error);

	/// <summary>A module that could not even start: just its diagnostics.</summary>
	public static FxModuleResult Stopped(string module, string root, string? repository, params IReadOnlyList<FxDiagnostic> diagnostics)
		=> new(module, root, repository, [], [], diagnostics);
}
