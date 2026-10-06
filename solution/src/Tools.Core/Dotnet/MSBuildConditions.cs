using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Fuxion.Tools.Core.Dotnet;

/// <summary>
/// The MSBuild conditions of <c>dotnet.yaml</c>: expanding the <c>when</c> variables in them (design §7.3) and evaluating
/// the subset the coverage check understands (D-32).
/// </summary>
public static partial class MSBuildConditions
{
	// '$(X)' == 'true'  |  '$(X)' != 'true'  |  '$(X)' == 'false'  |  $(X) bare (not inside quotes)
	[GeneratedRegex(@"'\$\((?<name>[A-Za-z_][A-Za-z0-9_\-]*)\)'\s*(?<op>==|!=)\s*'(?<value>true|false)'", RegexOptions.IgnoreCase)]
	private static partial Regex Compared();

	[GeneratedRegex(@"(?<!')\$\((?<name>[A-Za-z_][A-Za-z0-9_\-]*)\)(?!')")]
	private static partial Regex Bare();

	/// <summary>
	/// Replaces every reference to a <c>when</c> variable by its own condition, recursively, so the result does not
	/// depend on the order MSBuild imports things. <paramref name="whenVariables"/> maps names to their raw conditions.
	/// </summary>
	public static string Expand(string condition, IReadOnlyDictionary<string, string> whenVariables)
		=> Expand(condition, whenVariables, []);

	static string Expand(string condition, IReadOnlyDictionary<string, string> whenVariables, HashSet<string> visiting)
	{
		string ExpandVariable(string name)
		{
			if (!visiting.Add(name))
				throw new InvalidOperationException($"The when variables refer to each other in a cycle ({string.Join(" → ", visiting)} → {name}).");
			var expanded = Expand(whenVariables[name], whenVariables, visiting);
			visiting.Remove(name);
			return expanded;
		}

		var result = Compared().Replace(condition, m =>
		{
			var name = m.Groups["name"].Value;
			if (!whenVariables.ContainsKey(name))
				return m.Value;
			var positive = (m.Groups["op"].Value == "==") == m.Groups["value"].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
			return positive ? $"({ExpandVariable(name)})" : $"!({ExpandVariable(name)})";
		});
		return Bare().Replace(result, m => whenVariables.ContainsKey(m.Groups["name"].Value) ? $"({ExpandVariable(m.Groups["name"].Value)})" : m.Value);
	}

	/// <summary>
	/// Evaluates a condition (already expanded) for a target framework. Understands string comparisons with
	/// <c>==</c>/<c>!=</c> (with <c>$(TargetFramework)</c> inside the quotes), <c>And</c>, <c>Or</c>, <c>!</c>,
	/// parentheses and <c>true</c>/<c>false</c>; anything else makes it <see langword="null"/> (cannot verify it).
	/// </summary>
	public static bool? Evaluate(string condition, string targetFramework)
	{
		try
		{
			var parser = new Parser(Tokenize(condition), targetFramework);
			var value = parser.ParseOr();
			return parser.AtEnd ? value : null;
		}
		catch (FormatException)
		{
			return null;
		}
	}

	enum Kind { String, And, Or, Not, Equal, NotEqual, Open, Close, True, False }

	readonly record struct Token(Kind Kind, string Text = "");

	static List<Token> Tokenize(string text)
	{
		var tokens = new List<Token>();
		for (var i = 0; i < text.Length;)
		{
			var c = text[i];
			if (char.IsWhiteSpace(c)) { i++; continue; }
			if (c == '\'')
			{
				var end = text.IndexOf('\'', i + 1);
				if (end < 0) throw new FormatException("Unclosed quote.");
				tokens.Add(new(Kind.String, text[(i + 1)..end]));
				i = end + 1;
				continue;
			}
			if (c == '(') { tokens.Add(new(Kind.Open)); i++; continue; }
			if (c == ')') { tokens.Add(new(Kind.Close)); i++; continue; }
			if (text.AsSpan(i).StartsWith("==")) { tokens.Add(new(Kind.Equal)); i += 2; continue; }
			if (text.AsSpan(i).StartsWith("!=")) { tokens.Add(new(Kind.NotEqual)); i += 2; continue; }
			if (c == '!') { tokens.Add(new(Kind.Not)); i++; continue; }
			if (char.IsLetter(c))
			{
				var start = i;
				while (i < text.Length && char.IsLetter(text[i])) i++;
				tokens.Add(text[start..i].ToLowerInvariant() switch
				{
					"and" => new(Kind.And),
					"or" => new(Kind.Or),
					"true" => new(Kind.True),
					"false" => new(Kind.False),
					var word => throw new FormatException($"Unknown word '{word}'.")
				});
				continue;
			}
			throw new FormatException($"Unexpected '{c}'.");
		}
		return tokens;
	}

	sealed class Parser(List<Token> tokens, string targetFramework)
	{
		int _position;

		public bool AtEnd => _position == tokens.Count;

		Token? Peek => _position < tokens.Count ? tokens[_position] : null;

		Token Next() => _position < tokens.Count ? tokens[_position++] : throw new FormatException("Unexpected end.");

		public bool ParseOr()
		{
			var value = ParseAnd();
			while (Peek?.Kind == Kind.Or)
			{
				Next();
				value = ParseAnd() | value;
			}
			return value;
		}

		bool ParseAnd()
		{
			var value = ParseUnary();
			while (Peek?.Kind == Kind.And)
			{
				Next();
				value = ParseUnary() & value;
			}
			return value;
		}

		bool ParseUnary()
		{
			var token = Next();
			switch (token.Kind)
			{
				case Kind.Not:
					return !ParseUnary();
				case Kind.Open:
					var inner = ParseOr();
					if (Next().Kind != Kind.Close) throw new FormatException("Missing ')'.");
					return inner;
				case Kind.True:
					return true;
				case Kind.False:
					return false;
				case Kind.String:
					var op = Next();
					var right = Next();
					if (op.Kind is not (Kind.Equal or Kind.NotEqual) || right.Kind != Kind.String)
						throw new FormatException("Expected 'a' == 'b'.");
					var equal = string.Equals(Substitute(token.Text), Substitute(right.Text), StringComparison.OrdinalIgnoreCase);
					return op.Kind == Kind.Equal ? equal : !equal;
				default:
					throw new FormatException($"Unexpected {token.Kind}.");
			}
		}

		string Substitute(string text)
		{
			var result = text.Replace("$(TargetFramework)", targetFramework, StringComparison.OrdinalIgnoreCase);
			return result.Contains("$(", StringComparison.Ordinal) ? throw new FormatException($"Unknown property in '{text}'.") : result;
		}
	}

	/// <summary>The <c>value</c> variables expanded in a text (like <c>$(CoreFrameworks)</c> in a .csproj).</summary>
	public static string ExpandValues(string text, IReadOnlyDictionary<string, string> valueVariables)
	{
		for (var i = 0; i < 10 && text.Contains("$(", StringComparison.Ordinal); i++)
			text = Bare().Replace(text, m => valueVariables.TryGetValue(m.Groups["name"].Value, out var value) ? value : m.Value);
		return text;
	}

	/// <summary>The target frameworks of a <c>TargetFramework(s)</c> value, or <see langword="null"/> if something is left unresolved.</summary>
	public static IReadOnlyList<string>? Frameworks(string value, IReadOnlyDictionary<string, string> valueVariables)
	{
		var expanded = ExpandValues(value, valueVariables);
		if (expanded.Contains("$(", StringComparison.Ordinal))
			return null;
		return expanded.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}
}
