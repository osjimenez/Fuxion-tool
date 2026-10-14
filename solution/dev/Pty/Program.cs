// A harness to try fx in a real terminal without a person (Windows only; plan O, O3.2): it runs a command in a
// pseudoconsole (ConPTY), as a terminal would, types keys once some text appears or some time passes, and prints what
// the terminal received, with the escapes made visible, or pictures of the screen.
//
//   Pty <folder> <never|ms|text:text> <command line>
//
// - The second argument says when to type: never, after some milliseconds, or once some text has appeared.
// - PTY_KEYS, what to type (Ctrl+C by default), with C# escapes (\r, \u001b, \u0003…), in parts split by '~': each
//   part is typed and followed by a wait of 200 ms; '?text' waits (10 s at most) until the terminal shows the text;
//   '!name' takes a picture of the screen, printed at the end instead of the escapes.
// - PTY_SIZE, the size of the terminal (100x30 by default).
// The variables that make fx think it is not in front of a person (CI, agents, NO_COLOR…) are removed.
//
//   PTY_SIZE=160x45 PTY_KEYS='?problems~!doctor~\u001bOQ~!f2~\u0011' Pty <workspace> 'text:fx ui' 'fx.exe ui --lang en'
//
// The pictures read the screen with the interpreter of the tests (TestScreen): characters are one cell each, so a
// glyph the terminal takes as wide shifts its line.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Fuxion.Tools.Test;
using Microsoft.Win32.SafeHandles;

var sizeText = Environment.GetEnvironmentVariable("PTY_SIZE") ?? "100x30";
var W = int.Parse(sizeText.Split('x')[0]);
var H = int.Parse(sizeText.Split('x')[1]);
var shots = new List<string>();
var workdir = args[0];
var trigger = args[1];
var commandLine = string.Join(' ', args.Skip(2));
foreach (var v in new[] { "CLAUDECODE", "AI_AGENT", "AGENT", "CURSOR_AGENT", "GEMINI_CLI", "CODEX_SANDBOX", "CI", "NO_COLOR", "TERM", "COLUMNS", "FORCE_COLOR" })
	Environment.SetEnvironmentVariable(v, null);

// Git Bash starts processes ignoring Ctrl+C, and children inherit it: a real shell does not
N.SetConsoleCtrlHandler(IntPtr.Zero, false);
N.CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0);
N.CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0);
if (N.CreatePseudoConsole(new N.COORD { X = (short)W, Y = (short)H }, inRead, outWrite, 0, out var hpc) != 0) throw new Exception("CreatePseudoConsole");
inRead.Dispose(); outWrite.Dispose();

var size = IntPtr.Zero;
N.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
var list = Marshal.AllocHGlobal(size);
if (!N.InitializeProcThreadAttributeList(list, 1, 0, ref size)) throw new Exception("Init");
if (!N.UpdateProcThreadAttribute(list, 0, (IntPtr)0x00020016, hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Exception("Update");
var si = new N.STARTUPINFOEX();
si.StartupInfo.cb = Marshal.SizeOf<N.STARTUPINFOEX>();
si.StartupInfo.dwFlags = 0x100; // STARTF_USESTDHANDLES, with no handles: the pseudoconsole's, not ours
si.lpAttributeList = list;
if (!N.CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, 0x00080000, IntPtr.Zero, workdir, ref si, out var pi))
	throw new Exception("CreateProcess " + Marshal.GetLastWin32Error());

var output = new StringBuilder();
var reader = new Thread(() =>
{
	using var stream = new FileStream(outRead, FileAccess.Read);
	var buffer = new byte[4096];
	var decoder = Encoding.UTF8.GetDecoder();
	var chars = new char[8192];
	int n;
	try
	{
		while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
		{
			var c = decoder.GetChars(buffer, 0, n, chars, 0);
			lock (output) output.Append(chars, 0, c);
		}
	}
	catch (IOException) { }
}) { IsBackground = true };
reader.Start();

var input = new FileStream(inWrite, FileAccess.Write);
// answer the cursor position request conhost may send at start (ESC[6n)
var started = DateTime.UtcNow;
var sent = false;
var answered = 0;
while (N.WaitForSingleObject(pi.hProcess, 20) != 0)
{
	string text;
	lock (output) text = output.ToString();
	var requests = CountOf(text, "\x1b[6n");
	while (answered < requests) { input.Write("\x1b[1;1R"u8); input.Flush(); answered++; }
	if (!sent && trigger != "never")
	{
		var fire = trigger.StartsWith("text:") ? text.Contains(trigger[5..]) : (DateTime.UtcNow - started).TotalMilliseconds >= int.Parse(trigger);
		if (fire)
		{
			// PTY_KEYS: what to type, with C# escapes (\r, \u001b, \x03) and ~ for a wait of 200 ms; Ctrl+C by default
			var keys = Environment.GetEnvironmentVariable("PTY_KEYS") is { Length: > 0 } k ? System.Text.RegularExpressions.Regex.Unescape(k) : "\u0003";
			foreach (var part in keys.Split('~'))
			{
				// ?text: wait (10 s at most) until the terminal shows it, for a chain of questions
				if (part.StartsWith('?'))
				{
					var until = DateTime.UtcNow.AddSeconds(10);
					while (DateTime.UtcNow < until) { lock (output) if (output.ToString().Contains(part[1..])) break; Thread.Sleep(20); }
				}
				// !name: a picture of the screen as it is now
				else if (part.StartsWith('!'))
				{
					Thread.Sleep(300);
					var screen = new TestScreen(W, H);
					lock (output) screen.Apply(output.ToString());
					shots.Add($"=== {part[1..]} at {(DateTime.UtcNow - started).TotalMilliseconds:0} ms\n" + string.Join("\n", screen.ToString().Split('\n').Select(l => l.TrimEnd())));
					continue;
				}
				else if (part.Length > 0) { input.Write(Encoding.UTF8.GetBytes(part)); input.Flush(); }
				Thread.Sleep(200);
			}
			sent = true;
			lock (output) output.Append($"\n<<<KEYS at {(DateTime.UtcNow - started).TotalMilliseconds:0} ms>>>\n");
		}
	}
	if ((DateTime.UtcNow - started).TotalSeconds > 60) { Console.WriteLine("TIMEOUT"); break; }
}
N.GetExitCodeProcess(pi.hProcess, out var code);
var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
Thread.Sleep(300);
N.ClosePseudoConsole(hpc);
reader.Join(2000);
string all;
lock (output) all = output.ToString();
Console.OutputEncoding = Encoding.UTF8;
if (shots.Count > 0) foreach (var shot in shots) Console.WriteLine(shot); else Console.WriteLine(Visible(all));
Console.WriteLine($"=== exit code {code} (0x{code:X8}) after {elapsed:0} ms");

static int CountOf(string s, string what) { int c = 0, i = 0; while ((i = s.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { c++; i += what.Length; } return c; }
static string Visible(string s)
{
	var b = new StringBuilder();
	foreach (var ch in s)
		b.Append(ch switch { '\x1b' => "␛", '\r' => "␍", '\n' => "␊\n", '\a' => "␇", '\b' => "␈", _ when ch < ' ' => $"^{(char)(ch + 64)}", _ => ch.ToString() });
	return b.ToString();
}

static class N
{
	[DllImport("kernel32.dll", SetLastError = true)] public static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);
	[DllImport("kernel32.dll", SetLastError = true)] public static extern void ClosePseudoConsole(IntPtr hPC);
	[DllImport("kernel32.dll", SetLastError = true)] public static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);
	[DllImport("kernel32.dll", SetLastError = true)] public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);
	[DllImport("kernel32.dll", SetLastError = true)] public static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);
	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
	[DllImport("kernel32.dll")] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
	[DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
	[DllImport("kernel32.dll")] public static extern bool GetExitCodeProcess(IntPtr h, out uint code);
	[StructLayout(LayoutKind.Sequential)] public struct COORD { public short X; public short Y; }
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	public struct STARTUPINFO { public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
	[StructLayout(LayoutKind.Sequential)] public struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
	[StructLayout(LayoutKind.Sequential)] public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
}
