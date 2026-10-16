namespace Fuxion.Tool.Core.Versioning;

public static class DefaultVersionPropsProvider
{
	public static VersionProps FromInputs(VersioningInputs inputs)
	{
		var version = inputs.BaseVersion;
		var informational = version + inputs.InformationalSuffix;
		var assemblyBase = version;
		if (Fuxion.SemanticVersion.TryParse(version, out var semver))
			assemblyBase = $"{semver.Major}.{semver.Minor}.{semver.Patch}";

		return new(
			Version: version,
			PackageVersion: version,
			AssemblyVersion: assemblyBase + ".0",
			FileVersion: assemblyBase + ".0",
			InformationalVersion: informational
		);
	}

	public static VersionProps GetDefaultLocal() => FromInputs(VersioningInputs.Default);
}
