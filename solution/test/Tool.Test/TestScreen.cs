using System;
using System.Text;

namespace Fuxion.Tool.Test;

/// <summary>
/// A screen of characters that applies what a full screen app writes (cursor moves, erases and text; colors and modes
/// are skipped), to read back what is on it: how the tests see <c>fx ui</c> without a terminal, and how the harness of
/// <c>dev/Pty</c> takes pictures of a real one. A full line wraps when the next character comes, as terminals do (a
/// pseudoconsole counts on it).
/// </summary>
sealed class TestScreen(int width, int height)
{
	readonly char[] _cells = new string(' ', width * height).ToCharArray();
	int _row, _col, _savedRow, _savedCol;
	bool _wrap;

	public void Apply(string text)
	{
		var i = 0;
		while (i < text.Length)
		{
			var c = text[i++];
			if (c != '\x1b')
			{
				Write(c);
				continue;
			}
			if (i >= text.Length)
				break;
			var kind = text[i++];
			if (kind == ']')
			{
				// OSC: up to BEL or ESC \
				while (i < text.Length && text[i] != '\a' && !(text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '\\'))
					i++;
				i += i < text.Length && text[i] == '\a' ? 1 : 2;
				continue;
			}
			if (kind == '7')
				(_savedRow, _savedCol) = (_row, _col);
			else if (kind == '8')
				(_row, _col) = (_savedRow, _savedCol);
			if (kind != '[')
				continue;
			var parameters = new StringBuilder();
			while (i < text.Length && text[i] is >= '0' and <= '?' or ';')
				parameters.Append(text[i++]);
			while (i < text.Length && text[i] is >= ' ' and <= '/')
				i++;
			if (i >= text.Length)
				break;
			Csi(text[i++], parameters.ToString());
		}
	}

	void Csi(char final, string parameters)
	{
		_wrap = false;
		if (parameters.StartsWith('?'))
			return;
		var parts = parameters.Split(';');
		int P(int index, int fallback) => index < parts.Length && int.TryParse(parts[index], out var value) ? value : fallback;
		switch (final)
		{
			case 'H' or 'f':
				(_row, _col) = (Math.Clamp(P(0, 1) - 1, 0, height - 1), Math.Clamp(P(1, 1) - 1, 0, width - 1));
				break;
			case 'G':
				_col = Math.Clamp(P(0, 1) - 1, 0, width - 1);
				break;
			case 'A':
				_row = Math.Max(0, _row - P(0, 1));
				break;
			case 'B':
				_row = Math.Min(height - 1, _row + P(0, 1));
				break;
			case 'C':
				_col = Math.Min(width - 1, _col + P(0, 1));
				break;
			case 'D':
				_col = Math.Max(0, _col - P(0, 1));
				break;
			case 'E':
				(_row, _col) = (Math.Min(height - 1, _row + P(0, 1)), 0);
				break;
			case 'F':
				(_row, _col) = (Math.Max(0, _row - P(0, 1)), 0);
				break;
			case 'J' when P(0, 0) == 2:
				Array.Fill(_cells, ' ');
				break;
			case 'K':
				var from = P(0, 0) == 2 ? 0 : _col;
				Array.Fill(_cells, ' ', _row * width + from, width - from);
				break;
			case 'X':
				Array.Fill(_cells, ' ', _row * width + _col, Math.Min(P(0, 1), width - _col));
				break;
		}
	}

	void Write(char c)
	{
		switch (c)
		{
			case '\r':
				(_col, _wrap) = (0, false);
				return;
			case '\n':
				(_row, _col, _wrap) = (Math.Min(height - 1, _row + 1), 0, false);
				return;
			case < ' ':
				return;
		}
		if (_wrap)
			(_row, _col, _wrap) = (Math.Min(height - 1, _row + 1), 0, false);
		_cells[_row * width + _col] = c;
		if (_col == width - 1)
			_wrap = true;
		else
			_col++;
	}

	public override string ToString()
	{
		var text = new StringBuilder();
		for (var row = 0; row < height; row++)
			text.Append(_cells, row * width, width).Append('\n');
		return text.ToString();
	}
}
