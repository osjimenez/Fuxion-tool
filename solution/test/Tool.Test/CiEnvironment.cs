using System;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

namespace Fuxion.Tool.Test;

/// <summary>Sets the CI variables the versioning reads (<c>CI</c>, <c>GITHUB_ACTIONS</c>) and restores them.</summary>
public sealed class CiEnvironment : IDisposable
{
	readonly string? _ci = Environment.GetEnvironmentVariable("CI");
	readonly string? _githubActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");

	public CiEnvironment(string? ci = null, string? githubActions = null)
	{
		Environment.SetEnvironmentVariable("CI", ci);
		Environment.SetEnvironmentVariable("GITHUB_ACTIONS", githubActions);
	}

	/// <summary>Outside CI: both variables unset.</summary>
	public static CiEnvironment Local() => new();

	public void Dispose()
	{
		Environment.SetEnvironmentVariable("CI", _ci);
		Environment.SetEnvironmentVariable("GITHUB_ACTIONS", _githubActions);
	}
}

/// <summary>
/// The tests that change the process: its environment variables (CI, GITHUB_ACTIONS, FX_CACHE, FX_WORKTREES, FX_LANG) or
/// its language (fx in Spanish sets the culture of every thread). They go alone, after the rest, which run in parallel,
/// a class at a time (I-44).
/// </summary>
[CollectionDefinition(nameof(ProcessCollection), DisableParallelization = true)]
public sealed class ProcessCollection;
