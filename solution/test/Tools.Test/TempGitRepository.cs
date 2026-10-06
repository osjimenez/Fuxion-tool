using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Fuxion.Tools.Test;

/// <summary>
/// A throwaway git repository under %TEMP%, built with the git executable. It is isolated from the user's git
/// configuration (no global or system config: no signing, no hooks, no default branch surprises), and every commit
/// gets a later timestamp than the previous one, so the date order of the history is deterministic.
/// </summary>
public sealed class TempGitRepository : IDisposable
{
	readonly string _emptyConfig;
	DateTimeOffset _clock = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

	/// <param name="init">Run <c>git init</c> (otherwise it is just an empty folder).</param>
	/// <param name="path">Where (a new folder under %TEMP% by default); it is deleted on dispose.</param>
	public TempGitRepository(bool init = true, string? path = null)
	{
		Path = path ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fx-test-" + Guid.NewGuid().ToString("N")[..12]);
		Directory.CreateDirectory(Path);
		_emptyConfig = Path + ".gitconfig";
		File.WriteAllText(_emptyConfig, "");
		if (init)
			Git("init", "--quiet", "--initial-branch=main");
	}

	public string Path { get; }

	public string Commit(string message = "commit")
	{
		_clock = _clock.AddMinutes(1);
		Git("commit", "--quiet", "--allow-empty", "-m", message);
		return Head();
	}

	/// <summary>Writes a file (relative to the repository) without committing it.</summary>
	public string WriteFile(string relativePath, string content)
	{
		var path = System.IO.Path.Combine(Path, relativePath);
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
		return path;
	}

	/// <summary>Commits everything in the working tree.</summary>
	public string CommitAll(string message = "commit")
	{
		Git("add", "-A");
		return Commit(message);
	}

	/// <summary>Committer date of the last commit (the clock of this repository).</summary>
	public DateTimeOffset LastCommitDate => _clock;

	public string Head() => Git("rev-parse", "HEAD").Trim();

	public void Branch(string name) => Git("checkout", "--quiet", "-b", name);

	public void Checkout(string nameOrSha) => Git("checkout", "--quiet", nameOrSha);

	public string Merge(string branch)
	{
		_clock = _clock.AddMinutes(1);
		Git("merge", "--quiet", "--no-ff", "--no-edit", branch);
		return Head();
	}

	public void Tag(string name, string? commit = null, bool annotated = false)
	{
		var args = new List<string> { "tag" };
		if (annotated)
			args.AddRange(["-a", "-m", name]);
		args.Add(name);
		if (commit is not null)
			args.Add(commit);
		Git([.. args]);
	}

	public void SetOrigin(string url) => Git("remote", "add", "origin", url);

	public string Git(params string[] args)
	{
		var psi = new ProcessStartInfo("git")
		{
			WorkingDirectory = Path,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		foreach (var arg in args)
			psi.ArgumentList.Add(arg);
		psi.Environment["GIT_CONFIG_GLOBAL"] = _emptyConfig;
		psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
		psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
		psi.Environment["GIT_AUTHOR_NAME"] = psi.Environment["GIT_COMMITTER_NAME"] = "Fuxion Test";
		psi.Environment["GIT_AUTHOR_EMAIL"] = psi.Environment["GIT_COMMITTER_EMAIL"] = "test@fuxion.dev";
		psi.Environment["GIT_AUTHOR_DATE"] = psi.Environment["GIT_COMMITTER_DATE"] = _clock.ToString("yyyy-MM-ddTHH:mm:ssK");

		using var process = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
		{
			process.Kill(true);
			throw new TimeoutException($"git {string.Join(' ', args)} timed out");
		}
		if (process.ExitCode != 0)
			throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({process.ExitCode}): {stderr.Result}");
		return stdout.Result;
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(Path))
			{
				// git writes its objects read-only
				foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
					File.SetAttributes(file, FileAttributes.Normal);
				Directory.Delete(Path, true);
			}
			File.Delete(_emptyConfig);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}
}
