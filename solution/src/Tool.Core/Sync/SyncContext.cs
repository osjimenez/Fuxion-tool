using System.Threading;
using Fuxion.Tool.Core.Results;

namespace Fuxion.Tool.Core.Sync;

/// <summary>What <c>fx sync</c> or <c>fx doctor</c> was asked to do.</summary>
/// <param name="Module">Only this module (<c>workspace</c>, <c>dotnet</c>); all of them, without it.</param>
/// <param name="FxVersion">
/// The version of fx that runs (its informational version, <see cref="Layout.FxToolVersion"/>), for <c>requires</c>
/// (decision 4) and for the <c>Fuxion.Tool.Sdk</c> the tree pins (plan S); none: not checked.
/// </param>
/// <param name="OnlyHere">Only the root where it runs, not what is mounted below it (<c>--only</c>).</param>
/// <param name="ParentRef">
/// The branch of the parent to read, from its remote (<c>--parent-ref</c>): to try what another branch of it gives,
/// without versioning that choice anywhere.
/// </param>
/// <param name="Skill">The skill of fx for coding agents (its <c>SKILL.md</c>, made by the CLI from its commands): written in every root of fx (plan O, O5.1). Without it, nothing is written.</param>
public sealed record SyncOptions(bool DryRun = false, bool Offline = false, bool Adopt = false, string? Module = null, string? FxVersion = null,
	bool OnlyHere = false, string? ParentRef = null, string? Skill = null);

/// <summary>
/// One run of <c>fx sync</c> or <c>fx doctor</c>, as every module sees it: where it started (paths are shown from
/// there), whether it writes, and its progress channel.
/// </summary>
public sealed record SyncContext(string DisplayRoot, bool Write, bool Adopt, bool Doctor, bool Offline, IFxProgress? Progress, CancellationToken CancellationToken)
{
	/// <summary>Whether the run is limited to another module than <paramref name="module"/>.</summary>
	public string? Only { get; init; }

	/// <summary>Only the root where it runs (<c>--only</c>).</summary>
	public bool OnlyHere { get; init; }

	/// <summary>The skill of fx, to write in every root of fx (plan O, O5.1); null, nothing.</summary>
	public string? Skill { get; init; }

	public bool Runs(string module) => Only is null || Only == module;
}
