namespace Fuxion.Tools.Core.Versioning;

public sealed record VersionProps(
	string Version,
	string PackageVersion,
	string AssemblyVersion,
	string FileVersion,
	string InformationalVersion,
	string Fingerprint
);
