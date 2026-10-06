using System;
using Microsoft.Build.Framework;

namespace Fuxion.Tools.Sdk;

/// <summary>
/// Values computed once per build and shared by every project and target framework of that build (the version of a
/// repo is the same for all its projects): MSBuild keeps them until the build ends, so a new build sees new commits
/// and tags. If the host does not offer it, the value is just computed again.
/// </summary>
static class BuildCache
{
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
