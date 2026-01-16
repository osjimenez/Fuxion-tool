using System;

namespace Fuxion.Tools.Core.Versioning;

public static class VersioningOrchestrator
{
	public static bool EnsurePropsUpToDate(string outputPath, VersioningInputs inputs)
	{
		var desiredFingerprint = VersionFingerprint.Compute(inputs);
		var existingFingerprint = PropsFileReader.TryReadFingerprint(outputPath);

		if (string.Equals(existingFingerprint, desiredFingerprint, StringComparison.OrdinalIgnoreCase))
			return false;

		var props = DefaultVersionPropsProvider.FromInputs(inputs);
		VersionPropsWriter.WriteMsBuildPropsFile(outputPath, props);
		return true;
	}
}
