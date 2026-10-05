using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Fuxion.Tools.Core.Processes;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Elapsed);

public sealed class ProcessException(string message, string command, int? exitCode, string standardError, Exception? inner = null)
	: Exception(message, inner)
{
	public string Command { get; } = command;
	/// <summary><see langword="null"/> when the process did not start or timed out.</summary>
	public int? ExitCode { get; } = exitCode;
	public string StandardError { get; } = standardError;
}

/// <summary>
/// The only way the tool launches processes (design §10.4): explicit working directory, a timeout always, UTF-8 output.
/// A non-zero exit code is not an error here; the caller decides what it means.
/// </summary>
public static class ProcessRunner
{
	public static ProcessResult Run(
		string fileName,
		string workingDirectory,
		IEnumerable<string> arguments,
		TimeSpan timeout,
		IReadOnlyDictionary<string, string?>? environment = null)
	{
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
		Process process;
		try
		{
			process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
		}
		catch (Exception ex)
		{
			throw new ProcessException($"Could not start '{command}' in '{workingDirectory}': {ex.Message}", command, null, "", ex);
		}

		using (process)
		{
			process.StandardInput.Close();
			var stdout = process.StandardOutput.ReadToEndAsync();
			var stderr = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit(timeout))
			{
				try { process.Kill(entireProcessTree: true); }
				catch (InvalidOperationException) { }
				throw new ProcessException($"'{command}' in '{workingDirectory}' timed out after {timeout.TotalSeconds:0.#} s.", command, null, "");
			}
			process.WaitForExit();
			return new(process.ExitCode, stdout.Result, stderr.Result, stopwatch.Elapsed);
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
