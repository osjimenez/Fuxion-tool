using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SharpYaml.Events;

namespace Fuxion.Tools.Core.Yaml;

/// <summary>A YAML node with its position in the text (plan O, decision 11: the one YAML reader of fx).</summary>
public abstract class YamlNode
{
	/// <summary>The mapping or sequence that contains it.</summary>
	public YamlNode? Parent { get; internal set; }

	/// <summary>1-based line, as fx reports it in its diagnostics.</summary>
	public int Line { get; internal init; }

	/// <summary>1-based column.</summary>
	public int Column { get; internal init; }

	/// <summary>Offset of its first character in the text (UTF-16).</summary>
	public int Start { get; internal init; }

	/// <summary>Offset just after its last character, without trailing comments or blanks.</summary>
	public abstract int End { get; }
}

public sealed class YamlScalar : YamlNode
{
	internal int EndIndex { get; init; }

	public required string Value { get; init; }

	/// <summary>Written with quotes (<c>'…'</c> or <c>"…"</c>) or as a block (<c>|</c>, <c>&gt;</c>), not plain.</summary>
	public bool IsQuoted { get; init; }

	public override int End => EndIndex;

	public override string ToString() => Value;
}

/// <summary>An alias (<c>*name</c>). fx's files do not use anchors; kept so that nothing is lost.</summary>
public sealed class YamlAlias : YamlNode
{
	internal int EndIndex { get; init; }

	public required string Name { get; init; }

	public override int End => EndIndex;
}

public sealed record YamlEntry(YamlNode Key, YamlNode Value);

public sealed class YamlMapping : YamlNode
{
	internal int FlowEnd { get; set; }

	public List<YamlEntry> Entries { get; } = [];

	/// <summary>Written as <c>{ a: 1 }</c>.</summary>
	public bool IsFlow { get; init; }

	public override int End => IsFlow ? FlowEnd : Entries.Count == 0 ? Start : Entries[^1].Value.End;

	/// <summary>The value of a plain key, or null.</summary>
	public YamlNode? this[string key] => Entries.FirstOrDefault(e => e.Key is YamlScalar s && s.Value == key)?.Value;
}

public sealed class YamlSequence : YamlNode
{
	internal int FlowEnd { get; set; }

	public List<YamlNode> Items { get; } = [];

	/// <summary>Written as <c>[a, b]</c>.</summary>
	public bool IsFlow { get; init; }

	public override int End => IsFlow ? FlowEnd : Items.Count == 0 ? Start : Items[^1].End;
}

/// <summary>Invalid YAML: the message of the parser and where (1-based).</summary>
public sealed class YamlSyntaxException(string message, int line, int column) : Exception(message)
{
	public int Line { get; } = line;

	public int Column { get; } = column;
}

/// <summary>
/// Reads YAML with SharpYaml's parser (AOT, no reflection) into a tree of <see cref="YamlNode"/> with positions. The
/// positions also serve the edit engine, which changes the text without losing comments or format.
/// </summary>
public static partial class YamlDocument
{
	/// <summary>The root node of the first document, or null if the text has none.</summary>
	/// <exception cref="YamlSyntaxException">Invalid YAML, or a key repeated in a mapping.</exception>
	public static YamlNode? Load(string text)
	{
		var events = new List<ParsingEvent>();
		try
		{
			var parser = SharpYaml.Parser.CreateParser(new StringReader(text));
			while (parser.MoveNext())
				events.Add(parser.Current!);
		}
		catch (SharpYaml.YamlException ex)
		{
			throw new YamlSyntaxException(CleanMessage(ex.Message), (int)ex.Start.Line + 1, (int)ex.Start.Column + 1);
		}
		var i = 0;
		while (i < events.Count && events[i] is StreamStart or DocumentStart)
			i++;
		if (i >= events.Count || events[i] is DocumentEnd or StreamEnd)
			return null;
		return Read(text, events, ref i, null);
	}

	static YamlNode Read(string text, List<ParsingEvent> events, ref int i, YamlNode? parent)
	{
		var e = events[i++];
		var line = (int)e.Start.Line + 1;
		var column = (int)e.Start.Column + 1;
		var start = (int)e.Start.Index;
		switch (e)
		{
			case Scalar s:
				return new YamlScalar
				{
					Value = s.Value,
					IsQuoted = s.Style != SharpYaml.ScalarStyle.Plain && s.Style != SharpYaml.ScalarStyle.Any,
					Line = line, Column = column, Start = start, EndIndex = (int)s.End.Index, Parent = parent
				};
			case AnchorAlias a:
				return new YamlAlias { Name = a.Value, Line = line, Column = column, Start = start, EndIndex = (int)a.End.Index, Parent = parent };
			case MappingStart ms:
			{
				var map = new YamlMapping { IsFlow = ms.Style == SharpYaml.YamlStyle.Flow, Line = line, Column = column, Start = start, Parent = parent };
				while (events[i] is not MappingEnd)
				{
					var key = Read(text, events, ref i, map);
					var value = Read(text, events, ref i, map);
					if (key is YamlScalar k && map.Entries.Any(x => x.Key is YamlScalar other && other.Value == k.Value))
						throw new YamlSyntaxException($"Duplicate key '{k.Value}'.", key.Line, key.Column);
					map.Entries.Add(new(key, value));
				}
				var end = events[i++];
				if (map.IsFlow)
					map.FlowEnd = After(text, (int)end.Start.Index, '}');
				return map;
			}
			case SequenceStart ss:
			{
				var sequence = new YamlSequence { IsFlow = ss.Style == SharpYaml.YamlStyle.Flow, Line = line, Column = column, Start = start, Parent = parent };
				while (events[i] is not SequenceEnd)
					sequence.Items.Add(Read(text, events, ref i, sequence));
				var end = events[i++];
				if (sequence.IsFlow)
					sequence.FlowEnd = After(text, (int)end.Start.Index, ']');
				return sequence;
			}
			default:
				throw new YamlSyntaxException($"Unexpected {e.GetType().Name}.", line, column);
		}
	}

	// The end event of a flow collection points at its closing bracket (a zero-width token): the offset after it
	static int After(string text, int index, char bracket)
	{
		var j = index;
		while (j < text.Length && text[j] != bracket)
			j++;
		return j < text.Length ? j + 1 : index;
	}

	// SharpYaml prefixes its messages with 0-based positions, "(Lin: 60, Col: 8, Chr: 1234) - (Lin: …): ": fx gives
	// the position apart, 1-based
	[GeneratedRegex(@"^(\(Lin:[^)]*\)\s*-\s*)?\(Lin:[^)]*\):\s*")]
	private static partial Regex PositionPrefix();

	static string CleanMessage(string message) => PositionPrefix().Replace(message, "");
}
