using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;
using Fuxion.Tool.Core.Results;

namespace Fuxion.Tool.Core.Processes;

/// <summary>How a process ended: its exit code (data: the caller decides what it means), its output and its error output, and how long it took.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Elapsed)
{
	/// <summary>
	/// A run as its result, for who reports what failed and goes on: one that did not run is a failed one (exit code
	/// -1), with the message of its error, in English, as its error output.
	/// </summary>
	public static ProcessResult Of(Response<ProcessResult> run)
		=> run is ProcessResult result ? result : new(-1, "", string.Join(" ", run.Diagnostics.Select(d => d.Message.English)), TimeSpan.Zero);
}

/// <summary>
/// The only way the tool launches processes (design §10.4): explicit working directory, a timeout always, UTF-8 output.
/// A non-zero exit code is not an error here: it is data, and the caller decides what it means. A process that does not
/// start is an error (<see cref="HttpStatusCode.ServiceUnavailable"/>), and one that does not end in time too
/// (<see cref="HttpStatusCode.RequestTimeout"/>). A cancellation kills the process (and its children) at once and throws
/// <see cref="OperationCanceledException"/>.
/// </summary>
public static class ProcessRunner
{
	public const string NotStarted = "process.not-started";
	public const string TimedOut = "process.timed-out";

	public static Response<ProcessResult> Run(
		string fileName,
		string workingDirectory,
		IEnumerable<string> arguments,
		TimeSpan timeout,
		IReadOnlyDictionary<string, string?>? environment = null,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var psi = new ProcessStartInfo(fileName)
		{
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};
		foreach (var argument in arguments)
			psi.ArgumentList.Add(argument);
		if (environment is not null)
			foreach (var (name, value) in environment)
				if (value is null)
					psi.Environment.Remove(name);
				else
					psi.Environment[name] = value;

		var command = Describe(fileName, psi.ArgumentList);
		var stopwatch = Stopwatch.StartNew();
		Process? process;
		try
		{
			process = Process.Start(psi);
		}
		catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
		{
			return NotStartedError(command, workingDirectory, ex.Message, ex);
		}
		if (process is null)
			return NotStartedError(command, workingDirectory, "no process", null);


		using (process)
		using (cancellationToken.Register(() => Kill(process)))
		{
			process.StandardInput.Close();
			var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
			var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
			if (!process.WaitForExit(timeout))
			{
				Kill(process);
				cancellationToken.ThrowIfCancellationRequested();
				return FxErrors.Of(HttpStatusCode.RequestTimeout,
					FxDiagnostic.Error(TimedOut, new(TimedOut, command, workingDirectory, timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture))));
			}
			process.WaitForExit();
			cancellationToken.ThrowIfCancellationRequested();
			return new ProcessResult(process.ExitCode, stdout.Result, stderr.Result, stopwatch.Elapsed);
		}
	}

	static Error NotStartedError(string command, string workingDirectory, string reason, Exception? exception)
		=> FxErrors.Of(HttpStatusCode.ServiceUnavailable, FxDiagnostic.Error(NotStarted, new(NotStarted, command, workingDirectory, reason)), exception);

	static void Kill(Process process)
	{
		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
		{
			// it has just exited
		}
	}

	static string Describe(string fileName, IEnumerable<string> arguments)
	{
		var b = new StringBuilder(fileName);
		foreach (var argument in arguments)
			b.Append(' ').Append(argument.Contains(' ') ? $"\"{argument}\"" : argument);
		return b.ToString();
	}
}
