using Fuxion.Tools.Core.Versioning;
using Xunit;

namespace Fuxion.Tools.Test;

/// <summary>From the versioning inputs to the versions MSBuild gets (plan K, K2.1).</summary>
public sealed class VersionPropsTest
{
	[Fact(DisplayName = "props from a prerelease version")]
	public void FromPrerelease()
	{
		var props = DefaultVersionPropsProvider.FromInputs(new("1.2.1-feature.x.2", "+local"));
		Assert.Equal("1.2.1-feature.x.2", props.Version);
		Assert.Equal("1.2.1-feature.x.2", props.PackageVersion);
		Assert.Equal("1.2.1.0", props.AssemblyVersion);
		Assert.Equal("1.2.1.0", props.FileVersion);
		Assert.Equal("1.2.1-feature.x.2+local", props.InformationalVersion);
	}

	[Fact(DisplayName = "a version that is not SemVer is used as is")]
	public void NotSemVer()
	{
		var props = DefaultVersionPropsProvider.FromInputs(new("abc", ""));
		Assert.Equal("abc.0", props.AssemblyVersion);
		Assert.Equal("abc", props.InformationalVersion);
	}
}
