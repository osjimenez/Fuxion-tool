namespace Fuxion.Tools.Core.Versioning;

public sealed record VersioningInputs(
	string BaseVersion,
	string InformationalSuffix
)
{
	public static VersioningInputs Default => new("0.1.0", "+local");
}
