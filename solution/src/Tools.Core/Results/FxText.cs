using System;
using System.Globalization;
using System.Linq;
using System.Resources;

namespace Fuxion.Tools.Core.Results;

/// <summary>
/// A message for the user (plan O, decision 7): a key of <c>Resources/Strings.resx</c> (English) and its arguments,
/// translated when it is written, not when it is created. <see cref="English"/> is what the JSON and the logs carry;
/// <see cref="ToString()"/>, the language of the user (the current UI culture: <c>Strings.es.resx</c> for Spanish).
/// Codes, commands and JSON are never translated.
/// </summary>
public sealed record FxText(string Key, params object?[] Args)
{
	static readonly ResourceManager Resources = new("Fuxion.Tools.Core.Resources.Strings", typeof(FxText).Assembly);

	/// <summary>A text that is not fx's (git's stderr, a parser's message): written as it is, in any language.</summary>
	public static FxText Plain(string text) => new("", text);

	public string English => ToString(CultureInfo.InvariantCulture);

	public override string ToString() => ToString(CultureInfo.CurrentUICulture);

	public string ToString(CultureInfo culture) => Format(Resources, Key, Args, culture);

	/// <summary>
	/// The text of <paramref name="key"/> in <paramref name="culture"/> with its arguments (texts among them are
	/// translated too). The numbers are formatted invariant: fx's output does not depend on the regional settings.
	/// Without arguments the text is written as it is (it can have braces); an unknown key writes itself, so a missing
	/// text shows instead of failing.
	/// </summary>
	public static string Format(ResourceManager resources, string key, object?[] args, CultureInfo culture)
	{
		if (key.Length == 0)
			return args.Length > 0 ? args[0]?.ToString() ?? "" : "";
		var format = resources.GetString(key, culture);
		if (format is null)
			return args.Length == 0 ? key : $"{key}({string.Join(", ", args)})";
		if (args.Length == 0)
			return format;
		return string.Format(CultureInfo.InvariantCulture, format, args.Select(a => a is FxText text ? text.ToString(culture) : a).ToArray());
	}

	public bool Equals(FxText? other) => other is not null && Key == other.Key && Args.SequenceEqual(other.Args);

	public override int GetHashCode() => HashCode.Combine(Key, Args.Length);
}
