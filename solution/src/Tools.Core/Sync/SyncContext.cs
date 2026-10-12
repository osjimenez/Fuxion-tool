using System.Threading;
using Fuxion.Tools.Core.Results;

namespace Fuxion.Tools.Core.Sync;

/// <summary>What <c>fx sync</c> or <c>fx doctor</c> was asked to do.</summary>
/// <param name="Module">Only this module (<c>workspace</c>, <c>dotnet</c>); all of them, without it.</param>
/// <param name="FxVersion">The version of fx that runs, for <c>requires</c> (decision 4); none: not checked.</param>
public sealed record SyncOptions(bool DryRun = false, bool Offline = false, bool Adopt = false, string? Module = null, string? FxVersion = null);

/// <summary>
/// One run of <c>fx sync</c> or <c>fx doctor</c>, as every module sees it: where it started (paths are shown from
/// there), whether it writes, and its progress channel.
/// </summary>
public sealed record SyncContext(string DisplayRoot, bool Write, bool Adopt, bool Doctor, bool Offline, IFxProgress? Progress, CancellationToken CancellationToken)
{
	/// <summary>Whether the run is limited to another module than <paramref name="module"/>.</summary>
	public string? Only { get; init; }

	public bool Runs(string module) => Only is null || Only == module;
}
