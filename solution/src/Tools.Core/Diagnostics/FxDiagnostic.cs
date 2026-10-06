namespace Fuxion.Tools.Core.Diagnostics;

/// <summary>A problem found by a module, with a stable code (design §10.4) and, if it has one, where.</summary>
public sealed record FxDiagnostic(string Code, FxSeverity Severity, string Message, string? File = null, int? Line = null)
{
	public static FxDiagnostic Error(string code, string message, string? file = null, int? line = null) => new(code, FxSeverity.Error, message, file, line);

	public static FxDiagnostic Warning(string code, string message, string? file = null, int? line = null) => new(code, FxSeverity.Warning, message, file, line);

	/// <summary><c>file(line): message</c>, or just the message.</summary>
	public string Where => File is null ? Message : Line is null ? $"{File}: {Message}" : $"{File}({Line}): {Message}";
}

public enum FxSeverity
{
	Warning,
	Error
}
