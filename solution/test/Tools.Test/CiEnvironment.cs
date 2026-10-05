using System;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

// The versioning reads CI variables from the process environment, which is shared by every test: no parallel runs.
[assembly: Parallelization(Mode = ParallelMode.None)]

namespace Fuxion.Tools.Test;

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
