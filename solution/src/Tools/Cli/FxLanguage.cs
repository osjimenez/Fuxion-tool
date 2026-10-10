using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace Fuxion.Tools.Cli;

/// <summary>
/// The language of fx's messages (plan O, decision 7), English or Spanish: <c>--lang</c>, then <c>FX_LANG</c>, then the
/// system's, then English. Only the messages to the user: codes, commands and JSON are always English. The formats
/// (numbers, dates) stay invariant, so that nothing fx generates depends on the regional settings.
/// </summary>
public static partial class FxLanguage
{
	public const string Variable = "FX_LANG";

	public static readonly IReadOnlyList<string> Supported = ["en", "es"];

	/// <summary>The language for these arguments, read before the commands are built (their help is in it).</summary>
	public static string Resolve(IReadOnlyList<string> args, Func<string, string?>? environment = null, bool? windows = null)
	{
		environment ??= Environment.GetEnvironmentVariable;
		return FromArgs(args)
		       ?? Normalize(environment(Variable))
		       ?? ((windows ?? OperatingSystem.IsWindows()) ? FromWindows() : FromPosix(environment));
	}

	/// <summary>The UI culture of the process: English is the invariant culture (the neutral texts).</summary>
	public static void Apply(string language)
	{
		var culture = language == "en" ? CultureInfo.InvariantCulture : new CultureInfo(language);
		CultureInfo.CurrentUICulture = culture;
		CultureInfo.DefaultThreadCurrentUICulture = culture;
	}

	/// <summary>The value of <c>--lang xx</c> or <c>--lang=xx</c>, if it is a supported language.</summary>
	static string? FromArgs(IReadOnlyList<string> args)
	{
		for (var i = 0; i < args.Count; i++)
		{
			if (args[i] == "--lang" && i + 1 < args.Count)
				return Normalize(args[i + 1]);
			if (args[i].StartsWith("--lang=", StringComparison.Ordinal))
				return Normalize(args[i]["--lang=".Length..]);
		}
		return null;
	}

	/// <summary><c>es</c>, <c>es-ES</c>, <c>es_ES.UTF-8</c>… → <c>es</c>; null if it is not a supported language.</summary>
	static string? Normalize(string? value)
	{
		if (string.IsNullOrWhiteSpace(value) || value.Length < 2)
			return null;
		var language = value[..2].ToLowerInvariant();
		return Supported.Contains(language) && (value.Length == 2 || !char.IsAsciiLetter(value[2])) ? language : null;
	}

	/// <summary>The user's UI language on Windows: Spanish if its primary language is Spanish.</summary>
	static string FromWindows() => (GetUserDefaultUILanguage() & 0x3FF) == 0x0A ? "es" : "en";

	[LibraryImport("kernel32.dll")]
	private static partial ushort GetUserDefaultUILanguage();

	/// <summary>
	/// Linux and macOS, as gettext: the locale is the first of <c>LC_ALL</c>, <c>LC_MESSAGES</c> and <c>LANG</c>; with
	/// the C locale, English; otherwise <c>LANGUAGE</c> (a list separated by ':') goes first, if there is one.
	/// </summary>
	public static string FromPosix(Func<string, string?> environment)
	{
		var locale = new[] { "LC_ALL", "LC_MESSAGES", "LANG" }.Select(environment).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
		if (locale is null || locale is "C" or "POSIX" || locale.StartsWith("C.", StringComparison.Ordinal))
			return "en";
		var candidates = environment("LANGUAGE") is { Length: > 0 } list ? list.Split(':') : [locale];
		return candidates.Select(Normalize).FirstOrDefault(l => l is not null) ?? Normalize(locale) ?? "en";
	}
}
