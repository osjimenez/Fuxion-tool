using System;
using System.IO;
using Fuxion.Tools.Core.Versioning;
using Xunit;

namespace Fuxion.Tools.Test;

/// <summary>From the versioning inputs to the MSBuild props file (plan K, K2.1).</summary>
public sealed class VersionPropsTest : IDisposable
{
	readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fx-test-" + Guid.NewGuid().ToString("N")[..12])).FullName;

	public void Dispose() => Directory.Delete(_dir, true);

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

	[Fact(DisplayName = "the fingerprint is stable (value written by Fuxion-plus on 06-oct)")]
	public void Fingerprint()
		=> Assert.Equal("39A780C3DBC1A41CD4E34D368D78EE248F74BCE1516797DFABD2F4533B0792BA",
			VersionFingerprint.Compute(new("10.0.31-feature.net11.0", "")));

	[Fact(DisplayName = "the props file")]
	public void PropsFile()
	{
		var path = Path.Combine(_dir, "sub", "version.g.props");
		VersionPropsWriter.WriteMsBuildPropsFile(path, DefaultVersionPropsProvider.FromInputs(new("1.2.1-feature.x.2", "+local")));
		var expected = """
			<Project>
				<PropertyGroup>
					<FuxionToolsVersioningFingerprint>{0}</FuxionToolsVersioningFingerprint>
					<Version>1.2.1-feature.x.2</Version>
					<PackageVersion>1.2.1-feature.x.2</PackageVersion>
					<AssemblyVersion>1.2.1.0</AssemblyVersion>
					<FileVersion>1.2.1.0</FileVersion>
					<InformationalVersion>1.2.1-feature.x.2+local</InformationalVersion>
				</PropertyGroup>
			</Project>
			""";
		Assert.Equal(
			Normalize(string.Format(expected, VersionFingerprint.Compute(new("1.2.1-feature.x.2", "+local")))),
			Normalize(File.ReadAllText(path)));
	}

	[Fact(DisplayName = "values are escaped for XML")]
	public void PropsFile_Escaped()
	{
		var path = Path.Combine(_dir, "version.g.props");
		VersionPropsWriter.WriteMsBuildPropsFile(path, DefaultVersionPropsProvider.FromInputs(new("1.0.0", "+a&b<c>")));
		Assert.Contains("<InformationalVersion>1.0.0+a&amp;b&lt;c&gt;</InformationalVersion>", File.ReadAllText(path));
	}

	[Fact(DisplayName = "written only when the fingerprint changes")]
	public void Orchestrator_WritesOnlyOnChange()
	{
		var path = Path.Combine(_dir, "version.g.props");
		Assert.True(VersioningOrchestrator.EnsurePropsUpToDate(path, new("1.0.0", "+local")));
		Assert.False(VersioningOrchestrator.EnsurePropsUpToDate(path, new("1.0.0", "+local")));
		Assert.True(VersioningOrchestrator.EnsurePropsUpToDate(path, new("1.0.1", "+local")));
		Assert.Equal(VersionFingerprint.Compute(new("1.0.1", "+local")), PropsFileReader.TryReadFingerprint(path));
	}

	[Fact(DisplayName = "fingerprint of a missing or broken props file: none")]
	public void Reader_MissingOrBroken()
	{
		Assert.Null(PropsFileReader.TryReadFingerprint(Path.Combine(_dir, "missing.props")));
		var broken = Path.Combine(_dir, "broken.props");
		File.WriteAllText(broken, "<Project>");
		Assert.Null(PropsFileReader.TryReadFingerprint(broken));
	}

	static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd();
}
