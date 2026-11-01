using System;
using System.Collections.Generic;
using System.IO;
using Fuxion.Tool.Core.Results;
using Fuxion.Tool.Core.Versioning;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

namespace Fuxion.Tool.Sdk;

/// <summary>
/// The version of the repository from its git history: the same calculation as <c>fx version</c>.
/// </summary>
public sealed class FxVersion : Task
{
	[Required] public string RepositoryRoot { get; set; } = "";

	public bool FailOnError { get; set; } = true;

	public bool ForceCi { get; set; }

	public string? InformationalSuffix { get; set; }

	[Output] public string Version { get; private set; } = "";

	[Output] public string PackageVersion { get; private set; } = "";

	[Output] public string AssemblyVersion { get; private set; } = "";

	[Output] public string FileVersion { get; private set; } = "";

	[Output] public string InformationalVersion { get; private set; } = "";

	public override bool Execute()
	{
		var root = Path.GetFullPath(RepositoryRoot);
		var ci = $"{Environment.GetEnvironmentVariable("CI")}|{Environment.GetEnvironmentVariable("GITHUB_ACTIONS")}";
		var key = $"Fuxion.Tool.Sdk.FxVersion|{root}|{FailOnError}|{ForceCi}|{InformationalSuffix}|{ci}";
		var cached = BuildCache.TryGet<VersionProps>(BuildEngine, key, out var props);
		if (!cached)
		{
			var calculated = VersioningInputsFactory.Calculate(new GitVersioningOptions(root, FailOnError, ForceCi,
				string.IsNullOrWhiteSpace(InformationalSuffix) ? null : InformationalSuffix));
			Write(calculated.Diagnostics);
			if (calculated is not VersioningResult result)
				return false;
			props = DefaultVersionPropsProvider.FromInputs(result.Inputs);
			BuildCache.Add(BuildEngine, key, props);
		}
		Version = props!.Version;
		PackageVersion = props.PackageVersion;
		AssemblyVersion = props.AssemblyVersion;
		FileVersion = props.FileVersion;
		InformationalVersion = props.InformationalVersion;
		Log.LogMessage(MessageImportance.Normal, $"Fuxion.Tool.Sdk: version {InformationalVersion} for '{root}'{(cached ? " (cached for this build)" : "")}.");
		return true;
	}

	/// <summary>The diagnostics of the versioning as MSBuild's: the errors as errors, the warnings as warnings, with their code, file and line.</summary>
	void Write(IEnumerable<FxDiagnostic> diagnostics)
	{
		foreach (var d in diagnostics)
			if (d.Severity == FxSeverity.Error)
				Log.LogError(null, d.Code, null, d.File, d.Line ?? 0, 0, 0, 0, d.Message.English);
			else if (d.Severity == FxSeverity.Warning)
				Log.LogWarning(null, d.Code, null, d.File, d.Line ?? 0, 0, 0, 0, d.Message.English);
	}

}
