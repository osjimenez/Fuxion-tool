using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Fuxion.Tool.Core.Yaml;

/// <summary>
/// Writes a whole YAML document from <see cref="YamlValue"/>s, the way fx writes what it generates: two spaces per
/// level, sequences under a key indented, scalars plain when they read back the same (<see cref="YamlEditor.Quote"/>).
/// Text with '\n'.
/// </summary>
public static class YamlWriter
{
	const int Indent = 2;

	public static string Document(YamlValue.Map document)
	{
		var b = new StringBuilder();
		foreach (var (key, value) in document.Entries)
			foreach (var line in EntryLines(key, value, 0))
				b.Append(line).Append('\n');
		return b.ToString();
	}

	static IEnumerable<string> EntryLines(string key, YamlValue value, int column)
	{
		var pad = new string(' ', column);
		if (YamlEditor.Inline(value) is { } inline)
		{
			yield return pad + YamlEditor.Quote(key) + ": " + inline;
			yield break;
		}
		yield return pad + YamlEditor.Quote(key) + ":";
		var lines = value switch
		{
			YamlValue.Map m => m.Entries.SelectMany(e => EntryLines(e.Key, e.Value, column + Indent)),
			YamlValue.Block b => b.Items.SelectMany(i => ItemLines(i, column + Indent)),
			_ => []
		};
		foreach (var line in lines)
			yield return line;
	}

	static IEnumerable<string> ItemLines(YamlValue value, int dashColumn)
	{
		var pad = new string(' ', dashColumn);
		if (YamlEditor.Inline(value) is { } inline)
		{
			yield return pad + "- " + inline;
			yield break;
		}
		var first = true;
		var lines = value switch
		{
			YamlValue.Map m => m.Entries.SelectMany(e => EntryLines(e.Key, e.Value, dashColumn + 2)),
			YamlValue.Block b => b.Items.SelectMany(i => ItemLines(i, dashColumn + 2)),
			_ => []
		};
		foreach (var line in lines)
		{
			yield return first ? pad + "- " + line[(dashColumn + 2)..] : line;
			first = false;
		}
	}
}
