using System;
using System.IO;
using Fuxion.Tools.Core.Configuration;
using Fuxion.Tools.Core.Versioning;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

namespace Fuxion.Tools.Sdk;

/// <summary>
/// The version of the repository from its git history (design §9, D-15): the same calculation as <c>fx version</c>.
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
		var key = $"Fuxion.Tools.Sdk.FxVersion|{root}|{FailOnError}|{ForceCi}|{InformationalSuffix}|{ci}";
		try
		{
			var props = BuildCache.GetOrAdd(BuildEngine, key, () =>
			{
				var result = VersioningInputsFactory.Calculate(new FuxionToolsConfig
				{
					Versioning = new GitVersioningConfig
					{
						RepositoryPath = root,
						FailOnError = FailOnError,
						ForceCIEnvironment = ForceCi,
						InformationalSuffix = string.IsNullOrWhiteSpace(InformationalSuffix) ? null : InformationalSuffix
					}
				}, warn: message => Log.LogWarning(message));
				return DefaultVersionPropsProvider.FromInputs(result.Inputs);
			}, out var cached);
			Version = props.Version;
			PackageVersion = props.PackageVersion;
			AssemblyVersion = props.AssemblyVersion;
			FileVersion = props.FileVersion;
			InformationalVersion = props.InformationalVersion;
			Log.LogMessage(MessageImportance.Normal, $"Fuxion.Tools.Sdk: version {InformationalVersion} for '{root}'{(cached ? " (cached for this build)" : "")}.");
			return true;
		}
		catch (VersioningException ex)
		{
			Log.LogError(null, ex.Code, null, null, 0, 0, 0, 0, ex.Message);
			return false;
		}
	}
}
