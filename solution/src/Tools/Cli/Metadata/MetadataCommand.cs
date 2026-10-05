using System;
using System.Threading;
using Fuxion.Tools.Core.Configuration;
using Fuxion.Tools.Core.StaticMetadata;
using Fuxion.Tools.Core.Versioning;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Fuxion.Tools.Cli.Metadata;

public sealed class MetadataCommand : Command<MetadataCommandSettings>
{
	protected override int Execute(CommandContext context, MetadataCommandSettings settings, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(settings.OutputPath))
		{
			AnsiConsole.MarkupLine("[red]Missing[/] [yellow]--output[/].");
			return 2;
		}

		var config = FuxionToolsConfigLoader.LoadOrDefault(settings.ConfigPath);
		var staticCfg = config.StaticMetadata;
		if (staticCfg?.Enabled != true)
		{
			StaticMetadataFileGenerator.DeleteIfExists(settings.OutputPath);
			if (!settings.NonInteractive)
				AnsiConsole.MarkupLine("[yellow]StaticMetadata disabled[/].");
			return 0;
		}

		VersionProps? versionProps = null;
		if (config.Versioning is not null)
		{
			var inputs = VersioningInputsFactory.Create(config, settings.ConfigPath);
			versionProps = DefaultVersionPropsProvider.FromInputs(inputs);
		}

		var projectName = settings.ProjectName
			?? Environment.GetEnvironmentVariable("MSBuildProjectName")
			?? "<unknown>";
		var ns = string.IsNullOrWhiteSpace(staticCfg.Namespace) ? "Fx.Metadata" : staticCfg.Namespace.Trim();
		var (branch, commit,  originUrlSha256, firstCommit) = RepositoryInfoReader.TryRead(config, settings.ConfigPath);
		var targetFramework = Environment.GetEnvironmentVariable("TargetFramework") ??
		                      Environment.GetEnvironmentVariable("TARGET_FRAMEWORK");
		var updated = StaticMetadataFileGenerator.Generate(new(
			OutputPath: settings.OutputPath,
			ProjectName: projectName,
			Namespace: ns,
			VersionProps: versionProps,
			BuildDateUtc: ResolveBuildDateUtc(),
			Branch: branch,
			Commit: commit,
			OriginUrlSHA256: originUrlSha256,
			FirstCommit: firstCommit,
			TargetFramework: targetFramework,
			Configuration: Environment.GetEnvironmentVariable("Configuration")
		));

		if (settings.NonInteractive)
		{
			Console.WriteLine($"[Info] - {projectName} ({targetFramework}) - Metadata: {(updated ? "UPDATED" : "SKIP")}");
			return 0;
		}

		var table = new Table().RoundedBorder().BorderColor(Color.Grey);
		table.AddColumn("Property");
		table.AddColumn("Value");
		table.AddRow("Output", settings.OutputPath);
		table.AddRow("Namespace", ns);
		table.AddRow("Class", StaticMetadataFileGenerator.GetClassName(projectName));
		table.AddRow("Version", versionProps?.Version ?? "(none)");
		table.AddRow("Branch", branch ?? "(none)");
		table.AddRow("Commit", commit ?? "(none)");
		table.AddRow("OriginUrlSHA256", originUrlSha256 ?? "(none)");
		table.AddRow("FirstCommit", firstCommit ?? "(none)");
		table.AddRow("Written", updated ? "Yes" : "No (up-to-date)");
		AnsiConsole.Write(table);
		return 0;
	}

	static string ResolveBuildDateUtc()
	{
		var sourceDateEpoch = Environment.GetEnvironmentVariable("SOURCE_DATE_EPOCH");
		if (long.TryParse(sourceDateEpoch, out var epochSeconds) && epochSeconds >= 0)
			return DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime.ToString("O");
		return DateTimeOffset.UtcNow.UtcDateTime.ToString("O");
	}

}
