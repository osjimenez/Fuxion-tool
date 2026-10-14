using System.CommandLine;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Fuxion.Tools.Cli;

/// <summary>
/// What <c>fx describe</c> says of a command besides its arguments and options (plan O, decision 12): the schema of its
/// JSON (<c>--output json</c>), whether it changes something (files, clones, branches, tags) and whether it may use the
/// network. Whether it takes <c>--dry-run</c> comes from its options.
/// </summary>
public sealed record FxCommandInfo(string? Schema, bool Writes = false, bool Network = false);

public static class FxCommandInfoExtensions
{
	static readonly ConditionalWeakTable<Command, FxCommandInfo> Infos = new();

	/// <summary>Says what <paramref name="command"/> outputs and does, for <c>fx describe</c>.</summary>
	public static T Describe<T>(this T command, string? schema, bool writes = false, bool network = false) where T : Command
	{
		Infos.AddOrUpdate(command, new(schema, writes, network));
		return command;
	}

	public static FxCommandInfo? Info(this Command command) => Infos.TryGetValue(command, out var info) ? info : null;

	public static bool HasDryRun(this Command command) => command.Options.Any(o => o.Name == "--dry-run");
}
