using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Fuxion.Tools.Cli;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using Xunit;

namespace Fuxion.Tools.Test;

/// <summary>The questions of fx (plan O, O3.3), on an in-memory terminal: keys in, the answer out.</summary>
[Collection(nameof(TerminalCollection))]
public sealed class AskTest
{
	static readonly string[] Repos = ["lab", "docs", "archive"];

	static TerminalKeyEvent Key(TerminalKey key) => new() { Key = key };

	static readonly TerminalKeyEvent CtrlC = new() { Key = TerminalKey.Unknown, Char = TerminalChar.CtrlC, Modifiers = TerminalModifiers.Ctrl };

	/// <summary>Runs the question, waits for it to be on screen, presses the keys and returns the answer (or throws).</summary>
	static (T Answer, string Err) Answer<T>(Func<FxConsole, ITerminalBackend, T> ask, params TerminalKeyEvent[] keys)
	{
		var stderr = new StringWriter();
		var console = new FxConsole(new StringWriter(), stderr, new GlobalSettings(Path.GetTempPath(), OutputFormat.Human, Verbose: false, Plain: true));
		var backend = new InMemoryTerminalBackend(new TerminalSize(80, 20));
		var task = Task.Run(() => ask(console, backend), TestContext.Current.CancellationToken);
		var clock = Stopwatch.StartNew();
		while (!backend.GetOutText().Contains("Repo", StringComparison.Ordinal) && clock.Elapsed < TimeSpan.FromSeconds(10))
			Thread.Sleep(20);
		Thread.Sleep(100);
		foreach (var key in keys)
			backend.PushEvent(key);
		Assert.True(((IAsyncResult)task).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10)), "the question did not end");
		return (task.GetAwaiter().GetResult(), stderr.ToString());
	}

	[Fact(DisplayName = "one: ↑↓ and Enter; the answer stays as a line")]
	public void One()
	{
		var (answer, err) = Answer((c, b) => Ask.One(c, "Repo", Repos, r => r, TestContext.Current.CancellationToken, b), Key(TerminalKey.Down), Key(TerminalKey.Enter));
		Assert.Equal("docs", answer);
		Assert.Contains("? Repo docs", err);
	}

	[Fact(DisplayName = "many: Space marks, Enter confirms")]
	public void Many()
	{
		var (answer, err) = Answer((c, b) => Ask.Many(c, "Repos", Repos, r => r, TestContext.Current.CancellationToken, b),
			Key(TerminalKey.Space), Key(TerminalKey.Down), Key(TerminalKey.Down), Key(TerminalKey.Space), Key(TerminalKey.Enter));
		Assert.Equal(["lab", "archive"], answer);
		Assert.Contains("? Repos lab, archive", err);
	}

	[Fact(DisplayName = "many: Enter with nothing marked takes the highlighted one")]
	public void Many_Highlighted()
	{
		var (answer, _) = Answer((c, b) => Ask.Many(c, "Repos", Repos, r => r, TestContext.Current.CancellationToken, b), Key(TerminalKey.Down), Key(TerminalKey.Enter));
		Assert.Equal(["docs"], answer);
	}

	[Fact(DisplayName = "Esc and Ctrl+C cancel")]
	public void Cancel()
	{
		Assert.Throws<OperationCanceledException>(() => Answer((c, b) => Ask.One(c, "Repo", Repos, r => r, TestContext.Current.CancellationToken, b), Key(TerminalKey.Escape)));
		Assert.Throws<OperationCanceledException>(() => Answer((c, b) => Ask.Many(c, "Repos", Repos, r => r, TestContext.Current.CancellationToken, b), CtrlC));
	}

	[Fact(DisplayName = "Ctrl+C as a signal: the token of the command cancels the question")]
	public void Cancel_Token()
	{
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		Assert.Throws<OperationCanceledException>(() => Answer((c, b) =>
		{
			cancel.CancelAfter(TimeSpan.FromMilliseconds(300));
			return Ask.One(c, "Repo", Repos, r => r, cancel.Token, b);
		}));
	}
}

/// <summary>Tests that open the global terminal of XenoAtom: alone, never at the same time as others.</summary>
[CollectionDefinition(nameof(TerminalCollection), DisableParallelization = true)]
public sealed class TerminalCollection;
