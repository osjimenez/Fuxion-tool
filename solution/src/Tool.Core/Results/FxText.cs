using System;
using System.Globalization;
using System.Linq;
using System.Resources;

namespace Fuxion.Tool.Core.Results;

/// <summary>
/// A message for the user: a key of <c>Resources/Strings.resx</c> (English) and its arguments,
/// translated when it is written, not when it is created. <see cref="English"/> is what the JSON and the logs carry;
/// <see cref="ToString()"/>, the language of the user (the current UI culture: <c>Strings.es.resx</c> for Spanish).
/// Codes, commands and JSON are never translated. Its arguments are a closed set (<see cref="FxArg"/>: a text, a number
/// or another text of fx), so that a message crosses a process as data (<see cref="FxTextJsonConverter"/>) and is still
/// translated where it is written.
/// </summary>
public sealed record FxText(string Key, params FxArg[] Args)
{
	static readonly ResourceManager Resources = new("Fuxion.Tool.Core.Resources.Strings", typeof(FxText).Assembly);

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
	public static string Format(ResourceManager resources, string key, FxArg[] args, CultureInfo culture)
	{
		if (key.Length == 0)
			return args.Length > 0 ? args[0].Format(culture)?.ToString() ?? "" : "";
		var format = resources.GetString(key, culture);
		if (format is null)
			return args.Length == 0 ? key : $"{key}({string.Join(", ", args.Select(a => a.Format(culture)))})";
		if (args.Length == 0)
			return format;
		return string.Format(CultureInfo.InvariantCulture, format, args.Select(a => a.Format(culture)).ToArray());
	}

	public bool Equals(FxText? other) => other is not null && Key == other.Key && Args.SequenceEqual(other.Args);

	public override int GetHashCode() => HashCode.Combine(Key, Args.Length);
}

/// <summary>
/// An argument of an <see cref="FxText"/>: a text (null: none), a number (an integer or a real) or another text of fx,
/// translated with it. Anything else (a version, a duration) is given its form where the message is made.
/// </summary>
public readonly struct FxArg : IEquatable<FxArg>
{
	readonly object? _value;

	FxArg(object? value) => _value = value;

	/// <summary>Its text, if it is one (null too).</summary>
	public string? Text => _value as string;

	/// <summary>Its number, if it is one: a <see cref="long"/> or a <see cref="double"/>.</summary>
	public object? Number => _value is long or double ? _value : null;

	/// <summary>Its text of fx, if it is one.</summary>
	public FxText? Nested => _value as FxText;

	/// <summary>Whether it is a number (an integer or a real).</summary>
	public bool IsNumber => _value is long or double;

	public static implicit operator FxArg(string? text) => new(text);

	public static implicit operator FxArg(long number) => new(number);

	public static implicit operator FxArg(int number) => new((long)number);

	/// <summary>A number that may be missing: none, as a missing text.</summary>
	public static implicit operator FxArg(long? number) => new(number);

	/// <inheritdoc cref="op_Implicit(long?)"/>
	public static implicit operator FxArg(int? number) => new(number is { } n ? (long)n : null);

	public static implicit operator FxArg(double number) => new(number);

	public static implicit operator FxArg(FxText? text) => new(text);

	/// <summary>What <see cref="string.Format(IFormatProvider, string, object[])"/> takes: a text, a number, or the text of fx in <paramref name="culture"/>.</summary>
	public object? Format(CultureInfo culture) => _value is FxText text ? text.ToString(culture) : _value;

	public bool Equals(FxArg other) => Equals(_value, other._value);

	public override bool Equals(object? obj) => obj is FxArg other && Equals(other);

	public override int GetHashCode() => _value?.GetHashCode() ?? 0;

	public override string ToString() => Format(CultureInfo.InvariantCulture)?.ToString() ?? "";

	public static bool operator ==(FxArg left, FxArg right) => left.Equals(right);

	public static bool operator !=(FxArg left, FxArg right) => !left.Equals(right);
}
