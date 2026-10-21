using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Fuxion.Tool.Core.Docs;

/// <summary>A value without a canonical form: a number out of range, a string that is not Unicode, a repeated property.</summary>
public sealed class JcsException(string message) : Exception(message);

/// <summary>
/// The JSON Canonicalization Scheme (RFC 8785), the form the integrity of a sidecar is computed on (the standard, its
/// metadata model): no whitespace, the properties sorted by their UTF-16 code units, strings and numbers as ECMAScript
/// writes them, in UTF-8.
/// </summary>
public static class Jcs
{
	/// <summary>
	/// The canonical form of <paramref name="value"/>, its UTF-8 bytes; without the property at the JSON pointer
	/// <paramref name="omit"/>, if there is one (<c>/integrity/metadata</c>).
	/// </summary>
	public static byte[] Canonicalize(JsonElement value, string? omit = null) => Encoding.UTF8.GetBytes(Text(value, omit));

	/// <summary>The canonical form of <paramref name="value"/>, as text.</summary>
	public static string Text(JsonElement value, string? omit = null)
	{
		var text = new StringBuilder();
		Write(text, value, "", omit);
		return text.ToString();
	}

	static void Write(StringBuilder text, JsonElement value, string pointer, string? omit)
	{
		switch (value.ValueKind)
		{
			case JsonValueKind.Object:
				List<(string Name, JsonElement Value)> properties;
				try
				{
					properties = value.EnumerateObject().Select(p => (p.Name, p.Value)).ToList();
				}
				catch (InvalidOperationException)
				{
					throw new JcsException($"A property name at '{pointer}' is not Unicode");
				}
				if (properties.GroupBy(p => p.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } repeated)
					throw new JcsException($"The property '{repeated.Key}' is repeated at '{pointer}'");
				text.Append('{');
				var first = true;
				foreach (var property in properties.OrderBy(p => p.Name, StringComparer.Ordinal))
				{
					var at = $"{pointer}/{property.Name.Replace("~", "~0").Replace("/", "~1")}";
					if (at == omit)
						continue;
					if (!first)
						text.Append(',');
					first = false;
					String(text, property.Name);
					text.Append(':');
					Write(text, property.Value, at, omit);
				}
				text.Append('}');
				break;
			case JsonValueKind.Array:
				text.Append('[');
				var index = 0;
				foreach (var item in value.EnumerateArray())
				{
					if (index > 0)
						text.Append(',');
					Write(text, item, $"{pointer}/{index++}", omit);
				}
				text.Append(']');
				break;
			case JsonValueKind.String:
				string read;
				try
				{
					read = value.GetString()!;
				}
				catch (InvalidOperationException)
				{
					throw new JcsException($"The string at '{pointer}' is not Unicode");
				}
				String(text, read);
				break;
			case JsonValueKind.Number:
				if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
					throw new JcsException($"The number at '{pointer}' is not a double ({value.GetRawText()})");
				text.Append(Number(number));
				break;
			case JsonValueKind.True:
				text.Append("true");
				break;
			case JsonValueKind.False:
				text.Append("false");
				break;
			default:
				text.Append("null");
				break;
		}
	}

	/// <summary>A string as ECMAScript writes it: the control characters escaped (the five short ones by name), '"' and '\'; the rest as it is.</summary>
	static void String(StringBuilder text, string value)
	{
		text.Append('"');
		for (var i = 0; i < value.Length; i++)
		{
			var c = value[i];
			switch (c)
			{
				case '"':
					text.Append("\\\"");
					break;
				case '\\':
					text.Append("\\\\");
					break;
				case '\b':
					text.Append("\\b");
					break;
				case '\t':
					text.Append("\\t");
					break;
				case '\n':
					text.Append("\\n");
					break;
				case '\f':
					text.Append("\\f");
					break;
				case '\r':
					text.Append("\\r");
					break;
				case < ' ':
					text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
					break;
				default:
					if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
						text.Append(c).Append(value[++i]);
					else if (char.IsSurrogate(c))
						throw new JcsException($"The string '{value}' has a lone surrogate");
					else
						text.Append(c);
					break;
			}
		}
		text.Append('"');
	}

	/// <summary>
	/// A number as ECMAScript writes it (Number::toString, with its note 2): the shortest digits that read back as the same
	/// double, in plain notation from 1e-6 to 1e21 and in exponential notation outside.
	/// </summary>
	public static string Number(double value)
	{
		if (!double.IsFinite(value))
			throw new JcsException($"{value.ToString(CultureInfo.InvariantCulture)} has no JSON form");
		if (value == 0)
			return "0";
		// .NET gives the shortest round-trip digits too; only the notation differs
		var r = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
		var exponent = 0;
		if (r.IndexOf('E') is var e and >= 0)
		{
			exponent = int.Parse(r[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
			r = r[..e];
		}
		var dot = r.IndexOf('.');
		var all = dot < 0 ? r : r[..dot] + r[(dot + 1)..];
		// value = 0.digits × 10^point
		var point = (dot < 0 ? r.Length : dot) + exponent;
		var digits = all.TrimStart('0');
		point -= all.Length - digits.Length;
		digits = digits.TrimEnd('0');
		var k = digits.Length;
		string text;
		if (k <= point && point <= 21)
			text = digits + new string('0', point - k);
		else if (0 < point && point <= 21)
			text = $"{digits[..point]}.{digits[point..]}";
		else if (-6 < point && point <= 0)
			text = $"0.{new string('0', -point)}{digits}";
		else
		{
			var power = point - 1;
			text = $"{(k == 1 ? digits : $"{digits[..1]}.{digits[1..]}")}e{(power < 0 ? '-' : '+')}{Math.Abs(power).ToString(CultureInfo.InvariantCulture)}";
		}
		return value < 0 ? $"-{text}" : text;
	}
}
