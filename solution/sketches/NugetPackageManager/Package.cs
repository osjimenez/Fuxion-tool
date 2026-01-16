using System.ComponentModel.DataAnnotations;
using Fuxion;

namespace NugetPackageManager;

using System;
using System.Collections.Generic;
using System.Text;

//internal record Package(string Id, (SemanticVersion Limit, SemanticVersion[] Skip) ListConfiguration, (SemanticVersion Limit, SemanticVersion[] Skip) DeprecationConfiguration)
internal record Package(string Id, Func<SemanticVersion, bool> ListMatch, Func<SemanticVersion, bool> DeprecationMatch)
{
	public List<PackageVersion> Versions { get; } = [];
	public override string ToString() => Id;
}

internal record PackageVersion(Package Package, SemanticVersion Version, bool Listed, bool Deprecated, NugetAction Action);

[Flags]
internal enum NugetAction
{
	None = 0,
	Error = 1,
	Unlist = 2,
	List = 4,
	Deprecate = 8,
	Undeprecate = 16,
}