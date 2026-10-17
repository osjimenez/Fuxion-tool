using System;

namespace Fuxion.Tool.Core.Layout;

/// <summary>
/// The version of the fx that runs, from its informational version: <c>0.7.4+62d65aa…</c> made by the CI and published,
/// <c>0.7.6+local.62d65aa…</c> built from its sources. fx and <see cref="SdkPackage"/> are released together, with the
/// same version (plan S).
/// </summary>
public static class FxToolVersion
{
	/// <summary>The SDK of fx, the one a tree pins in the <c>sdks</c> of its <c>dotnet.yaml</c>.</summary>
	public const string SdkPackage = "Fuxion.Tool.Sdk";

	/// <summary>The version without its metadata: <c>0.7.4</c>.</summary>
	public static string Core(string informational) => informational.Split('+')[0];

	/// <summary>
	/// Built from its sources, not by the CI (its metadata starts with <c>local</c>): its version comes from the commits
	/// of its branch, and was never published.
	/// </summary>
	public static bool IsLocal(string informational)
		=> informational.Split('+', 2) is [_, var metadata] && metadata.StartsWith("local", StringComparison.Ordinal);

	/// <summary>
	/// How <paramref name="version"/> compares with <paramref name="other"/>, by major, minor and patch (what comes after
	/// <c>-</c> or <c>+</c> does not count); null if either is not a version.
	/// </summary>
	public static int? Compare(string version, string other)
		=> FxYamlReader.Parse(version) is { } a && FxYamlReader.Parse(other) is { } b ? a.CompareTo(b) : null;
}
