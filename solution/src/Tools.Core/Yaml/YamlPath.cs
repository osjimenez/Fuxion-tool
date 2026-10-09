using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Fuxion.Tools.Core.Yaml;

/// <summary>
/// A path to a node of a YAML document:
/// <list type="bullet">
/// <item><c>workspace.major</c>: keys;</item>
/// <item><c>packages[3].ids</c>: an item by position;</item>
/// <item><c>repositories[name=plus].tags</c>: the first mapping whose <c>name</c> is <c>plus</c>, or a list that
/// contains it (<c>packages[ids=PolySharp]</c>);</item>
/// <item><c>tags[=public]</c>: the scalar item <c>public</c>;</item>
/// <item><c>sdks["Fuxion.Tools.Sdk"]</c>: a key with dots.</item>
/// </list>
/// </summary>
public static class YamlPath
{
	abstract record Segment;

	sealed record Key(string Name) : Segment;

	sealed record Index(int Value) : Segment;

	sealed record Filter(string Field, string Value) : Segment;

	/// <summary>The node at <paramref name="path"/> (the root for an empty path), or null.</summary>
	/// <exception cref="FormatException">The path is not well formed.</exception>
	public static YamlNode? Resolve(YamlNode? root, string path)
	{
		var node = root;
		foreach (var segment in Parse(path))
		{
			node = (segment, node) switch
			{
				(Key k, YamlMapping m) => m[k.Name],
				(Index ix, YamlSequence s) => ix.Value < s.Items.Count ? s.Items[ix.Value] : null,
				(Filter { Field: "" } f, YamlSequence s) => s.Items.FirstOrDefault(item => item is YamlScalar sc && sc.Value == f.Value),
				(Filter f, YamlSequence s) => s.Items.FirstOrDefault(item => item is YamlMapping m && Matches(m[f.Field], f.Value)),
				_ => null
			};
			if (node is null)
				return null;
		}
		return node;
	}

	static bool Matches(YamlNode? node, string value) => node switch
	{
		YamlScalar s => s.Value == value,
		YamlSequence q => q.Items.OfType<YamlScalar>().Any(s => s.Value == value),
		_ => false
	};

	static List<Segment> Parse(string path)
	{
		var segments = new List<Segment>();
		var i = 0;
		while (i < path.Length)
		{
			if (path[i] == '.')
			{
				i++;
				continue;
			}
			if (path[i] == '[')
			{
				if (i + 1 < path.Length && path[i + 1] == '"')
				{
					var quote = path.IndexOf("\"]", i + 2, StringComparison.Ordinal);
					if (quote < 0)
						throw new FormatException($"Unclosed [\" in the path '{path}'.");
					segments.Add(new Key(path[(i + 2)..quote]));
					i = quote + 2;
					continue;
				}
				var close = path.IndexOf(']', i);
				if (close < 0)
					throw new FormatException($"Unclosed [ in the path '{path}'.");
				var inner = path[(i + 1)..close];
				var eq = inner.IndexOf('=');
				if (eq >= 0)
					segments.Add(new Filter(inner[..eq], inner[(eq + 1)..]));
				else if (int.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
					segments.Add(new Index(index));
				else
					throw new FormatException($"'[{inner}]' in the path '{path}' is neither a position nor a filter (key=value).");
				i = close + 1;
				continue;
			}
			var name = new StringBuilder();
			while (i < path.Length && path[i] is not ('.' or '['))
				name.Append(path[i++]);
			segments.Add(new Key(name.ToString()));
		}
		return segments;
	}
}
