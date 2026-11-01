using System;
using Microsoft.Build.Framework;

namespace Fuxion.Tool.Sdk;

/// <summary>
/// Values computed once per build and shared by every project and target framework of that build (the version of a
/// repo is the same for all its projects): MSBuild keeps them until the build ends, so a new build sees new commits
/// and tags. If the host does not offer it, the value is just computed again.
/// </summary>
static class BuildCache
{
	/// <summary>The value of <paramref name="key"/> for this build, if there is one.</summary>
	public static bool TryGet<T>(IBuildEngine engine, string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? value) where T : class
	{
		value = engine is IBuildEngine4 engine4 ? engine4.GetRegisteredTaskObject(key, RegisteredTaskObjectLifetime.Build) as T : null;
		return value is not null;
	}

	/// <summary>Keeps <paramref name="value"/> as the one of <paramref name="key"/> until the build ends (if the host offers it).</summary>
	public static void Add<T>(IBuildEngine engine, string key, T value) where T : class
	{
		if (engine is IBuildEngine4 engine4)
			engine4.RegisterTaskObject(key, value, RegisteredTaskObjectLifetime.Build, allowEarlyCollection: false);
	}

	public static T GetOrAdd<T>(IBuildEngine engine, string key, Func<T> create, out bool cached) where T : class
	{
		cached = false;
		if (engine is not IBuildEngine4 engine4)
			return create();
		if (engine4.GetRegisteredTaskObject(key, RegisteredTaskObjectLifetime.Build) is T value)
		{
			cached = true;
			return value;
		}
		var created = create();
		engine4.RegisterTaskObject(key, created, RegisteredTaskObjectLifetime.Build, allowEarlyCollection: false);
		return created;
	}
}
