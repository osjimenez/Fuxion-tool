using System;
using System.IO;
using Fuxion.Tools.Core.Configuration;
using Fuxion.Tools.Core.StaticMetadata;
using Fuxion.Tools.Core.Versioning;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

namespace Fuxion.Tools.Sdk;

/// <summary>
/// Writes <c>Fuxion.StaticMetadata.g.cs</c>: project, build, version and repository data as constants
/// (<c>Fuxion.Metadata.&lt;Project&gt;</c>). Only rewritten when something changes.
/// </summary>
public sealed class FxStaticMetadata : Task
{
	[Required] public string RepositoryRoot { get; set; } = "";

	[Required] public string OutputPath { get; set; } = "";

	[Required] public string ProjectName { get; set; } = "";

	public string Namespace { get; set; } = "Fuxion.Metadata";

	public string? TargetFramework { get; set; }

	public string? Configuration { get; set; }

	/// <summary>Whether <see cref="FxVersion"/> ran: without it there is no Versioning class.</summary>
	public bool VersionCalculated { get; set; }

	public string? Version { get; set; }

	public string? PackageVersion { get; set; }

	public string? AssemblyVersion { get; set; }

	public string? FileVersion { get; set; }

	public string? InformationalVersion { get; set; }

	/// <summary>
	/// Build date: SOURCE_DATE_EPOCH if set, otherwise the date of the HEAD commit, so the file only changes with the
	/// commit (reproducible builds); now, only outside a repository.
	/// </summary>
	public string? BuildDateUtc { get; set; }

	[Output] public bool Written { get; private set; }

	public override bool Execute()
	{
		var root = Path.GetFullPath(RepositoryRoot);
		var repository = BuildCache.GetOrAdd(BuildEngine, $"Fuxion.Tools.Sdk.RepositoryInfo|{root}",
			() => RepositoryInfoReader.TryRead(new FuxionToolsConfig { Versioning = new GitVersioningConfig { RepositoryPath = root } }, null), out _);

		VersionProps? versionProps = VersionCalculated && !string.IsNullOrWhiteSpace(Version)
			? new(Version!, PackageVersion ?? Version!, AssemblyVersion ?? "", FileVersion ?? "", InformationalVersion ?? Version!, "")
			: null;

		Written = StaticMetadataFileGenerator.Generate(new(
			OutputPath: OutputPath,
			ProjectName: ProjectName,
			Namespace: string.IsNullOrWhiteSpace(Namespace) ? "Fuxion.Metadata" : Namespace.Trim(),
			VersionProps: versionProps,
			BuildDateUtc: ResolveBuildDate(repository),
			Branch: repository.Branch,
			Commit: repository.Commit,
			OriginUrlSHA256: repository.OriginUrlSHA256,
			FirstCommit: repository.FirstCommit,
			TargetFramework: TargetFramework,
			Configuration: Configuration));
		Log.LogMessage(MessageImportance.Low, $"Fuxion.Tools.Sdk: static metadata {(Written ? "written" : "up to date")}: {OutputPath}");
		return true;
	}

	string ResolveBuildDate(RepositoryInfo repository)
	{
		if (!string.IsNullOrWhiteSpace(BuildDateUtc))
			return BuildDateUtc!;
		if (long.TryParse(Environment.GetEnvironmentVariable("SOURCE_DATE_EPOCH"), out var epoch) && epoch >= 0)
			return DateTimeOffset.FromUnixTimeSeconds(epoch).ToString("O");
		return (repository.CommitDateUtc ?? DateTimeOffset.UtcNow).ToString("O");
	}
}
