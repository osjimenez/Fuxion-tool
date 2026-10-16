using System;
using System.IO;
using System.Threading;
using Fuxion.Tool.Cli;

namespace Fuxion.Tool.Test;

/// <summary>
/// fx run in process, as the tests run it. XenoAtom.Terminal.UI has one dispatcher per process (<c>Dispatcher.Current</c>):
/// while an app of it runs on its thread (<c>fx ui</c>, in <see cref="UiTree"/>), no other thread can build a visual, as
/// the tables of the output for people. The classes run in parallel (I-44), so the command line shares this gate and
/// the app of <see cref="UiTree"/> takes it alone.
/// </summary>
static class InProcess
{
	static readonly ReaderWriterLockSlim Gate = new(LockRecursionPolicy.SupportsRecursion);

	/// <summary><see cref="FxApp.Run"/>, never while an app of XenoAtom runs.</summary>
	public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string currentDirectory)
	{
		Gate.EnterReadLock();
		try
		{
			return FxApp.Run(args, stdout, stderr, currentDirectory);
		}
		finally
		{
			Gate.ExitReadLock();
		}
	}

	/// <summary>The gate alone, for an app of XenoAtom and the visuals it builds, until it is disposed (on the same thread).</summary>
	public static IDisposable Alone()
	{
		Gate.EnterWriteLock();
		return new Exit();
	}

	sealed class Exit : IDisposable
	{
		public void Dispose() => Gate.ExitWriteLock();
	}
}
