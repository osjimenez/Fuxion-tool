using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Net;
using Fuxion.Tool.Core.Layout;
using Fuxion.Tool.Core.Results;
using SharpYaml.Syntax;

namespace Fuxion.Tool.Core.Yaml;

/// <summary>A value to write: rendered as YAML with the indentation and line endings of the file.</summary>
public abstract record YamlValue
{
	/// <summary>A string: plain when it reads back the same, double-quoted otherwise (or as the scalar it replaces).</summary>
	public sealed record Text(string Value) : YamlValue;

	/// <summary>Plain YAML written as is (numbers, booleans).</summary>
	public sealed record Raw(string Yaml) : YamlValue;

	/// <summary>A flow sequence: <c>[a, b]</c>.</summary>
	public sealed record Flow(params YamlValue[] Items) : YamlValue;

	/// <summary>A block sequence: one <c>- item</c> per line.</summary>
	public sealed record Block(params YamlValue[] Items) : YamlValue;

	/// <summary>A block mapping, in this order.</summary>
	public sealed record Map(params (string Key, YamlValue Value)[] Entries) : YamlValue;

	public static implicit operator YamlValue(string value) => new Text(value);
}

/// <summary>
/// A change to the text: replace <see cref="Length"/> characters at <see cref="Start"/> with <see cref="Text"/>.
/// <see cref="What"/> says it in words; <see cref="RemovedComments"/> counts the comments that go with it.
/// </summary>
public sealed record YamlEdit(int Start, int Length, string Text, string What, int RemovedComments = 0);

/// <summary>A comment of the file: where it starts and its text (with the <c>#</c>).</summary>
public sealed record YamlComment(int Start, string Text);

/// <summary>
/// Edits YAML written by hand without losing anything (plan O, decision 11). Every edit changes one range of the text
/// and leaves the rest byte for byte: comments, blank lines, order, quoting and line endings. It locates nodes by
/// <see cref="YamlPath"/>, plans a <see cref="YamlEdit"/> in the style of the file (indentation, sequences under a key
/// indented or not, quoting) and applies it with SharpYaml's lossless syntax tree, which reparses and validates.
/// Immutable: <see cref="Apply"/> returns a new editor, whose positions are the new ones.
/// </summary>
public sealed class YamlEditor
{
	readonly YamlSyntaxTree _tree;

	YamlEditor(YamlSyntaxTree tree, YamlNode? root, bool hasBom)
	{
		_tree = tree;
		HasBom = hasBom;
		Root = root;
		NewLine = DetectNewLine(tree.Text);
		(IndentUnit, IndentedSequences) = InferIndentation(Root);
	}

	/// <summary>The text, without the byte order mark.</summary>
	public string Text => _tree.Text;

	public YamlNode? Root { get; }

	/// <summary>The line ending of the file (<c>\r\n</c> or <c>\n</c>; the system's for a file without any).</summary>
	public string NewLine { get; }

	/// <summary>The indentation step of nested mappings.</summary>
	public int IndentUnit { get; }

	/// <summary>Whether a block sequence under a key is indented (<c>key:\n  - a</c>) or not (<c>key:\n- a</c>).</summary>
	public bool IndentedSequences { get; }

	/// <summary>The file started with a UTF-8 byte order mark: <see cref="WriteFile"/> keeps it.</summary>
	public bool HasBom { get; }

	/// <summary>The code of what the editor cannot do to the file as it is written (<see cref="HttpStatusCode.Conflict"/>).</summary>
	public const string Unsupported = "yaml.unsupported";

	/// <summary>An empty text, to edit from nothing.</summary>
	public static YamlEditor Empty { get; } = new(YamlSyntaxTree.Parse(""), null, false);

	/// <summary>The editor of <paramref name="text"/>; an error (<see cref="YamlDocument.Syntax"/>) if it is not valid YAML.</summary>
	public static Response<YamlEditor> Parse(string text, bool hasBom = false)
	{
		YamlSyntaxTree tree;
		try
		{
			tree = YamlSyntaxTree.Parse(text);
		}
		catch (SharpYaml.YamlException ex)
		{
			return YamlDocument.Invalid(ex.Message, (int)ex.Start.Line + 1);
		}
		return Create(tree, hasBom, null);
	}

	/// <summary>The editor of a tree, its nodes read; <paramref name="what"/>, the edit that made it, if one.</summary>
	static Response<YamlEditor> Create(YamlSyntaxTree tree, bool hasBom, string? what)
	{
		var root = YamlDocument.Load(tree.Text);
		if (root is Error error)
			return what is null ? error : YamlDocument.Invalid($"{what}: {error.Message}", error.Diagnostics[0].Line ?? 1);
		return new YamlEditor(tree, root is YamlNode node ? node : null, hasBom);
	}


	/// <summary>Reads a file as it is: UTF-8, with or without a byte order mark (SharpYaml does not take one).</summary>
	public static Response<YamlEditor> ReadFile(string path)
	{
		var bytes = FxFiles.ReadAllBytes(path);
		var bom = bytes is [0xEF, 0xBB, 0xBF, ..];
		return Parse(new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
	}

	/// <summary>Writes the text, with the byte order mark if the file had one.</summary>
	public void WriteFile(string path) => File.WriteAllBytes(path, new UTF8Encoding(HasBom).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(Text)).ToArray());

	/// <summary>The editor of the text with <paramref name="edit"/> applied; an error if the result is not valid YAML (this editor stays as it was).</summary>
	public Response<YamlEditor> Apply(YamlEdit edit)
	{
		YamlSyntaxTree tree;
		try
		{
			tree = _tree.WithTextChange(edit.Start, edit.Length, edit.Text);
		}
		catch (SharpYaml.YamlException ex)
		{
			return YamlDocument.Invalid($"{edit.What}: {ex.Message}", (int)ex.Start.Line + 1);
		}
		return Create(tree, HasBom, edit.What);
	}

	/// <summary>The editor with <paramref name="edit"/> applied, if it could be planned; its error, if not.</summary>
	public Response<YamlEditor> Apply(Response<YamlEdit> edit) => edit is YamlEdit planned ? Apply(planned) : (Error)edit;


	public YamlNode? Resolve(string path) => path.Length == 0 ? Root : YamlPath.Resolve(Root, path);

	/// <summary>The node at <paramref name="path"/>, of the kind an edit needs; an error if the file has another there.</summary>
	Response<T> Find<T>(string path) where T : YamlNode
		=> Resolve(path) is T node ? node : Cannot($"yaml.not-{typeof(T).Name[4..].ToLowerInvariant()}", path);

	/// <summary>What the editor cannot do to the file as it is written: its text, about <paramref name="path"/>, at the line of its node.</summary>
	Error Cannot(string key, string path)
		=> FxErrors.Of(HttpStatusCode.Conflict, FxDiagnostic.Error(Unsupported, new(key, path), line: Resolve(path)?.Line));


	/// <summary>
	/// The comments of the file. SharpYaml 3.15.0 also gives comment trivia that starts inside a scalar: <c>c#d</c>
	/// yields <c>#d</c>, which is no comment and is left out; and <c>"x # y" # real</c> yields a single <c># y" # real</c>,
	/// which swallows the real comment: that one is cut where it really starts, at the first <c>#</c> after the scalar
	/// that follows a blank.
	/// </summary>
	public IReadOnlyList<YamlComment> Comments()
	{
		var scalars = _tree.Tokens.Where(t => t.Kind == YamlSyntaxKind.Scalar).Select(t => (Start: (int)t.Span.Start.Index, End: (int)t.Span.End.Index)).ToList();
		var comments = new List<YamlComment>();
		foreach (var token in _tree.Tokens.Where(t => t.Kind == YamlSyntaxKind.CommentTrivia))
		{
			var start = (int)token.Span.Start.Index;
			var end = (int)token.Span.End.Index;
			var inside = scalars.FirstOrDefault(s => start >= s.Start && start < s.End);
			if (inside == default)
			{
				comments.Add(new(start, token.Text));
				continue;
			}
			for (var i = inside.End; i < end; i++)
				if (Text[i] == '#' && char.IsWhiteSpace(Text[i - 1]))
				{
					comments.Add(new(i, Text[i..end]));
					break;
				}
		}
		return comments;
	}

	// ---------------------------------------------------------------- edits

	/// <summary>Changes a scalar, keeping its quotes (single or double) if it had them.</summary>
	public Response<YamlEdit> SetScalar(string path, YamlValue value)
	{
		if (Find<YamlScalar>(path) is not YamlScalar node)
			return (Error)Find<YamlScalar>(path);
		var text = value switch
		{
			YamlValue.Text s when node.Style == YamlScalarStyle.DoubleQuoted => DoubleQuote(s.Value),
			YamlValue.Text s when node.Style == YamlScalarStyle.SingleQuoted => "'" + s.Value.Replace("'", "''") + "'",
			_ => Inline(value) ?? throw new NotSupportedException("A scalar takes an inline value.")
		};
		// An empty value sits right after its ':'
		if (node.Start == node.End && node.Start > 0 && Text[node.Start - 1] == ':')
			text = " " + text;
		return new YamlEdit(node.Start, node.End - node.Start, text, $"set {path}");
	}

	/// <summary>Adds an item at the end of a sequence (flow or block).</summary>
	public Response<YamlEdit> Append(string path, YamlValue item)
	{
		if (Find<YamlSequence>(path) is not YamlSequence sequence)
			return (Error)Find<YamlSequence>(path);
		if (sequence.IsFlow)
		{
			if (Inline(item) is not { } inline)
				return Cannot("yaml.flow-sequence", path);
			if (sequence.Items.Count == 0)
				return new YamlEdit(sequence.Start + 1, sequence.FlowEnd - 1 - (sequence.Start + 1), inline, $"append to {path}");
			// The separator the sequence already uses (", ", or a line break and indentation)
			var separator = sequence.Items.Count >= 2 ? Text[sequence.Items[^2].End..sequence.Items[^1].Start] : ", ";
			return new YamlEdit(sequence.Items[^1].End, 0, separator + inline, $"append to {path}");
		}
		var last = sequence.Items[^1];
		var at = NextLineStart(last.End);
		var blank = sequence.Items.Count >= 2 && IsBlankLine(PreviousLineStart(LineStart(DashIndex(last)))) ? NewLine : "";
		return new YamlEdit(at, 0, blank + JoinLines(ItemLines(item, sequence.Column - 1), at), $"append to {path}");
	}

	/// <summary>Adds a key to a mapping (flow or block), at the end or after <paramref name="after"/>.</summary>
	public Response<YamlEdit> AddKey(string path, string key, YamlValue value, string? after = null)
	{
		if (Find<YamlMapping>(path) is not YamlMapping map)
			return (Error)Find<YamlMapping>(path);
		if (map.Entry(key) is not null)
			throw new InvalidOperationException($"'{key}' already exists in '{path}'.");
		if (map.Entries.Count == 0 && !map.IsFlow)
			return Cannot("yaml.empty-block-mapping", path);
		var anchor = after is null ? map.Entries.LastOrDefault() : map.Entry(after) ?? throw new InvalidOperationException($"No '{after}' in '{path}'.");
		if (map.IsFlow)
		{
			if (Inline(value) is not { } inline)
				return Cannot("yaml.flow-mapping", path);
			var text = Quote(key) + ": " + inline;
			if (map.Entries.Count == 0)
				return new YamlEdit(map.Start + 1, map.FlowEnd - 1 - (map.Start + 1), text, $"add {path}.{key}");
			var separator = map.Entries.Count >= 2 ? Text[map.Entries[^2].Value.End..map.Entries[^1].Key.Start] : ", ";
			return new YamlEdit(anchor!.Value.End, 0, separator + text, $"add {path}.{key}");
		}
		var at = NextLineStart(anchor!.Value.End);
		return new YamlEdit(at, 0, JoinLines(EntryLines(key, value, map.Entries[0].Key.Column - 1), at), $"add {path}.{key}");
	}

	/// <summary>
	/// Removes the node at <paramref name="path"/>: a sequence item or a mapping entry (the path of its value). Block
	/// items and entries take their whole lines, their end-of-line comments and the comment lines right above them at
	/// their own indentation (with no blank line between); other comments stay. The last item of a block sequence
	/// leaves <c>key: []</c>, the last entry of a block mapping <c>key: {}</c>.
	/// </summary>
	public Response<YamlEdit> Remove(string path)
	{
		var node = Resolve(path) ?? throw new InvalidOperationException($"'{path}' not found.");
		return node.Parent switch
		{
			YamlSequence { IsFlow: true } s => RemoveFlow(s.Items.IndexOf(node), s.Items.Count, s.Start, s.FlowEnd,
				i => s.Items[i].Start, i => s.Items[i].End, path),
			YamlSequence s => RemoveBlockItem(s, node, path),
			YamlMapping { IsFlow: true } m => RemoveFlow(m.Entries.FindIndex(e => e.Value == node), m.Entries.Count, m.Start, m.FlowEnd,
				i => m.Entries[i].Key.Start, i => m.Entries[i].Value.End, path),
			YamlMapping m => RemoveBlockEntry(m, m.Entries.First(e => e.Value == node), path),
			_ => throw new NotSupportedException("The root cannot be removed.")
		};
	}

	YamlEdit RemoveFlow(int index, int count, int open, int flowEnd, Func<int, int> start, Func<int, int> end, string path)
	{
		if (count == 1)
			return Removal(open + 1, flowEnd - 1, path);
		return index < count - 1 ? Removal(start(index), start(index + 1), path) : Removal(end(index - 1), end(index), path);
	}

	Response<YamlEdit> RemoveBlockItem(YamlSequence sequence, YamlNode item, string path)
	{
		var dash = DashIndex(item);
		var lineStart = LineStart(dash);
		if (!OnlySpaces(lineStart, dash))
			return Cannot("yaml.compact-nested", path);
		var end = NextLineStart(item.End);
		if (sequence.Items.Count == 1 && sequence.Parent is YamlMapping parent)
		{
			// Keep the key, as an empty sequence
			var colon = Text.IndexOf(':', parent.Entries.First(e => e.Value == sequence).Key.End);
			return new YamlEdit(colon + 1, end - colon - 1, " []" + (end > 0 && Text[end - 1] == '\n' ? NewLine : ""), $"remove {path}",
				CountComments(colon + 1, end));
		}
		return RemoveLines(ExtendOverLeadingComments(lineStart, sequence.Column - 1), end, path);
	}

	Response<YamlEdit> RemoveBlockEntry(YamlMapping map, YamlEntry entry, string path)
	{
		var keyLineStart = LineStart(entry.Key.Start);
		if (OnlySpaces(keyLineStart, entry.Key.Start))
		{
			if (map.Entries.Count == 1 && map.Parent is YamlMapping parent)
			{
				var colon = Text.IndexOf(':', parent.Entries.First(e => e.Value == map).Key.End);
				var end = NextLineStart(entry.Value.End);
				return new YamlEdit(colon + 1, end - colon - 1, " {}" + NewLine, $"remove {path}", CountComments(colon + 1, end));
			}
			return RemoveLines(ExtendOverLeadingComments(keyLineStart, entry.Key.Column - 1), NextLineStart(entry.Value.End), path);
		}
		// The first key of a compact sequence item (- name: x): the next key moves up after the dash
		if (map.Entries.Count == 1)
			return RemoveBlockItem((YamlSequence)map.Parent!, map, path);
		var next = map.Entries[map.Entries.IndexOf(entry) + 1];
		return Removal(entry.Key.Start, next.Key.Start, path);
	}

	YamlEdit RemoveLines(int start, int end, string path)
	{
		// Do not leave two blank lines where the removed block was between them
		if (start > 0 && IsBlankLine(PreviousLineStart(start)) && end < Text.Length && IsBlankLine(end))
			end = NextLineStart(end);
		return Removal(start, end, path);
	}

	YamlEdit Removal(int start, int end, string path) => new(start, end - start, "", $"remove {path}", CountComments(start, end));

	int CountComments(int start, int end) => Comments().Count(c => c.Start >= start && c.Start < end);

	// ---------------------------------------------------------------- rendering

	/// <summary>The value on one line, or null if it needs several (a block sequence or mapping with items).</summary>
	public static string? Inline(YamlValue value) => value switch
	{
		YamlValue.Text s => Quote(s.Value),
		YamlValue.Raw r => r.Yaml,
		YamlValue.Flow f => "[" + string.Join(", ", f.Items.Select(i => Inline(i) ?? throw new NotSupportedException("A block inside a flow sequence."))) + "]",
		YamlValue.Block { Items.Length: 0 } => "[]",
		YamlValue.Map { Entries.Length: 0 } => "{}",
		_ => null
	};

	IEnumerable<string> EntryLines(string key, YamlValue value, int column)
	{
		var pad = new string(' ', column);
		if (Inline(value) is { } inline)
		{
			yield return pad + Quote(key) + ": " + inline;
			yield break;
		}
		yield return pad + Quote(key) + ":";
		var lines = value switch
		{
			YamlValue.Map m => m.Entries.SelectMany(e => EntryLines(e.Key, e.Value, column + IndentUnit)),
			YamlValue.Block b => b.Items.SelectMany(i => ItemLines(i, IndentedSequences ? column + IndentUnit : column)),
			_ => []
		};
		foreach (var line in lines)
			yield return line;
	}

	IEnumerable<string> ItemLines(YamlValue value, int dashColumn)
	{
		var pad = new string(' ', dashColumn);
		if (Inline(value) is { } inline)
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

	string JoinLines(IEnumerable<string> lines, int at)
		=> at == Text.Length && (Text.Length == 0 || Text[^1] != '\n')
			? NewLine + string.Join(NewLine, lines)
			: string.Concat(lines.Select(l => l + NewLine));

	static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
	{
		"null", "~", "true", "false", "yes", "no", "on", "off", "y", "n", ".inf", "-.inf", "+.inf", ".nan"
	};

	/// <summary>Plain when it reads back as the same string, in block and in flow context; double-quoted otherwise.</summary>
	public static string Quote(string value)
	{
		var plain = value.Length > 0
		            && !char.IsWhiteSpace(value[0]) && !char.IsWhiteSpace(value[^1])
		            && !"-?:,[]{}#&*!|>'\"%@`".Contains(value[0])
		            && !value.Contains(": ") && !value.Contains(" #") && !value.EndsWith(':')
		            && value.IndexOfAny([',', '[', ']', '{', '}']) < 0
		            && !value.Any(char.IsControl)
		            && !Reserved.Contains(value)
		            && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
		            && !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("0o", StringComparison.OrdinalIgnoreCase);
		return plain ? value : DoubleQuote(value);
	}

	static string DoubleQuote(string value)
	{
		var quoted = new StringBuilder("\"");
		foreach (var c in value)
			quoted.Append(c switch
			{
				'"' => "\\\"",
				'\\' => "\\\\",
				'\n' => "\\n",
				'\r' => "\\r",
				'\t' => "\\t",
				< ' ' => $"\\u{(int)c:X4}",
				_ => c.ToString()
			});
		return quoted.Append('"').ToString();
	}

	// ---------------------------------------------------------------- text

	int LineStart(int index)
	{
		var i = index;
		while (i > 0 && Text[i - 1] != '\n')
			i--;
		return i;
	}

	int NextLineStart(int index)
	{
		var i = Text.IndexOf('\n', index);
		return i < 0 ? Text.Length : i + 1;
	}

	int PreviousLineStart(int lineStart) => lineStart == 0 ? -1 : LineStart(lineStart - 1);

	bool OnlySpaces(int from, int to)
	{
		for (var i = from; i < to; i++)
			if (Text[i] != ' ')
				return false;
		return true;
	}

	bool IsBlankLine(int lineStart)
	{
		if (lineStart < 0 || lineStart >= Text.Length)
			return false;
		for (var i = lineStart; i < Text.Length && Text[i] != '\n'; i++)
			if (Text[i] is not (' ' or '\t' or '\r'))
				return false;
		return true;
	}

	bool IsCommentLine(int lineStart, out int indent)
	{
		var i = lineStart;
		while (i < Text.Length && Text[i] == ' ')
			i++;
		indent = i - lineStart;
		return i < Text.Length && Text[i] == '#';
	}

	int ExtendOverLeadingComments(int lineStart, int column)
	{
		var start = lineStart;
		for (var p = PreviousLineStart(start); p >= 0 && IsCommentLine(p, out var indent) && indent == column; p = PreviousLineStart(start))
			start = p;
		return start;
	}

	/// <summary>The '-' of a block sequence item (its node starts after "- ").</summary>
	int DashIndex(YamlNode item)
	{
		var i = item.Start - 1;
		while (i >= 0 && Text[i] == ' ')
			i--;
		return i >= 0 && Text[i] == '-' ? i : throw new InvalidOperationException($"No '-' before the item at line {item.Line}.");
	}

	static string DetectNewLine(string text)
	{
		int crlf = 0, lf = 0;
		for (var i = 0; i < text.Length; i++)
			if (text[i] == '\n')
			{
				if (i > 0 && text[i - 1] == '\r')
					crlf++;
				else
					lf++;
			}
		return crlf == 0 && lf == 0 ? Environment.NewLine : crlf >= lf ? "\r\n" : "\n";
	}

	/// <summary>The indentation step of nested mappings (the smallest seen) and whether sequences under a key are indented.</summary>
	static (int Unit, bool IndentedSequences) InferIndentation(YamlNode? root)
	{
		var unit = int.MaxValue;
		int indented = 0, flush = 0;

		void Walk(YamlNode? node)
		{
			switch (node)
			{
				case YamlMapping m:
					foreach (var e in m.Entries)
					{
						if (!m.IsFlow && e.Value is YamlMapping { IsFlow: false, Entries.Count: > 0 } child && child.Entries[0].Key.Line > e.Key.Line)
							unit = Math.Min(unit, child.Entries[0].Key.Column - e.Key.Column);
						if (!m.IsFlow && e.Value is YamlSequence { IsFlow: false } sequence && sequence.Line > e.Key.Line)
						{
							if (sequence.Column > e.Key.Column)
								indented++;
							else
								flush++;
						}
						Walk(e.Value);
					}
					break;
				case YamlSequence s:
					foreach (var item in s.Items)
						Walk(item);
					break;
			}
		}

		Walk(root);
		return (unit is int.MaxValue or <= 0 ? 2 : unit, indented >= flush);
	}
}
